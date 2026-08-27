using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Vortice.MediaFoundation;

namespace YuCap;

/// <summary>A video input device (friendly name + symbolic link).</summary>
public sealed record VideoDeviceInfo(string Name, string Id);

/// <summary>A selectable capture format (compression/FOURCC + resolution + fps).</summary>
public sealed record CaptureMode(string Compression, int Width, int Height, int Fps);

/// <summary>How the video is fitted into the window.</summary>
public enum VideoDisplayMode { AspectFit, Stretch, OneToOne, IntegerScale }

/// <summary>
/// Capture + display via the Media Foundation Capture Engine. Its preview sink
/// renders straight to the host window on the GPU and, unlike DirectShow, handles
/// NV12/P010 natively — so the device's full high-fps modes (1080p120 / 1440p60 /
/// 4K30) are available. Aspect/position/snapshot reuse the EVR display control the
/// preview sink exposes via GetService.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private static readonly object StartupLock = new();
    private static bool _mfStarted;

    private IMFCaptureEngine? _engine;
    private IMFCaptureSource? _source;   // Vortice
    private IMFCapturePreviewSink? _sink;
    private CaptureEventCallback? _callback;

    private IntPtr _hwnd;
    private Control? _hostControl;  // for BeginInvoke — see SetVideoRect
    private int _sinkStream;
    private int _rotation;   // requested rotation (0/90/180/270), reapplied on Start
    private bool _mirror;

    // WHY UpdateVideo RUNS ON A WORKER THREAD (proven by the freeze logs):
    // Growing the destination rect to full-screen size makes MF rebuild its swap
    // chain, which takes SECONDS and internally needs the window's UI thread to
    // keep pumping messages. Called on the UI thread it therefore deadlocks —
    // the watchdog caught it red-handed:
    //     UpdateVideo: begin rect=1920x1080   (no "end" line ever)
    //     *** UI THREAD STUCK for 4578ms — in-flight operation: UpdateVideo
    // The identical call from a worker completed in 3891ms with S_OK, because
    // the UI thread was free to service it. Deferring on the UI thread
    // (BeginInvoke) does NOT help: the problem is not reentrancy, it is that the
    // calling thread must not be the one the callee is waiting on.
    // NOTE: hr = S_FALSE (0x1) is a NORMAL return here — windowed calls return it
    // constantly while video displays correctly. Do not read it as failure.
    private readonly object _reqLock = new();
    private Rectangle _reqRect;
    private bool _reqClear;
    private long _reqQueuedTick;      // to expose time lost queueing behind a slow call
    private Rectangle _appliedDest;   // last rect actually handed to UpdateVideo
    private System.Windows.Forms.Timer? _flushTimer;

    // The RCW is apartment-bound: our custom [ComImport] interface has no
    // registered proxy/stub, so the CLR cannot marshal it to the worker's
    // apartment (E_NOINTERFACE). MF's sink is free-threaded, so the worker calls
    // through the raw vtable instead — legitimate for a free-threaded object,
    // and observed returning S_OK after doing several seconds of real work.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UpdateVideoNative(IntPtr self, IntPtr srcRect, IntPtr dstRect, IntPtr borderColor);

    /// <summary>
    /// Everything tied to one preview instance, so a worker stuck inside a call
    /// can be abandoned wholesale. UpdateVideo has been observed never returning
    /// (log: "begin rect=1920x1080" with no "end" for the rest of the session),
    /// which used to wedge the single worker permanently — after that no resize
    /// ever took effect again. Each Start() gets a fresh session with its own
    /// thread, signal and lock, so a dead one can never block the new one.
    /// </summary>
    private sealed class SinkSession
    {
        public IntPtr Raw;                       // AddRef'd IMFCapturePreviewSink*
        public UpdateVideoNative? Fn;            // vtable slot 10
        public readonly object Gate = new();     // call vs. teardown
        public readonly AutoResetEvent Signal = new(false);
        public readonly IntPtr RectBuf = Marshal.AllocHGlobal(16); // RECT
        public readonly IntPtr ColorBuf = Marshal.AllocHGlobal(4); // COLORREF
        public volatile bool Retired;
        public volatile bool CallInFlight;
        public long CallStartTick;
    }

    private SinkSession? _session;
    private bool _reqValid;

    // Photo sink, created lazily on the first snapshot and torn down with the
    // engine. Serialised: TakePhoto is a single-shot operation on the engine.
    private readonly object _photoLock = new();
    private IMFCapturePhotoSink? _photoSink;
    private PhotoReceiver? _photoReceiver;
    /// <summary>Set once the photo path proves unusable on this device, so
    /// snapshots go straight to the screen-copy fallback instead of paying the
    /// setup cost and timeout on every shot.</summary>
    private bool _photoUnavailable;

    public string? CurrentDeviceName { get; private set; }
    public Size CurrentResolution { get; private set; }
    public CaptureMode? CurrentMode { get; private set; }
    public int NegotiatedFps { get; private set; }
    public bool IsRunning => _engine != null;

    public int Rotation => _rotation;
    public bool Mirror => _mirror;

    /// <summary>Resolution as displayed: width/height swap under 90°/270° rotation.</summary>
    public Size DisplayResolution =>
        _rotation is 90 or 270
            ? new Size(CurrentResolution.Height, CurrentResolution.Width)
            : CurrentResolution;

    /// <summary>Rotate the preview (0/90/180/270). Returns false if the pipeline
    /// rejects rotation (driver-dependent); the request is kept for restarts.</summary>
    public bool SetRotation(int degrees)
    {
        _rotation = ((degrees % 360) + 360) % 360 is var d && d is 0 or 90 or 180 or 270 ? d : 0;
        SinkSession? s = _session;
        if (_sink == null || s == null) return true;
        // Don't overlap a resize in flight, and never block the UI thread for long.
        if (!Monitor.TryEnter(s.Gate, 1500)) return false;
        try { return _sink.SetRotation(_sinkStream, _rotation) >= 0; }
        catch { return false; }
        finally { Monitor.Exit(s.Gate); }
    }

    /// <summary>Mirror the preview horizontally. Returns false if unsupported.</summary>
    public bool SetMirror(bool mirrored)
    {
        _mirror = mirrored;
        SinkSession? s = _session;
        if (_sink == null || s == null) return true;
        if (!Monitor.TryEnter(s.Gate, 1500)) return false;
        try { return _sink.SetMirrorState(mirrored ? 1 : 0) >= 0; }
        catch { return false; }
        finally { Monitor.Exit(s.Gate); }
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hostControl = Control.FromHandle(hwnd);
    }

    private static void EnsureStartup()
    {
        lock (StartupLock)
        {
            if (_mfStarted) return;
            MediaFactory.MFStartup(false);
            _mfStarted = true;
        }
    }

    // ---- Device enumeration ---------------------------------------------

    public static List<VideoDeviceInfo> EnumerateDevices()
    {
        EnsureStartup();
        var list = new List<VideoDeviceInfo>();
        using IMFAttributes attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        using IMFActivateCollection collection = MediaFactory.MFEnumDeviceSources(attrs);
        foreach (IMFActivate act in collection)
        {
            using (act)
            {
                string name = SafeString(act, CaptureDeviceAttributeKeys.FriendlyName);
                string link = SafeString(act, CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
                if (!string.IsNullOrEmpty(link))
                    list.Add(new VideoDeviceInfo(name, link));
            }
        }
        return list;
    }

    private static string SafeString(IMFAttributes attr, Guid key)
    {
        try { return attr.GetString(key); } catch { return string.Empty; }
    }

    public static VideoDeviceInfo? PickPreferred(IReadOnlyList<VideoDeviceInfo> devices, string keyword)
    {
        if (devices.Count == 0) return null;
        return devices.FirstOrDefault(d =>
                   d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
               ?? devices[0];
    }

    private IMFActivate FindActivate(VideoDeviceInfo info)
    {
        using IMFAttributes attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        using IMFActivateCollection collection = MediaFactory.MFEnumDeviceSources(attrs);

        IMFActivate? match = null;
        foreach (IMFActivate act in collection)
        {
            string link = SafeString(act, CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink);
            if (match == null && (link == info.Id ||
                SafeString(act, CaptureDeviceAttributeKeys.FriendlyName) == info.Name))
                match = act;      // keep this one
            else
                act.Dispose();
        }
        return match ?? throw new InvalidOperationException("映像デバイスが見つかりません。");
    }

    // ---- Start / Stop ----------------------------------------------------

    public void Start(VideoDeviceInfo info, CaptureMode? mode = null)
    {
        Log.Info($"VideoEngine.Start: {info.Name} mode={(mode == null ? "auto" : $"{mode.Width}x{mode.Height}@{mode.Fps}")}");
        Stop();
        EnsureStartup();
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("表示ウィンドウが設定されていません。");

        using IMFActivate activate = FindActivate(info);

        // Create the capture engine via its class factory.
        Guid clsid = Mf.ClsidCaptureEngine, iidFactory = Mf.IidClassFactory;
        Marshal.ThrowExceptionForHR(Mf.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iidFactory, out IntPtr facPtr));
        using (var factory = (IMFCaptureEngineClassFactory)facPtr)
        {
            IntPtr engPtr = factory.CreateInstance(Mf.ClsidCaptureEngine, Mf.IidCaptureEngine);
            _engine = (IMFCaptureEngine)Marshal.GetObjectForIUnknown(engPtr);
            Marshal.Release(engPtr);
        }

        _callback = new CaptureEventCallback();
        Marshal.ThrowExceptionForHR(
            _engine.Initialize(_callback, IntPtr.Zero, IntPtr.Zero, activate.NativePointer));
        if (!_callback.Initialized.Wait(5000))
            throw new TimeoutException("キャプチャエンジンの初期化がタイムアウトしました。");
        if (_callback.LastHr < 0)
            Marshal.ThrowExceptionForHR(_callback.LastHr);

        // Configure the source format. With no explicit mode we keep the device
        // default (on JVA14 that is already 1080p120), which is the best viewer
        // default; an explicit mode (menu/persisted) is applied verbatim.
        _engine.GetSource(out IntPtr srcPtr);
        _source = (IMFCaptureSource)srcPtr;
        if (mode != null) ConfigureFormat(mode);

        // Preview sink → render into our window.
        _engine.GetSink(Mf.SinkTypePreview, out IntPtr sinkPtr);
        _sink = (IMFCapturePreviewSink)Marshal.GetObjectForIUnknown(sinkPtr);
        // Raw pointer + UpdateVideo vtable slot for the rect worker (see the
        // field comments: the RCW cannot cross apartments, the raw call can).
        var session = new SinkSession();
        Guid iidPreview = new("77346cfd-5b49-4d73-ace0-5b52a859f2e0");
        if (Marshal.QueryInterface(sinkPtr, ref iidPreview, out IntPtr rawSink) == 0
            && rawSink != IntPtr.Zero)
        {
            session.Raw = rawSink;
            IntPtr vtbl = Marshal.ReadIntPtr(rawSink);
            // 3 IUnknown + 5 IMFCaptureSink + SetRenderHandle + SetRenderSurface
            session.Fn = Marshal.GetDelegateForFunctionPointer<UpdateVideoNative>(
                Marshal.ReadIntPtr(vtbl, 10 * IntPtr.Size));
        }
        else
        {
            Log.Info("VideoEngine.Start: raw preview sink unavailable — resizes will not work");
        }
        _session = session;
        Marshal.Release(sinkPtr);
        _sink.SetRenderHandle(_hwnd);
        // The preview sink needs the source stream added with its media type,
        // otherwise StartPreview fails with MF_E_INVALIDREQUEST.
        using (IMFMediaType cur = _source.GetCurrentDeviceMediaType(Mf.PreferredPreviewStream))
        {
            _sink.RemoveAllStreams();
            _sink.AddStream(Mf.PreferredPreviewStream, cur.NativePointer, IntPtr.Zero, out _sinkStream);
        }

        // NOTE: do NOT set a sample callback on the preview/render stream — it
        // diverts frames away from the window and the preview goes blank (white).

        // Start preview (async → wait for the event).
        _callback.PreviewStarted.Reset();
        Marshal.ThrowExceptionForHR(_engine.StartPreview());
        if (!_callback.PreviewStarted.Wait(5000))
            throw new TimeoutException("プレビュー開始がタイムアウトしました。");
        if (_callback.LastHr < 0)
            Marshal.ThrowExceptionForHR(_callback.LastHr);

        ReadCurrentFormat();
        CurrentDeviceName = info.Name;
        _appliedDest = Rectangle.Empty;
        StartRectWorker(session);
        Log.Info($"VideoEngine.Start: preview running {CurrentResolution.Width}x{CurrentResolution.Height}@{NegotiatedFps}");
        // Aspect/letterbox is achieved by sizing the host window (MainForm),
        // since the preview always stretch-fills its render window.

        // Re-apply persisted orientation on every (re)start (best effort).
        if (_rotation != 0) { try { _sink.SetRotation(_sinkStream, _rotation); } catch { } }
        if (_mirror) { try { _sink.SetMirrorState(1); } catch { } }
    }

    public void Stop()
    {
        if (_engine != null) Log.Info("VideoEngine.Stop");
        try { _flushTimer?.Stop(); } catch { /* ignore */ }
        lock (_reqLock) { _reqValid = false; }   // drop any queued rect update

        SinkSession? session = _session;
        _session = null;
        if (session != null)
        {
            session.Retired = true;
            try { session.Signal.Set(); }        // let an idle worker exit
            catch (ObjectDisposedException) { /* already gone */ }

            // Only touch COM if no call is in flight. A wedged UpdateVideo can
            // never be cancelled, so the objects are abandoned (leaked) instead —
            // the alternative is a use-after-free or hanging here forever.
            // NOTE: the session's own raw pointer and buffers are freed ONLY by
            // its worker thread (see the end of RectWorkerLoop). Releasing them
            // here too raced with that and double-freed — an access violation
            // that killed the process outright. Single owner, no race.
            bool gated = Monitor.TryEnter(session.Gate, 400);
            if (!gated)
            {
                Log.Info("VideoEngine.Stop: UpdateVideo still in flight — abandoning this preview session");
            }
            try
            {
                if (gated)
                {
                    try { _engine?.StopPreview(); } catch { /* ignore */ }
                    SafeRelease(_sink);
                    try { _source?.Dispose(); } catch { /* ignore */ }
                    SafeRelease(_engine);
                }
            }
            finally
            {
                if (gated) Monitor.Exit(session.Gate);
            }
        }

        lock (_photoLock)
        {
            SafeRelease(_photoSink);
            _photoSink = null;
            _photoReceiver?.Reset();
            _photoReceiver = null;
            _photoUnavailable = false;   // re-probe against the next device
        }

        _sink = null;
        _source = null;
        _engine = null;

        _callback?.Initialized.Dispose();
        _callback?.PreviewStarted.Dispose();
        _callback?.PhotoTaken.Dispose();
        _callback = null;

        CurrentDeviceName = null;
        CurrentResolution = Size.Empty;
        CurrentMode = null;
        NegotiatedFps = 0;
        _lastDest = Rectangle.Empty;
        _appliedDest = Rectangle.Empty;
        try { _flushTimer?.Stop(); } catch { /* ignore */ }
    }

    private static void SafeRelease(object? o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        catch { /* ignore */ }
    }

    // ---- Format enumeration & selection ---------------------------------

    private readonly record struct RawType(int Index, string Comp, int W, int H, int Fps);

    private List<RawType> EnumerateRaw()
    {
        var list = new List<RawType>();
        if (_source == null) return list;
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = _source.GetAvailableDeviceMediaType(Mf.PreferredPreviewStream, i); }
            catch { break; }
            using (type)
            {
                if (TryDescribe(type, out string comp, out int w, out int h, out int fps))
                    list.Add(new RawType(i, comp, w, h, fps));
            }
        }
        return list;
    }

    public List<CaptureMode> GetModes()
    {
        return EnumerateRaw()
            .GroupBy(r => (r.Comp, r.W, r.H))
            .Select(g => new CaptureMode(g.Key.Comp, g.Key.W, g.Key.H, g.Max(r => r.Fps)))
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Fps)
            .ToList();
    }

    private void ConfigureFormat(CaptureMode? desired)
    {
        List<RawType> types = EnumerateRaw();
        if (types.Count == 0) return;

        // NOTE: RawType is a struct, so FirstOrDefault() would return default(RawType)
        // (Index = 0 — a real, arbitrary format!) when nothing matches. Use FindIndex
        // so "not found" is unambiguous and the fallbacks actually run.
        // An empty Compression means "any" (used by command-line --mode).
        bool CompOk(RawType r) =>
            string.IsNullOrEmpty(desired!.Compression) || r.Comp == desired.Compression;
        RawType pick;
        int exact = desired == null ? -1 : types.FindIndex(r =>
            CompOk(r) && r.W == desired.Width && r.H == desired.Height &&
            (desired.Fps <= 0 || r.Fps == desired.Fps));
        int sameRes = desired == null ? -1 : types.FindIndex(r =>
            CompOk(r) && r.W == desired.Width && r.H == desired.Height);

        if (exact >= 0)
        {
            pick = types[exact];
        }
        else if (sameRes >= 0)
        {
            pick = types.Where(r => CompOk(r) &&
                    r.W == desired!.Width && r.H == desired.Height)
                .OrderByDescending(r => r.Fps).First();
        }
        else
        {
            pick = types.OrderByDescending(r => (long)r.W * r.H).ThenByDescending(r => r.Fps).First();
        }

        using IMFMediaType chosen = _source!.GetAvailableDeviceMediaType(Mf.PreferredPreviewStream, pick.Index);
        _source.SetCurrentDeviceMediaType(Mf.PreferredPreviewStream, chosen);
    }

    private void ReadCurrentFormat()
    {
        if (_source == null) return;
        try
        {
            using IMFMediaType type = _source.GetCurrentDeviceMediaType(Mf.PreferredPreviewStream);
            if (TryDescribe(type, out string comp, out int w, out int h, out int fps))
            {
                CurrentResolution = new Size(w, h);
                NegotiatedFps = fps;
                CurrentMode = new CaptureMode(comp, w, h, fps);
            }
        }
        catch { /* leave defaults */ }
    }

    private static bool TryDescribe(IMFMediaType type, out string comp, out int w, out int h, out int fps)
    {
        comp = string.Empty; w = h = fps = 0;
        try
        {
            Guid subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
            comp = Fourcc(subtype);
            ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            w = (int)(size >> 32);
            h = (int)(size & 0xFFFFFFFF);
            ulong rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            uint num = (uint)(rate >> 32), den = (uint)(rate & 0xFFFFFFFF);
            fps = den > 0 ? (int)Math.Round((double)num / den) : 0;
            return w > 0 && h > 0;
        }
        catch { return false; }
    }

    private static string Fourcc(Guid subtype)
    {
        byte[] b = subtype.ToByteArray();
        uint c = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

        // Uncompressed RGB subtypes use small numeric Data1 values, not FOURCCs.
        switch (c)
        {
            case 20: return "RGB24";
            case 21: return "ARGB32";
            case 22: return "RGB32";
            case 23: return "RGB565";
            case 24: return "RGB555";
        }

        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char ch = (char)((c >> (8 * i)) & 0xFF);
            chars[i] = char.IsControl(ch) ? ' ' : ch;
        }
        string s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? $"0x{c:X}" : s;
    }

    // ---- Display positioning / repaint / snapshot -----------------------

    /// <summary>
    /// Set the destination rectangle (in render-window client pixels) the preview
    /// video is drawn into, so it scales with the window instead of cropping.
    /// </summary>
    private Rectangle _lastDest; // where the video actually lands inside the host window

    /// <summary>Set the video destination rect. <paramref name="clearBorder"/>
    /// repaints the letterbox black — skip it during interactive resizing: the
    /// per-move clear+blit is what makes the video flicker black/stale.
    /// The actual call is deferred one message via Control.BeginInvoke (still
    /// the UI/STA thread MF needs, but no longer nested inside the caller's own
    /// window-message handling — see the field comment above for why).</summary>
    /// <param name="settleMs">Extra delay before the call. Use a non-zero value
    /// after a window-mode change (fullscreen enter/exit): the resize makes MF
    /// rebuild its swap chain, and doing that while the window/DWM transition is
    /// still in flight is what the freeze logs point at. Waiting lets the
    /// transition finish first.</param>
    public int SetVideoRect(Rectangle dst, bool clearBorder = true, int settleMs = 0)
    {
        if (_sink == null) return -1;
        _lastDest = dst; // optimistic; used only for snapshot cropping

        Control? host = _hostControl;
        if (host == null || !host.IsHandleCreated) return -1;

        lock (_reqLock)
        {
            _reqRect = dst;
            _reqClear = clearBorder;
            _reqQueuedTick = Environment.TickCount64;
        }
        if (host.InvokeRequired)
        {
            // Not expected (callers are on the UI thread), but stay correct.
            try { host.BeginInvoke(new Action(() => ScheduleFlush(settleMs))); }
            catch { return -2; }
            return 0;
        }
        ScheduleFlush(settleMs);
        return 0;
    }

    /// <summary>Arm the one-shot flush timer (restarting it coalesces bursts of
    /// layout calls into a single UpdateVideo with the latest rect).</summary>
    private void ScheduleFlush(int settleMs)
    {
        _flushTimer ??= new System.Windows.Forms.Timer();
        if (_flushTimer.Tag == null)
        {
            _flushTimer.Tag = "wired";
            _flushTimer.Tick += (_, _) =>
            {
                _flushTimer!.Stop();
                FlushVideoRect();
            };
        }
        _flushTimer.Stop();
        _flushTimer.Interval = Math.Max(1, settleMs);
        _flushTimer.Start();
    }

    /// <summary>Hand the pending rect to the worker. Runs on the UI thread but
    /// only signals — it must never call UpdateVideo itself (see field comments).
    /// </summary>
    private void FlushVideoRect()
    {
        lock (_reqLock) { _reqValid = true; }
        // The session may retire (and dispose its signal) concurrently.
        try { _session?.Signal.Set(); } catch (ObjectDisposedException) { /* retired */ }
    }

    private void StartRectWorker(SinkSession session)
    {
        var t = new Thread(() => RectWorkerLoop(session))
        {
            IsBackground = true,
            Name = "YuCap.UpdateVideo",
        };
        t.Start();
    }

    /// <summary>True when a rect update has been in flight longer than
    /// <paramref name="thresholdMs"/> — i.e. UpdateVideo is wedged and the video
    /// will never resize until the preview is restarted.</summary>
    public bool IsRectUpdateStuck(int thresholdMs)
    {
        SinkSession? s = _session;
        return s is { CallInFlight: true }
               && Environment.TickCount64 - s.CallStartTick > thresholdMs;
    }

    private void RectWorkerLoop(SinkSession session)
    {
        while (!session.Retired)
        {
            session.Signal.WaitOne();
            if (session.Retired) break;

            Rectangle r;
            bool clear;
            long queuedTick;
            lock (_reqLock)
            {
                if (!_reqValid) continue;
                r = _reqRect;
                clear = _reqClear;
                queuedTick = _reqQueuedTick;
                _reqValid = false;
            }

            // Teardown must not release the sink while a call is in flight.
            if (!Monitor.TryEnter(session.Gate, 5000))
            {
                Log.Info("UpdateVideo(worker): session busy, dropping request");
                continue;
            }
            try
            {
                if (session.Retired) break;
                UpdateVideoNative? fn = session.Fn;
                if (session.Raw == IntPtr.Zero || fn == null) continue;

                // Only a size change makes MF rebuild the swap chain (the slow
                // path that can wedge) — log those, stay quiet otherwise.
                // ALWAYS pass an explicit destination rect. Passing null ("fill
                // the window") was tried as an optimisation and made fullscreen
                // render nothing at all — with DirectComposition the explicit
                // rect is what actually resizes the composition visual.
                bool sizeChanged = r.Size != _appliedDest.Size;
                Marshal.WriteInt32(session.RectBuf, 0, r.Left);
                Marshal.WriteInt32(session.RectBuf, 4, r.Top);
                Marshal.WriteInt32(session.RectBuf, 8, r.Right);
                Marshal.WriteInt32(session.RectBuf, 12, r.Bottom);
                IntPtr dstArg = session.RectBuf;
                IntPtr border = IntPtr.Zero;
                if (clear)
                {
                    Marshal.WriteInt32(session.ColorBuf, 0, 0); // black
                    border = session.ColorBuf;
                }

                long waited = Environment.TickCount64 - queuedTick;
                if (sizeChanged)
                    Log.Info($"UpdateVideo(worker): begin rect={r.Width}x{r.Height}@{r.X},{r.Y} queued={waited}ms");
                else if (waited > 500)
                    Log.Info($"UpdateVideo(worker): request waited {waited}ms in queue");

                session.CallStartTick = Environment.TickCount64;
                session.CallInFlight = true;      // watched by IsRectUpdateStuck
                long t0 = Environment.TickCount64;
                int hr;
                try { hr = fn(session.Raw, IntPtr.Zero, dstArg, border); }
                finally { session.CallInFlight = false; }
                long dt = Environment.TickCount64 - t0;

                // hr == S_FALSE (0x1) is normal here; only hr < 0 is an error.
                if (sizeChanged) Log.Info($"UpdateVideo(worker): end hr=0x{hr:X} {dt}ms");
                else if (dt > 500) Log.Info($"UpdateVideo(worker) slow (same size): {dt}ms hr=0x{hr:X}");
                if (hr < 0) Log.Info($"UpdateVideo(worker) failed hr=0x{hr:X}");
                if (session.Retired) break;       // a restart already superseded us
                _appliedDest = r;

                // Nudge the host to repaint so the resized surface is presented
                // promptly instead of waiting for the next incidental paint.
                if (sizeChanged)
                {
                    Control? host = _hostControl;
                    if (host is { IsHandleCreated: true })
                    {
                        try { host.BeginInvoke(new Action(host.Invalidate)); } catch { /* closing */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("UpdateVideo(worker): " + ex.Message);
            }
            finally
            {
                Monitor.Exit(session.Gate);
            }
        }

        // This thread is the sole owner of the session's native resources, so
        // freeing here needs no coordination and cannot double-free. If the
        // thread is wedged inside UpdateVideo it never runs — the session leaks
        // by design, which is the price of not corrupting or hanging.
        IntPtr raw = Interlocked.Exchange(ref session.Raw, IntPtr.Zero);
        if (raw != IntPtr.Zero)
        {
            try { Marshal.Release(raw); } catch { /* ignore */ }
        }
        try { Marshal.FreeHGlobal(session.RectBuf); } catch { /* ignore */ }
        try { Marshal.FreeHGlobal(session.ColorBuf); } catch { /* ignore */ }
        session.Signal.Dispose();
    }

    /// <summary>
    /// Capture a frame at the source's own resolution using the capture engine's
    /// photo sink. Unlike the screen-copy fallback this is independent of the
    /// window: full sensor resolution, nothing overlapping it, and correct even
    /// when the window is small, minimised or covered.
    /// Returns null if the photo path is unavailable, so callers can fall back.
    /// </summary>
    public Bitmap? PhotoSnapshot(int timeoutMs = 4000)
    {
        IMFCaptureEngine? engine = _engine;
        CaptureEventCallback? cb = _callback;
        if (engine == null || cb == null) return null;
        if (_photoUnavailable) return null;   // probed once and found wanting

        lock (_photoLock)
        {
            try
            {
                if (!EnsurePhotoSink(engine)) { _photoUnavailable = true; return null; }

                _photoReceiver!.Reset();
                cb.PhotoTaken.Reset();
                cb.LastPhotoHr = 0;

                int hr = engine.TakePhoto();
                if (hr < 0) { Log.Info($"photo: TakePhoto failed hr=0x{hr:X}"); return null; }

                // Wait on the delivered sample rather than the PHOTO_TAKEN
                // event: the frame itself is the thing we need, and waiting for
                // it keeps this working regardless of which completion events a
                // given driver raises.
                Bitmap? bmp = _photoReceiver.TakeBitmap(timeoutMs);
                if (bmp == null)
                {
                    Log.Info(cb.LastPhotoHr < 0
                        ? $"photo: capture reported hr=0x{cb.LastPhotoHr:X}"
                        : "photo: no sample delivered before the timeout");
                }
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Info("photo: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Create the photo sink on first use and wire up the receiver. A capture
    /// card usually has no dedicated photo pin, so the preferred-photo stream
    /// fails with MF_E_INVALIDSTREAMNUMBER; the video streams are tried next so
    /// the still is taken from the same pin the preview uses.
    /// </summary>
    private bool EnsurePhotoSink(IMFCaptureEngine engine)
    {
        if (_photoSink != null && _photoReceiver != null) return true;
        if (_source == null) return false;

        engine.GetSink(Mf.SinkTypePhoto, out IntPtr sinkPtr);
        if (sinkPtr == IntPtr.Zero) return false;
        var sink = (IMFCapturePhotoSink)Marshal.GetObjectForIUnknown(sinkPtr);
        Marshal.Release(sinkPtr);

        foreach (int stream in new[] { Mf.PreferredPhotoStream, Mf.PreferredPreviewStream, Mf.FirstVideoStream })
        {
            ulong frameSize;
            try
            {
                using IMFMediaType src = _source.GetCurrentDeviceMediaType(stream);
                frameSize = src.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            }
            catch (Exception ex)
            {
                Log.Info($"photo: stream 0x{stream:X} unusable — {ex.Message.Split('\n')[0]}");
                continue;
            }

            int w = (int)(frameSize >> 32), h = (int)(frameSize & 0xFFFFFFFF);
            if (w <= 0 || h <= 0) continue;

            // Uncompressed RGB32 at the source's frame size, so the delivered
            // buffer maps straight onto a Bitmap with no decoding.
            using IMFMediaType outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, Mf.MFMediaTypeVideo);
            outType.Set(MediaTypeAttributeKeys.Subtype, Mf.MFVideoFormatRGB32);
            outType.Set(MediaTypeAttributeKeys.FrameSize, frameSize);

            sink.RemoveAllStreams();
            int hrAdd = sink.AddStream(stream, outType.NativePointer, IntPtr.Zero, out int _);
            if (hrAdd < 0)
            {
                Log.Info($"photo: AddStream on 0x{stream:X} failed hr=0x{hrAdd:X}");
                continue;
            }

            var receiver = new PhotoReceiver(w, h);
            int hrCb = sink.SetSampleCallback(receiver);
            if (hrCb < 0)
            {
                Log.Info($"photo: SetSampleCallback failed hr=0x{hrCb:X}");
                continue;
            }

            _photoSink = sink;
            _photoReceiver = receiver;
            Log.Info($"photo: sink ready on stream 0x{stream:X} at {w}x{h} RGB32");
            return true;
        }

        Log.Info("photo: no usable stream for the photo sink");
        SafeRelease(sink);
        return false;
    }

    /// <summary>
    /// Turns the delivered photo sample into a Bitmap. The buffer arrives on an
    /// MF thread, so the frame is handed over through an event rather than being
    /// touched directly by the caller.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class PhotoReceiver : IMFCaptureEngineOnSampleCallback
    {
        private readonly int _width, _height;
        private readonly ManualResetEventSlim _ready = new(false);
        private Bitmap? _frame;

        public PhotoReceiver(int width, int height) { _width = width; _height = height; }

        public void Reset()
        {
            _ready.Reset();
            Bitmap? old = Interlocked.Exchange(ref _frame, null);
            old?.Dispose();
        }

        public Bitmap? TakeBitmap(int timeoutMs)
        {
            if (!_ready.Wait(timeoutMs)) return null;
            return Interlocked.Exchange(ref _frame, null);
        }

        public int OnSample(IntPtr sample)
        {
            try
            {
                if (sample == IntPtr.Zero) return 0;
                Marshal.AddRef(sample);
                using var s = (IMFSample)sample;
                using IMFMediaBuffer buffer = s.ConvertToContiguousBuffer();

                buffer.Lock(out IntPtr scan0, out _, out int length);
                try
                {
                    // RGB32 rows arrive bottom-up; a negative stride flips them.
                    int stride = _width * 4;
                    if (length < stride * _height) return 0;
                    using var src = new Bitmap(_width, _height, -stride,
                        PixelFormat.Format32bppRgb, scan0 + stride * (_height - 1));
                    var copy = new Bitmap(src);   // detach from the MF buffer
                    Bitmap? old = Interlocked.Exchange(ref _frame, copy);
                    old?.Dispose();
                }
                finally { buffer.Unlock(); }

                _ready.Set();
            }
            catch (Exception ex) { Log.Info("photo receiver: " + ex.Message); }
            return 0;
        }
    }

    /// <summary>
    /// Snapshot the displayed video by copying the composited screen region of
    /// the render window. PrintWindow cannot capture the D3D/EVR surface, so the
    /// compositor copy is used. Resolution follows the window; the window must be
    /// visible and unobscured. Used as the fallback when the photo sink is
    /// unavailable, and for the freeze-frame overlay which wants exactly what is
    /// on screen.
    /// </summary>
    public Bitmap? Snapshot(bool cropToVideo = true)
    {
        if (_hwnd == IntPtr.Zero || !IsRunning) return null;
        if (!GetWindowRect(_hwnd, out RECT rc)) return null;
        var win = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
        if (win.Width <= 0 || win.Height <= 0) return null;

        // Crop to the video destination rect so letterbox bars are not saved.
        // _lastDest is in host-window client pixels; the host is a plain child
        // window (no non-client area) so its window rect == client area on screen.
        Rectangle crop = win;
        if (cropToVideo && _lastDest.Width > 0 && _lastDest.Height > 0)
        {
            var d = new Rectangle(win.X + _lastDest.X, win.Y + _lastDest.Y,
                _lastDest.Width, _lastDest.Height);
            d.Intersect(win);
            if (d.Width > 0 && d.Height > 0) crop = d;
        }

        var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(crop.Left, crop.Top, 0, 0, crop.Size);
        }
        catch { bmp.Dispose(); return null; }
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public void Dispose() => Stop(); // Stop retires the session and wakes its worker
}
