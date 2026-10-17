using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace YuCap;

/// <summary>
/// The Direct3D 11 renderer: frames are pulled from a Media Foundation source reader and
/// presented through our own flip-model swap chain, which is what makes processing (FSR 1
/// upscaling, exact stills) possible.
///
/// THREADS. The UI thread only posts requests and waits a bounded time; it never blocks on
/// the GPU or Media Foundation, and nothing here ever needs the UI thread to respond (that
/// is how the Capture Engine path deadlocked on fullscreen):
///   render thread  — owns the D3D11 device, swap chain, DirectComposition objects, shaders
///                    and every GPU resource, and services snapshot requests;
///   capture thread — owns the media source and source reader, loops on a synchronous
///                    ReadSample and hands each frame over through a "latest frame" slot.
/// </summary>
public sealed class D3DVideoEngine : IVideoEngine
{
    private static readonly object StartupLock = new();
    private static bool _mfStarted;

    private IntPtr _hwnd;
    private Session? _s;

    // Orientation and quality knobs. Plain fields: each is one atomic word, and the render
    // thread re-reads them on every frame.
    private volatile int _rotation;
    private volatile bool _mirror;
    private volatile ScalingQuality _quality = ScalingQuality.Fsr;
    private float _sharpness = 0.5f;
    private volatile bool _pixelPerfect;
    private readonly object _adjLock = new();
    private VideoAdjustments _adj = VideoAdjustments.Neutral;   // four floats: read/written under _adjLock
    private volatile int _videoDelayMs;
    private readonly object _cropLock = new();
    private Padding _crop = Padding.Empty;                     // five ints: read/written under _cropLock
    private volatile bool _lowLatency;
    private volatile bool _interpolation;
    private volatile int _nrMode;                              // 0 off, -1 auto, >0 a fixed height
    private string _nrModeStr = "off";

    private volatile OverlayLayer[]? _overlays;
    private volatile bool _crispOn;
    private volatile int _crispMs = 5000;

    private readonly object _dstLock = new();
    private Rectangle _dst;

    public D3DVideoEngine() { }

    // ---- state reported to the UI ------------------------------------------
    public string? CurrentDeviceName { get; private set; }
    public Size CurrentResolution { get; private set; }
    public CaptureMode? CurrentMode { get; private set; }
    public int NegotiatedFps { get; private set; }
    public string? LastSnapshotError { get; private set; }
    public bool IsRunning => _s is { Alive: true };
    private List<CaptureMode> _modes = new();

    public int Rotation => _rotation;
    public bool Mirror => _mirror;
    /// <summary>The cropped, then rotated, size - what the convert pass really produces.</summary>
    public Size DisplayResolution
    {
        get
        {
            Size src = CurrentResolution;
            if (src.Width <= 0 || src.Height <= 0) return src;
            Size kept = CropMath.Effective(src, Crop).Size;
            return _rotation is 90 or 270 ? new Size(kept.Height, kept.Width) : kept;
        }
    }

    public bool SupportsCrop => true;
    public Padding Crop
    {
        get { lock (_cropLock) return _crop; }
        set
        {
            lock (_cropLock) _crop = new Padding(Math.Max(0, value.Left), Math.Max(0, value.Top),
                Math.Max(0, value.Right), Math.Max(0, value.Bottom));
            _s?.PokeConvert();   // the crop lives in the convert pass, like the adjustments
        }
    }

    public bool SupportsLowLatency => true;
    /// <summary>Read when a session starts: the swap chain kind cannot change under a running one.</summary>
    public bool LowLatency
    {
        get => _lowLatency;
        set => _lowLatency = value;
    }
    public string PresentMode => _s?.PresentMode ?? string.Empty;

    public bool SupportsOverlays => true;
    public void SetOverlays(IReadOnlyList<OverlayLayer>? layers)
    {
        _overlays = layers == null || layers.Count == 0 ? null : layers.ToArray();
        _s?.Poke();   // the overlay changed but the video may not have: present again
    }

    public bool SupportsCrispness => true;
    public bool CrispnessSampling { get => _crispOn; set => _crispOn = value; }
    public int CrispnessIntervalMs { get => _crispMs; set => _crispMs = Math.Clamp(value, 200, 600000); }
    public CrispnessReading? Crispness => _s?.Crisp;

    public bool SupportsScalingQuality => true;
    public ScalingQuality Quality
    {
        get => _quality;
        set { _quality = value; _s?.Poke(); }
    }
    public float Sharpness
    {
        get => _sharpness;
        set { _sharpness = Math.Clamp(value, 0f, 1f); _s?.Poke(); }
    }
    public bool PixelPerfect
    {
        get => _pixelPerfect;
        set { _pixelPerfect = value; _s?.Poke(); }
    }
    public bool SupportsNativeRes => true;
    public string NativeResScale
    {
        get => _nrModeStr;
        set
        {
            string v = AppSettings.NormalizeNativeRes(value);
            _nrModeStr = v;
            _nrMode = v == "off" ? 0 : v == "auto" ? -1 : int.Parse(v);
            _s?.Poke();
        }
    }
    public NativeResStatus? NativeResState => _s?.NativeRes(_nrModeStr, _nrMode);

    public Bitmap? RenderOffscreen(Size output, ScalingQuality quality, int nativeH, int filter)
    {
        Session? s = _s;
        if (s == null || !s.Alive) return null;
        float sharp = _sharpness;
        return s.Run(r => r.RenderOffscreen(output, quality, sharp, nativeH, filter), 8000, needFrame: true, out _);
    }

    public Bitmap? RenderBitmap(Bitmap source, Size output, ScalingQuality quality, int nativeH, int filter)
    {
        Session? s = _s;
        if (s == null || !s.Alive) return null;
        float sharp = _sharpness;
        Bitmap? bmp = s.Run(r => r.RenderFromBitmap(source, output, quality, sharp, nativeH, filter), 15000, needFrame: false, out _);
        s.PokeConvert();   // the live picture was replaced: convert the next frame again
        return bmp;
    }

    public bool BenchPrescale(Bitmap? source, Size output, ScalingQuality quality, int nativeH, int filter, int frames,
        out double[] plain, out double[] withPre, out double[] passOnly)
    {
        double[] a = Array.Empty<double>(), b = a, c = a;
        bool ok = false;
        Session? s = _s;
        float sharp = _sharpness;
        if (s != null && s.Alive)
            s.Run(r => { ok = r.PrescaleBench(source, output, quality, sharp, nativeH, filter, frames, out a, out b, out c); return null; }, 90000, needFrame: source == null, out _);
        plain = a; withPre = b; passOnly = c;
        return ok;
    }

    public bool BenchQuality(Bitmap a, Bitmap? b, Size output, ScalingQuality quality, int nativeH, int frames, bool cached, out double[] ms)
    {
        double[] r = Array.Empty<double>();
        Session? s = _s;
        float sharp = _sharpness;
        if (s != null && s.Alive)
            s.Run(rr => { r = rr.QualityBench(a, b, output, quality, sharp, nativeH, frames, cached); return null; }, 120000, needFrame: false, out _, allowNull: true);
        s?.PokeConvert();   // the live picture was replaced: convert the next frame again
        ms = r;
        return r.Length > 0;
    }

    public RenderStats? Stats => _s?.Stats;
    public long FramesReceived => _s?.FramesReceived ?? 0;
    public long FramesPresented => _s?.FramesPresented ?? 0;
    public long FramesDropped => _s?.FramesDropped ?? 0;

    public bool SupportsAdjustments => true;
    public VideoAdjustments Adjustments
    {
        get { lock (_adjLock) return _adj; }
        set
        {
            // A re-convert, not just a re-compose: the adjustment lives in the oriented texture.
            lock (_adjLock) _adj = value.Clamped();
            _s?.PokeConvert();
        }
    }
    private VideoAdjustments CurrentAdjustments() { lock (_adjLock) return _adj; }

    public bool SupportsInterpolation => true;
    public bool Interpolation
    {
        get => _interpolation;
        set { _interpolation = value; _s?.PokeInterpolation(); }
    }
    public InterpolationStatus? InterpolationState => _s?.InterpStatus();

    public bool SupportsVideoDelay => true;
    public int VideoDelayMs
    {
        get => _videoDelayMs;
        set { _videoDelayMs = Math.Clamp(value, 0, 1000); _s?.Poke(); }
    }
    public double VideoDelayActualMs => _s?.DelayActualMs ?? 0;
    public double VideoPacingMarginMs => _s?.PacingMarginMs ?? 0;
    public double VideoLatencyMs => _s?.Renderer?.Latency.Ms ?? 0;
    public bool VideoLatencyEstimated => _s?.Renderer?.Latency.Estimated ?? false;

    public List<CaptureMode> GetModes() => new(_modes);
    public bool IsRectUpdateStuck(int thresholdMs) => false;

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    public bool SetRotation(int degrees)
    {
        int d = ((degrees % 360) + 360) % 360;
        _rotation = d is 0 or 90 or 180 or 270 ? d : 0;
        _s?.PokeConvert();
        return true;
    }

    public bool SetMirror(bool mirrored)
    {
        _mirror = mirrored;
        _s?.PokeConvert();
        return true;
    }

    public int SetVideoRect(Rectangle dst, bool clearBorder = true, int settleMs = 0)
    {
        lock (_dstLock) _dst = dst;
        Session? s = _s;
        if (s == null || !s.Alive) return -1;
        s.Poke();
        return 0;
    }

    private Rectangle CurrentDst() { lock (_dstLock) return _dst; }

    internal static void EnsureStartup()
    {
        lock (StartupLock)
        {
            if (_mfStarted) return;
            MediaFactory.MFStartup(false);
            _mfStarted = true;
        }
    }

    // =====================================================================
    //  Start / Stop
    // =====================================================================

    public void Start(VideoDeviceInfo info, CaptureMode? mode = null)
    {
        Log.Info($"D3D: Start {info.Name} mode={(mode == null ? "auto" : $"{mode.Compression} {mode.Width}x{mode.Height}@{mode.Fps}")}");
        Stop();
        if (_hwnd == IntPtr.Zero)
            throw new UserFacingException(L.T("表示ウィンドウが設定されていません。"));

        var s = new Session(this, _hwnd, info, mode);
        s.Begin();
        if (!s.Started.Wait(8000))
        {
            Log.Info("D3D: Start timed out after 8s — abandoning the threads");
            s.RequestStop("start timeout");
            throw new UserFacingException(L.T("映像の開始がタイムアウトしました。"));
        }
        if (s.StartError != null)
        {
            s.JoinRender(2000);   // let it release the window's composition target before a retry
            ExceptionDispatchInfo.Capture(s.StartError).Throw();
        }

        _s = s;
        CurrentDeviceName = info.Name;
        CurrentResolution = s.Resolution;
        NegotiatedFps = s.Fps;
        CurrentMode = s.Mode;
        _modes = s.Modes;
        Log.Info($"D3D: running {CurrentResolution.Width}x{CurrentResolution.Height}@{NegotiatedFps} ({s.Mode?.Compression})");
    }

    public void Stop()
    {
        Session? s = _s;
        _s = null;
        if (s != null)
        {
            Log.Info("D3D: Stop");
            s.Shutdown(3000);
        }
        CurrentDeviceName = null;
        CurrentResolution = Size.Empty;
        CurrentMode = null;
        NegotiatedFps = 0;
        _modes = new();
    }

    public void Dispose() => Stop();

    // =====================================================================
    //  Stills
    // =====================================================================

    public Bitmap? PhotoSnapshot(int timeoutMs = 1500)
    {
        Session? s = _s;
        if (s == null || !s.Alive) return null;
        return s.Run(r => r.ReadOriented(), timeoutMs, needFrame: true, out _);
    }

    public Bitmap? Snapshot(bool cropToVideo = true)
    {
        LastSnapshotError = null;
        Session? s = _s;
        if (s == null || !s.Alive)
        {
            LastSnapshotError = L.T("映像がありません");
            return null;
        }
        Bitmap? bmp = s.Run(r => r.HasComposed ? r.ReadComposed(cropToVideo) : null, 1500,
            needFrame: false, out string? err);
        if (bmp == null) LastSnapshotError = err ?? L.T("映像がありません");
        return bmp;
    }

    public Bitmap? RenderOffscreen(Size output, ScalingQuality quality)
    {
        Session? s = _s;
        if (s == null || !s.Alive) return null;
        float sharp = _sharpness;
        return s.Run(r => r.RenderOffscreen(output, quality, sharp), 5000, needFrame: true, out _);
    }

    public GpuBenchResult? GpuBench(Size output, ScalingQuality quality, int frames)
    {
        Session? s = _s;
        if (s == null || !s.Alive) return null;
        float sharp = _sharpness;
        GpuBenchResult? result = null;
        s.Run(r =>
        {
            result = r.GpuBench(output, quality, sharp, frames, _rotation, _mirror, CurrentAdjustments(), Crop, _nrMode > 0 ? _nrMode : 0);
            return null;
        }, 60000, needFrame: true, out _);
        return result;
    }

    /// <summary>Debug hook for <c>--interp-probe</c>: the cached pair's A and B and the picture synthesised between them.</summary>
    internal bool DebugInterpStills(double alpha, out Bitmap? a, out Bitmap? mid, out Bitmap? b)
    {
        Bitmap? ra = null, rm = null, rb = null;
        bool ok = false;
        Session? s = _s;
        if (s != null && s.Alive)
            s.Run(r => { ok = r.InterpDebugStills(alpha, out ra, out rm, out rb); return null; }, 3000, needFrame: false, out _);
        a = ra; mid = rm; b = rb;
        return ok;
    }

    /// <summary>Test mode: keep more frames in the ring (about half a second) for the live hold-out check.</summary>
    internal void DebugSetRingExtra(int frames) => _s?.Renderer?.SetRingExtra(frames);

    /// <summary>Test mode: the ring frames newer than <paramref name="afterSeq"/> with their luma (oldest first).</summary>
    internal bool DebugInterpScan(long afterSeq, List<D3DRenderer.ScanFrame> into)
    {
        var got = new List<D3DRenderer.ScanFrame>();
        bool ok = false;
        Session? s = _s;
        if (s != null && s.Alive)
            s.Run(r => { ok = r.InterpDebugScan(afterSeq, got); return null; }, 5000, needFrame: false, out _);
        into.AddRange(got);
        return ok;
    }

    /// <summary>Test mode: three chosen live frames plus the picture interpolated between the outer two.</summary>
    internal bool DebugInterpHoldout(long q0, long q1, long q2,
        out Bitmap? f0, out Bitmap? f1, out Bitmap? f2, out Bitmap? mid)
    {
        Bitmap? r0 = null, r1 = null, r2 = null, rm = null;
        bool ok = false;
        Session? s = _s;
        if (s != null && s.Alive)
            s.Run(r => { ok = r.InterpDebugHoldout(q0, q1, q2, out r0, out r1, out r2, out rm); return null; }, 5000, needFrame: false, out _);
        f0 = r0; f1 = r1; f2 = r2; mid = rm;
        return ok;
    }

    internal bool DebugInterpSynthetic(Bitmap a, Bitmap b, double alpha, int benchFrames,
        out Bitmap? mid, out float[] fieldXy, out Size fieldSize, out string timing)
    {
        Bitmap? rm = null; float[] rf = Array.Empty<float>(); Size rs = Size.Empty; string rt = string.Empty;
        bool ok = false;
        Session? s = _s;
        if (s != null && s.Alive)
            s.Run(r => { ok = r.InterpDebugSynthetic(a, b, alpha, benchFrames, out rm, out rf, out rs, out rt); return null; }, 60000, needFrame: false, out _);
        mid = rm; fieldXy = rf; fieldSize = rs; timing = rt;
        return ok;
    }

    public string? SourceColorInfo => _s?.ColorInfo;

    public void BeginMeasure() => _s?.Renderer?.BeginMeasure();
    public RenderMeasurement? EndMeasure() => _s?.Renderer?.EndMeasure();

    // =====================================================================
    //  One running session: render thread + capture thread
    // =====================================================================

    private sealed class SnapRequest
    {
        public Func<D3DRenderer, Bitmap?> Work = null!;
        public bool NeedFrame;
        public readonly ManualResetEventSlim Done = new(false);
        public Bitmap? Result;
        public string? Error;
        public volatile bool Abandoned;
        public bool AllowNull;     // the work reports through a closure, not a bitmap
    }

    private sealed class Session
    {
        private readonly D3DVideoEngine _e;
        private readonly IntPtr _hwnd;
        private readonly VideoDeviceInfo _info;
        private readonly CaptureMode? _wantedMode;

        private D3DRenderer? _r;
        private Thread? _renderThread, _captureThread;
        private readonly AutoResetEvent _wake = new(false);
        private readonly ConcurrentQueue<SnapRequest> _requests = new();

        // Result of Start
        public readonly ManualResetEventSlim Started = new(false);
        public Exception? StartError;
        public Size Resolution;
        public int Fps;
        public CaptureMode? Mode;
        public List<CaptureMode> Modes = new();

        // Handshake between the render thread (device owner) and the capture thread.
        private readonly ManualResetEventSlim _deviceReady = new(false);
        private readonly ManualResetEventSlim _captureOpened = new(false);
        private Exception? _captureOpenError;
        private IMFDXGIDeviceManager? _dxgiManager;
        private IMFMediaSource? _source;

        private volatile bool _stopping;
        private volatile int _alive = 1;
        public bool Alive => _alive == 1 && !_stopping;

        // Frame slot and counters.
        private int _frameReady;          // 1 = a frame the render thread has not consumed yet
        private long _inFrames, _dropped, _presented;
        private int _dirtyCompose, _dirtyConvert;
        private volatile RenderStats? _stats;
        public RenderStats? Stats => _stats;
        public long FramesReceived => Interlocked.Read(ref _inFrames);
        public long FramesPresented => Interlocked.Read(ref _presented);
        public long FramesDropped => Interlocked.Read(ref _dropped);
        public string? ColorInfo;     // set once by OpenReader, for --hdr-probe

        // ---- "元の細かさに合わせて拡大" (auto mode: judged on a background thread) ----
        private int _nrTarget;                 // auto: the height decided (0 = none / native)
        private volatile bool _nrDecided;
        private volatile int _nrSourceH;
        private Thread? _nrThread;

        public NativeResStatus NativeRes(string modeStr, int mode)
        {
            int target = mode > 0 ? mode : mode < 0 ? Volatile.Read(ref _nrTarget) : 0;
            int active = _r?.PrescaleActiveHeight ?? 0;
            return new NativeResStatus(modeStr, target, active, mode < 0 && !_nrDecided, _nrSourceH);
        }

        /// <summary>The height the render loop shrinks the picture to this frame (0 = no prescale).</summary>
        private int NativeTargetHeight()
        {
            int m = _e._nrMode;
            if (m == 0 || _e._rotation is 90 or 270) return 0;   // a rotated picture's rows are the source's columns: not judged
            return m > 0 ? m : Volatile.Read(ref _nrTarget);
        }

        private void StartNativeResThread()
        {
            _nrThread = new Thread(NativeResLoop) { IsBackground = true, Name = "YuCap.D3D.NativeRes", Priority = ThreadPriority.BelowNormal };
            _nrThread.Start();
        }

        /// <summary>Every ~3 s (every 1.5 s until the first decision) read one full-size oriented frame back through the render
        /// thread's request queue and estimate its real vertical detail. A decision (or a change) needs the same estimate three
        /// samples in a row; black, flat and capture-card message pictures are skipped.</summary>
        private void NativeResLoop()
        {
            const int Need = 3;
            bool wasAuto = false;
            int lastEst = -1, count = 0, decidedEst = -1;
            Size lastSize = Size.Empty;
            long nextAt = 0;
            while (!_stopping)
            {
                Thread.Sleep(100);
                long now = Environment.TickCount64;
                if (_e._nrMode != -1)
                {
                    if (wasAuto) { wasAuto = false; Volatile.Write(ref _nrTarget, 0); _nrDecided = false; Poke(); }
                    continue;
                }
                if (!wasAuto)
                {
                    wasAuto = true;
                    lastEst = -1; count = 0; decidedEst = -1; lastSize = Size.Empty;
                    Volatile.Write(ref _nrTarget, 0); _nrDecided = false;
                    nextAt = now + 800;
                }
                if (now < nextAt) continue;
                nextAt = now + (_nrDecided ? 3000 : 1500);
                D3DRenderer? r = _r;
                if (r == null || !r.HasFrame || _e._rotation is 90 or 270) continue;

                Bitmap? bmp = null;
                try
                {
                    bmp = Run(rr => rr.ReadOrientedForAnalysis(), 1500, needFrame: true, out _);
                    if (bmp == null) continue;
                    float[] y = ResEstimate.LumaOf(bmp, out int w, out int h);
                    bmp.Dispose(); bmp = null;
                    var size = new Size(w, h);
                    if (size != lastSize)
                    {
                        // a new picture size (mode, crop, device): everything judged so far is about something else
                        lastSize = size; lastEst = -1; count = 0; decidedEst = -1;
                        Volatile.Write(ref _nrTarget, 0); _nrDecided = false; Poke();
                    }
                    _nrSourceH = h;
                    if (ResEstimate.LooksLikeCardMessage(y, w, h) || ResEstimate.MeanGradient(y, w, h) < ResEstimate.MinDetail) continue;
                    ResEstimate.FrameResult fr = ResEstimate.Analyze(y, w, h);
                    if (_stopping || _e._nrMode != -1) continue;
                    int est = Math.Min(fr.Vertical.EstimateHeight, h);
                    if (est == lastEst) count++; else { lastEst = est; count = 1; }
                    if (!_nrDecided || est != decidedEst)
                        Log.Info($"D3D: native-res sample {w}x{h}: {est}p ({Math.Min(count, Need)}/{Need}); H {fr.Horizontal.EstimateHeight}p");
                    if (count >= Need && (!_nrDecided || est != decidedEst))
                    {
                        decidedEst = est;
                        Volatile.Write(ref _nrTarget, est < h ? est : 0);
                        _nrDecided = true;
                        Log.Info(est < h ? $"D3D: native-res auto: detail {est}p - shrinking {w}x{h} to {(int)Math.Round((double)w * est / h)}x{est} before scaling"
                                         : $"D3D: native-res auto: detail is the full {h}p - no shrinking");
                        Poke();
                    }
                }
                catch (Exception ex) { Log.Info("D3D: native-res analysis failed: " + ex.Message.Split('\n')[0]); }
                finally { bmp?.Dispose(); }
            }
        }

        // Video delay bookkeeping (render thread writes, anyone reads).
        private double _delayActual;
        public double DelayActualMs => Volatile.Read(ref _delayActual);

        private readonly bool _lowLatency;   // fixed for the session: decides the swap chain kind
        public string PresentMode => _r?.PresentMode ?? string.Empty;
        public D3DRenderer? Renderer => _r;

        // Display-driven pacing (vsync path): frames are held for a small margin so that arrival
        // jitter does not decide which refresh shows them. The capture thread measures the jitter.
        private double _marginMs;
        public double PacingMarginMs => Volatile.Read(ref _marginMs);
        private volatile bool _ringActive;     // the ring (not the single frame slot) receives frames
        private long _lastArrival;
        private double _nominalMs;
        private readonly double[] _dev = new double[90];
        private int _devCount, _devPos, _sinceCalc;
        private double _loggedMargin;

        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        // Smoothed arrival timeline (capture thread): a phase-locked loop on the frame interval, so that
        // the few ms of USB / scheduling jitter in the raw arrival times do not wobble the interpolation phase.
        private double _smoothTicks;
        private bool _smoothValid;

        private long TrackSmooth(long arrival)
        {
            double nom = Volatile.Read(ref _nominalMs) * Stopwatch.Frequency / 1000.0;
            double sm;
            if (!_smoothValid || nom <= 0) sm = arrival;
            else
            {
                double pred = _smoothTicks + nom;
                double err = arrival - pred;
                if (Math.Abs(err) > 6 * nom) sm = arrival;          // a long stall: start over
                else
                {
                    double k = Math.Round(err / nom);                // dropped (k>0) or duplicated (k<0) frames
                    pred += k * nom;
                    err -= k * nom;
                    sm = pred + 0.05 * err;
                }
                if (sm < _smoothTicks + 0.2 * nom) sm = _smoothTicks + 0.2 * nom;   // always moves forward
            }
            _smoothValid = true;
            _smoothTicks = sm;
            return (long)sm;
        }

        // ---- frame interpolation ----
        private volatile bool _interpActive;
        private volatile bool _displayDriven;
        private int _interpReeval;
        private int _refreshHz;
        private long _refreshTick;
        public void PokeInterpolation() { Volatile.Write(ref _interpReeval, 1); TryWake(); }

        /// <summary>The refresh rate Windows reports for the monitor the window is on (59.94 Hz shows as 59 or 60).</summary>
        private int RefreshHz()
        {
            long now = Environment.TickCount64;
            if (_refreshHz == 0 || now - _refreshTick > 500)
            {
                _refreshHz = Displays.RefreshHzOfWindow(_hwnd);
                _refreshTick = now;
            }
            return _refreshHz;
        }

        /// <summary>The rate frames can really be shown at: the measured vertical-blank period of the output the window
        /// is on while the display-driven beat runs, else what Windows reports.</summary>
        private double ActualRefreshHz()
        {
            D3DRenderer? rr = _r;
            if (rr != null && _displayDriven && rr.Beat.Active && rr.Beat.PeriodMs > 1)
            {
                double measured = 1000.0 / rr.Beat.PeriodMs;
                if (measured >= 20) return measured;
            }
            return RefreshHz();
        }

        /// <summary>Whether interpolation should run now. It starts when the refresh is at least 1.1x the
        /// measured source frame rate and stops again below 1.05x (so a borderline pair does not flap).</summary>
        private InterpolationStatus EvaluateInterpolation(bool wasActive)
        {
            double nominal = Volatile.Read(ref _nominalMs);
            double src = nominal > 0 ? 1000.0 / nominal : 0;
            double hz = ActualRefreshHz();
            if (!_e._interpolation) return new InterpolationStatus(false, src, hz, InterpolationIdle.Disabled);
            if (_r is { InterpAvailable: false }) return new InterpolationStatus(false, src, hz, InterpolationIdle.Unavailable);
            if (_lowLatency || !_displayDriven) return new InterpolationStatus(false, src, hz, InterpolationIdle.LowLatency);
            if (src <= 0 || hz <= 0) return new InterpolationStatus(false, src, hz, InterpolationIdle.NoSignal);
            if (hz < src * (wasActive ? 1.05 : 1.10)) return new InterpolationStatus(false, src, hz, InterpolationIdle.RefreshTooLow);
            return new InterpolationStatus(true, src, hz, InterpolationIdle.None);
        }

        public InterpolationStatus InterpStatus() => EvaluateInterpolation(_interpActive);

        /// <summary>Capture thread: fold one arrival into the jitter estimate (p90 of how far the
        /// arrival interval strays from nominal) and update the margin.</summary>
        private void TrackArrival(long arrival)
        {
            double nominal = Fps > 0 ? 1000.0 / Fps : 16.7;
            double maxMargin = nominal * 0.5;
            if (_nominalMs == 0)
            {
                _nominalMs = nominal;
                double m0 = Math.Clamp(nominal * 0.25, 2.0, Math.Max(2.0, maxMargin));
                Volatile.Write(ref _marginMs, m0);
                _loggedMargin = m0;
            }
            long last = _lastArrival;
            _lastArrival = arrival;
            if (last == 0) return;
            double iv = (arrival - last) * 1000.0 / Stopwatch.Frequency;
            if (iv > nominal * 6) return;   // a stall, not jitter
            if (iv > nominal * 0.5 && iv < nominal * 1.5) _nominalMs = _nominalMs * 0.98 + iv * 0.02;
            _dev[_devPos] = Math.Abs(iv - _nominalMs);
            _devPos = (_devPos + 1) % _dev.Length;
            if (_devCount < _dev.Length) _devCount++;
            if (++_sinceCalc < 30 || _devCount < 60) return;
            _sinceCalc = 0;
            double[] tmp = new double[_devCount];
            Array.Copy(_dev, tmp, _devCount);
            Array.Sort(tmp);
            double p90 = tmp[(int)(tmp.Length * 0.9)];
            double target = Math.Clamp(p90, 2.0, Math.Max(2.0, maxMargin));
            double cur = Volatile.Read(ref _marginMs);
            double next = cur * 0.7 + target * 0.3;
            Volatile.Write(ref _marginMs, next);
            if (Math.Abs(next - _loggedMargin) >= Math.Max(2.0, _loggedMargin * 0.4))
            {
                _loggedMargin = next;
                Log.Info($"D3D: pacing margin now {next:0.0}ms (arrival jitter p90 {p90:0.0}ms)");
            }
        }

        // Start-up trace (temporary diagnostics): state transitions are logged for the first 2 s only.
        private readonly long _t0 = Stopwatch.GetTimestamp();
        private long TraceMs => (Stopwatch.GetTimestamp() - _t0) * 1000 / Stopwatch.Frequency;
        private bool Early => TraceMs < 2000;

        public Session(D3DVideoEngine e, IntPtr hwnd, VideoDeviceInfo info, CaptureMode? mode)
        {
            _e = e; _hwnd = hwnd; _info = info; _wantedMode = mode;
            _lowLatency = e._lowLatency;
        }

        public void Begin()
        {
            _renderThread = new Thread(RenderMain) { IsBackground = true, Name = "YuCap.D3D.Render" };
            _renderThread.SetApartmentState(ApartmentState.MTA);
            _renderThread.Start();
            _crispThread = new Thread(CrispLoop) { IsBackground = true, Name = "YuCap.D3D.Crisp", Priority = ThreadPriority.BelowNormal };
            _crispThread.Start();
        }

        // ---- くっきり度: pipeline vs bilinear edge strength on one crop, every few seconds ----
        private Thread? _crispThread;
        private volatile CrispnessReading? _crisp;
        public CrispnessReading? Crisp => _crisp;

        private void CrispLoop()
        {
            const int CropMax = 512;
            long lastSample = 0, lastRecv = -1;
            int samples = 0, steadyCount = 0;
            double maxMs = 0;
            int searchIdx = 0;                                // 0..4: candidate being tried, 5 = search done
            double[] candGrad = new double[5];
            int bestCand = 0;
            Size lastDstSize = Size.Empty;
            double lastMs = 0;
            while (!_stopping)
            {
                Thread.Sleep(100);
                D3DRenderer? r = _r;
                if (!_e._crispOn || r == null || !r.HasFrame) { if (_crisp != null && !_e._crispOn) _crisp = null; continue; }
                Rectangle dst = _e.CurrentDst();
                Size client = r.SwapSize;
                Size src = r.OrientedSize;
                Rectangle vis = Rectangle.Intersect(dst, new Rectangle(Point.Empty, client));
                // Only meaningful when the picture is drawn larger than the source in both directions.
                if (src.Width <= 0 || dst.Width <= src.Width || dst.Height <= src.Height || vis.Width < 64 || vis.Height < 64)
                {
                    _crisp = null; searchIdx = 0; continue;
                }
                if (dst.Size != lastDstSize) { lastDstSize = dst.Size; searchIdx = 0; steadyCount = 0; _crisp = null; }
                long now = Environment.TickCount64;
                int interval = searchIdx < 5 ? Math.Min(1000, _e._crispMs) : _e._crispMs;
                if (now - lastSample < interval) continue;
                long recv = FramesReceived;
                if (recv == lastRecv) continue;               // paused / no signal: no new picture
                lastRecv = recv;
                lastSample = now;

                int cw = Math.Min(CropMax, vis.Width) & ~1, ch = Math.Min(CropMax, vis.Height) & ~1;
                int cand = searchIdx < 5 ? searchIdx : bestCand;
                (double fx, double fy)[] spots = { (0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) };
                int cx = vis.X + (int)(vis.Width * spots[cand].fx) - cw / 2, cy = vis.Y + (int)(vis.Height * spots[cand].fy) - ch / 2;
                cx = Math.Clamp(cx, vis.X, vis.Right - cw); cy = Math.Clamp(cy, vis.Y, vis.Bottom - ch);
                var crop = new Rectangle(cx, cy, cw, ch);
                ScalingQuality q = _e._quality; bool pp = _e._pixelPerfect; float sh = _e._sharpness; int nh = NativeTargetHeight();

                double ms1 = 0, ms2 = 0; bool ok = false;
                try
                {
                    Run(rr => { var sw = Stopwatch.StartNew(); ok = rr.CrispSubmit(dst, crop, q, pp, sh, nh); ms1 = sw.Elapsed.TotalMilliseconds; return null; },
                        1000, needFrame: false, out _, allowNull: true);
                    if (!ok) continue;
                    Thread.Sleep(40);                         // the GPU finishes these two small renders long before this
                    byte[] a = Array.Empty<byte>(), b = Array.Empty<byte>(); Size sz = Size.Empty; bool got = false;
                    Run(rr => { var sw = Stopwatch.StartNew(); got = rr.CrispRead(out a, out b, out sz); ms2 = sw.Elapsed.TotalMilliseconds; return null; },
                        1000, needFrame: false, out _, allowNull: true);
                    if (!got) continue;
                    double gp = TestProbes.MeanGradient(a, sz.Width, sz.Height), gb = TestProbes.MeanGradient(b, sz.Width, sz.Height);
                    lastMs = ms1 + ms2; maxMs = Math.Max(maxMs, lastMs);
                    samples++;
                    if (searchIdx < 5)
                    {
                        candGrad[searchIdx] = gb;
                        searchIdx++;
                        if (searchIdx == 5)
                        {
                            for (int i = 1; i < 5; i++) if (candGrad[i] > candGrad[bestCand]) bestCand = i;
                            steadyCount = 0;
                        }
                    }
                    else if (++steadyCount % 12 == 0) searchIdx = 0;   // look for the most detailed spot again now and then
                    if (gb >= 0.5)                            // a flat picture says nothing about sharpness
                    {
                        double ratio = gp / gb - 1;
                        _crisp = new CrispnessReading(ratio, Environment.TickCount64, samples, lastMs, maxMs);
                    }
                    if (samples <= 3 || samples % 20 == 0)
                        Log.Info($"D3D: crispness sample #{samples} pipeline/bilinear = {(gb > 0 ? gp / gb : 0):0.000} (spot {cand}, {cw}x{ch}); render thread {ms1:0.00}+{ms2:0.00}ms (max {maxMs:0.00}ms)");
                }
                catch (Exception ex) { Log.Info("D3D: crispness sample failed - " + ex.Message.Split('\n')[0]); Thread.Sleep(1000); }
            }
        }

        public void Poke() { Volatile.Write(ref _dirtyCompose, 1); TryWake(); }
        public void PokeConvert() { Volatile.Write(ref _dirtyConvert, 1); Poke(); }
        private void TryWake() { try { _wake.Set(); } catch (ObjectDisposedException) { } }

        public void RequestStop(string why)
        {
            if (!_stopping) Log.Info("D3D: stopping — " + why);
            _stopping = true;
            TryWake();
        }

        public void JoinRender(int ms) { _renderThread?.Join(ms); }

        /// <summary>Stop both threads, never blocking the caller longer than <paramref name="budgetMs"/>.</summary>
        public void Shutdown(int budgetMs)
        {
            var sw = Stopwatch.StartNew();
            RequestStop("Stop()");

            // The render thread owns the teardown order (it stops the capture thread first).
            Thread? cap = _captureThread, rnd = _renderThread;
            rnd?.Join(budgetMs);
            cap?.Join(Math.Max(50, budgetMs - (int)sw.ElapsedMilliseconds));
            if ((cap?.IsAlive ?? false) || (rnd?.IsAlive ?? false))
                Log.Info($"D3D: threads still running after {sw.ElapsedMilliseconds}ms — abandoning them (capture alive={cap?.IsAlive}, render alive={rnd?.IsAlive})");
            else
                Log.Info($"D3D: stopped in {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>Run <paramref name="work"/> on the render thread and wait for it.</summary>
        public Bitmap? Run(Func<D3DRenderer, Bitmap?> work, int timeoutMs, bool needFrame, out string? error, bool allowNull = false)
        {
            error = null;
            var req = new SnapRequest { Work = work, NeedFrame = needFrame, AllowNull = allowNull };
            _requests.Enqueue(req);
            TryWake();
            if (!req.Done.Wait(timeoutMs))
            {
                req.Abandoned = true;
                // The render thread may have finished just now.
                Bitmap? late = Interlocked.Exchange(ref req.Result, null);
                late?.Dispose();
                error = L.T("応答がありませんでした（タイムアウト）。");
                return null;
            }
            error = req.Error;
            return req.Result;
        }

        // ------------------------------------------------------------------
        //  Render thread
        // ------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private Size ClientSize()
        {
            if (!GetClientRect(_hwnd, out RECT rc)) return Size.Empty;
            return new Size(Math.Max(0, rc.Right - rc.Left), Math.Max(0, rc.Bottom - rc.Top));
        }

        private void RenderMain()
        {
            D3DRenderer? r = null;
            bool captureMayUseDevice = false;
            try
            {
                var sw = Stopwatch.StartNew();
                r = new D3DRenderer();
                _r = r;
                r.CreateDevice();
                r.CreateShaders();

                // The source reader decodes / converts on our device through this manager.
                IMFDXGIDeviceManager mgr = MediaFactory.MFCreateDXGIDeviceManager();
                _dxgiManager = mgr;
                mgr.ResetDevice(r.Device);

                Size client = ClientSize();
                if (client.Width <= 0 || client.Height <= 0) client = new Size(16, 16);
                r.CreatePresentation(_hwnd, client, _lowLatency);
                r.Resize(client);   // creates the client-sized textures
                _deviceReady.Set();

                _captureThread = new Thread(CaptureMain) { IsBackground = true, Name = "YuCap.D3D.Capture" };
                _captureThread.SetApartmentState(ApartmentState.MTA);
                captureMayUseDevice = true;
                _captureThread.Start();
                if (!_captureOpened.Wait(7000))
                    throw new UserFacingException(L.T("映像の開始がタイムアウトしました。"));
                if (_captureOpenError != null) ExceptionDispatchInfo.Capture(_captureOpenError).Throw();

                Log.Info($"D3D: initialised in {sw.ElapsedMilliseconds}ms");
                Started.Set();
                StartNativeResThread();
                RenderLoop(r);
            }
            catch (Exception ex)
            {
                if (!Started.IsSet)
                {
                    StartError = ex;
                    Log.Info("D3D: start failed — " + ex.Message.Split('\n')[0]);
                    _stopping = true;
                    Started.Set();
                }
                else
                {
                    Log.Info($"D3D: render thread failed — {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                    _stopping = true;
                }
            }
            finally
            {
                _stopping = true;
                // Fail any waiting stills requests.
                while (_requests.TryDequeue(out SnapRequest? q)) q.Done.Set();

                bool captureDone = true;
                Thread? cap = _captureThread;
                if (captureMayUseDevice && cap != null && cap.IsAlive)
                {
                    // Frames normally keep flowing, so the loop sees _stopping within a frame.
                    // Shutting the media source down is the fallback for a device that has
                    // stopped delivering (it can unblock a pending ReadSample).
                    captureDone = cap.Join(800);
                    if (!captureDone)
                    {
                        Log.Info("D3D: capture thread still in ReadSample after 800ms — shutting the media source down");
                        IMFMediaSource? src = _source;
                        if (src != null)
                        {
                            var t = new Thread(() => { try { src.Shutdown(); } catch { } }) { IsBackground = true, Name = "YuCap.D3D.SrcShutdown" };
                            t.Start();
                            t.Join(1000);
                        }
                        captureDone = cap.Join(1200);
                    }
                    if (!captureDone) Log.Info("D3D: capture thread did not exit — leaking the device instead of releasing it under its feet");
                }
                try { r?.DisposeAll(releaseDevice: captureDone); } catch (Exception ex) { Log.Info("D3D: cleanup: " + ex.Message); }
                if (captureDone)
                {
                    try { _dxgiManager?.Dispose(); } catch { }
                }
                _alive = 0;
                if (!Started.IsSet) Started.Set();
            }
        }

        private void RenderLoop(D3DRenderer r)
        {
            Size client = r.SwapSize;
            bool drawnOnce = false;
            long statTick = Environment.TickCount64;
            long lastIn = 0, lastPresented = 0;
            bool firstPresentLogged = false;

            int trackedDelay = 0, delaySamples = 0;
            bool delayLogged = false;

            // Vsync path: the display, not the capture card, clocks presentation (see below).
            // Low-latency mode (sync 0 / tearing) keeps presenting on arrival.
            bool displayDriven = r.VsyncPaced;
            _displayDriven = displayDriven;
            bool presentedLast = false;
            long interpEvalTick = 0;
            InterpolationIdle interpIdleLogged = (InterpolationIdle)(-1);
            double interpHoldMs = 0;
            bool marginLogged = false;
            int iter = 0;
            bool dueLogged = false;
            // Presentation must never depend on pacing state alone: the first frame of a session, any
            // change of the destination rectangle or window size, and a long silence all force a present.
            Rectangle lastDstSeen = Rectangle.Empty;
            long lastPresentTick = Stopwatch.GetTimestamp();

            while (!_stopping)
            {
                bool newFrame, reconvert, dirty;
                iter++;
                int userDelayMs = _e._videoDelayMs;
                double marginMs = displayDriven ? PacingMarginMs : 0;
                if (displayDriven && !marginLogged && marginMs > 0)
                {
                    marginLogged = true;   // the capture thread sets the first margin with the first frame
                    Log.Info($"D3D: pacing = display-driven, margin={marginMs:0.0}ms");
                }
                int delayMs = userDelayMs + (int)Math.Round(marginMs);   // what the ring holds frames back by
                bool waitedLatency = false;

                if (displayDriven)
                {
                    long tk = Environment.TickCount64;
                    bool reeval = Interlocked.Exchange(ref _interpReeval, 0) == 1;
                    if (reeval || tk - interpEvalTick >= (interpIdleLogged == InterpolationIdle.NoSignal ? 100 : 1000))
                    {
                        interpEvalTick = tk;
                        InterpolationStatus st = EvaluateInterpolation(_interpActive);
                        if (st.Active != _interpActive || st.Idle != interpIdleLogged)
                        {
                            interpIdleLogged = st.Idle;
                            if (st.Active)
                                Log.Info($"D3D: frame interpolation ON {st.SourceFps:0.0} fps -> {st.RefreshHz:0} Hz");
                            else if (_interpActive || st.Idle != InterpolationIdle.Disabled)
                                Log.Info($"D3D: frame interpolation {(_interpActive ? "OFF" : "idle")} ({st.Idle}; source {st.SourceFps:0.0} fps, refresh {st.RefreshHz:0} Hz)");
                            _interpActive = st.Active;
                        }
                    }
                }
                long arrivalShown = 0;

                if (displayDriven)
                {
                    // One iteration per refresh. The beat is the vertical blank of the output the window is on
                    // (IDXGIOutput.WaitForVBlank), which is the same clock the panel runs on whichever monitor
                    // that is. Right after it the frame presented one refresh ago has been taken by DWM, so a
                    // short, bounded look at the swap chain's latency waitable only guards against over-queuing
                    // when DWM is late; it never waits a whole refresh (that would halve the rate). Without a
                    // usable beat: after a Present the waitable signals when the display has taken that frame,
                    // and when we did not present DwmFlush gives the same once-per-refresh beat.
                    long t0 = Stopwatch.GetTimestamp();
                    double roughMs = 1000.0 / Math.Max(1, RefreshHz() > 0 ? RefreshHz() : 60);
                    bool viaBeat = r.Beat.Wait(r.Device, _hwnd, roughMs);
                    if (viaBeat)
                    {
                        if (presentedLast && r.LatencyWaitable != IntPtr.Zero)
                            WaitForSingleObject(r.LatencyWaitable, (uint)Math.Clamp(roughMs * 0.35, 1, 6));
                    }
                    else
                    {
                        if (presentedLast && r.LatencyWaitable != IntPtr.Zero) WaitForSingleObject(r.LatencyWaitable, 100);
                        else if (DwmFlush() != 0) _wake.WaitOne(4);
                        if ((Stopwatch.GetTimestamp() - t0) * 1000 < Stopwatch.Frequency) _wake.WaitOne(3);   // returned at once: do not spin
                    }
                    waitedLatency = true;
                    if (iter <= 4 && Early)
                        Log.Info($"D3D: t+{TraceMs}ms iter={iter} waited {(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:0.0}ms via {(viaBeat ? "vblank" : presentedLast ? "latency-waitable" : "DwmFlush")}");
                    presentedLast = false;
                    if (_stopping) break;
                    ServiceRequests(r);
                    if (!IsWindow(_hwnd)) { RequestStop("window gone"); break; }
                    Interlocked.Exchange(ref _frameReady, 0);   // arrivals feed the ring here, not presentation
                    newFrame = false;
                }
                else
                {
                    // With a video delay the next frame is due at a known time: sleep until then
                    // instead of until the next capture wake-up, or every frame would be shown up
                    // to one frame late.
                    int waitMs = 250;
                    if (userDelayMs > 0)
                    {
                        long due = r.NextDueTicks(userDelayMs * Stopwatch.Frequency / 1000);
                        if (due != long.MaxValue)
                            waitMs = (int)Math.Clamp((due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency, 1, 250);
                    }
                    _wake.WaitOne(waitMs);
                    if (_stopping) break;

                    // Stills first: they must see the state the user is looking at.
                    ServiceRequests(r);

                    if (!IsWindow(_hwnd)) { RequestStop("window gone"); break; }
                    newFrame = Interlocked.Exchange(ref _frameReady, 0) == 1;
                    delayMs = userDelayMs;
                }
                reconvert = Interlocked.Exchange(ref _dirtyConvert, 0) == 1;
                dirty = Interlocked.Exchange(ref _dirtyCompose, 0) == 1;
                r.PollLatency();   // how long the frames presented so far took to reach the screen
                long contentTicks = 0;   // the instant on the source timeline of the picture shown this iteration

                // The capture thread files frames into the renderer's ring; show the oldest
                // due one that arrived at least `delayMs` ago (user delay + pacing margin).
                // With interpolation the hold-back also includes one source frame interval: the picture
                // shown is that of time now - hold, made from the two frames around it.
                bool interp = displayDriven && _interpActive;
                double nominalNow = Volatile.Read(ref _nominalMs);
                interpHoldMs = interp ? delayMs + nominalNow : 0;
                int ringMs = interp ? (int)Math.Ceiling(interpHoldMs) : delayMs;
                if (r.ConfigureRing(ringMs, Fps))
                {
                    Log.Info(interp ? $"D3D: interpolation hold {interpHoldMs:0.0}ms (ring {r.RingSlots} frames)"
                        : delayMs > 0
                        ? $"D3D: video delay configured {delayMs}ms (ring {r.RingSlots} frames)"
                        : "D3D: video delay off");
                }
                r.SetInterpolation(interp, (long)(interpHoldMs * Stopwatch.Frequency / 1000.0));
                bool ringed = delayMs > 0;
                _ringActive = ringed || interp;
                if (interp)
                {
                    newFrame = false;
                    long holdTicks = (long)(interpHoldMs * Stopwatch.Frequency / 1000.0);
                    long interpT = Stopwatch.GetTimestamp() - holdTicks;
                    if (r.InterpUpdate(interpT, _e._rotation, _e._mirror,
                            _e.CurrentAdjustments(), _e.Crop, reconvert, out bool changed))
                    {
                        newFrame = changed;
                        if (changed) contentTicks = interpT;
                    }
                    long rd2 = r.TakeRingDropped();
                    if (rd2 > 0) Interlocked.Add(ref _dropped, rd2);
                }
                else if (ringed)
                {
                    newFrame = false;   // capture arrivals do not drive presentation here
                    long delayTicks = drawnOnce ? delayMs * Stopwatch.Frequency / 1000 : 0;   // the first frame needs no pacing margin
                    bool due = r.HasDueFrame(Stopwatch.GetTimestamp(), delayTicks);
                    if (!dueLogged && Early && (due || iter <= 3))
                    {
                        if (due) dueLogged = true;
                        Log.Info($"D3D: t+{TraceMs}ms iter={iter} ringed delay={delayMs}ms due={due} hasFrame={r.HasFrame} rx={FramesReceived} presentedLast={presentedLast}");
                    }
                    if (due)
                    {
                        if (!waitedLatency && r.LatencyWaitable != IntPtr.Zero) WaitForSingleObject(r.LatencyWaitable, 100);
                        waitedLatency = true;
                        newFrame = r.AdvanceDelayed(Stopwatch.GetTimestamp(), delayTicks, out arrivalShown);
                        if (newFrame) contentTicks = arrivalShown;
                    }
                    long rd = r.TakeRingDropped();
                    if (rd > 0) Interlocked.Add(ref _dropped, rd);
                }
                bool delayed = userDelayMs > 0 || interp;
                int delayKey = interp ? (int)Math.Ceiling(interpHoldMs) : delayMs;
                if (!delayed)
                {
                    trackedDelay = 0;
                    Volatile.Write(ref _delayActual, 0);
                }

                Size now = ClientSize();
                bool resized = now.Width > 0 && now.Height > 0 && now != client;
                if (resized)
                {
                    r.Resize(now);
                    r.Beat.Recheck();
                    Log.Info($"D3D: resize {client.Width}x{client.Height} -> {now.Width}x{now.Height}");
                    client = now;
                    dirty = true;
                }

                Rectangle dstSeen = _e.CurrentDst();
                if (dstSeen != lastDstSeen) dirty = true;                     // rect change: independent of Poke
                if (!drawnOnce) dirty = true;                                 // session start: present as soon as there is a frame
                else if ((Stopwatch.GetTimestamp() - lastPresentTick) * 4 > Stopwatch.Frequency) dirty = true;   // nothing shown for 250 ms
                if (!r.HasFrame || client.Width <= 0 || client.Height <= 0)
                {
                    // Nothing to draw yet: keep the requests instead of dropping them.
                    if (reconvert) Volatile.Write(ref _dirtyConvert, 1);
                }
                else if (newFrame || reconvert || dirty)
                {
                    lastDstSeen = dstSeen;
                    lastPresentTick = Stopwatch.GetTimestamp();
                    if (newFrame)
                    {
                        // Wait until the swap chain has room, so latency stays at one frame.
                        if (!waitedLatency && r.LatencyWaitable != IntPtr.Zero) WaitForSingleObject(r.LatencyWaitable, 100);
                    }
                    if (!interp && (newFrame || reconvert || !drawnOnce))
                    {
                        r.GpuBegin();   // the GPU-time span starts with the convert pass and ends in PresentFrame
                        r.Convert(_e._rotation, _e._mirror, _e.CurrentAdjustments(), _e.Crop);
                    }

                    // Before the first SetVideoRect the destination is empty. The Media
                    // Foundation preview fills the whole window in that state, so do the
                    // same here (stretch to the client area) or nothing would be drawn
                    // until the first layout pass.
                    Rectangle dst = _e.CurrentDst();
                    if (dst.Width <= 0 || dst.Height <= 0)
                        dst = new Rectangle(0, 0, client.Width, client.Height);
                    Result hr = r.PresentFrame(dst, _e._quality, _e._pixelPerfect, _e._sharpness, NativeTargetHeight(), _e._overlays);
                    if (hr.Failure)
                    {
                        HandlePresentFailure(r, hr);
                        break;
                    }
                    drawnOnce = true;
                    presentedLast = true;
                    Interlocked.Increment(ref _presented);
                    if (newFrame)
                    {
                        // Direct path (no ring): the frame shown is the newest one that arrived.
                        long content = contentTicks != 0 ? contentTicks : (interp || ringed ? 0 : r.LatestArrival);
                        if (content != 0)
                        {
                            r.Latency.SetRefreshMs(r.Beat.Active && r.Beat.PeriodMs > 1 ? r.Beat.PeriodMs : 1000.0 / Math.Max(1, RefreshHz()));
                            r.Latency.Shown(r.LastPresentCount, content, Stopwatch.GetTimestamp(), r.Beat.Active ? r.Beat.Count : -1);
                        }
                    }
                    if (delayed && newFrame && (arrivalShown != 0 || interp))
                    {
                        // Age of the frame we just handed to the swap chain (it reaches the
                        // screen at the next vsync, so the real figure is up to one refresh more).
                        // An interpolated picture shows the instant now - hold, so its age is the hold.
                        double ageMs = interp ? interpHoldMs : (Stopwatch.GetTimestamp() - arrivalShown) * 1000.0 / Stopwatch.Frequency;
                        if (trackedDelay != delayKey)
                        {
                            trackedDelay = delayKey; delaySamples = 0; delayLogged = false;
                        }
                        double prev = Volatile.Read(ref _delayActual);
                        Volatile.Write(ref _delayActual, delaySamples == 0 ? ageMs : prev * 0.9 + ageMs * 0.1);
                        delaySamples++;
                        if (!delayLogged && delaySamples >= 30)
                        {
                            delayLogged = true;
                            Log.Info($"D3D: video delay target={delayKey}ms actual\u2248{Volatile.Read(ref _delayActual):0}ms");
                        }
                    }
                    if (!firstPresentLogged)
                    {
                        firstPresentLogged = true;
                        Log.Info($"D3D: first frame presented (t+{TraceMs}ms)");
                    }
                }

                long nowTick = Environment.TickCount64;
                if (nowTick - statTick >= 1000)
                {
                    double secs = (nowTick - statTick) / 1000.0;
                    long inF = Interlocked.Read(ref _inFrames), pr = Interlocked.Read(ref _presented);
                    r.GpuRecent(out double gpuAvg, out double gpuMax);
                    _stats = new RenderStats((inF - lastIn) / secs, (pr - lastPresented) / secs, Interlocked.Read(ref _dropped),
                        gpuAvg, gpuMax);
                    lastIn = inF; lastPresented = pr; statTick = nowTick;
                }
            }
        }

        private void HandlePresentFailure(D3DRenderer r, Result hr)
        {
            if (hr == Vortice.DXGI.ResultCode.DeviceRemoved || hr == Vortice.DXGI.ResultCode.DeviceReset)
            {
                string reason = "?";
                try { reason = r.Device.DeviceRemovedReason.ToString(); } catch { }
                Log.Info($"D3D: device removed/reset (Present hr=0x{hr.Code:X8}, reason {reason}) — stopping so the UI can restart");
            }
            else
            {
                Log.Info($"D3D: Present failed hr=0x{hr.Code:X8} — stopping");
            }
            RequestStop("present failure");
        }

        private void ServiceRequests(D3DRenderer r)
        {
            while (_requests.TryDequeue(out SnapRequest? q))
            {
                if (q.Abandoned) { q.Done.Set(); continue; }
                try
                {
                    if (q.NeedFrame)
                    {
                        if (!r.HasFrame) { q.Error = L.T("映像がありません"); q.Done.Set(); continue; }
                        r.Convert(_e._rotation, _e._mirror, _e.CurrentAdjustments(), _e.Crop);   // make sure the oriented texture is current
                    }
                    q.Result = q.Work(r);
                    if (q.Result == null && q.Error == null && !q.AllowNull) q.Error = L.T("映像がありません");
                }
                catch (Exception ex)
                {
                    Log.Info("D3D: still request failed — " + ex.Message.Split('\n')[0]);
                    q.Error = Errors.Describe(ex);
                    if (ex.HResult == unchecked((int)0x887A0005) || ex.HResult == unchecked((int)0x887A0007))
                        RequestStop("device removed during still request");
                }
                if (q.Abandoned) { Interlocked.Exchange(ref q.Result, null)?.Dispose(); }
                q.Done.Set();
            }
        }

        // ------------------------------------------------------------------
        //  Capture thread
        // ------------------------------------------------------------------

        private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly int VideoStream = unchecked((int)0xFFFFFFFC);   // MF_SOURCE_READER_FIRST_VIDEO_STREAM
        private static readonly int CurrentTypeIndex = unchecked((int)0xFFFFFFFF);

        private void CaptureMain()
        {
            IMFSourceReader? reader = null;
            bool opened = false;
            try
            {
                EnsureStartup();
                if (!_deviceReady.Wait(5000) || _r == null || _dxgiManager == null)
                    throw new UserFacingException(L.T("映像の開始がタイムアウトしました。"));
                D3DRenderer r = _r;

                reader = OpenReader(out YuvInfo yuv0, out int w0, out int h0);
                opened = true;
                _captureOpened.Set();

                YuvInfo yuv = yuv0;
                int width = w0, height = h0;
                bool firstFrame = true;
                var texIid = IidTexture2D;

                while (!_stopping)
                {
                    IMFSample? sample = reader.ReadSample(VideoStream, SourceReaderControlFlag.None,
                        out _, out SourceReaderFlag flags, out _);
                    long arrival = Stopwatch.GetTimestamp();   // when the frame reached us: the video delay counts from here
                    using (sample)
                    {
                        if (_stopping) break;
                        if ((flags & SourceReaderFlag.EndOfStream) != 0)
                        {
                            Log.Info("D3D: end of stream (device removed?)");
                            RequestStop("end of stream");
                            break;
                        }
                        if ((flags & SourceReaderFlag.Error) != 0)
                        {
                            Log.Info("D3D: source reader reported an error");
                            RequestStop("source reader error");
                            break;
                        }
                        if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                        {
                            using IMFMediaType t = reader.GetCurrentMediaType(VideoStream);
                            (width, height, yuv) = ReadOutputType(t, width, height);
                            Log.Info($"D3D: output type changed -> {width}x{height} matrix={yuv.Matrix} fullRange={yuv.FullRange}");
                        }
                        if (sample == null) continue;

                        Interlocked.Increment(ref _inFrames);
                        TrackArrival(arrival);
                        SubmitSample(r, sample, width, height, yuv, arrival, TrackSmooth(arrival), ref texIid);
                        // With the ring active the render thread counts what the ring loses.
                        if (Interlocked.Exchange(ref _frameReady, 1) == 1 && !_ringActive) Interlocked.Increment(ref _dropped);
                        TryWake();
                        if (firstFrame) { firstFrame = false; Log.Info($"D3D: first frame received (t+{TraceMs}ms)"); }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!opened)
                {
                    _captureOpenError = ex;
                    _captureOpened.Set();
                }
                else if (!_stopping)
                {
                    Log.Info($"D3D: capture failed — {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Split('\n')[0]}");
                    RequestStop("capture failure");
                }
            }
            finally
            {
                var cs = Stopwatch.StartNew();
                try { reader?.Dispose(); } catch { }
                try { _source?.Shutdown(); } catch { }
                try { _source?.Dispose(); } catch { }
                Log.Info($"D3D: capture thread done ({cs.ElapsedMilliseconds}ms)");
                if (!_captureOpened.IsSet) _captureOpened.Set();
            }
        }

        private void SubmitSample(D3DRenderer r, IMFSample sample, int w, int h, YuvInfo yuv, long arrival, long smooth, ref Guid texIid)
        {
            if (sample.BufferCount < 1) return;
            using IMFMediaBuffer buf = sample.GetBufferByIndex(0);
            using IMFDXGIBuffer? dxgi = buf.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgi != null)
            {
                IntPtr p = dxgi.GetResource(texIid);
                using var tex = new ID3D11Texture2D(p);
                if (tex.Description.Format == Format.NV12)
                {
                    r.SubmitGpuFrame(tex, (uint)dxgi.SubresourceIndex, w, h, yuv, arrival, smooth);
                    return;
                }
            }

            // System-memory buffer: prefer the 2-D lock for the real pitch.
            using IMF2DBuffer? b2 = buf.QueryInterfaceOrNull<IMF2DBuffer>();
            if (b2 != null)
            {
                b2.Lock2D(out IntPtr scan0, out int pitch);
                try { r.SubmitCpuFrame(scan0, pitch, w, h, yuv, arrival, smooth); }
                finally { b2.Unlock2D(); }
                return;
            }
            buf.Lock(out IntPtr data, out _, out _);
            try { r.SubmitCpuFrame(data, w, w, h, yuv, arrival, smooth); }
            finally { buf.Unlock(); }
        }

        /// <summary>Open the device, build the source reader and select the format.</summary>
        private IMFSourceReader OpenReader(out YuvInfo yuv, out int width, out int height)
        {
            using IMFAttributes devAttrs = MediaFactory.MFCreateAttributes(2);
            devAttrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            devAttrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, _info.Id);

            IMFMediaSource source;
            try { source = MediaFactory.MFCreateDeviceSource(devAttrs); }
            catch (Exception ex)
            {
                Log.Info($"D3D: opening by symbolic link failed (0x{ex.HResult:X8}); looking the device up by name");
                string? link = null;
                foreach (VideoDeviceInfo d in VideoEngine.EnumerateDevices())
                    if (d.Name == _info.Name) { link = d.Id; break; }
                if (link == null) throw new UserFacingException(L.T("映像デバイスが見つかりません。"));
                devAttrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, link);
                source = MediaFactory.MFCreateDeviceSource(devAttrs);
            }
            _source = source;

            using IMFAttributes ra = MediaFactory.MFCreateAttributes(4);
            ra.Set(SourceReaderAttributeKeys.D3DManager, _dxgiManager!);
            ra.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);    // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS
            ra.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, ra);

            reader.SetStreamSelection(unchecked((int)0xFFFFFFFE), false);   // all streams off
            reader.SetStreamSelection(VideoStream, true);

            List<CaptureFormats.RawType> types = CaptureFormats.Enumerate(reader, VideoStream);
            if (types.Count == 0) throw new UserFacingException(L.T("映像デバイスの形式を取得できません。"));
            Modes = CaptureFormats.ToModes(types);

            CaptureFormats.RawType? cur = null;
            try
            {
                using IMFMediaType ct = reader.GetNativeMediaType(VideoStream, CurrentTypeIndex);
                if (CaptureFormats.TryDescribe(ct, out string cc, out int cw, out int ch, out int cf))
                    cur = new CaptureFormats.RawType(-1, cc, cw, ch, cf);
            }
            catch { /* no "current" type: fall back to the largest */ }

            CaptureFormats.RawType pick = CaptureFormats.Pick(types, _wantedMode, cur)!.Value;
            string pickedDesc, currentNativeDesc = "-", outputDesc = "-";
            using (IMFMediaType native = reader.GetNativeMediaType(VideoStream, pick.Index))
            {
                pickedDesc = CaptureFormats.DescribeType(native);
                IMFSourceReaderEx? ex = reader.QueryInterfaceOrNull<IMFSourceReaderEx>();
                if (ex != null)
                {
                    using (ex) ex.SetNativeMediaType(VideoStream, native, out _);
                }
                else
                {
                    reader.SetCurrentMediaType(VideoStream, native);
                }

                // Ask the reader for NV12 (it converts YUY2 / MJPG / P010 as needed).
                using IMFMediaType nv12 = MediaFactory.MFCreateMediaType();
                nv12.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                nv12.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                nv12.Set(MediaTypeAttributeKeys.FrameSize, native.GetUInt64(MediaTypeAttributeKeys.FrameSize));
                nv12.Set(MediaTypeAttributeKeys.FrameRate, native.GetUInt64(MediaTypeAttributeKeys.FrameRate));
                try { reader.SetCurrentMediaType(VideoStream, nv12); }
                catch (Exception e)
                {
                    Log.Info($"D3D: NV12 output rejected (0x{e.HResult:X8})");
                    throw new UserFacingException(L.T("映像形式を NV12 に変換できません。"));
                }
            }

            using (IMFMediaType outType = reader.GetCurrentMediaType(VideoStream))
            {
                (width, height, yuv) = ReadOutputType(outType, pick.W, pick.H);
                outputDesc = CaptureFormats.DescribeType(outType);
            }
            try
            {
                using IMFMediaType curNative = reader.GetNativeMediaType(VideoStream, CurrentTypeIndex);
                currentNativeDesc = CaptureFormats.DescribeType(curNative);
            }
            catch (Exception ex) { currentNativeDesc = $"(unavailable, 0x{ex.HResult:X8})"; }
            bool tenBit = pick.Comp is "P010" or "P016" or "Y210" or "Y216" or "Y410";
            ColorInfo =
                $"native (selected): {pickedDesc}\n" +
                $"native (current):  {currentNativeDesc}\n" +
                $"output (reader):   {outputDesc}\n" +
                $"converted to NV12: {(pick.Comp == "NV12" ? "no" : "yes")}" +
                (tenBit ? $" ({pick.Comp} -> NV12: 10-bit precision is lost)" : string.Empty) + "\n" +
                $"renderer assumes:  matrix={(yuv.Matrix == 2 ? "601" : yuv.Matrix is 4 or 5 ? "2020" : "709")}, {(yuv.FullRange ? "0-255" : "16-235")}, no PQ/HLG handling (shown as SDR)";
            // NV12 needs even dimensions for the chroma plane.
            width &= ~1; height &= ~1;

            Resolution = new Size(width, height);
            Fps = pick.Fps;
            Mode = new CaptureMode(pick.Comp, pick.W, pick.H, pick.Fps);
            Log.Info($"D3D: format {pick.Comp} {pick.W}x{pick.H}@{pick.Fps} -> NV12 {width}x{height}, matrix={(yuv.Matrix == 2 ? "601" : yuv.Matrix is 4 or 5 ? "2020" : "709")}, {(yuv.FullRange ? "0-255" : "16-235")}");
            return reader;
        }

        private static (int w, int h, YuvInfo yuv) ReadOutputType(IMFMediaType t, int defW, int defH)
        {
            int w = defW, h = defH;
            try
            {
                ulong size = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF);
            }
            catch { }
            w &= ~1; h &= ~1;

            int matrix = h >= 720 ? 1 : 2;   // 709 for HD and up, 601 below
            try
            {
                int m = (int)t.GetUInt32(MediaTypeAttributeKeys.YuvMatrix);
                if (m is 1 or 2 or 4 or 5) matrix = m;
            }
            catch { }
            bool full = false;
            try
            {
                int range = (int)t.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange);
                full = range == 1;   // MFNominalRange_0_255; unknown / 16-235 stay limited
            }
            catch { }
            return (w, h, new YuvInfo(matrix, full));
        }
    }
}
