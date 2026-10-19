using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace YuCap;

// Same-picture detection and the game frame rate (see README, "ゲームの実際のコマ数").
//
// For every captured frame (capture thread, under FrameLock) one tiny compute dispatch builds a 120x68 luma thumbnail of the
// frame's Y plane and reduces its difference to the previous frame's thumbnail to four numbers (Shaders/dup.hlsl). The four
// numbers are copied into a small ring of staging buffers and read back later with Map(DO_NOT_WAIT), so neither the capture
// thread nor the render thread ever waits for the GPU: a ticket whose answer is not there yet is simply looked at again at the
// next poll, and the poll stops at the first one that is not ready, so answers are always taken in capture order.
// An answer decides "same picture as the frame before" (DupMath), feeds the game-rate meter and the unique-picture timeline,
// and marks the frame's ring slot (if it has one) so that the interpolation skips repeats.
internal sealed partial class D3DRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DupCb
    {
        public uint W, H, HasPrev, Pad;
        public float T1, T2, P2, P3;
    }

    private sealed class DupTicket
    {
        public ID3D11Buffer Staging = null!;
        public long FrameNo, Arrival, Smooth;
        public bool HasPrev;
    }

    private const int TicketCount = 12;
    private ID3D11ComputeShader? _csDup;
    private ID3D11Buffer? _dupCb, _dupStat;
    private ID3D11UnorderedAccessView? _dupStatUav;
    private readonly ID3D11Buffer?[] _thumb = new ID3D11Buffer?[2];
    private readonly ID3D11ShaderResourceView?[] _thumbSrv = new ID3D11ShaderResourceView?[2];
    private readonly ID3D11UnorderedAccessView?[] _thumbUav = new ID3D11UnorderedAccessView?[2];
    private readonly DupTicket?[] _tk = new DupTicket?[TicketCount];
    private int _tkHead, _tkCount;                    // oldest unanswered ticket / how many are pending
    private int _thumbCur;
    private bool _thumbValid;
    private int _thumbW, _thumbH;
    private long _frameNo;                            // frames submitted since the device was made
    private readonly IntPtr _dupScratch = Marshal.AllocHGlobal(64);

    private readonly GameRateMeter _meter = new();
    /// <summary>No signal / signal, judged from the thumbnails (see Signal.cs).</summary>
    public readonly SignalDetector Signal = new();
    private readonly UniqueTimeline _uniq = new();
    private volatile bool _dupDisabled;               // test hook: no detection at all (the old behaviour)
    private double _nominalMs = 1000.0 / 60;
    private long _resolvedFrameNo;                    // newest frame whose answer has been taken

    // how long an answer takes to arrive after the frame did (ticks), worst of the last 60
    private readonly long[] _answerLat = new long[60];
    private int _answerLatPos, _answerLatN;

    /// <summary>The nominal frame interval the capture thread measured (ms); the timeline of unique pictures needs it.</summary>
    public void SetNominalMs(double ms) { if (ms > 1) Volatile.Write(ref _nominalMs, ms); }

    /// <summary>True when the thumbnail pass exists (the shader compiled and the buffers could be made).</summary>
    public bool DupAvailable => _csDup != null && !_dupDisabled;

    /// <summary>Test hook: switch the detection off (frames are then all "unique" and the timeline is the former one) or on.</summary>
    public bool DupEnabled
    {
        get => !_dupDisabled;
        set { _dupDisabled = !value; }
    }

    /// <summary>Test hook: interpolate on the unique-picture timeline (true, the default) or on the raw frame timeline (false).</summary>
    public bool UniqueAware { get; set; } = true;

    /// <summary>Unique pictures per second over about the last second; 0 = unknown or a still picture (see <see cref="PictureStill"/>).</summary>
    public double GameFps => _meter.FpsAt(MeterNow);
    public bool GameFpsReliable => _meter.ReliableAt(MeterNow);
    public bool PictureStill => _meter.StillAt(MeterNow);
    private long MeterNow => ClockOverride?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>Test hook: called for every answer (capture/render thread, under FrameLock): frame number, arrival tick, sum*64, max*64, counts, verdict "same".</summary>
    public Action<long, long, uint, uint, uint, uint, bool>? DupSink { get; set; }

    private void DupInit(D3DShaders.Bytecode b)
    {
        if (!b.DupOk) return;
        try
        {
            _csDup = Device.CreateComputeShader(b.Dup);
            _dupCb = Device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Default));
            _dupStat = Device.CreateBuffer(new BufferDescription(32, BindFlags.UnorderedAccess, ResourceUsage.Default));
            _dupStatUav = Device.CreateUnorderedAccessView(_dupStat, new UnorderedAccessViewDescription
            {
                Format = Format.R32_UInt, ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = 8 },
            });
            for (int i = 0; i < 2; i++)
            {
                _thumb[i] = Device.CreateBuffer(new BufferDescription(DupMath.ThumbPixels * 4, BindFlags.ShaderResource | BindFlags.UnorderedAccess, ResourceUsage.Default));
                _thumbSrv[i] = Device.CreateShaderResourceView(_thumb[i]!, new ShaderResourceViewDescription
                {
                    Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Buffer,
                    Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)DupMath.ThumbPixels },
                });
                _thumbUav[i] = Device.CreateUnorderedAccessView(_thumb[i]!, new UnorderedAccessViewDescription
                {
                    Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)DupMath.ThumbPixels },
                });
            }
            for (int i = 0; i < TicketCount; i++)
            {
                _tk[i] = new DupTicket
                {
                    Staging = Device.CreateBuffer(new BufferDescription(32, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)),
                };
            }
        }
        catch (Exception ex)
        {
            Log.Info("D3D: same-picture detection could not be set up - the game frame rate is not measured: " + ex.Message.Split('\n')[0]);
            DupFree();
        }
    }

    private void DupFree()
    {
        _csDup?.Dispose(); _csDup = null;
        _dupCb?.Dispose(); _dupCb = null;
        _dupStatUav?.Dispose(); _dupStatUav = null;
        _dupStat?.Dispose(); _dupStat = null;
        for (int i = 0; i < 2; i++)
        {
            _thumbSrv[i]?.Dispose(); _thumbSrv[i] = null;
            _thumbUav[i]?.Dispose(); _thumbUav[i] = null;
            _thumb[i]?.Dispose(); _thumb[i] = null;
        }
        for (int i = 0; i < TicketCount; i++) { _tk[i]?.Staging.Dispose(); _tk[i] = null; }
        _tkCount = 0; _tkHead = 0; _thumbValid = false;
    }

    /// <summary>The two dispatches: thumbnail of the frame into buffer <paramref name="cur"/>, then its difference to buffer <paramref name="prev"/>.</summary>
    private void DupPasses(ID3D11ShaderResourceView ySrv, int cur, int prev, int part = 0)
    {
        Ctx.ClearUnorderedAccessView(_dupStatUav!, new Vortice.Mathematics.Int4(0, 0, 0, 0));
        if (part == 1) return;
        Ctx.CSSetConstantBuffer(0, _dupCb);
        Ctx.CSSetShader(_csDup);
        Ctx.CSSetSampler(0, _linear);
        Ctx.CSSetShaderResource(0, ySrv);
        Ctx.CSSetShaderResource(1, _thumbSrv[prev]);
        Ctx.CSSetUnorderedAccessView(0, _thumbUav[cur]);
        Ctx.CSSetUnorderedAccessView(1, _dupStatUav);
        Ctx.Dispatch((DupMath.ThumbW + 7) / 8, (DupMath.ThumbH + 7) / 8, 1);
        Ctx.CSSetUnorderedAccessView(0, null!);
        Ctx.CSSetUnorderedAccessView(1, null!);
        Ctx.CSSetShaderResource(0, null!);
        Ctx.CSSetShaderResource(1, null!);
    }

    /// <summary>Capture thread, FrameLock held, right after the frame's Y plane was written: queue the thumbnail pass and give the
    /// frame its number. <paramref name="ySrv"/> is an R8_UNorm view of the frame's luma.</summary>
    private long DupSubmit(ID3D11ShaderResourceView? ySrv, int w, int h, long arrival, long smooth)
    {
        long fn = ++_frameNo;
        if (_csDup == null || _dupDisabled || ySrv == null) return fn;
        try
        {
            DupPoll();
            if (_tkCount == TicketCount)
            {
                // the oldest answer never came: give it up (that frame counts as a new picture)
                DupTicket lost = _tk[_tkHead]!;
                DupTaken(lost, false, 0, 0, 0, 0, 0, 0);
                _tkHead = (_tkHead + 1) % TicketCount; _tkCount--;
            }
            bool hasPrev = _thumbValid && _thumbW == w && _thumbH == h;
            int cur = _thumbCur, prev = 1 - cur;
            Marshal.StructureToPtr(new DupCb { W = (uint)w, H = (uint)h, HasPrev = hasPrev ? 1u : 0u, T1 = DupMath.T1, T2 = DupMath.T2 }, _dupScratch, false);
            Ctx.UpdateSubresource(_dupCb!, 0, null, _dupScratch, 0, 0);
            DupPasses(ySrv, cur, prev);
            DupTicket tk = _tk[(_tkHead + _tkCount) % TicketCount]!;
            Ctx.CopyResource(tk.Staging, _dupStat!);
            _dupFlushPending = true;                       // flushed by the caller once FrameLock is released
            tk.FrameNo = fn; tk.Arrival = arrival; tk.Smooth = smooth; tk.HasPrev = hasPrev;
            _tkCount++;
            _thumbCur = prev; _thumbValid = true; _thumbW = w; _thumbH = h;
        }
        catch (Exception ex)
        {
            Log.Info("D3D: same-picture detection failed and is switched off: " + ex.Message.Split('\n')[0]);
            _dupDisabled = true;
        }
        return fn;
    }

    private volatile bool _dupFlushPending;

    /// <summary>Capture thread, FrameLock NOT held: hand the queued thumbnail pass to the GPU now, so its answer is there by the next frame
    /// instead of waiting for the render thread's next Present (the render thread must not wait behind a flush that is not its own).</summary>
    private void DupFlush()
    {
        if (!_dupFlushPending) return;
        _dupFlushPending = false;
        try { Ctx.Flush(); } catch { }
    }

    /// <summary>Take every answer that is ready, oldest first, never waiting. FrameLock held.</summary>
    private void DupPoll()
    {
        while (_tkCount > 0)
        {
            DupTicket tk = _tk[_tkHead]!;
            Result hr = Ctx.Map(tk.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource m);
            if (hr.Failure) break;                         // still drawing (or lost): look again later
            uint sum, max, c1, c2, dark, lit;
            try
            {
                sum = (uint)Marshal.ReadInt32(m.DataPointer, 0); max = (uint)Marshal.ReadInt32(m.DataPointer, 4);
                c1 = (uint)Marshal.ReadInt32(m.DataPointer, 8); c2 = (uint)Marshal.ReadInt32(m.DataPointer, 12);
                dark = (uint)Marshal.ReadInt32(m.DataPointer, 16); lit = (uint)Marshal.ReadInt32(m.DataPointer, 20);
            }
            finally { Ctx.Unmap(tk.Staging, 0); }
            _tkHead = (_tkHead + 1) % TicketCount; _tkCount--;
            DupTaken(tk, true, sum, max, c1, c2, dark, lit);
        }
    }

    /// <summary>Test hook (offline replay): wait until every frame up to <paramref name="frameNo"/> has been answered.</summary>
    internal void DupResolveUpTo(long frameNo)
    {
        lock (FrameLock)
        {
            var sw = Stopwatch.StartNew();
            while (_tkCount > 0 && _tk[_tkHead]!.FrameNo <= frameNo && sw.ElapsedMilliseconds < 2000)
            {
                DupPoll();
                if (_tkCount > 0 && _tk[_tkHead]!.FrameNo <= frameNo) { Ctx.Flush(); Thread.Sleep(0); }
            }
        }
    }

    /// <summary>Frames submitted so far (replay: the number the next frame will follow).</summary>
    internal long FramesSubmitted { get { lock (FrameLock) return _frameNo; } }

    private void DupTaken(DupTicket tk, bool known, uint sum, uint max, uint c1, uint c2, uint dark, uint lit)
    {
        if (known)
        {
            // "Is the console sending a picture?": the card's own No Signal screen is mostly black with a little bright text and never changes.
            bool message = SignalMath.MessageLike(dark, lit);
            bool still = tk.HasPrev && SignalMath.Still(sum, c2);
            Signal.Feed(tk.Arrival, message, SignalMath.BlackAll(dark, lit), still);
        }
        bool same = known && tk.HasPrev && DupMath.Same(sum, max, c1, c2);
        bool unique = !same;
        long now = ClockOverride?.Invoke() ?? Stopwatch.GetTimestamp();
        _resolvedFrameNo = tk.FrameNo;
        if (known)
        {
            _answerLat[_answerLatPos] = Math.Max(0, now - tk.Arrival);
            _answerLatPos = (_answerLatPos + 1) % _answerLat.Length;
            if (_answerLatN < _answerLat.Length) _answerLatN++;
        }
        double nomTicks = Volatile.Read(ref _nominalMs) * Stopwatch.Frequency / 1000.0;
        long u = tk.Smooth;
        if (!tk.HasPrev) _uniq.Reset();
        if (unique) u = _uniq.Resolve(tk.Smooth, nomTicks);
        _meter.Add(tk.Arrival, unique);
        foreach (Slot s in _ring)
        {
            if (s.Seq == 0 || s.FrameNo != tk.FrameNo) continue;
            s.Dup = same; s.U = u; s.Resolved = true;
            break;
        }
        DupSink?.Invoke(tk.FrameNo, tk.Arrival, sum, max, c1, c2, same);
    }

    /// <summary>Extra hold-back (ms) the interpolation needs while pictures repeat, on top of one frame interval: the unique-picture
    /// interval minus the frame interval, the worst early lag of the evened-out times, and the time an answer takes to arrive
    /// (a repeat is only known after it). 0 when every frame is a new picture or detection is off.</summary>
    public double CadenceExtraHoldMs()
    {
        lock (FrameLock)
        {
            if (!UniqueAware || !DupAvailable || !_uniq.Cadence || _uniq.PeriodTicks <= 0) return 0;
            double nomTicks = Volatile.Read(ref _nominalMs) * Stopwatch.Frequency / 1000.0;
            double lat = 0;
            for (int i = 0; i < _answerLatN; i++) lat = Math.Max(lat, _answerLat[i]);
            lat = Math.Min(lat, 3 * nomTicks);
            double ticks = Math.Max(0, _uniq.PeriodTicks - nomTicks) + _uniq.PhaseLagTicks + lat;
            return ticks * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>Unique-picture interval (ms) while pictures repeat, else 0.</summary>
    public double UniquePeriodMs { get { lock (FrameLock) return _uniq.Cadence ? _uniq.PeriodTicks * 1000.0 / Stopwatch.Frequency : 0; } }

    /// <summary>Worst answer delay of the last 60 frames (ms).</summary>
    public double AnswerLatencyMs
    {
        get
        {
            lock (FrameLock)
            {
                long m = 0;
                for (int i = 0; i < _answerLatN; i++) m = Math.Max(m, _answerLat[i]);
                return m * 1000.0 / Stopwatch.Frequency;
            }
        }
    }

    /// <summary>One ring frame as the pair selection sees it, for the test mode's simulation of the timeline.</summary>
    internal readonly record struct TimelineFrame(long Seq, double ArrivalMs, double SmoothMs, double UMs, bool Dup, bool Resolved);

    /// <summary>Test mode: the newest <paramref name="count"/> ring frames (oldest first) with their times in ms (Stopwatch origin 0) and
    /// repeat flags, plus the figures the live timeline works with. False when the ring is not in use.</summary>
    internal bool DebugTimeline(int count, List<TimelineFrame> into, out double nominalMs, out double periodMs, out double extraHoldMs, out double answerLatMs)
    {
        nominalMs = Volatile.Read(ref _nominalMs); periodMs = 0; extraHoldMs = 0; answerLatMs = 0;
        lock (FrameLock)
        {
            if (!_interpHold) return false;
            DupPoll();
            double k = 1000.0 / Stopwatch.Frequency;
            var slots = _ring.Where(s => s.Seq != 0).OrderBy(s => s.Seq).ToList();
            if (slots.Count > count) slots = slots.GetRange(slots.Count - count, count);
            foreach (Slot s in slots) into.Add(new TimelineFrame(s.Seq, s.Arrival * k, s.Smooth * k, s.U * k, s.Dup, s.Resolved));
            periodMs = _uniq.Cadence ? _uniq.PeriodTicks * k : 0;
        }
        extraHoldMs = CadenceExtraHoldMs();
        answerLatMs = AnswerLatencyMs;
        return true;
    }

    /// <summary>GPU milliseconds of the thumbnail pass over <paramref name="n"/> runs on the current frame (benchmark).</summary>
    internal double[] DupBench(int n, int part = 0)
    {
        lock (FrameLock)
        {
            ID3D11ShaderResourceView? y = _gpuFrame ? _nv12Y : _srvY;
            if (_csDup == null || y == null) return Array.Empty<double>();
            return TimeGpu(() => DupPasses(y, _thumbCur, 1 - _thumbCur, part), n);
        }
    }
}
