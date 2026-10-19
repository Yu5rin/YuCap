using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

// NVIDIA Optical Flow (the OFA block of RTX 20/30/40 GPUs) through nvofapi64.dll, which ships with the display driver.
// Only the structure layouts and the call order are taken from NVIDIA's Optical Flow SDK headers (nvOpticalFlowCommon.h /
// nvOpticalFlowD3D11.h, MIT licence); no NVIDIA binary is bundled - the DLL is loaded from the driver at run time and every
// failure (no NVIDIA GPU, old driver, unsupported size) simply reports "unavailable" so that the caller falls back to its own search.
internal sealed class NvOfSession : IDisposable
{
    // ---- native layouts (nvOpticalFlowCommon.h, API 5.0) ----
    private const uint ApiVersion = 0x50;                       // (5 << 4) | 0
    private const int BufUsageInput = 1, BufUsageOutput = 2;
    private const int ModeOpticalFlow = 1;
    private const int PredForward = 0, PredBoth = 2;
    private const int FmtAbgr8 = 3;                              // DXGI B8G8R8A8_UNORM
    private const int PerfSlow = 5, PerfMedium = 10, PerfFast = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct InitParams
    {
        public uint Width, Height;
        public int OutGridSize, HintGridSize, Mode, PerfLevel;
        public int EnableExternalHints, EnableOutputCost;
        public IntPtr PrivData;
        public int DisparityRange, EnableRoi, PredDirection, EnableGlobalFlow, InputBufferFormat;
        public int Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExecIn
    {
        public IntPtr InputFrame, ReferenceFrame, ExternalHints;
        public int DisableTemporalHints, Padding;
        public IntPtr PrivData;
        public int Padding2, NumRois;
        public IntPtr RoiData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExecOut
    {
        public IntPtr Output, OutputCost, PrivData, BwdOutput, BwdOutputCost, GlobalFlow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FnList
    {
        public IntPtr Create, Init, GetFormatCount, GetFormat, Register, Unregister, Execute, Destroy, GetLastError, GetCaps;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateInstanceFn(uint apiVer, ref FnList list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MaxVersionFn(out uint version);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateFn(IntPtr device, IntPtr context, out IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitFn(IntPtr h, ref InitParams p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegisterFn(IntPtr h, IntPtr resource, out IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnregisterFn(IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ExecuteFn(IntPtr h, ref ExecIn i, ref ExecOut o);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DestroyFn(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCapsFn(IntPtr h, int cap, uint[] vals, ref uint size);

    private static IntPtr _lib;
    private static bool _libTried;
    private static CreateInstanceFn? _createInstance;
    private static uint _maxVersion;

    /// <summary>Why the last attempt to use the engine failed ("" when it did not).</summary>
    public static string LastFailure { get; private set; } = string.Empty;

    private FnList _fn;
    private CreateFn _create = null!;
    private InitFn _init = null!;
    private RegisterFn _register = null!;
    private UnregisterFn _unregister = null!;
    private ExecuteFn _execute = null!;
    private DestroyFn _destroy = null!;
    private GetCapsFn _getCaps = null!;
    private IntPtr _h;
    private readonly System.Collections.Generic.List<IntPtr> _buffers = new();
    private IntPtr _outFwd, _outBwd;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Grid { get; private set; }
    public bool Bidirectional { get; private set; }
    /// <summary>Flow vectors, DXGI R16G16_SINT, S10.5 fixed point (value / 32 = pixels), one per Grid x Grid pixels of the input.</summary>
    public ID3D11Texture2D FwdTex { get; private set; } = null!;
    public ID3D11Texture2D? BwdTex { get; private set; }
    public Size2 OutSize { get; private set; }

    public readonly record struct Size2(int W, int H);

    private static bool LoadLibraryOnce()
    {
        if (_libTried) return _lib != IntPtr.Zero;
        _libTried = true;
        try
        {
            if (!NativeLibrary.TryLoad("nvofapi64.dll", out _lib)) { LastFailure = "nvofapi64.dll not found (no NVIDIA driver with Optical Flow)"; return false; }
            if (!NativeLibrary.TryGetExport(_lib, "NvOFAPICreateInstanceD3D11", out IntPtr ci)) { LastFailure = "NvOFAPICreateInstanceD3D11 missing"; return false; }
            _createInstance = Marshal.GetDelegateForFunctionPointer<CreateInstanceFn>(ci);
            if (NativeLibrary.TryGetExport(_lib, "NvOFGetMaxSupportedApiVersion", out IntPtr mv))
            {
                var f = Marshal.GetDelegateForFunctionPointer<MaxVersionFn>(mv);
                if (f(out _maxVersion) != 0) _maxVersion = 0;
            }
            if (_maxVersion < ApiVersion) { LastFailure = $"driver supports optical flow API 0x{_maxVersion:X} only (0x{ApiVersion:X} needed)"; return false; }
            return true;
        }
        catch (Exception ex) { LastFailure = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>Open the engine on <paramref name="device"/> for input pictures of w x h pixels. Null (and
    /// <see cref="LastFailure"/>) when the GPU / driver cannot do it.</summary>
    public static NvOfSession? TryCreate(ID3D11Device device, ID3D11DeviceContext ctx, int w, int h, int grid, bool bidirectional, int perf = 0)
    {
        LastFailure = string.Empty;
        if (!LoadLibraryOnce()) return null;
        var s = new NvOfSession();
        try
        {
            if (!s.Open(device, ctx, w, h, grid, bidirectional, perf)) { s.Dispose(); return null; }
            return s;
        }
        catch (Exception ex)
        {
            LastFailure = ex.GetType().Name + ": " + ex.Message;
            s.Dispose();
            return null;
        }
    }

    private bool Fail(string what, int status)
    {
        LastFailure = $"{what} failed (NV_OF_STATUS {status})";
        return false;
    }

    private bool Open(ID3D11Device device, ID3D11DeviceContext ctx, int w, int h, int grid, bool bidir, int perf)
    {
        _fn = default;
        int st = _createInstance!(ApiVersion, ref _fn);
        if (st != 0) return Fail("NvOFAPICreateInstanceD3D11", st);
        _create = Marshal.GetDelegateForFunctionPointer<CreateFn>(_fn.Create);
        _init = Marshal.GetDelegateForFunctionPointer<InitFn>(_fn.Init);
        _register = Marshal.GetDelegateForFunctionPointer<RegisterFn>(_fn.Register);
        _unregister = Marshal.GetDelegateForFunctionPointer<UnregisterFn>(_fn.Unregister);
        _execute = Marshal.GetDelegateForFunctionPointer<ExecuteFn>(_fn.Execute);
        _destroy = Marshal.GetDelegateForFunctionPointer<DestroyFn>(_fn.Destroy);
        _getCaps = Marshal.GetDelegateForFunctionPointer<GetCapsFn>(_fn.GetCaps);

        st = _create(device.NativePointer, ctx.NativePointer, out _h);
        if (st != 0 || _h == IntPtr.Zero) return Fail("nvCreateOpticalFlowD3D11", st);

        // supported output grid sizes: a bit mask (1, 2, 4)
        uint[] caps = new uint[8]; uint cs = 8;
        if (_getCaps(_h, 0, caps, ref cs) == 0 && cs > 0)
        {
            uint mask = 0; for (int i = 0; i < Math.Min(cs, 8u); i++) mask |= caps[i];
            SupportedGrids = mask;
        }
        else SupportedGrids = 0;
        if (SupportedGrids != 0)
        {
            // the caps value is a list of the sizes (1/2/4) or a bit mask depending on the API revision; accept both
            bool has(int g) => (SupportedGrids & (1u << (g - 1))) != 0 || Array.IndexOf(caps, (uint)g, 0, (int)Math.Min(cs, 8u)) >= 0;
            if (!has(grid)) return Fail($"output grid {grid} not supported (caps {SupportedGrids:X})", 0);
        }

        var ip = new InitParams
        {
            Width = (uint)w, Height = (uint)h, OutGridSize = grid, HintGridSize = 0, Mode = ModeOpticalFlow,
            PerfLevel = perf == 0 ? PerfSlow : perf, EnableExternalHints = 0, EnableOutputCost = 0, PrivData = IntPtr.Zero,
            DisparityRange = 0, EnableRoi = 0, PredDirection = bidir ? PredBoth : PredForward, EnableGlobalFlow = 0, InputBufferFormat = FmtAbgr8,
        };
        st = _init(_h, ref ip);
        if (st != 0) return Fail("nvOFInit", st);
        Width = w; Height = h; Grid = grid; Bidirectional = bidir;
        OutSize = new Size2((w + grid - 1) / grid, (h + grid - 1) / grid);
        FwdTex = MakeOut(device, out _outFwd);
        if (_outFwd == IntPtr.Zero) return false;
        if (bidir)
        {
            BwdTex = MakeOut(device, out _outBwd);
            if (_outBwd == IntPtr.Zero) return false;
        }
        return true;
    }

    public uint SupportedGrids { get; private set; }

    private ID3D11Texture2D MakeOut(ID3D11Device device, out IntPtr reg)
    {
        var t = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)OutSize.W, Height = (uint)OutSize.H, MipLevels = 1, ArraySize = 1, Format = Format.R16G16_SInt,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        });
        reg = Register(t);
        return t;
    }

    /// <summary>Make an input picture (B8G8R8A8_UNorm, Width x Height) known to the engine. IntPtr.Zero on failure.</summary>
    public IntPtr Register(ID3D11Texture2D tex)
    {
        int st = _register(_h, tex.NativePointer, out IntPtr buf);
        if (st != 0 || buf == IntPtr.Zero) { Fail("nvOFRegisterResourceD3D11", st); return IntPtr.Zero; }
        _buffers.Add(buf);
        return buf;
    }

    public void Unregister(IntPtr buf)
    {
        if (buf == IntPtr.Zero || _h == IntPtr.Zero || !_buffers.Contains(buf)) return;    // already gone with the session
        try { _unregister(buf); } catch { }
        _buffers.Remove(buf);
    }

    /// <summary>Flow of <paramref name="input"/> against <paramref name="reference"/> into <see cref="FwdTex"/> (and the
    /// reverse into <see cref="BwdTex"/> when bidirectional). The call is queued on the engine; D3D11 orders later
    /// reads of the output after it.</summary>
    public bool Execute(IntPtr input, IntPtr reference)
    {
        var i = new ExecIn { InputFrame = input, ReferenceFrame = reference, DisableTemporalHints = 1 };
        var o = new ExecOut { Output = _outFwd, BwdOutput = Bidirectional ? _outBwd : IntPtr.Zero };
        int st = _execute(_h, ref i, ref o);
        if (st != 0) return Fail("nvOFExecute", st);
        return true;
    }

    public void Dispose()
    {
        try
        {
            foreach (IntPtr b in _buffers.ToArray()) try { _unregister(b); } catch { }
            _buffers.Clear();
            if (_h != IntPtr.Zero) { try { _destroy(_h); } catch { } _h = IntPtr.Zero; }
        }
        catch { }
        FwdTex?.Dispose(); BwdTex?.Dispose();
    }
}
