using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace YuCap;

/// <summary>
/// Our own small WASAPI shared-mode render loop. WasapiOut sizes the device buffer from one number and refills the
/// WHOLE buffer on every event, so the device queue was as long as the jitter buffer (200 ms + rounding on some
/// outputs). Here the device buffer is independent of the user's setting: the engine's own period (typically 10 ms,
/// via IAudioClient3.InitializeSharedAudioStream) and only ~2 periods are kept queued.
/// </summary>
internal sealed class SharedRender : IDisposable
{
    // ---- COM interop ---------------------------------------------------------------------------

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        // IAudioClient2
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
        // IAudioClient3
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint period);
        [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr sessionGuid);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    private const uint STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    // ---- state ---------------------------------------------------------------------------------

    private IAudioClient3? _client3;
    private IAudioClient? _client;      // same object when only the v1 interface is available
    private IAudioRenderClient? _render;
    private IMMDevice? _device;
    private EventWaitHandle? _event;
    private Thread? _thread;
    private volatile bool _stop;
    private uint _bufferFrames;
    private int _targetFrames;

    public int Rate { get; private set; }
    public int Channels { get; private set; }
    /// <summary>True when the engine's low-latency shared stream (IAudioClient3) is in use.</summary>
    public bool LowLatencyApi { get; private set; }
    public double PeriodMs { get; private set; }
    public double StreamLatencyMs { get; private set; }
    /// <summary>Smoothed audio queued in the device after each write (ms).</summary>
    public double PaddingMs => Volatile.Read(ref _paddingMs);
    private double _paddingMs;
    private long _lastWriteTick;
    public bool HasRun => Volatile.Read(ref _lastWriteTick) != 0;

    /// <summary>Times the device itself ran dry (our loop was late).</summary>
    public long DeviceStarved => Interlocked.Read(ref _deviceStarved);
    private long _deviceStarved;
    /// <summary>Times the jitter buffer was empty when the device wanted audio.</summary>
    public long BufferStarved => Interlocked.Read(ref _bufferStarved);
    private long _bufferStarved;

    /// <summary>Called on the render thread with the frames (source side, approximately) about to be pulled; true when the jitter buffer cannot supply them.</summary>
    public Func<int, bool>? IsStarving;
    /// <summary>Raised (render thread) when the device is lost or the loop failed.</summary>
    public event Action<Exception>? Failed;

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    /// <summary>Opens the render endpoint (does not start). Rate/Channels are the engine mix format.</summary>
    public SharedRender(string deviceId)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                Check(enumerator.GetDevice(deviceId, out var dev), "GetDevice");
                _device = dev;
            }
            finally { Marshal.ReleaseComObject(enumerator); }

            object? obj;
            Guid iid3 = IID_IAudioClient3;
            if (_device!.Activate(ref iid3, 23 /* CLSCTX_ALL */, IntPtr.Zero, out obj) >= 0 && obj is IAudioClient3 c3)
            {
                _client3 = c3;
            }
            else
            {
                if (obj != null) { try { Marshal.ReleaseComObject(obj); } catch { } }
                Guid iid1 = IID_IAudioClient;
                Check(_device.Activate(ref iid1, 23, IntPtr.Zero, out obj), "Activate(IAudioClient)");
                _client = (IAudioClient)obj;
            }

            IntPtr fmt = IntPtr.Zero;
            try
            {
                Check(_client3 != null ? _client3.GetMixFormat(out fmt) : _client!.GetMixFormat(out fmt), "GetMixFormat");
                ParseFormat(fmt);
            }
            finally { if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt); }
        }
        catch
        {
            // The caller never gets an object to dispose when the constructor throws: release the COM objects here.
            Dispose();
            throw;
        }
    }

    private void ParseFormat(IntPtr fmt)
    {
        ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
        Channels = (ushort)Marshal.ReadInt16(fmt, 2);
        Rate = Marshal.ReadInt32(fmt, 4);
        int bits = (ushort)Marshal.ReadInt16(fmt, 14);
        bool isFloat = tag == 3;
        if (tag == 0xFFFE) isFloat = Marshal.ReadInt32(fmt, 24) == 3;   // WAVEFORMATEXTENSIBLE SubFormat data1
        if (!isFloat || bits != 32)
            throw new NotSupportedException($"output mix format is not 32-bit float (tag {tag}, {bits} bit)");
    }

    /// <summary>Initialises the stream with the engine's smallest sensible period (IAudioClient3) or a small v1 buffer.</summary>
    public void Init(double queuePeriods)
    {
        IntPtr fmt = IntPtr.Zero;
        try
        {
            Check(_client3 != null ? _client3.GetMixFormat(out fmt) : _client!.GetMixFormat(out fmt), "GetMixFormat");
            uint periodFrames = 0;
            bool done = false;
            if (_client3 != null)
            {
                try
                {
                    Check(_client3.GetSharedModeEnginePeriod(fmt, out uint def, out uint fund, out uint min, out uint max), "GetSharedModeEnginePeriod");
                    // The default (usually 10 ms) is safe everywhere; take the minimum only when the default is long.
                    periodFrames = def * 1000.0 / Rate <= 15 ? def : min;
                    Check(_client3.InitializeSharedAudioStream(STREAMFLAGS_EVENTCALLBACK, periodFrames, fmt, IntPtr.Zero), "InitializeSharedAudioStream");
                    LowLatencyApi = true;
                    done = true;
                    Log.Info($"Audio: low-latency shared stream, period {periodFrames} frames ({periodFrames * 1000.0 / Rate:0.0} ms; engine default {def}, min {min}, max {max})");
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    Log.Info($"Audio: low-latency stream unavailable ({ex.Message}) - using the normal shared stream");
                    // A failed Initialize leaves the client unusable: reopen it as a plain IAudioClient.
                    ReopenAsV1();
                }
            }
            if (!done)
            {
                // ~30 ms buffer; in event mode the engine still works in its own period.
                Check(Client1.Initialize(0 /* shared */, STREAMFLAGS_EVENTCALLBACK, 30 * 10000, 0, fmt, IntPtr.Zero), "Initialize");
                Client1.GetDevicePeriod(out long defP, out _);
                periodFrames = (uint)Math.Max(64, defP * Rate / 10_000_000L);
                Log.Info($"Audio: shared stream (v1), device period {periodFrames * 1000.0 / Rate:0.0} ms");
            }
            PeriodMs = periodFrames * 1000.0 / Rate;
        }
        finally { if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt); }

        Check(GetBufferSize(out _bufferFrames), "GetBufferSize");
        if (GetStreamLatency(out long lat) >= 0) StreamLatencyMs = lat / 10000.0;
        _targetFrames = (int)Math.Min(_bufferFrames, Math.Max(1, (long)Math.Round(PeriodMs * queuePeriods * Rate / 1000.0)));

        Guid rid = IID_IAudioRenderClient;
        object svc;
        Check(Client1GetService(ref rid, out svc), "GetService(IAudioRenderClient)");
        _render = (IAudioRenderClient)svc;

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        Check(SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
        Log.Info($"Audio: output buffer {_bufferFrames} frames ({_bufferFrames * 1000.0 / Rate:0} ms), kept at ~{_targetFrames * 1000.0 / Rate:0.0} ms, stream latency {StreamLatencyMs:0.0} ms, {Rate} Hz x {Channels}");
    }

    private IAudioClient Client1 => _client ?? throw new InvalidOperationException();

    private void ReopenAsV1()
    {
        if (_client3 != null) { Marshal.ReleaseComObject(_client3); _client3 = null; }
        Guid iid1 = IID_IAudioClient;
        Check(_device!.Activate(ref iid1, 23, IntPtr.Zero, out var obj), "Activate(IAudioClient)");
        _client = (IAudioClient)obj;
    }

    private int GetBufferSize(out uint f) => _client3 != null ? _client3.GetBufferSize(out f) : Client1.GetBufferSize(out f);
    private int GetStreamLatency(out long l) => _client3 != null ? _client3.GetStreamLatency(out l) : Client1.GetStreamLatency(out l);
    private int GetCurrentPadding(out uint f) => _client3 != null ? _client3.GetCurrentPadding(out f) : Client1.GetCurrentPadding(out f);
    private int SetEventHandle(IntPtr h) => _client3 != null ? _client3.SetEventHandle(h) : Client1.SetEventHandle(h);
    private int Client1GetService(ref Guid iid, out object s) => _client3 != null ? _client3.GetService(ref iid, out s) : Client1.GetService(ref iid, out s);
    private int StartClient() => _client3 != null ? _client3.Start() : Client1.Start();
    private int StopClient() => _client3 != null ? _client3.Stop() : Client1.Stop();
    private int ResetClient() => _client3 != null ? _client3.Reset() : Client1.Reset();

    /// <summary>Starts the render thread pulling from <paramref name="source"/>.</summary>
    public void Start(ISampleProvider source)
    {
        // Prime the queue with silence so the first event has something to play.
        WriteFrames(null, _targetFrames, 0);
        Check(StartClient(), "Start");
        _thread = new Thread(() => Loop(source)) { IsBackground = true, Name = "YuCap audio render", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private float[] _src = Array.Empty<float>();
    private float[] _dst = Array.Empty<float>();

    private void WriteFrames(float[]? data, int frames, int srcChannels)
    {
        if (frames <= 0) return;
        Check(_render!.GetBuffer((uint)frames, out IntPtr ptr), "GetBuffer");
        if (data == null)
        {
            Check(_render.ReleaseBuffer((uint)frames, 2 /* AUDCLNT_BUFFERFLAGS_SILENT */), "ReleaseBuffer");
            return;
        }
        Marshal.Copy(data, 0, ptr, frames * Channels);
        Check(_render.ReleaseBuffer((uint)frames, 0), "ReleaseBuffer");
    }

    private void Loop(ISampleProvider source)
    {
        uint taskIndex = 0;
        IntPtr mmcss = IntPtr.Zero;
        try { mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex); } catch { /* optional */ }
        int srcCh = source.WaveFormat.Channels;
        bool starved = false;
        try
        {
            while (!_stop)
            {
                if (!_event!.WaitOne(250))
                {
                    if (_stop) break;
                    // No event for a while: a removed / reconfigured device never signals again, so ask it directly.
                    // AUDCLNT_E_DEVICE_INVALIDATED (or any failure) ends the loop through the Failed event.
                    Check(GetCurrentPadding(out _), "GetCurrentPadding");
                    continue;
                }
                if (_stop) break;
                Check(GetCurrentPadding(out uint padding), "GetCurrentPadding");
                if (HasRun && padding == 0) Interlocked.Increment(ref _deviceStarved);
                int want = (int)Math.Min(_bufferFrames - padding, Math.Max(0, _targetFrames - (long)padding));
                if (want <= 0) { Volatile.Write(ref _paddingMs, Ema(_paddingMs, padding * 1000.0 / Rate)); continue; }

                bool dry = IsStarving?.Invoke(want) == true;
                if (dry && !starved) Interlocked.Increment(ref _bufferStarved);
                starved = dry;

                if (_src.Length < want * srcCh) _src = new float[want * srcCh];
                int got = source.Read(_src, 0, want * srcCh) / srcCh;
                if (got < want) Array.Clear(_src, got * srcCh, (want - got) * srcCh);
                float[] outData = _src;
                if (srcCh != Channels)
                {
                    if (_dst.Length < want * Channels) _dst = new float[want * Channels];
                    Remap(_src, srcCh, _dst, Channels, want);
                    outData = _dst;
                }
                WriteFrames(outData, want, srcCh);
                Volatile.Write(ref _lastWriteTick, System.Diagnostics.Stopwatch.GetTimestamp());
                Volatile.Write(ref _paddingMs, Ema(_paddingMs, (padding + want) * 1000.0 / Rate));
            }
        }
        catch (Exception ex)
        {
            if (!_stop) { Log.Info($"Audio: render loop stopped: {ex.Message}"); try { Failed?.Invoke(ex); } catch { } }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) try { AvRevertMmThreadCharacteristics(mmcss); } catch { }
        }
    }

    private static double Ema(double prev, double v) => prev <= 0 ? v : prev * 0.95 + v * 0.05;

    private static void Remap(float[] src, int sc, float[] dst, int dc, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int s = f * sc, d = f * dc;
            if (dc == 1)
            {
                float sum = 0; for (int c = 0; c < sc; c++) sum += src[s + c];
                dst[d] = sum / sc;
            }
            else
            {
                int n = Math.Min(sc, dc);
                for (int c = 0; c < n; c++) dst[d + c] = src[s + c];
                for (int c = n; c < dc; c++) dst[d + c] = 0f;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _event?.Set(); } catch { }
        var t = _thread;
        if (t != null && t != Thread.CurrentThread) t.Join(2000);
        try { StopClient(); ResetClient(); } catch { }
        try { _event?.Dispose(); } catch { }
        foreach (object? o in new object?[] { _render, _client3, _client, _device })
            if (o != null) try { Marshal.ReleaseComObject(o); } catch { }
        _render = null; _client3 = null; _client = null; _device = null; _event = null; _thread = null;
    }
}
