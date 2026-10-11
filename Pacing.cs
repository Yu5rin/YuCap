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
public sealed record RenderMeasurement(PacingCapture Pacing, double[] GpuMs, string PresentMode);
