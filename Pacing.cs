using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.DXGI;

namespace YuCap;

/// <summary>One frame the display actually showed, as <c>IDXGISwapChain.GetFrameStatistics</c> reports it.
/// <see cref="SyncQpc"/> is in QueryPerformanceCounter ticks (the same clock as <see cref="Stopwatch"/>).</summary>
public readonly record struct StatSample(uint PresentCount, uint PresentRefreshCount, uint SyncRefreshCount, long SyncQpc);

/// <summary>
/// Raw frame-pacing data collected by the Direct3D renderer between <see cref="IVideoEngine.BeginMeasure"/>
/// and <see cref="IVideoEngine.EndMeasure"/> (test mode only; nothing is recorded otherwise).
/// Two views of the same stream of frames:
///   - <see cref="Stats"/>: what the display really showed, from GetFrameStatistics after each Present
///     (refresh counter + vsync time). Composition and some tearing swap chains cannot supply this - the
///     call then fails (typically DXGI_ERROR_FRAME_STATISTICS_DISJOINT or a "not supported" code) and
///     <see cref="StatsFailures"/> / <see cref="LastFailure"/> say how often and with what.
///   - <see cref="PresentTicks"/>: when the render thread returned from Present, always available. It
///     shows how steadily frames are handed over, not when the panel showed them.
/// </summary>
public sealed class PacingCapture
{
    private readonly object _gate = new();
    private readonly List<long> _presentTicks = new();
    private readonly List<StatSample> _stats = new();
    private uint _lastPresentCount = uint.MaxValue;

    public int StatsAttempts { get; private set; }
    public int StatsFailures { get; private set; }
    /// <summary>HRESULT text of the most recent GetFrameStatistics failure, or empty.</summary>
    public string LastFailure { get; private set; } = string.Empty;

    public long[] PresentTicks { get { lock (_gate) return _presentTicks.ToArray(); } }
    public StatSample[] Stats { get { lock (_gate) return _stats.ToArray(); } }

    /// <summary>Called by the renderer right after a successful Present (render thread).</summary>
    internal void Record()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate) _presentTicks.Add(now);
    }

    // ---- frame statistics poller ----
    // GetFrameStatistics describes the most recent frame the display showed. Asking once per Present
    // misses frames whenever the display shows more than one per Present (or the reverse), so a small
    // thread asks every millisecond or so and keeps each newly displayed frame once. DXGI swap chains
    // may be queried from a thread other than the one that presents.
    private Thread? _poller;
    private volatile bool _stopPolling;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    internal void StartPolling(IDXGISwapChain swap)
    {
        _stopPolling = false;
        _poller = new Thread(() => PollLoop(swap)) { IsBackground = true, Name = "YuCap.Pacing.Poll" };
        _poller.Start();
    }

    internal void StopPolling()
    {
        _stopPolling = true;
        _poller?.Join(500);
        _poller = null;
    }

    private void PollLoop(IDXGISwapChain swap)
    {
        timeBeginPeriod(1);
        try
        {
            while (!_stopPolling)
            {
                try
                {
                    StatsAttempts++;
                    var r = swap.GetFrameStatistics(out FrameStatistics fs);
                    if (r.Failure)
                    {
                        StatsFailures++;
                        LastFailure = $"0x{r.Code:X8}";
                    }
                    else
                    {
                        lock (_gate)
                        {
                            // The same displayed frame is reported again until the next one reaches the screen.
                            if (fs.PresentCount != _lastPresentCount)
                            {
                                _lastPresentCount = fs.PresentCount;
                                _stats.Add(new StatSample(fs.PresentCount, fs.PresentRefreshCount, fs.SyncRefreshCount, fs.SyncQPCTime));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LastFailure = $"0x{ex.HResult:X8}";
                    StatsFailures++;
                    return;   // the swap chain is gone
                }
                Thread.Sleep(1);
            }
        }
        finally { timeEndPeriod(1); }
    }
}

/// <summary>What <see cref="IVideoEngine.EndMeasure"/> hands back: pacing plus the GPU time of every frame
/// rendered in the window, and the present mode they went through.</summary>
public sealed record RenderMeasurement(PacingCapture Pacing, double[] GpuMs, string PresentMode,
    double[]? LatencyMs = null, bool LatencyEstimated = false);

/// <summary>
/// How long a picture takes from the moment YuCap received it from the capture card until the display shows it.
/// The render thread notes every present that shows NEW content (<see cref="Shown"/>: swap chain present count +
/// the instant on the source timeline that the picture belongs to) and calls <see cref="Poll"/> once per loop; when
/// GetFrameStatistics reports that present count as displayed, latency = SyncQPCTime - content time. Where the
/// statistics are unavailable (some low-latency swap chains) the value falls back to "time Present returned -
/// content time + one refresh" and is marked estimated.
/// Writers run on the render thread; readers (<see cref="Ms"/>, <see cref="Estimated"/>) use volatile access and
/// the measurement window is guarded by a lock.
/// </summary>
internal sealed class VideoLatencyTracker
{
    private const int Ring = 128;
    private readonly uint[] _pc = new uint[Ring];
    private readonly long[] _content = new long[Ring];
    private int _count;
    private double _ema;
    private long _lastSampleTick;
    private volatile bool _estimated;
    private long _firstShown;          // Stopwatch tick of the first Shown (0 = none yet)
    private bool _statsEverOk;
    private double _refreshMs = 16.7;
    private readonly object _gate = new();
    private List<double>? _collect;

    /// <summary>Smoothed latency in ms; 0 when unknown (nothing shown lately).</summary>
    public double Ms
    {
        get
        {
            long last = Volatile.Read(ref _lastSampleTick);
            if (last == 0 || (Stopwatch.GetTimestamp() - last) > 3 * Stopwatch.Frequency) return 0;
            return Volatile.Read(ref _ema);
        }
    }

    public bool Estimated => _estimated;

    public void SetRefreshMs(double ms) { if (ms > 1 && ms < 100) _refreshMs = ms; }

    /// <summary>Render thread: a present that shows new content went through.</summary>
    public void Shown(uint presentCount, long contentTicks, long returnedTick)
    {
        if (contentTicks == 0) return;
        int i = _count++ % Ring;
        _pc[i] = presentCount;
        _content[i] = contentTicks;
        if (_firstShown == 0) _firstShown = returnedTick;
        // No statistics for a second: estimate from when Present returned.
        if (!_statsEverOk && returnedTick - _firstShown > Stopwatch.Frequency)
        {
            double ms = (returnedTick - contentTicks) * 1000.0 / Stopwatch.Frequency + _refreshMs;
            Add(ms, estimated: true);
        }
    }

    /// <summary>Render thread, once per loop: pick up the statistics of the most recently displayed frame.</summary>
    public void Poll(IDXGISwapChain swap)
    {
        if (_count == 0) return;
        try
        {
            var r = swap.GetFrameStatistics(out FrameStatistics fs);
            if (r.Failure || fs.SyncQPCTime == 0) return;
            int n = Math.Min(_count, Ring);
            for (int k = 0; k < n; k++)
            {
                int i = (_count - 1 - k) % Ring;
                if (_pc[i] != fs.PresentCount) continue;
                double ms = (fs.SyncQPCTime - _content[i]) * 1000.0 / Stopwatch.Frequency;
                _pc[i] = uint.MaxValue;   // count each shown frame once
                if (ms < 0 || ms > 5000) return;
                _statsEverOk = true;
                Add(ms, estimated: false);
                return;
            }
        }
        catch { /* swap chain gone or statistics unsupported */ }
    }

    private void Add(double ms, bool estimated)
    {
        _estimated = estimated;
        double prev = Volatile.Read(ref _ema);
        bool fresh = Volatile.Read(ref _lastSampleTick) == 0 || prev <= 0;
        Volatile.Write(ref _ema, fresh ? ms : prev * 0.95 + ms * 0.05);
        Volatile.Write(ref _lastSampleTick, Stopwatch.GetTimestamp());
        lock (_gate) _collect?.Add(ms);
    }

    public void BeginCollect() { lock (_gate) _collect = new List<double>(); }

    public (double[] Samples, bool Estimated) EndCollect()
    {
        lock (_gate)
        {
            double[] a = _collect?.ToArray() ?? Array.Empty<double>();
            _collect = null;
            return (a, _estimated);
        }
    }
}
