using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Linq;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using SharpGen.Runtime;
using Box = Vortice.Mathematics.Box;
using Color4 = Vortice.Mathematics.Color4;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace YuCap;

/// <summary>Black-border crop arithmetic, shared by the renderer and the engine so that the size the UI
/// lays out (<see cref="D3DVideoEngine.DisplayResolution"/>) is exactly the size the convert pass draws.</summary>
internal static class CropMath
{
    public const int MinSide = 64;

    /// <summary>The kept sub-rectangle of a <paramref name="src"/>-sized frame: each requested margin is
    /// limited so that at least 64x64 pixels remain (left and top win over right and bottom). A source
    /// already smaller than that is left whole.</summary>
    public static Rectangle Effective(Size src, System.Windows.Forms.Padding crop)
    {
        (int x, int w) = Axis(src.Width, crop.Left, crop.Right);
        (int y, int h) = Axis(src.Height, crop.Top, crop.Bottom);
        return new Rectangle(x, y, w, h);
    }

    private static (int start, int len) Axis(int size, int near, int far)
    {
        int room = size - MinSide;
        if (room <= 0) return (0, size);
        near = Math.Clamp(near, 0, room);
        far = Math.Clamp(far, 0, room - near);
        return (near, size - near - far);
    }
}

/// <summary>Colour interpretation of the incoming NV12 frames.</summary>
internal readonly record struct YuvInfo(int Matrix, bool FullRange);

/// <summary>
/// All GPU state of the Direct3D renderer. Everything except the two frame-upload methods
/// is touched only by the render thread; the upload methods are called by the capture
/// thread and are serialised with the convert pass by <see cref="FrameLock"/> (the device is
/// multithread-protected, so the immediate context tolerates the cross-thread calls).
/// </summary>
internal sealed partial class D3DRenderer : IDisposable
{
    public readonly object FrameLock = new();

    public ID3D11Device Device = null!;
    public ID3D11DeviceContext Ctx = null!;

    // ---- presentation ----
    private IDXGISwapChain1? _swap;
    private IDXGISwapChain2? _swap2;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _dcompTarget;
    private IDCompositionVisual? _dcompVisual;
    private Size _swapSize;
    public IntPtr LatencyWaitable { get; private set; }
    /// <summary>How frames reach the screen: "composition vsync" (DirectComposition, the normal path),
    /// "hwnd tearing" (window swap chain, sync interval 0, tearing allowed - low latency on a system
    /// that supports it), "hwnd sync0" (window swap chain, sync interval 0, no tearing) or
    /// "hwnd vsync" (the fallback when DirectComposition cannot be used).</summary>
    public string PresentMode { get; private set; } = "?";
    private SwapChainFlags _swapFlags = SwapChainFlags.FrameLatencyWaitableObject;
    private PresentFlags _presentFlags = PresentFlags.None;
    private uint _syncInterval = 1;

    // ---- shaders / states / constant buffers ----
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psConvert, _psScale, _psEasu, _psRcas, _psOverlay;
    private ID3D11BlendState? _blendOver;
    private ID3D11PixelShader? _psPre;       // "match the original detail" downscale (null = unavailable)
    private ID3D11Buffer? _cbPre;
    private ID3D11PixelShader? _psIbp;           // back-projection refinement of the shrunk picture
    private ID3D11Texture2D?[] _pf = new ID3D11Texture2D?[2];   // float16 ping-pong textures of the refinement
    private ID3D11ShaderResourceView?[] _pfSrv = new ID3D11ShaderResourceView?[2];
    private ID3D11RenderTargetView?[] _pfRtv = new ID3D11RenderTargetView?[2];
    private Size _pfSize;
    private ID3D11PixelShader? _psPyr1, _psPyrD, _psMe, _psMeCost, _psCut, _psMed, _psSynth;   // frame interpolation (null = unavailable)
    private ID3D11SamplerState? _linear, _point;
    private ID3D11RasterizerState? _raster;
    private ID3D11Buffer? _cbConvert, _cbScale, _cbEasu, _cbRcas, _cbInterp;
    private readonly IntPtr _scratch = Marshal.AllocHGlobal(256);
    private GpuTimer? _gpu;                       // GPU time per presented frame (render thread only)

    // ---- per-frame input (guarded by FrameLock) ----
    private int _fw, _fh;
    private bool _gpuFrame;                       // true: frame lives in _nv12, false: in _texY/_texUV
    private ID3D11Texture2D? _nv12;
    private ID3D11ShaderResourceView? _nv12Y, _nv12UV;
    private ID3D11Texture2D? _texY, _texUV;
    private ID3D11ShaderResourceView? _srvY, _srvUV;
    private YuvInfo _yuv = new(1, false);
    private bool _hasFrame;

    // ---- render-thread textures ----
    private ID3D11Texture2D? _oriented;
    private ID3D11ShaderResourceView? _orientedSrv;
    private ID3D11RenderTargetView? _orientedRtv;
    private Size _orientedSize;
    private ID3D11Texture2D? _pre;                // oriented picture shrunk to its real detail (render thread)
    private ID3D11ShaderResourceView? _preSrv;
    private ID3D11RenderTargetView? _preRtv;
    private Size _preSize;
    private ID3D11Texture2D? _inter;              // FSR intermediate, client sized
    private ID3D11ShaderResourceView? _interSrv;
    private ID3D11RenderTargetView? _interRtv;
    private ID3D11Texture2D? _lastComposed;       // copy of what was last presented
    private Rectangle _composedDst;               // destination rectangle of that frame

    // ---- video delay ring (guarded by FrameLock) ----
    // Used only while a video delay is set (A/V sync, "audio earlier"). Each slot is a full
    // copy of one received frame plus the Stopwatch tick at which the capture thread got it.
    // With no delay none of this exists and frames go straight into the single set of frame
    // textures above, exactly as before. The render thread decides the ring's size and frees
    // it; slot textures are created on first use by whichever thread writes the slot (the
    // device is multithread-protected, as for the frame textures).
    private sealed class Slot
    {
        public ID3D11Texture2D? Nv12, Y, UV;
        public int W, H;
        public bool Gpu;
        public YuvInfo Yuv;
        public long Arrival;
        public long Smooth;                    // phase-locked arrival (Stopwatch ticks), see Session.TrackSmooth
        public long Seq;                       // 0 = empty
        public ID3D11ShaderResourceView? YSrv; // luma view for the same-picture pass
        public long FrameNo;                   // number the same-picture pass gave the frame (0 = none)
        public bool Dup, Resolved;             // the frame repeats the picture before it / that is known
        public long U;                         // time on the unique-picture timeline (= Smooth until resolved)
        public void Free()
        {
            YSrv?.Dispose(); YSrv = null;
            Nv12?.Dispose(); Nv12 = null; Y?.Dispose(); Y = null; UV?.Dispose(); UV = null;
            Seq = 0; W = H = 0; FrameNo = 0; Dup = Resolved = false;
        }
    }
    private readonly List<Slot> _ring = new();
    private int _ringNeed;                     // slots wanted; 0 = delay not in use
    private long _ringSeq, _shownSeq;

    public bool HasFrame { get { lock (FrameLock) return _hasFrame; } }
    public Size FrameSize { get { lock (FrameLock) return new Size(_fw, _fh); } }
    public bool HasComposed => _lastComposed != null && _composedValid;
    private bool _composedValid;
    public Size SwapSize => _swapSize;

    [DllImport("kernel32.dll")]
    private static extern void RtlMoveMemory(IntPtr dst, IntPtr src, UIntPtr len);

    // =====================================================================
    //  Initialisation
    // =====================================================================

    public void CreateDevice()
    {
        // BGRA for the swap chain / D2D-style formats, VIDEO_SUPPORT because the source
        // reader's DXGI device manager hands our device to the decoder / video processor.
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3DFeatureLevel[] levels = { D3DFeatureLevel.Level_11_1, D3DFeatureLevel.Level_11_0 };
        Result hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels,
            out ID3D11Device? dev, out D3DFeatureLevel got, out ID3D11DeviceContext? ctx);
        if (hr.Failure || dev == null || ctx == null)
        {
            // Some systems reject 11_1 in the list (older runtimes): retry with 11_0 only.
            hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, new[] { D3DFeatureLevel.Level_11_0 },
                out dev, out got, out ctx);
            if (hr.Failure || dev == null || ctx == null)
                throw new UserFacingException(L.T("Direct3D 11 を初期化できません。"));
        }
        Device = dev;
        Ctx = ctx;
        using (ID3D11Multithread mt = Device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);
        Log.Info($"D3D: device created, feature level {got}");
    }

    public void CreateShaders()
    {
        D3DShaders.Bytecode b = D3DShaders.Get();
        _vs = Device.CreateVertexShader(b.Vs);
        _psConvert = Device.CreatePixelShader(b.Convert);
        _psScale = Device.CreatePixelShader(b.Scale);
        _psOverlay = Device.CreatePixelShader(b.Overlay);
        _blendOver = Device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        _psEasu = Device.CreatePixelShader(b.Easu);
        _psRcas = Device.CreatePixelShader(b.Rcas);
        CreateHdrShaders(b);

        _linear = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _point = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _raster = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _cbConvert = Device.CreateBuffer(new BufferDescription(112, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbScale = Device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbEasu = Device.CreateBuffer(new BufferDescription(96, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbRcas = Device.CreateBuffer(new BufferDescription(96, BindFlags.ConstantBuffer, ResourceUsage.Default));
        if (b.PrescaleOk)
        {
            try
            {
                _psPre = Device.CreatePixelShader(b.Prescale);
                _psIbp = Device.CreatePixelShader(b.PrescaleIbp);
                _cbPre = Device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Default));
            }
            catch (Exception ex)
            {
                Log.Info("D3D: prescale shader could not be created: " + ex.Message.Split('\n')[0]);
                _psPre?.Dispose(); _psPre = null; _psIbp?.Dispose(); _psIbp = null; _cbPre?.Dispose(); _cbPre = null;
            }
        }

        if (b.InterpOk)
        {
            try
            {
                _psPyr1 = Device.CreatePixelShader(b.PyrLuma);
                _psPyrD = Device.CreatePixelShader(b.PyrDown);
                _psMe = Device.CreatePixelShader(b.Me);
                _psMeCost = Device.CreatePixelShader(b.MeCost);
                _psCut = Device.CreatePixelShader(b.CutStat);
                _psMed = Device.CreatePixelShader(b.Med);
                _psSynth = Device.CreatePixelShader(b.Synth);
                _cbInterp = Device.CreateBuffer(new BufferDescription(64, BindFlags.ConstantBuffer, ResourceUsage.Default));
                CreateOfaShaders(b);
                CreateHudShader(b);
            }
            catch (Exception ex)
            {
                Log.Info("D3D: interpolation shaders could not be created - frame interpolation is disabled: " + ex.Message.Split('\n')[0]);
                _psPyr1?.Dispose(); _psPyrD?.Dispose(); _psMe?.Dispose(); _psMeCost?.Dispose(); _psCut?.Dispose(); _psMed?.Dispose(); _psSynth?.Dispose();
                _psPyr1 = _psPyrD = _psMe = _psMeCost = _psCut = _psMed = _psSynth = null;
                _cbInterp?.Dispose(); _cbInterp = null;
            }
        }

        DupInit(b);

        try { _gpu = new GpuTimer(Device, Ctx); _gpu.StopCollecting(); }   // live: keep only the 1 s window
        catch (Exception ex) { Log.Info("D3D: GPU timestamp queries unavailable: " + ex.Message.Split('\n')[0]); }
    }

    /// <summary>Open the GPU-time span of one frame (the convert pass belongs to it). PresentFrame
    /// closes it; calling this again while a span is open does nothing.</summary>
    public void GpuBegin() => _gpu?.Begin();

    /// <summary>Average and maximum GPU milliseconds per frame over about the last second.</summary>
    public void GpuRecent(out double avg, out double max)
    {
        avg = max = 0;
        if (_gpu == null) return;
        _gpu.Poll();
        _gpu.Recent(out avg, out max);
    }

    /// <summary>The adapter the device lives on (for the benchmark report).</summary>
    public string AdapterName
    {
        get
        {
            try
            {
                using IDXGIDevice dxgi = Device.QueryInterface<IDXGIDevice>();
                using IDXGIAdapter a = dxgi.GetAdapter();
                return a.Description.Description;
            }
            catch { return "?"; }
        }
    }

    /// <summary>Create the swap chain for <paramref name="hwnd"/> and bind it to the window.
    ///
    /// With <paramref name="lowLatency"/> the swap chain is a plain window (HWND) one and every frame
    /// is presented at once with sync interval 0, using DXGI_PRESENT_ALLOW_TEARING when the system
    /// supports it - that is what lets a borderless fullscreen window go independent-flip / VRR.
    /// Tearing is not available on a DirectComposition swap chain, hence the different path; the kind
    /// of swap chain is therefore fixed for the life of a session, and switching means a restart on a
    /// fresh window (a window that ever had a composition target cannot be trusted to take a window
    /// swap chain, nor the other way round - not verified, so not attempted in place).
    ///
    /// UNVERIFIED: whether the HWND path keeps YuCap's sibling child windows (the OSD, the info
    /// overlay and the pause image, all siblings stacked above the canvas) visible on top of the
    /// video. With DirectComposition the picture lives inside the canvas window's own visual tree;
    /// a flip-model window swap chain on a child window is composed by DWM on its own. This is part
    /// of the user's test plan; if they report those windows hidden under the video, low latency
    /// will need another approach (e.g. a layered overlay window).</summary>
    public void CreatePresentation(IntPtr hwnd, Size client, bool lowLatency = false)
    {
        _swapSize = client;
        _presentHwnd = hwnd;
        using IDXGIDevice1 dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        dxgiDevice.SetMaximumFrameLatency(1);

        SwapChainDescription1 desc = new()
        {
            Width = (uint)client.Width,
            Height = (uint)client.Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject,
        };

        if (lowLatency)
        {
            if (SupportsTearing(factory))
            {
                try
                {
                    desc.Flags = SwapChainFlags.FrameLatencyWaitableObject | SwapChainFlags.AllowTearing;
                    _swap = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
                    _swapFlags = desc.Flags;
                    _presentFlags = PresentFlags.AllowTearing;
                    PresentMode = "hwnd tearing";
                }
                catch (Exception ex)
                {
                    Log.Info($"D3D: tearing swap chain failed ({ex.Message.Split('\n')[0]}, 0x{ex.HResult:X8}) - using sync interval 0 without tearing");
                    _swap = null;
                }
            }
            if (_swap == null)
            {
                desc.Flags = SwapChainFlags.FrameLatencyWaitableObject;
                _swap = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
                _swapFlags = desc.Flags;
                _presentFlags = PresentFlags.None;
                PresentMode = "hwnd sync0";
            }
            _syncInterval = 0;
        }
        else
        {
            _syncInterval = 1;
            _presentFlags = PresentFlags.None;
            _swapFlags = SwapChainFlags.FrameLatencyWaitableObject;
            // Display-driven pacing renders right after each refresh; a third buffer keeps that work
            // from waiting for the buffer the display is still reading.
            desc.BufferCount = 3;
            _swap = factory.CreateSwapChainForComposition(Device, desc);
            try
            {
                _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
                _dcomp.CreateTargetForHwnd(hwnd, false, out _dcompTarget);
                _dcomp.CreateVisual(out _dcompVisual);
                _dcompVisual.SetContent(_swap);
                _dcompTarget.SetRoot(_dcompVisual);
                _dcomp.Commit();
                PresentMode = "composition vsync";
            }
            catch (Exception ex)
            {
                const int WindowAlreadyComposed = unchecked((int)0x88980800);
                DisposeComposition();
                _swap.Dispose();
                _swap = null;
                if (ex.HResult == WindowAlreadyComposed)
                {
                    Log.Info("D3D: the window already has a DirectComposition target (DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED) — the host window must be recreated before attaching this renderer");
                    throw new WindowAlreadyComposedException(L.T("このウィンドウは別の描画方式で使われています。映像ウィンドウを作り直す必要があります。"));
                }
                Log.Info($"D3D: DirectComposition binding failed ({ex.Message.Split('\n')[0]}, 0x{ex.HResult:X8}) — falling back to a window swap chain");
                desc.AlphaMode = AlphaMode.Ignore;
                _swap = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
                PresentMode = "hwnd vsync";
            }
        }

        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        _swap2 = _swap.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (_swap2 != null)
        {
            _swap2.MaximumFrameLatency = 1;
            LatencyWaitable = _swap2.FrameLatencyWaitableObject;
        }
        Log.Info($"D3D: present mode = {PresentMode}, {client.Width}x{client.Height}");
    }

    private static bool SupportsTearing(IDXGIFactory2 factory)
    {
        try
        {
            using IDXGIFactory5? f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>();
            return f5 != null && f5.PresentAllowTearing;
        }
        catch (Exception ex)
        {
            Log.Info("D3D: tearing support query failed: " + ex.Message.Split('\n')[0]);
            return false;
        }
    }

    private void DisposeComposition()
    {
        _dcompVisual?.Dispose(); _dcompVisual = null;
        _dcompTarget?.Dispose(); _dcompTarget = null;
        _dcomp?.Dispose(); _dcomp = null;
    }

    // =====================================================================
    //  Frame input (capture thread)
    // =====================================================================

    private void EnsureFrameTextures(int w, int h, bool gpu)
    {
        if (_fw != w || _fh != h)
        {
            DisposeFrameTextures();
            _fw = w; _fh = h;
        }
        if (gpu && _nv12 == null)
        {
            _nv12 = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            _nv12Y = Device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription
            {
                Format = Format.R8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            });
            _nv12UV = Device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            });
        }
        if (!gpu && _texY == null)
        {
            _texY = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.R8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            _texUV = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(w / 2), Height = (uint)(h / 2), MipLevels = 1, ArraySize = 1,
                Format = Format.R8G8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            _srvY = Device.CreateShaderResourceView(_texY);
            _srvUV = Device.CreateShaderResourceView(_texUV);
        }
    }

    private void DisposeFrameTextures()
    {
        _nv12Y?.Dispose(); _nv12Y = null;
        _nv12UV?.Dispose(); _nv12UV = null;
        _nv12?.Dispose(); _nv12 = null;
        _srvY?.Dispose(); _srvY = null;
        _srvUV?.Dispose(); _srvUV = null;
        _texY?.Dispose(); _texY = null;
        _texUV?.Dispose(); _texUV = null;
    }

    /// <summary>GPU path: copy the decoder/processor's NV12 texture into ours.</summary>
    public void SubmitGpuFrame(ID3D11Texture2D src, uint subresource, int w, int h, YuvInfo yuv, long arrival, long smooth)
    {
        SubmitGpuFrameLocked(src, subresource, w, h, yuv, arrival, smooth);
        DupFlush();
    }

    private void SubmitGpuFrameLocked(ID3D11Texture2D src, uint subresource, int w, int h, YuvInfo yuv, long arrival, long smooth)
    {
        lock (FrameLock)
        {
            if (_ringNeed > 0)
            {
                Slot s = AcquireSlot(w, h, gpu: true);
                Ctx.CopySubresourceRegion(s.Nv12!, 0, 0, 0, 0, src, subresource, new Box(0, 0, 0, w, h, 1));
                long fn = DupSubmit(s.YSrv, w, h, arrival, smooth);
                Stamp(s, yuv, arrival, smooth, fn);
                return;
            }
            EnsureFrameTextures(w, h, gpu: true);
            Ctx.CopySubresourceRegion(_nv12!, 0, 0, 0, 0, src, subresource, new Box(0, 0, 0, w, h, 1));
            DupSubmit(_nv12Y, w, h, arrival, smooth);
            Volatile.Write(ref _latestArrival, arrival);
            _gpuFrame = true;
            _yuv = yuv;
            _hasFrame = true;
        }
    }

    /// <summary>CPU path: upload NV12 from memory (Y plane, then the interleaved UV plane).</summary>
    public void SubmitCpuFrame(IntPtr scan0, int pitch, int w, int h, YuvInfo yuv, long arrival, long smooth)
    {
        SubmitCpuFrameLocked(scan0, pitch, w, h, yuv, arrival, smooth);
        DupFlush();
    }

    private void SubmitCpuFrameLocked(IntPtr scan0, int pitch, int w, int h, YuvInfo yuv, long arrival, long smooth)
    {
        lock (FrameLock)
        {
            if (_ringNeed > 0)
            {
                Slot s = AcquireSlot(w, h, gpu: false);
                Ctx.UpdateSubresource(s.Y!, 0, null, scan0, (uint)pitch, 0);
                Ctx.UpdateSubresource(s.UV!, 0, null, scan0 + pitch * h, (uint)pitch, 0);
                long fn = DupSubmit(s.YSrv, w, h, arrival, smooth);
                Stamp(s, yuv, arrival, smooth, fn);
                return;
            }
            EnsureFrameTextures(w, h, gpu: false);
            Ctx.UpdateSubresource(_texY!, 0, null, scan0, (uint)pitch, 0);
            Ctx.UpdateSubresource(_texUV!, 0, null, scan0 + pitch * h, (uint)pitch, 0);
            DupSubmit(_srvY, w, h, arrival, smooth);
            Volatile.Write(ref _latestArrival, arrival);
            _gpuFrame = false;
            _yuv = yuv;
            _hasFrame = true;
        }
    }

    // =====================================================================
    //  Video delay ring
    // =====================================================================

    /// <summary>Render thread: size the ring for <paramref name="delayMs"/> at <paramref name="fps"/>
    /// (fps x delay + 3 slots, so the frame that is due always still exists while newer ones
    /// keep arriving, plus one frame of backlog), or free it when the delay is 0. The ring only grows while a delay is
    /// active, so dragging a slider does not reallocate textures at every step.
    /// Returns true when the active/inactive state or the size changed.</summary>
    public bool ConfigureRing(int delayMs, int fps)
    {
        lock (FrameLock)
        {
            if (delayMs <= 0)
            {
                if (_ringNeed == 0 && _ring.Count == 0) return false;
                FreeRing();
                return true;
            }
            int need = (int)Math.Ceiling((fps > 0 ? fps : 60) * delayMs / 1000.0) + 3 + Volatile.Read(ref _ringExtra);
            if (need == _ringNeed) return false;
            if (_ringNeed == 0) _shownSeq = _ringSeq;   // nothing in the ring is "pending" yet
            _ringNeed = need;
            return true;
        }
    }

    private int _ringExtra;
    /// <summary>Test mode: keep this many more frames in the ring than the delay needs, so the live hold-out check can
    /// look back over about half a second of frames. Applied at the next ConfigureRing (every render loop).</summary>
    public void SetRingExtra(int frames) => Volatile.Write(ref _ringExtra, Math.Clamp(frames, 0, 120));

    public int RingSlots { get { lock (FrameLock) return _ringNeed; } }

    private void FreeRing()
    {
        foreach (Slot s in _ring) s.Free();
        _ring.Clear();
        _ringNeed = 0;
    }

    /// <summary>The slot to write next: a new one while the ring is filling, else the oldest.</summary>
    private Slot AcquireSlot(int w, int h, bool gpu)
    {
        Slot slot;
        if (_ring.Count < _ringNeed) { slot = new Slot(); _ring.Add(slot); }
        else
        {
            slot = _ring[0];
            foreach (Slot s in _ring) if (s.Seq < slot.Seq) slot = s;
            if (!_interpHold && slot.Seq > _shownSeq) _ringDropped++;   // overwritten before its turn (interpolation counts what it skipped itself)
        }
        slot.FrameNo = 0; slot.Dup = slot.Resolved = false;          // the old picture's answer must not land on the new one
        if (slot.W != w || slot.H != h || slot.Gpu != gpu || (gpu ? slot.Nv12 : slot.Y) == null)
        {
            slot.Free();
            slot.W = w; slot.H = h; slot.Gpu = gpu;
            if (gpu)
            {
                slot.Nv12 = Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });
                slot.YSrv = Device.CreateShaderResourceView(slot.Nv12, new ShaderResourceViewDescription
                {
                    Format = Format.R8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
                });
            }
            else
            {
                slot.Y = Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.R8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });
                slot.UV = Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)(w / 2), Height = (uint)(h / 2), MipLevels = 1, ArraySize = 1,
                    Format = Format.R8G8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });
                slot.YSrv = Device.CreateShaderResourceView(slot.Y);
            }
        }
        return slot;
    }

    private void Stamp(Slot s, YuvInfo yuv, long arrival, long smooth, long frameNo)
    {
        s.Yuv = yuv;
        s.Arrival = arrival;
        s.Smooth = smooth;
        s.U = smooth;                          // until the same-picture answer comes: a new picture at its own time
        s.FrameNo = frameNo; s.Dup = s.Resolved = false;
        s.Seq = ++_ringSeq;
    }

    /// <summary>Stopwatch tick at which the oldest not-yet-shown frame becomes due, or
    /// <see cref="long.MaxValue"/> when nothing is waiting.</summary>
    public long NextDueTicks(long delayTicks)
    {
        lock (FrameLock)
        {
            long best = long.MaxValue;
            foreach (Slot s in _ring)
                if (s.Seq > _shownSeq && s.Arrival < best) best = s.Arrival;
            return best == long.MaxValue ? best : best + delayTicks;
        }
    }

    public bool HasDueFrame(long nowTicks, long delayTicks)
    {
        lock (FrameLock)
        {
            foreach (Slot s in _ring)
                if (s.Seq > _shownSeq && s.Arrival <= nowTicks - delayTicks) return true;
            return false;
        }
    }

    private long _ringDropped;
    private readonly List<Slot> _elig = new();

    /// <summary>Frames the ring lost (skipped or overwritten before being shown) since the last call.</summary>
    public long TakeRingDropped()
    {
        lock (FrameLock) { long d = _ringDropped; _ringDropped = 0; return d; }
    }

    /// <summary>Make a frame whose arrival is at least the delay ago the current one (GPU-side copy
    /// into the frame textures that Convert reads). It is the OLDEST such frame that has not been
    /// shown, so a late frame followed by an on-time one costs a frame of backlog instead of a
    /// repeated and a dropped frame (a small jitter buffer); only when more than two are waiting
    /// is the surplus skipped, leaving one behind. With a source much faster than the display this
    /// shows every Nth frame, as expected.</summary>
    public bool AdvanceDelayed(long nowTicks, long delayTicks, out long arrival)
    {
        arrival = 0;
        lock (FrameLock)
        {
            _elig.Clear();
            foreach (Slot s in _ring)
                if (s.Seq > _shownSeq && s.Arrival <= nowTicks - delayTicks) _elig.Add(s);
            int c = _elig.Count;
            if (c == 0) return false;
            _elig.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            int idx = c >= 3 ? c - 2 : 0;
            Slot best = _elig[idx];
            _ringDropped += idx;

            EnsureFrameTextures(best.W, best.H, best.Gpu);
            if (best.Gpu) Ctx.CopyResource(_nv12!, best.Nv12!);
            else { Ctx.CopyResource(_texY!, best.Y!); Ctx.CopyResource(_texUV!, best.UV!); }
            _gpuFrame = best.Gpu;
            _yuv = best.Yuv;
            _hasFrame = true;
            _shownSeq = best.Seq;
            arrival = best.Arrival;
            return true;
        }
    }

    /// <summary>True when frames are presented once per refresh (vsync) through a swap chain
    /// that offers a frame-latency waitable; false for the tearing / sync-0 path.</summary>
    public bool VsyncPaced => _syncInterval != 0 && LatencyWaitable != IntPtr.Zero;

    // =====================================================================
    //  Render passes (render thread)
    // =====================================================================

    private static Size Oriented(Size src, int rotation) =>
        rotation is 90 or 270 ? new Size(src.Height, src.Width) : src;

    private void WriteCb<T>(ID3D11Buffer cb, T value) where T : struct
    {
        Marshal.StructureToPtr(value, _scratch, false);
        Ctx.UpdateSubresource(cb, 0, null, _scratch, 0, 0);
    }

    private void WriteCb(ID3D11Buffer cb, uint[] words)
    {
        Marshal.Copy((int[])(Array)words, 0, _scratch, words.Length);
        Ctx.UpdateSubresource(cb, 0, null, _scratch, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConvertCb
    {
        public int SrcW, SrcH, OutW, OutH;
        public int Rot, Mirror, P0, P1;
        public Vector4 Coef;
        public Vector4 Range;
        public Vector4 Adj;
        public int CropL, CropT, CropR, CropB;   // CropR/CropB: chroma upsampling mode / variant (convert.hlsl crop.z / crop.w)
        public Vector4 Tune;
    }

    private void SetupTriangle(ID3D11RenderTargetView rtv, Rectangle viewport)
    {
        Ctx.OMSetRenderTargets(rtv);
        Ctx.RSSetViewport(viewport.X, viewport.Y, viewport.Width, viewport.Height);
        Ctx.RSSetState(_raster);
        Ctx.IASetInputLayout(null);
        Ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Ctx.VSSetShader(_vs);
    }

    /// <summary>Pass (a): NV12 to RGB with rotation and mirror into the oriented texture.
    /// Returns false if there is no frame yet. While frame interpolation is active the oriented
    /// texture is produced by <see cref="InterpUpdate"/>; this then only makes sure it is current.</summary>
    public bool Convert(int rotation, bool mirror, VideoAdjustments adj, System.Windows.Forms.Padding crop)
    {
        lock (FrameLock)
        {
            if (_interpHold)
            {
                InterpUpdate(Stopwatch.GetTimestamp() - _interpHoldTicks, rotation, mirror, adj, crop, false, out _);
                return _hasFrame && _oriented != null;
            }
            if (!_hasFrame) return false;
            Size src = new(_fw, _fh);
            Rectangle keep = CropMath.Effective(src, crop);      // the sub-rectangle that is shown
            Size o = Oriented(keep.Size, rotation);
            EnsureOriented(o);
            ConvertInto(_orientedRtv!, src, keep, o, rotation, mirror, adj);
            _orientedVer++;
            return true;
        }
    }

    private void EnsureOriented(Size o)
    {
        if (_oriented != null && _orientedSize == o) return;
        _orientedRtv?.Dispose(); _orientedSrv?.Dispose(); _oriented?.Dispose();
        _oriented = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)o.Width, Height = (uint)o.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _orientedSrv = Device.CreateShaderResourceView(_oriented);
        _orientedRtv = Device.CreateRenderTargetView(_oriented);
        _orientedSize = o;
        _orientedVer++;
    }

    /// <summary>The convert draw: the frame textures (<c>_nv12</c> / <c>_texY,_texUV</c>) into <paramref name="rtv"/>,
    /// an <paramref name="o"/>-sized target. Caller holds <see cref="FrameLock"/>.</summary>
    private void ConvertInto(ID3D11RenderTargetView rtv, Size src, Rectangle keep, Size o,
        int rotation, bool mirror, VideoAdjustments adj)
    {
        // BT.601 / 709 / 2020 coefficients (Cr->R, Cb->G, Cr->G, Cb->B).
        Vector4 coef = _yuv.Matrix switch
        {
            2 => new Vector4(1.402f, 0.344136f, 0.714136f, 1.772f),
            4 or 5 => new Vector4(1.4746f, 0.164553f, 0.571353f, 1.8814f),
            _ => new Vector4(1.5748f, 0.187324f, 0.468124f, 1.8556f),
        };
        Vector4 range = _yuv.FullRange
            ? new Vector4(0f, 1f, 0.5f, 1f)
            : new Vector4(16f / 255f, 255f / 219f, 128f / 255f, 255f / 224f);

        WriteCb(_cbConvert!, new ConvertCb
        {
            SrcW = src.Width, SrcH = src.Height, OutW = o.Width, OutH = o.Height,
            Rot = rotation / 90, Mirror = mirror ? 1 : 0, Coef = coef, Range = range,
            P0 = adj.IsNeutral ? 0 : 1,   // orient.z: adjustments on
            P1 = Math.Clamp(adj.Shadow, 0, VideoAdjustments.MaxShadow),   // orient.w: shadow boost level
            Adj = new Vector4(adj.Brightness, adj.Contrast, 1f / adj.Gamma, adj.Saturation),
            CropL = keep.X, CropT = keep.Y, CropR = ChromaModeNow, CropB = ChromaVariant, Tune = ChromaTune,
        });

        Ctx.PSSetShaderResource(0, null!);
        SetupTriangle(rtv, new Rectangle(0, 0, o.Width, o.Height));
        Ctx.PSSetShader(_psConvert);
        Ctx.PSSetConstantBuffer(0, _cbConvert);
        Ctx.PSSetSampler(0, _linear);
        Ctx.PSSetShaderResource(0, (_gpuFrame ? _nv12Y : _srvY)!);
        Ctx.PSSetShaderResource(1, (_gpuFrame ? _nv12UV : _srvUV)!);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);
        Ctx.PSSetShaderResource(1, null!);
    }

    public Size OrientedSize => _orientedSize;

    /// <summary>Passes (b)+(c): clear the target black and scale the oriented picture into
    /// <paramref name="dst"/>. <paramref name="inter"/> is the intermediate FSR needs
    /// (same size as the target).</summary>
    private void Compose(ID3D11RenderTargetView target, Size targetSize, Rectangle dst,
        ScalingQuality quality, bool pixelPerfect, float sharpness,
        ID3D11RenderTargetView? interRtv, ID3D11ShaderResourceView? interSrv, int nativeH = 0, int filter = -1)
    {
        _prescaleActiveH = 0;
        Ctx.PSSetShaderResource(0, null!);
        Ctx.OMSetRenderTargets(target);
        Ctx.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 1f));

        Rectangle clip = Rectangle.Intersect(dst, new Rectangle(Point.Empty, targetSize));
        if (clip.Width <= 0 || clip.Height <= 0 || dst.Width <= 0 || dst.Height <= 0 || _orientedSrv == null) return;

        Size src = _orientedSize;
        ID3D11ShaderResourceView srcSrv = _orientedSrv!;

        // "元の細かさに合わせて拡大": shrink to the real detail first, then scale from there. Only when the
        // picture is drawn larger than that detail (a window at or below it gains nothing) and not for integer scaling.
        bool preDo = false; Size preTo = default;
        if (nativeH > 0 && !pixelPerfect && _psPre != null && nativeH < src.Height && dst.Height > nativeH)
        {
            int nw = Math.Max(2, (int)Math.Round((double)src.Width * nativeH / src.Height));
            if (nw < src.Width && dst.Width > nw) { preDo = true; preTo = new Size(nw, nativeH); }
        }
        Size effSrc = preDo ? preTo : src;

        // Anime4K: the CNN doubles the (prescaled) picture once, twice when the destination is 3x larger or more; a result
        // that is still current (same picture, same settings) is reused, which also spares the prescale pass.
        int a4kStages = quality == ScalingQuality.Anime4K && !pixelPerfect && interRtv != null && interSrv != null
            ? A4kPlan(effSrc, dst.Size) : 0;
        // 「ぼやけを取ってから拡大」: the Restore CNN runs on the (shrunk) picture before FSR / the Anime4K upscaler, at the picture's own size
        bool restore = !pixelPerfect && interRtv != null && dst.Width > effSrc.Width && dst.Height > effSrc.Height
            && quality != ScalingQuality.NvRtx && RestoreWanted(quality, preDo) && (a4kStages > 0 || quality != ScalingQuality.Anime4K);
        bool a4kHit = false, restHit = false;
        A4kKey a4kKey = default;
        RestoreKey restKey = default;
        int pf = preDo ? (filter < 0 ? PrescaleFilter : filter) : 0;
        if (restore)
        {
            restKey = new RestoreKey(_orientedVer, preDo ? nativeH : 0, pf, preDo ? BackProjectionIterations : 0, preDo ? BackProjectionClamp : 0f, RestoreVariantLetter, A4kHalf);
            restHit = !A4kNoCache && _restOut != null && _restHaveKey && _restKey == restKey;
        }
        if (a4kStages > 0)
        {
            a4kKey = new A4kKey(_orientedVer, preDo ? nativeH : 0, pf,
                preDo ? BackProjectionIterations : 0, preDo ? BackProjectionClamp : 0f, A4kVariantNow, a4kStages, A4kHalf,
                restore ? RestoreVariantLetter : '\0', A4kVariantNow == 'C' ? A4kCustomEpoch : 0);
            a4kHit = !A4kNoCache && _a4kResult != null && _a4kHaveKey && _a4kKey == a4kKey;
        }
        if (preDo)
        {
            if (!a4kHit && !restHit) DrawPrescale(src, preTo, filter);
            src = preTo;
            srcSrv = _preSrv!;
            _prescaleActiveH = nativeH;
        }
        if (restore && !a4kHit)
        {
            if (restHit || RestoreRun(srcSrv, src, restKey)) srcSrv = _restOut!.Srv;
        }
        if (a4kStages > 0)
        {
            if (!a4kHit)
            {
                if (A4kRun(srcSrv, src, a4kStages, a4kKey)) a4kHit = true;
                else a4kStages = 0;     // could not run (shader / memory): behave like FSR
            }
            if (a4kHit)
            {
                src = _a4kResult!.Size;
                srcSrv = _a4kResult.Srv;
            }
        }

        // 「AI 拡大（NVIDIA RTX）」: the video processor's super resolution scales the (prescaled) oriented picture to the destination size;
        // when it cannot run (not NVIDIA / failure) this is FSR
        bool nvOk = false;
        if (quality == ScalingQuality.NvRtx && !pixelPerfect && _oriented != null && dst.Width > src.Width && dst.Height > src.Height)
        {
            ID3D11Texture2D nvSrc = preDo ? _pre! : _oriented;
            if (NvScale(nvSrc, src, dst.Size, out ID3D11ShaderResourceView? nvSrv) && nvSrv != null) { nvOk = true; src = dst.Size; srcSrv = nvSrv; }
        }

        bool a4k = a4kStages > 0 && a4kHit;
        bool fsr = (quality == ScalingQuality.Fsr || (quality == ScalingQuality.NvRtx && !nvOk) || (quality == ScalingQuality.Anime4K && !a4k && A4kFallbackFsr))
                   && !pixelPerfect && interRtv != null && interSrv != null
                   && dst.Width > src.Width && dst.Height > src.Height;
        // after the CNN: EASU only for a (still) larger destination and only when asked for; the exact size needs no resampling
        bool a4kExact = a4k && src == dst.Size;
        bool a4kEasu = a4k && !a4kExact && A4kUseEasu && dst.Width > src.Width && dst.Height > src.Height;
        if (a4kEasu) fsr = true;

        if (!fsr && !a4k)
        {
            WriteCb(_cbScale!, new Vector4(dst.X, dst.Y, dst.Width, dst.Height));
            SetupTriangle(target, clip);
            Ctx.PSSetShader(_psScale);
            Ctx.PSSetConstantBuffer(0, _cbScale);
            Ctx.PSSetSampler(0, pixelPerfect ? _point : _linear);
            Ctx.PSSetShaderResource(0, srcSrv);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
            return;
        }

        ID3D11ShaderResourceView rcasIn = interSrv!;
        int rcasOrgX = 0, rcasOrgY = 0;
        if (a4kExact)
        {
            // the CNN output already has the destination size: sharpen it straight from there
            rcasIn = srcSrv; rcasOrgX = dst.X; rcasOrgY = dst.Y;
        }
        else if (fsr)
        {
            // EASU: oriented picture -> intermediate (target-sized), only inside the clip.
            var c0 = new uint[4]; var c1 = new uint[4]; var c2 = new uint[4]; var c3 = new uint[4];
            Fsr1.EasuCon(c0, c1, c2, c3, src.Width, src.Height, src.Width, src.Height, dst.Width, dst.Height);
            var easu = new uint[24];
            Array.Copy(c0, 0, easu, 0, 4); Array.Copy(c1, 0, easu, 4, 4);
            Array.Copy(c2, 0, easu, 8, 4); Array.Copy(c3, 0, easu, 12, 4);
            easu[16] = unchecked((uint)dst.X); easu[17] = unchecked((uint)dst.Y);
            WriteCb(_cbEasu!, easu);
            SetupTriangle(interRtv!, clip);
            Ctx.PSSetShader(_psEasu);
            Ctx.PSSetConstantBuffer(0, _cbEasu);
            Ctx.PSSetSampler(0, _linear);
            Ctx.PSSetShaderResource(0, srcSrv);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
        }
        else
        {
            // bilinear from the CNN output to the destination size (into the intermediate), then RCAS
            WriteCb(_cbScale!, new Vector4(dst.X, dst.Y, dst.Width, dst.Height));
            SetupTriangle(interRtv!, clip);
            Ctx.PSSetShader(_psScale);
            Ctx.PSSetConstantBuffer(0, _cbScale);
            Ctx.PSSetSampler(0, _linear);
            Ctx.PSSetShaderResource(0, srcSrv);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
        }

        // RCAS: intermediate (or the CNN output) -> target.
        var rc = new uint[4];
        Fsr1.RcasCon(rc, (1f - Math.Clamp(sharpness, 0f, 1f)) * 2.0f);
        var rcas = new uint[24];
        Array.Copy(rc, 0, rcas, 0, 4);
        rcas[16] = unchecked((uint)rcasOrgX); rcas[17] = unchecked((uint)rcasOrgY);
        rcas[20] = unchecked((uint)clip.Left); rcas[21] = unchecked((uint)clip.Top);
        rcas[22] = unchecked((uint)clip.Right); rcas[23] = unchecked((uint)clip.Bottom);
        WriteCb(_cbRcas!, rcas);
        SetupTriangle(target, clip);
        Ctx.PSSetShader(_psRcas);
        Ctx.PSSetConstantBuffer(0, _cbRcas);
        Ctx.PSSetSampler(0, _linear);
        Ctx.PSSetShaderResource(0, rcasIn);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);
    }

    /// <summary>Detail height of the last composed frame's prescale pass (0 = none). Render thread writes.</summary>
    private volatile int _prescaleActiveH;
    public int PrescaleActiveHeight => _prescaleActiveH;
    public bool PrescaleAvailable => _psPre != null;
    /// <summary>Filter of the prescale pass: 0 box, 1 tent, 2 Catmull-Rom, 3 Lanczos3, 4 back-projection (the default: it undoes
    /// the enlargement instead of just resampling its blur - see NativeResTest).</summary>
    public static int PrescaleFilter = FilterBackProjection;

    private void WriteCb(ID3D11Buffer cb, Vector4[] v)
    {
        for (int i = 0; i < v.Length; i++) Marshal.StructureToPtr(v[i], _scratch + i * 16, false);
        Ctx.UpdateSubresource(cb, 0, null, _scratch, 0, 0);
    }

    /// <summary>The prescale draw: _oriented (<paramref name="src"/>) shrunk into the _pre texture of size <paramref name="to"/>.</summary>
    private void DrawPrescale(Size src, Size to, int filter)
    {
        EnsurePre(to);
        if (filter < 0) filter = PrescaleFilter;
        Vector4 sizes = new(src.Width, src.Height, to.Width, to.Height);
        if (filter == FilterBackProjection && _psIbp != null)
        {
            EnsurePf(to);
            int n = Math.Max(1, BackProjectionIterations);
            // start: Lanczos shrink into pf0, then n refinements ping-ponging pf0/pf1; the last one lands in _pre (8 bit)
            WriteCb(_cbPre!, new Vector4[] { sizes, new(3, 0, 0, 0) });
            SetupTriangle(_pfRtv[0]!, new Rectangle(0, 0, to.Width, to.Height));
            Ctx.PSSetShader(_psPre);
            Ctx.PSSetConstantBuffer(0, _cbPre);
            Ctx.PSSetShaderResource(0, _orientedSrv!);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
            WriteCb(_cbPre!, new Vector4[] { sizes, new(filter, BackProjectionClamp, 0, 0) });
            for (int k = 0; k < n; k++)
            {
                int from = k % 2, toIdx = 1 - from;
                bool last = k == n - 1;
                Ctx.PSSetShaderResource(1, null!);
                SetupTriangle(last ? _preRtv! : _pfRtv[toIdx]!, new Rectangle(0, 0, to.Width, to.Height));
                Ctx.PSSetShader(_psIbp);
                Ctx.PSSetConstantBuffer(0, _cbPre);
                Ctx.PSSetSampler(0, _linear);
                Ctx.PSSetShaderResource(0, _orientedSrv!);
                Ctx.PSSetShaderResource(1, _pfSrv[from]!);
                Ctx.Draw(3, 0);
            }
            Ctx.PSSetShaderResource(0, null!);
            Ctx.PSSetShaderResource(1, null!);
            return;
        }
        WriteCb(_cbPre!, new Vector4[] { sizes, new(Math.Min(filter, 3), 0, 0, 0) });
        SetupTriangle(_preRtv!, new Rectangle(0, 0, to.Width, to.Height));
        Ctx.PSSetShader(_psPre);
        Ctx.PSSetConstantBuffer(0, _cbPre);
        Ctx.PSSetShaderResource(0, _orientedSrv!);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);
    }

    public const int FilterBackProjection = 4;
    public static int BackProjectionIterations = 3;
    /// <summary>How far (as a share of the local range of the captured picture) a refined value may leave that range; negative = no limit.</summary>
    public static float BackProjectionClamp = 0.0f;

    private void EnsurePf(Size sz)
    {
        if (_pf[0] != null && _pfSize == sz) return;
        for (int i = 0; i < 2; i++)
        {
            _pfRtv[i]?.Dispose(); _pfSrv[i]?.Dispose(); _pf[i]?.Dispose();
            _pf[i] = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)sz.Width, Height = (uint)sz.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _pfSrv[i] = Device.CreateShaderResourceView(_pf[i]!);
            _pfRtv[i] = Device.CreateRenderTargetView(_pf[i]!);
        }
        _pfSize = sz;
    }

    private void EnsurePre(Size sz)
    {
        if (_pre != null && _preSize == sz) return;
        _preRtv?.Dispose(); _preSrv?.Dispose(); _pre?.Dispose();
        _pre = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)sz.Width, Height = (uint)sz.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _preSrv = Device.CreateShaderResourceView(_pre);
        _preRtv = Device.CreateRenderTargetView(_pre);
        _preSize = sz;
    }

    private void EnsureClientTextures(Size client)
    {
        if (_lastComposed != null && _lastComposedSize == client) return;
        _interRtv?.Dispose(); _interSrv?.Dispose(); _inter?.Dispose();
        _lastComposed?.Dispose();
        _composedValid = false;
        _inter = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)client.Width, Height = (uint)client.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _interSrv = Device.CreateShaderResourceView(_inter);
        _interRtv = Device.CreateRenderTargetView(_inter);
        _lastComposed = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)client.Width, Height = (uint)client.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        _lastComposedSize = client;
    }
    private Size _lastComposedSize;

    /// <summary>Resize the swap chain (and everything sized after it) to the new client size.</summary>
    public void Resize(Size client)
    {
        if (_swap == null || client.Width <= 0 || client.Height <= 0) return;
        // The capture thread uses the immediate context (dup-detection dispatch) under FrameLock; the context is
        // not free-threaded, so ClearState/Flush must not interleave with it.
        lock (FrameLock)
        {
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            Ctx.ClearState();
            _nvH?.DropViews();
            Ctx.Flush();
        }
        _swap.ResizeBuffers(0, (uint)client.Width, (uint)client.Height, Format.Unknown,
            _swapFlags).CheckError();
        _swapSize = client;
        EnsureClientTextures(client);
    }

    /// <summary>ResizeBuffers to the current size. Measured (--vblank-probe --exp triggers) to make DWM re-evaluate the swap chain at a cost of one frame.</summary>
    public void NudgeSwapChain()
    {
        if (_swapSize.Width > 0 && _swapSize.Height > 0) Resize(_swapSize);
    }

    /// <summary>Compose into the back buffer and present. Returns the Present result.</summary>
    public Result PresentFrame(Rectangle dst, ScalingQuality quality, bool pixelPerfect, float sharpness, int nativeH = 0,
        OverlayLayer[]? overlays = null)
    {
        if (_swap == null) { _gpu?.Cancel(); return Result.Fail; }
        EnsureClientTextures(_swapSize);
        HdrUpdate();     // the swap chain follows the HDR request / the monitor's HDR state
        if (_hdrKind != 0) return PresentFrameHdr(dst, quality, pixelPerfect, sharpness, nativeH, overlays);
        _gpu?.Begin();   // no-op when the render loop already opened the span before Convert
        using (ID3D11Texture2D bb = _swap.GetBuffer<ID3D11Texture2D>(0))
        using (ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(bb))
        {
            Compose(rtv, _swapSize, dst, quality, pixelPerfect, sharpness, _interRtv, _interSrv, nativeH);
            // Flip-model back buffers are undefined after Present, so keep our own copy
            // for Snapshot().
            Ctx.CopyResource(_lastComposed!, bb);
            _composedDst = dst;
            _composedValid = true;
            _gpu?.End();   // convert + scale/FSR + copy; the vsync wait in Present is not GPU work
            // Overlays go on AFTER the copy stills are read from, so snapshots never contain them.
            DrawOverlays(rtv, _swapSize, overlays);
        }
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        Result pr = _swap.Present(_syncInterval, _presentFlags);
        if (pr.Success) Volatile.Read(ref _pacing)?.Record(Beat.Mark());
        _gpu?.Poll();
        return pr;
    }

    // ---- overlays (notice / info panel / caption blended into the back buffer) ----
    private sealed class OvTex
    {
        public ID3D11Texture2D Tex = null!;
        public ID3D11ShaderResourceView Srv = null!;
        public int W, H;
        public long Version;
        public void Dispose() { Srv.Dispose(); Tex.Dispose(); }
    }
    private readonly Dictionary<int, OvTex> _ov = new();

    private void FreeOverlays()
    {
        foreach (OvTex t in _ov.Values) t.Dispose();
        _ov.Clear();
    }

    private void DrawOverlays(ID3D11RenderTargetView rtv, Size target, OverlayLayer[]? layers)
    {
        if (layers == null || layers.Length == 0) { if (_ov.Count > 0) FreeOverlays(); return; }
        if (_ov.Count > 0)
        {
            var gone = new List<int>();
            foreach (int id in _ov.Keys) if (!layers.Any(l => l.Id == id)) gone.Add(id);
            foreach (int id in gone) { _ov[id].Dispose(); _ov.Remove(id); }
        }
        bool any = false;
        foreach (OverlayLayer l in layers)
        {
            if (l.W <= 0 || l.H <= 0 || l.Pixels.Length < l.W * l.H * 4) continue;
            Rectangle r = Rectangle.Intersect(new Rectangle(l.X, l.Y, l.W, l.H), new Rectangle(Point.Empty, target));
            if (r.Width <= 0 || r.Height <= 0) continue;
            if (!_ov.TryGetValue(l.Id, out OvTex? t) || t.W != l.W || t.H != l.H || t.Version != l.Version)
            {
                if (t != null && (t.W != l.W || t.H != l.H)) { t.Dispose(); _ov.Remove(l.Id); t = null; }
                if (t == null)
                {
                    var tex = Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)l.W, Height = (uint)l.H, MipLevels = 1, ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                    });
                    t = new OvTex { Tex = tex, Srv = Device.CreateShaderResourceView(tex), W = l.W, H = l.H };
                    _ov[l.Id] = t;
                }
                var h = GCHandle.Alloc(l.Pixels, GCHandleType.Pinned);
                try { Ctx.UpdateSubresource(t.Tex, 0, null, h.AddrOfPinnedObject(), (uint)(l.W * 4), 0); }
                finally { h.Free(); }
                t.Version = l.Version;
            }
            if (!any)
            {
                Ctx.OMSetBlendState(_blendOver);
                Ctx.PSSetShader(_overlayPq && _psOverlayPq != null ? _psOverlayPq : _psOverlay);
                Ctx.PSSetConstantBuffer(0, _cbScale);
                any = true;
            }
            WriteCb(_cbScale!, new Vector4(l.X, l.Y, l.W, l.H));
            SetupTriangle(rtv, r);
            Ctx.PSSetShaderResource(0, t.Srv);
            Ctx.Draw(3, 0);
        }
        if (any)
        {
            Ctx.PSSetShaderResource(0, null!);
            Ctx.OMSetBlendState(null);
        }
    }

    // ---- crispness sample (くっきり度): current pipeline vs plain bilinear on one crop ----
    private ID3D11Texture2D? _csA, _csB, _csInter, _csStA, _csStB;
    private ID3D11RenderTargetView? _csRtvA, _csRtvB, _csRtvI;
    private ID3D11ShaderResourceView? _csSrvI;
    private Size _csSize;

    private void FreeCrisp()
    {
        _csRtvA?.Dispose(); _csRtvB?.Dispose(); _csRtvI?.Dispose(); _csSrvI?.Dispose();
        _csA?.Dispose(); _csB?.Dispose(); _csInter?.Dispose(); _csStA?.Dispose(); _csStB?.Dispose();
        _csRtvA = _csRtvB = _csRtvI = null; _csSrvI = null; _csA = _csB = _csInter = _csStA = _csStB = null;
        _csSize = Size.Empty;
    }

    /// <summary>Queue the two renders of <paramref name="crop"/> (client pixels of the output whose picture is
    /// <paramref name="dst"/>) and the copies to staging textures; only submits work, nothing is waited for.
    /// False when there is no picture to sample.</summary>
    public bool CrispSubmit(Rectangle dst, Rectangle crop, ScalingQuality quality, bool pixelPerfect, float sharpness, int nativeH)
    {
        if (_orientedSrv == null || crop.Width < 16 || crop.Height < 16) return false;
        if (_csSize != crop.Size)
        {
            FreeCrisp();
            Texture2DDescription rt = new()
            {
                Width = (uint)crop.Width, Height = (uint)crop.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            Texture2DDescription st = rt;
            st.Usage = ResourceUsage.Staging; st.BindFlags = BindFlags.None; st.CPUAccessFlags = CpuAccessFlags.Read;
            _csA = Device.CreateTexture2D(rt); _csB = Device.CreateTexture2D(rt); _csInter = Device.CreateTexture2D(rt);
            _csStA = Device.CreateTexture2D(st); _csStB = Device.CreateTexture2D(st);
            _csRtvA = Device.CreateRenderTargetView(_csA); _csRtvB = Device.CreateRenderTargetView(_csB);
            _csRtvI = Device.CreateRenderTargetView(_csInter); _csSrvI = Device.CreateShaderResourceView(_csInter);
            _csSize = crop.Size;
        }
        var local = new Rectangle(dst.X - crop.X, dst.Y - crop.Y, dst.Width, dst.Height);
        int keep = _prescaleActiveH;
        try
        {
            Compose(_csRtvA!, _csSize, local, quality, pixelPerfect, sharpness, _csRtvI, _csSrvI, nativeH);
            Compose(_csRtvB!, _csSize, local, ScalingQuality.Standard, false, sharpness, null, null, 0);
        }
        finally { _prescaleActiveH = keep; Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>()); }
        Ctx.CopyResource(_csStA!, _csA!);
        Ctx.CopyResource(_csStB!, _csB!);
        Ctx.Flush();
        return true;
    }

    /// <summary>Copy the two staged crops out as tightly packed BGRA (call a few ms after <see cref="CrispSubmit"/>).</summary>
    public bool CrispRead(out byte[] pipeline, out byte[] bilinear, out Size size)
    {
        pipeline = bilinear = Array.Empty<byte>(); size = _csSize;
        if (_csStA == null || _csStB == null) return false;
        pipeline = CopyStaging(_csStA, _csSize);
        bilinear = CopyStaging(_csStB, _csSize);
        return true;
    }

    private byte[] CopyStaging(ID3D11Texture2D st, Size sz)
    {
        MappedSubresource map = Ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var buf = new byte[sz.Width * sz.Height * 4];
            for (int y = 0; y < sz.Height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, buf, y * sz.Width * 4, sz.Width * 4);
            return buf;
        }
        finally { Ctx.Unmap(st, 0); }
    }

    // ---- latency (YuCap-internal: frame received -> shown) ----
    internal readonly VideoLatencyTracker Latency = new();

    /// <summary>The vertical-blank beat of the window's output (display-driven pacing, render thread).</summary>
    internal readonly VBlankBeat Beat = new();

    /// <summary>The swap chain's present counter right after a Present (render thread); 0 if unavailable.</summary>
    public uint LastPresentCount
    {
        get { try { return _swap?.LastPresentCount ?? 0; } catch { return 0; } }
    }

    /// <summary>Arrival tick of the newest frame that went straight into the frame textures (no ring).</summary>
    public long LatestArrival => Volatile.Read(ref _latestArrival);
    private long _latestArrival;

    /// <summary>Render thread: let the latency tracker read GetFrameStatistics once.</summary>
    public void PollLatency()
    {
        IDXGISwapChain1? s = _swap;
        if (s != null) Latency.Poll(s);
        if (Beat.Active) Latency.PollVBlank(Beat.Count, Beat.LastQpc, Beat.PeriodMs);
    }

    // ---- measurement window (test mode) ----
    private PacingCapture? _pacing;

    /// <summary>Start recording frame pacing and per-frame GPU time. Any thread.</summary>
    public void BeginMeasure()
    {
        _gpu?.StartCollecting();
        Latency.BeginCollect();
        var p = new PacingCapture();
        if (_swap != null) p.StartPolling(_swap);
        Volatile.Write(ref _pacing, p);
    }

    /// <summary>Stop recording and return what was collected (empty data when never started).</summary>
    public RenderMeasurement EndMeasure()
    {
        PacingCapture p = Interlocked.Exchange(ref _pacing, null) ?? new PacingCapture();
        p.StopPolling();
        double[] gpu = _gpu?.StopCollecting() ?? Array.Empty<double>();
        var (lat, est) = Latency.EndCollect();
        return new RenderMeasurement(p, gpu, PresentMode, lat, est);
    }

    // =====================================================================
    //  Frame interpolation (render thread; the ring is read under FrameLock)
    // =====================================================================
    //
    // While interpolation is active (SetInterpolation) the ring always receives the frames and every
    // refresh shows the picture of time T = now - hold: A is the newest ring frame whose smoothed
    // timestamp is <= T, B the frame after it, alpha how far T lies between them. A and B are kept as
    // oriented BGRA textures with a luma pyramid each; motion is estimated once per (A, B) pair and the
    // in-between picture is synthesised into _oriented, so scaling / FSR / stills work as before.

    /// <summary>One converted frame: the oriented picture and its luma pyramid (1/2 .. 1/16).</summary>
    private sealed class OriFrame
    {
        public ID3D11Texture2D Tex = null!;
        public ID3D11ShaderResourceView Srv = null!;
        public ID3D11RenderTargetView Rtv = null!;
        public Size Size;
        public long Seq;                                  // 0 = holds nothing valid
        public readonly ID3D11Texture2D[] Luma = new ID3D11Texture2D[4];
        public readonly ID3D11ShaderResourceView[] LumaSrv = new ID3D11ShaderResourceView[4];
        public readonly ID3D11RenderTargetView[] LumaRtv = new ID3D11RenderTargetView[4];
        public readonly Size[] LumaSize = new Size[4];
        public NvOfSession? OfaOwner;                     // the optical-flow session the texture below (or Tex) is registered with
        public IntPtr OfaReg;
        public ID3D11Texture2D? OfaTex;                   // the picture shrunk for the optical-flow engine (big sources only)
        public ID3D11ShaderResourceView? OfaSrv;
        public ID3D11RenderTargetView? OfaRtv;
        public long OfaSeq;                               // the Seq OfaTex was made from (0 = stale)

        public void Dispose()
        {
            OfaOwner?.Unregister(OfaReg); OfaOwner = null; OfaReg = IntPtr.Zero;
            OfaRtv?.Dispose(); OfaSrv?.Dispose(); OfaTex?.Dispose(); OfaRtv = null; OfaSrv = null; OfaTex = null; OfaSeq = 0;
            for (int i = 0; i < 4; i++)
            {
                LumaRtv[i]?.Dispose(); LumaSrv[i]?.Dispose(); Luma[i]?.Dispose();
            }
            Rtv?.Dispose(); Srv?.Dispose(); Tex?.Dispose();
        }
    }

    /// <summary>The motion field of one pyramid level: raw result of the search and its median-filtered copy.</summary>
    private sealed class FieldLevel
    {
        public Size Size;
        public ID3D11Texture2D Raw = null!, Fin = null!;
        public ID3D11ShaderResourceView RawSrv = null!, FinSrv = null!;
        public ID3D11RenderTargetView RawRtv = null!, FinRtv = null!;
        public void Dispose()
        {
            RawRtv?.Dispose(); RawSrv?.Dispose(); Raw?.Dispose();
            FinRtv?.Dispose(); FinSrv?.Dispose(); Fin?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterpCb { public Vector4 P0, P1, P2, P3; }

    /// <summary>Everything that changes how a slot turns into an oriented picture; a change drops the cached pair.</summary>
    private readonly record struct InterpKey(int Rotation, bool Mirror, VideoAdjustments Adj,
        System.Windows.Forms.Padding Crop, int W, int H);

    // smoothness penalty per level pixel (measured on real footage: 0.005 and up pulls the vectors toward the parent's and loses real motion)
    private const float MeLambda = 0.0005f;
    // coarsest level: integer vectors -MeRange..MeRange level pixels, i.e. +-192 source pixels at 1080p (+-384 at 4K). A fast pan of a 30 fps game
    // moves the picture by 100-200 px between the two source frames; the former +-6 (+-96 px) matched wrongly there.
    private const int MeRange = 12;

    private bool _interpHold;                           // interpolation active (guarded by FrameLock)
    private long _interpHoldTicks;
    private OriFrame? _oriA, _oriB;
    private ID3D11Texture2D? _costVol;                 // matching costs of the coarsest level (see mecost)
    private ID3D11ShaderResourceView? _costVolSrv;
    private ID3D11RenderTargetView? _costVolRtv;
    private Size _costVolSize;
    private ID3D11Texture2D? _cutTex;                   // 1x1: scene-cut statistics (see cutstat), read by the synthesis
    private ID3D11ShaderResourceView? _cutSrv;
    private ID3D11RenderTargetView? _cutRtv;
    private FieldLevel[]? _fields;                      // index 0 = level 1 (1/2) ... 3 = level 4 (1/16)
    private InterpKey _interpKey;
    private bool _interpKeyValid;
    private readonly List<Slot> _selSlots = new();       // scratch of the pair selection
    private readonly List<TimelineEntry> _selEntries = new();
    private long _curA, _curB, _meA, _meB;              // the pair shown last / the pair the field belongs to
    private double _lastAlpha = -1;
    private long _interpMaxUsed;

    /// <summary>True when the interpolation shaders compiled and could be created.</summary>
    public bool InterpAvailable => _psSynth != null && _cbInterp != null;

    /// <summary>Render thread: switch interpolation on (with the hold-back in Stopwatch ticks) or off.</summary>
    public void SetInterpolation(bool on, long holdTicks)
    {
        bool wasOn;
        lock (FrameLock)
        {
            wasOn = _interpHold;
            _interpHold = on && InterpAvailable;
            _interpHoldTicks = holdTicks;
            if (_interpHold == wasOn) return;
            _interpMaxUsed = 0;
            _interpKeyValid = false;
        }
        if (!on) InterpFree();
    }

    private void InterpFree()
    {
        _oriA?.Dispose(); _oriA = null;
        _oriB?.Dispose(); _oriB = null;
        DisposeOfa();
        FreeHud();
        if (_fields != null) foreach (FieldLevel f in _fields) f.Dispose();
        _fields = null;
        _costVolRtv?.Dispose(); _costVolSrv?.Dispose(); _costVol?.Dispose();
        _costVolRtv = null; _costVolSrv = null; _costVol = null; _costVolSize = Size.Empty;
        _cutRtv?.Dispose(); _cutSrv?.Dispose(); _cutTex?.Dispose(); _cutRtv = null; _cutSrv = null; _cutTex = null;
        _curA = _curB = _meA = _meB = 0;
        _lastAlpha = -1;
        _interpKeyValid = false;
    }

    private ID3D11Texture2D MakeTarget(int w, int h, Format fmt, out ID3D11ShaderResourceView srv, out ID3D11RenderTargetView rtv)
    {
        ID3D11Texture2D t = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        srv = Device.CreateShaderResourceView(t);
        rtv = Device.CreateRenderTargetView(t);
        return t;
    }

    private void EnsureOriFrame(ref OriFrame? f, Size o)
    {
        if (f != null && f.Size == o) return;
        f?.Dispose();
        f = new OriFrame { Size = o };
        f.Tex = MakeTarget(o.Width, o.Height, Format.B8G8R8A8_UNorm, out f.Srv, out f.Rtv);
        int fct = InterpFactor(o);
        Size s = o;
        for (int i = 0; i < 4; i++)
        {
            int d = i == 0 ? fct : 2;
            s = new Size(Math.Max(1, (s.Width + d - 1) / d), Math.Max(1, (s.Height + d - 1) / d));
            f.LumaSize[i] = s;
            f.Luma[i] = MakeTarget(s.Width, s.Height, Format.R16_Float, out f.LumaSrv[i], out f.LumaRtv[i]);
        }
    }

    private void EnsureFields(OriFrame like)
    {
        if (_fields != null)
        {
            bool same = true;
            for (int i = 0; i < 4; i++) same &= _fields[i].Size == FieldSize(like.LumaSize[i]);
            if (same) return;
            foreach (FieldLevel f in _fields) f.Dispose();
        }
        _fields = new FieldLevel[4];
        for (int i = 0; i < 4; i++)
        {
            Size fs = FieldSize(like.LumaSize[i]);
            var f = new FieldLevel { Size = fs };
            f.Raw = MakeTarget(fs.Width, fs.Height, Format.R16G16B16A16_Float, out f.RawSrv, out f.RawRtv);
            f.Fin = MakeTarget(fs.Width, fs.Height, Format.R16G16B16A16_Float, out f.FinSrv, out f.FinRtv);
            _fields[i] = f;
        }
        if (_cutTex == null) _cutTex = MakeTarget(1, 1, Format.R32G32B32A32_Float, out _cutSrv!, out _cutRtv!);
        // the cost volume of the coarsest level: (2R+1)^2 pixels per block
        Size cs = new(_fields[3].Size.Width * (2 * MeRange + 1), _fields[3].Size.Height * (2 * MeRange + 1));
        if (_costVol == null || _costVolSize != cs)
        {
            _costVolRtv?.Dispose(); _costVolSrv?.Dispose(); _costVol?.Dispose();
            _costVol = MakeTarget(cs.Width, cs.Height, Format.R32_Float, out ID3D11ShaderResourceView srv, out ID3D11RenderTargetView rtv);
            _costVolSrv = srv; _costVolRtv = rtv; _costVolSize = cs;
        }
    }

    /// <summary>How much smaller than the picture the finest luma level of the motion search is. The search cost grows
    /// with the picture area, so a wide source (4K) is searched at 1/4 (960x540) instead of 1/2 (1920x1080): the same
    /// cost and quality as a 1080p source, and the 3-4 ms spike per source frame that 4K produced is gone. Sources up
    /// to 2560 px wide (1440p and below) keep the 1/2 search.</summary>
    internal static int InterpFactor(Size oriented) => Math.Max(oriented.Width, oriented.Height) > 2560 ? 4 : 2;

    /// <summary>Source pixels covered by one texel of the final motion field (4 luma pixels of the finest level).</summary>
    internal static int InterpBlockPx(Size oriented) => 4 * InterpFactor(oriented);

    private static Size FieldSize(Size level) => new(Math.Max(1, (level.Width + 3) / 4), Math.Max(1, (level.Height + 3) / 4));

    /// <summary>Copy a ring slot into the frame textures and convert it into <paramref name="f"/>, then build its
    /// luma pyramid. Keeps _hasFrame / _fw / _fh / _yuv as if the slot had been the current frame.
    /// Caller holds <see cref="FrameLock"/>.</summary>
    private void ConvertSlot(Slot s, OriFrame f, int rotation, bool mirror, VideoAdjustments adj, System.Windows.Forms.Padding crop)
    {
        EnsureFrameTextures(s.W, s.H, s.Gpu);
        if (s.Gpu) Ctx.CopyResource(_nv12!, s.Nv12!);
        else { Ctx.CopyResource(_texY!, s.Y!); Ctx.CopyResource(_texUV!, s.UV!); }
        _gpuFrame = s.Gpu;
        _yuv = s.Yuv;
        _hasFrame = true;
        Size src = new(_fw, _fh);
        ConvertInto(f.Rtv, src, CropMath.Effective(src, crop), f.Size, rotation, mirror, adj);
        BuildPyramid(f);
    }

    private void BuildPyramid(OriFrame f)
    {
        for (int k = 0; k < 4; k++)
        {
            Size dst = f.LumaSize[k];
            Size from = k == 0 ? f.Size : f.LumaSize[k - 1];
            WriteCb(_cbInterp!, new InterpCb { P0 = new Vector4(from.Width, from.Height, k == 0 ? InterpFactor(f.Size) : 0, 0) });
            SetupTriangle(f.LumaRtv[k], new Rectangle(0, 0, dst.Width, dst.Height));
            Ctx.PSSetShader(k == 0 ? _psPyr1 : _psPyrD);
            Ctx.PSSetConstantBuffer(0, _cbInterp);
            Ctx.PSSetShaderResource(0, k == 0 ? f.Srv : f.LumaSrv[k - 1]);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
        }
    }

    /// <summary>Motion of the pair: the hardware optical flow when it is available (the block matcher then only runs
    /// its coarsest level, for the scene-cut statistics), otherwise the block matcher.</summary>
    private void EstimateMotion(OriFrame a, OriFrame b)
    {
        if (TryOfaMotion(a, b)) { _fieldIsOfa = true; MotionLabel = "NVIDIA OFA（専用回路）"; EstimateBlocks(a, b, 3); return; }
        _fieldIsOfa = false;
        MotionLabel = "ブロックマッチング（GPU）";
        EstimateBlocks(a, b, 0);
    }

    /// <summary>Hierarchical symmetric block matching of A against B, coarse (1/16) down to level <paramref name="finest"/> (0 = 1/2).</summary>
    private void EstimateBlocks(OriFrame a, OriFrame b, int finest)
    {
        EnsureFields(a);
        FieldLevel[] fields = _fields!;
        for (int k = 3; k >= finest; k--)
        {
            FieldLevel fl = fields[k];
            Size ls = a.LumaSize[k];
            Size parent = k == 3 ? new Size(1, 1) : fields[k + 1].Size;

            const int rng = MeRange;
            if (k == 3)
            {
                WriteCb(_cbInterp!, new InterpCb
                {
                    P0 = new Vector4(ls.Width, ls.Height, 1f / ls.Width, 1f / ls.Height),
                    P3 = new Vector4(rng, 2 * rng + 1, 0, 0),
                });
                SetupTriangle(_costVolRtv!, new Rectangle(0, 0, _costVolSize.Width, _costVolSize.Height));
                Ctx.PSSetShader(_psMeCost);
                Ctx.PSSetConstantBuffer(0, _cbInterp);
                Ctx.PSSetSampler(0, _linear);
                Ctx.PSSetShaderResource(0, a.LumaSrv[k]);
                Ctx.PSSetShaderResource(1, b.LumaSrv[k]);
                Ctx.Draw(3, 0);
                Ctx.PSSetShaderResource(0, null!); Ctx.PSSetShaderResource(1, null!);
            }
            WriteCb(_cbInterp!, new InterpCb
            {
                P0 = new Vector4(ls.Width, ls.Height, 1f / ls.Width, 1f / ls.Height),
                P1 = new Vector4(fl.Size.Width, fl.Size.Height, parent.Width, parent.Height),
                P2 = new Vector4(MeLambda, k == 3 ? 1f : 0f, k == 0 ? 1f : 0f, 0),
                P3 = new Vector4(k == 3 ? rng : 0, k == 3 ? 2 * rng + 1 : 0, 0, 0),
            });
            SetupTriangle(fl.RawRtv, new Rectangle(0, 0, fl.Size.Width, fl.Size.Height));
            Ctx.PSSetShader(_psMe);
            Ctx.PSSetConstantBuffer(0, _cbInterp);
            Ctx.PSSetSampler(0, _linear);
            Ctx.PSSetShaderResource(0, a.LumaSrv[k]);
            Ctx.PSSetShaderResource(1, b.LumaSrv[k]);
            Ctx.PSSetShaderResource(2, k == 3 ? null! : fields[k + 1].FinSrv);
            Ctx.PSSetShaderResource(3, k == 3 ? _costVolSrv! : null!);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!); Ctx.PSSetShaderResource(1, null!); Ctx.PSSetShaderResource(2, null!); Ctx.PSSetShaderResource(3, null!);

            // 3x3 median; the finest level also converts the vectors to oriented-source pixels (x2)
            WriteCb(_cbInterp!, new InterpCb
            {
                P1 = new Vector4(fl.Size.Width, fl.Size.Height, 0, 0),
                P2 = new Vector4(0, k == 0 ? InterpFactor(a.Size) : 1f, 0, 0),
            });
            SetupTriangle(fl.FinRtv, new Rectangle(0, 0, fl.Size.Width, fl.Size.Height));
            Ctx.PSSetShader(_psMed);
            Ctx.PSSetConstantBuffer(0, _cbInterp);
            Ctx.PSSetShaderResource(0, fl.RawSrv);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);

            if (k == 3)
            {
                // scene-cut statistics of the coarsest field into a 1x1 texture (no read-back; the synthesis reads it)
                WriteCb(_cbInterp!, new InterpCb { P1 = new Vector4(fl.Size.Width, fl.Size.Height, 0, 0) });
                SetupTriangle(_cutRtv!, new Rectangle(0, 0, 1, 1));
                Ctx.PSSetShader(_psCut);
                Ctx.PSSetConstantBuffer(0, _cbInterp);
                Ctx.PSSetShaderResource(2, fl.FinSrv);
                Ctx.Draw(3, 0);
                Ctx.PSSetShaderResource(2, null!);
            }
        }
    }

    private void Synthesize(OriFrame a, OriFrame b, float alpha)
    {
        if (_fieldIsOfa) { SynthesizeOf(a, b, alpha); return; }
        FieldLevel f1 = _fields![0];
        Size o = _orientedSize;
        WriteCb(_cbInterp!, new InterpCb
        {
            P0 = new Vector4(o.Width, o.Height, 0, 0),
            P1 = new Vector4(f1.Size.Width, f1.Size.Height, 0, 0),
            P2 = new Vector4(0, 0, alpha, InterpBlockPx(a.Size)),
        });
        _orientedVer++;
        SetupTriangle(_orientedRtv!, new Rectangle(0, 0, o.Width, o.Height));
        Ctx.PSSetShader(_psSynth);
        Ctx.PSSetConstantBuffer(0, _cbInterp);
        Ctx.PSSetSampler(0, _linear);
        Ctx.PSSetShaderResource(0, a.Srv);
        Ctx.PSSetShaderResource(1, b.Srv);
        Ctx.PSSetShaderResource(2, f1.FinSrv);
        Ctx.PSSetShaderResource(4, _cutSrv!);
        Ctx.PSSetShaderResource(8, HudSrv());
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!); Ctx.PSSetShaderResource(1, null!); Ctx.PSSetShaderResource(2, null!); Ctx.PSSetShaderResource(4, null!); Ctx.PSSetShaderResource(8, null!);
    }

    /// <summary>Debug: the scene-cut statistics of the last motion search (mean cost, share of blocks above 0.04 / 0.08 / 0.15).</summary>
    internal Vector4 ReadCutStats()
    {
        using ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Ctx.CopyResource(staging, _cutTex!);
        MappedSubresource map = Ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var f = new float[4];
        try { Marshal.Copy(map.DataPointer, f, 0, 4); } finally { Ctx.Unmap(staging, 0); }
        return new Vector4(f[0], f[1], f[2], f[3]);
    }

    /// <summary>Debug hook for <c>--interp-probe</c>: with a motion-estimated pair cached, read back A, the picture
    /// synthesised at <paramref name="alpha"/>, and B. False when no complete pair is cached.</summary>
    internal bool InterpDebugStills(double alpha, out Bitmap? a, out Bitmap? mid, out Bitmap? b)
    {
        a = mid = b = null;
        lock (FrameLock)
        {
            if (!_interpHold || _oriA == null || _oriB == null || _oriA.Seq == 0 || _oriB.Seq == 0
                || _meA != _oriA.Seq || _meB != _oriB.Seq || _oriented == null || _orientedSize != _oriA.Size) return false;
            var full = new Rectangle(Point.Empty, _oriA.Size);
            a = ReadBack(_oriA.Tex, full);
            Synthesize(_oriA, _oriB, (float)alpha);
            mid = ReadBack(_oriented, full);
            b = ReadBack(_oriB.Tex, full);
            _lastAlpha = -1;       // _oriented no longer matches the cached alpha: redo it at the next refresh
            return true;
        }
    }

    /// <summary>Debug hook for <c>--interp-synth</c>: run the whole interpolation pipeline on two given pictures
    /// (no ring, no capture) and return the picture synthesised at <paramref name="alpha"/>, the final motion
    /// field (vectors in source pixels, one per 8x8 block) and the GPU time per stage.</summary>
    internal static Vector4 DebugCutStats;

    internal bool InterpDebugSynthetic(Bitmap a, Bitmap b, double alpha, int benchFrames,
        out Bitmap? mid, out float[] fieldXy, out Size fieldSize, out string timing)
    {
        mid = null; fieldXy = Array.Empty<float>(); fieldSize = Size.Empty; timing = string.Empty;
        if (!InterpAvailable || a.Size != b.Size) return false;
        lock (FrameLock)
        {
            Size o = a.Size;
            OriFrame? fa = null, fb = null;
            EnsureOriFrame(ref fa, o); EnsureOriFrame(ref fb, o);
            Upload(fa!, a); Upload(fb!, b);
            EnsureOriented(o);
            _lastAlpha = -1; _curA = _curB = _meA = _meB = 0;

            BuildPyramid(fa!); BuildPyramid(fb!);
            EstimateMotion(fa!, fb!);
            Synthesize(fa!, fb!, (float)alpha);
            DebugCutStats = ReadCutStats();
            mid = ReadBack(_oriented!, new Rectangle(Point.Empty, o));

            if (_fieldIsOfa) { fieldXy = ReadFieldOfa(out fieldSize); DebugFieldPx = _ofa!.Grid * _ofaScale; }
            else
            {
                FieldLevel f1 = _fields![0];
                fieldSize = f1.Size;
                fieldXy = ReadField(f1);
                DebugFieldPx = InterpBlockPx(o);
            }

            if (benchFrames > 0)
            {
                double[] pyr = TimeGpu(() => BuildPyramid(fb!), benchFrames);
                double[] me = TimeGpu(() => EstimateMotion(fa!, fb!), benchFrames);
                double[] syn = TimeGpu(() => Synthesize(fa!, fb!, (float)alpha), benchFrames);
                string Fmt(double[] v) => v.Length == 0 ? "n/a" : $"avg {v.Average():0.00} ms (max {v.Max():0.00})";
                timing = $"convert-less pyramid {Fmt(pyr)}; motion search {Fmt(me)}; synthesis {Fmt(syn)}";
                if (_fieldIsOfa) timing += $"; optical-flow engine {OfaBenchMs(fa!, fb!, benchFrames):0.00} ms per pair (wall, both directions)";
            }
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            fa!.Dispose(); fb!.Dispose();
            _lastAlpha = -1;
            return true;
        }
    }

    /// <summary>One ring frame seen by the live hold-out scan: its sequence number, its (phase-locked) time on the
    /// Stopwatch timeline and the luma of the finest search level as bytes, for telling repeated pictures apart.</summary>
    internal sealed class ScanFrame
    {
        public long Seq, Smooth;
        public int W, H;
        public byte[] Luma = Array.Empty<byte>();
    }

    private void InterpOrientation(out int rot, out bool mir, out VideoAdjustments adj, out System.Windows.Forms.Padding crop)
    {
        rot = _interpKeyValid ? _interpKey.Rotation : 0;
        mir = _interpKeyValid && _interpKey.Mirror;
        adj = _interpKeyValid ? _interpKey.Adj : VideoAdjustments.Neutral;
        crop = _interpKeyValid ? _interpKey.Crop : default;
    }

    /// <summary>Test-mode hook (live hold-out check, step 1): convert every ring frame newer than
    /// <paramref name="afterSeq"/> (oldest first) and hand back its luma, so the caller can tell which frames are new
    /// pictures and which are repeats. The live pair cache is invalidated (redone at the next refresh).</summary>
    internal bool InterpDebugScan(long afterSeq, List<ScanFrame> into)
    {
        if (!InterpAvailable) return false;
        lock (FrameLock)
        {
            if (!_interpHold) return false;
            var slots = _ring.Where(s => s.Seq > afterSeq && s.W > 0 && s.H > 0).OrderBy(s => s.Seq).ToList();
            if (slots.Count == 0) return true;
            InterpOrientation(out int rot, out bool mir, out VideoAdjustments adj, out System.Windows.Forms.Padding crop);
            OriFrame? sc = null;
            try
            {
                foreach (Slot s in slots)
                {
                    Size o = Oriented(CropMath.Effective(new Size(s.W, s.H), crop).Size, rot);
                    EnsureOriFrame(ref sc, o);
                    ConvertSlot(s, sc!, rot, mir, adj, crop);
                    Size ls = sc!.LumaSize[0];
                    into.Add(new ScanFrame { Seq = s.Seq, Smooth = s.Smooth, W = ls.Width, H = ls.Height, Luma = ReadLuma(sc, 0) });
                }
                return true;
            }
            finally
            {
                Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                sc?.Dispose();
                _curA = _curB = _meA = _meB = 0;
                _lastAlpha = -1;
            }
        }
    }

    private byte[] ReadLuma(OriFrame f, int level)
    {
        Size sz = f.LumaSize[level];
        using ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)sz.Width, Height = (uint)sz.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.R16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Ctx.CopyResource(staging, f.Luma[level]);
        MappedSubresource map = Ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var res = new byte[sz.Width * sz.Height];
        try
        {
            var row = new short[sz.Width];
            for (int y = 0; y < sz.Height; y++)
            {
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, row, 0, row.Length);
                for (int x = 0; x < sz.Width; x++)
                    res[y * sz.Width + x] = (byte)Math.Clamp((int)Math.Round((float)BitConverter.Int16BitsToHalf(row[x]) * 255f), 0, 255);
            }
        }
        finally { Ctx.Unmap(staging, 0); }
        return res;
    }

    /// <summary>Test-mode hook (live hold-out check, step 2): take the ring frames with the sequence numbers
    /// <paramref name="q0"/> &lt; <paramref name="q1"/> &lt; <paramref name="q2"/> (three different pictures, evenly
    /// spaced in time - the caller picked them), interpolate between F0 and F2 at 0.5 with the real pipeline and read
    /// all four pictures back. False when one of them has left the ring. The live pair cache is invalidated.</summary>
    internal bool InterpDebugHoldout(long q0, long q1, long q2, out Bitmap? f0, out Bitmap? f1, out Bitmap? f2, out Bitmap? mid)
    {
        f0 = f1 = f2 = mid = null;
        if (!InterpAvailable) return false;
        lock (FrameLock)
        {
            if (!_interpHold) return false;
            Slot? Find(long q) { foreach (Slot s in _ring) if (s.Seq == q && q != 0) return s; return null; }
            Slot? s0 = Find(q0), s1 = Find(q1), s2 = Find(q2);
            if (s0 == null || s1 == null || s2 == null) return false;
            if (s0.W != s1.W || s1.W != s2.W || s0.H != s1.H || s1.H != s2.H) return false;

            InterpOrientation(out int rot, out bool mir, out VideoAdjustments adj, out System.Windows.Forms.Padding crop);
            Size o = Oriented(CropMath.Effective(new Size(s0.W, s0.H), crop).Size, rot);
            OriFrame? o0 = null, o1 = null, o2 = null;
            try
            {
                EnsureOriFrame(ref o0, o); EnsureOriFrame(ref o1, o); EnsureOriFrame(ref o2, o);
                ConvertSlot(s0, o0!, rot, mir, adj, crop);
                ConvertSlot(s1, o1!, rot, mir, adj, crop);
                ConvertSlot(s2, o2!, rot, mir, adj, crop);
                EstimateMotion(o0!, o2!);
                EnsureOriented(o);
                Synthesize(o0!, o2!, 0.5f);
                var full = new Rectangle(Point.Empty, o);
                f0 = ReadBack(o0!.Tex, full); f1 = ReadBack(o1!.Tex, full); f2 = ReadBack(o2!.Tex, full);
                mid = ReadBack(_oriented!, full);
                return true;
            }
            finally
            {
                Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                o0?.Dispose(); o1?.Dispose(); o2?.Dispose();
                _curA = _curB = _meA = _meB = 0;      // the field and _oriented now belong to this test
                _lastAlpha = -1;
            }
        }
    }

    private void Upload(OriFrame f, Bitmap bmp)
    {
        BitmapData bd = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Ctx.UpdateSubresource(f.Tex, 0, null, bd.Scan0, (uint)bd.Stride, 0); }
        finally { bmp.UnlockBits(bd); }
    }

    private float[] ReadField(FieldLevel f)
    {
        using ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)f.Size.Width, Height = (uint)f.Size.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Ctx.CopyResource(staging, f.Fin);
        MappedSubresource map = Ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var xy = new float[f.Size.Width * f.Size.Height * 2];
        try
        {
            for (int y = 0; y < f.Size.Height; y++)
            {
                var row = new short[f.Size.Width * 4];
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, row, 0, row.Length);
                for (int x = 0; x < f.Size.Width; x++)
                {
                    xy[(y * f.Size.Width + x) * 2] = (float)BitConverter.Int16BitsToHalf(row[x * 4]);
                    xy[(y * f.Size.Width + x) * 2 + 1] = (float)BitConverter.Int16BitsToHalf(row[x * 4 + 1]);
                }
            }
        }
        finally { Ctx.Unmap(staging, 0); }
        return xy;
    }

    private double[] TimeGpu(Action work, int n)
    {
        using var timer = new GpuTimer(Device, Ctx, 8);
        for (int i = 0; i < 5; i++) work();
        Ctx.Flush();
        timer.Poll(); timer.TakeSamples();
        var sw = Stopwatch.StartNew();
        int submitted = 0;
        while (submitted < n && sw.ElapsedMilliseconds < 20000)
        {
            if (!timer.Begin()) { Ctx.Flush(); Thread.Sleep(0); timer.Poll(); continue; }
            work();
            timer.End();
            Ctx.Flush();
            submitted++;
            timer.Poll();
        }
        while (timer.AnyPending && sw.ElapsedMilliseconds < 25000) { Ctx.Flush(); Thread.Sleep(1); timer.Poll(); }
        return timer.TakeSamples().ToArray();
    }

    /// <summary>Make _oriented the picture of time <paramref name="t"/> (Stopwatch ticks on the smoothed timeline).
    /// <paramref name="changed"/> is true when the picture differs from what the last call left there.
    /// Returns false while the ring holds nothing to show.</summary>
    public bool InterpUpdate(long t, int rotation, bool mirror, VideoAdjustments adj,
        System.Windows.Forms.Padding crop, bool force, out bool changed)
    {
        changed = false;
        if (!_interpHold) return false;
        lock (FrameLock)
        {
            // ---- pick the pair ----
            // Frames the same-picture pass found to repeat the picture before them are skipped and the new pictures run on
            // their own, evenly spaced timeline (PairSelect); a frame whose answer has not come yet counts as a new picture.
            DupPoll();
            bool uniqueAware = UniqueAware && DupAvailable;
            _selSlots.Clear(); _selEntries.Clear();
            foreach (Slot s in _ring)
            {
                if (s.Seq == 0) continue;
                _selSlots.Add(s);
                _selEntries.Add(new TimelineEntry(s.Seq, s.Smooth, s.U, s.Dup));
            }
            double nomTicks = Volatile.Read(ref _nominalMs) * Stopwatch.Frequency / 1000.0;
            int ia = PairSelect.Pick(_selEntries, t, uniqueAware, _uniq.Cadence ? _uniq.PeriodTicks : nomTicks, nomTicks, out int ib, out double alpha);
            if (ia < 0) return _hasFrame && _oriented != null;
            Slot a = _selSlots[ia];
            Slot? b = ib >= 0 ? _selSlots[ib] : null;
            if (b != null && (b.W != a.W || b.H != a.H)) { b = null; alpha = 0; }     // a mode change in between

            // ---- cache validity ----
            var key = new InterpKey(rotation, mirror, adj, crop, a.W, a.H);
            if (force || !_interpKeyValid || key != _interpKey)
            {
                if (_oriA != null) _oriA.Seq = 0;
                if (_oriB != null) _oriB.Seq = 0;
                _curA = _curB = _meA = _meB = 0;
                _lastAlpha = -1;
                _interpKey = key;
                _interpKeyValid = true;
            }
            Size src = new(a.W, a.H);
            Size o = Oriented(CropMath.Effective(src, crop).Size, rotation);

            // frames nobody ever looked at
            if (_interpMaxUsed != 0 && a.Seq > _interpMaxUsed + 1)
            {
                if (!uniqueAware) _ringDropped += a.Seq - _interpMaxUsed - 1;
                else
                    foreach (Slot s in _selSlots)       // repeats that were skipped on purpose are not drops
                        if (!s.Dup && s.Seq > _interpMaxUsed && s.Seq < a.Seq) _ringDropped++;
            }
            _interpMaxUsed = Math.Max(_interpMaxUsed, Math.Max(a.Seq, b?.Seq ?? 0));
            _shownSeq = a.Seq;

            // ---- convert what the pair needs ----
            if (_oriA == null || _oriA.Seq != a.Seq || _oriA.Size != o)
            {
                if (_oriB != null && _oriB.Seq == a.Seq && _oriB.Size == o)
                {
                    (_oriA, _oriB) = (_oriB, _oriA);       // the old B is the new A: its pyramid is reused
                    if (_oriB != null) _oriB.Seq = 0;
                }
                else
                {
                    _gpu?.Begin();
                    EnsureOriFrame(ref _oriA, o);
                    ConvertSlot(a, _oriA!, rotation, mirror, adj, crop);
                    _oriA!.Seq = a.Seq;
                }
            }
            if (b != null && (_oriB == null || _oriB.Seq != b.Seq || _oriB.Size != o))
            {
                _gpu?.Begin();
                EnsureOriFrame(ref _oriB, o);
                ConvertSlot(b, _oriB!, rotation, mirror, adj, crop);
                _oriB!.Seq = b.Seq;
            }

            // ---- motion, once per pair ----
            long bSeq = b?.Seq ?? 0;
            if (b != null && (_meA != a.Seq || _meB != bSeq))
            {
                _gpu?.Begin();
                EstimateMotion(_oriA!, _oriB!);
                HudUpdate(_oriA!, _oriB!, a.Seq, bSeq);
                _meA = a.Seq; _meB = bSeq;
            }

            // ---- the picture ----
            bool pairChanged = a.Seq != _curA || bSeq != _curB;
            double eff = b != null ? alpha : 0.0;
            if (pairChanged || _lastAlpha < 0 || Math.Abs(eff - _lastAlpha) > 0.002 || _oriented == null || _orientedSize != o)
            {
                _gpu?.Begin();
                EnsureOriented(o);
                if (b == null || eff <= 0.002) Ctx.CopyResource(_oriented!, _oriA!.Tex);
                else if (eff >= 0.998) Ctx.CopyResource(_oriented!, _oriB!.Tex);
                else Synthesize(_oriA!, _oriB!, (float)eff);
                _curA = a.Seq; _curB = bSeq;
                _lastAlpha = eff;
                _orientedVer++;
                changed = true;
            }
            else if (uniqueAware && b == null)
            {
                // A still picture (or the wait for the next new one): the frames keep arriving but they only repeat the picture, so the
                // pair no longer changes. Interpolating identical frames used to present at every refresh and the rest of the pipeline
                // (pacing figures, latency, the tests) counts on that, so it still does; the oriented texture itself is left as it is.
                foreach (Slot s in _selSlots)
                    if (s.Seq > a.Seq) { changed = true; break; }
            }
            return true;
        }
    }

    // =====================================================================
    //  Stills
    // =====================================================================

    /// <summary>Read back the oriented RGB texture (the full-resolution, rotated/mirrored frame).</summary>
    public Bitmap? ReadOriented()
    {
        if (_oriented == null) return null;
        return ReadBack(_oriented, new Rectangle(Point.Empty, _orientedSize));
    }

    /// <summary>The picture for judging its real detail: the oriented frame, or while interpolating the unmixed
    /// frame A (an in-between picture is softer than any real one and would read as a lower resolution).</summary>
    public Bitmap? ReadOrientedForAnalysis()
    {
        lock (FrameLock)
        {
            if (_interpHold && _oriA is { Seq: not 0 } a && a.Size == _orientedSize)
                return ReadBack(a.Tex, new Rectangle(Point.Empty, a.Size));
            return ReadOriented();
        }
    }

    /// <summary>A real captured picture for a photo: the oriented frame, or while interpolating the nearer of the two real
    /// frames of the pair (the in-between picture is synthesised and must not be saved as if the device had sent it).</summary>
    public Bitmap? ReadOrientedNearest()
    {
        lock (FrameLock)
        {
            if (_interpHold && _oriA is { Seq: not 0 } a && a.Size == _orientedSize)
            {
                OriFrame pick = a;
                if (_oriB is { Seq: not 0 } b && b.Size == a.Size && _curB == b.Seq && _lastAlpha >= 0.5) pick = b;
                return ReadBack(pick.Tex, new Rectangle(Point.Empty, pick.Size));
            }
            return ReadOriented();
        }
    }

    /// <summary>A staging copy of the picture to analyse, taken on the GPU; the CPU reads it later without holding any lock
    /// or the render thread (see <see cref="AnalysisBegin"/>).</summary>
    public sealed class AnalysisShot : IDisposable
    {
        internal ID3D11Texture2D? Staging;
        internal ID3D11DeviceContext? Ctx;
        internal bool Mapped;
        public Size Size { get; }
        public IntPtr Data { get; internal set; }
        public int RowPitch { get; internal set; }
        internal AnalysisShot(ID3D11Texture2D staging, ID3D11DeviceContext ctx, Size size) { Staging = staging; Ctx = ctx; Size = size; }
        public void Dispose()
        {
            try { if (Mapped) Ctx?.Unmap(Staging, 0); } catch { }
            Mapped = false;
            Staging?.Dispose(); Staging = null;
        }
    }

    /// <summary>Render thread: queue a GPU copy of the picture for judging its detail (the oriented frame, or while interpolating
    /// the unmixed frame A) and return at once. Only the copy command runs under FrameLock.</summary>
    public AnalysisShot? AnalysisBegin()
    {
        lock (FrameLock)
        {
            ID3D11Texture2D? src; Size size;
            if (_interpHold && _oriA is { Seq: not 0 } a && a.Size == _orientedSize) { src = a.Tex; size = a.Size; }
            else if (_oriented != null) { src = _oriented; size = _orientedSize; }
            else return null;
            if (size.Width <= 0 || size.Height <= 0) return null;
            ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            Ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, src, 0, new Box(0, 0, 0, size.Width, size.Height, 1));
            Ctx.Flush();
            return new AnalysisShot(staging, Ctx, size);
        }
    }

    /// <summary>Any thread (the device is multithread-protected): map the staging copy if the GPU has finished it, without
    /// waiting. False = not ready yet (ask again); throws on a real failure.</summary>
    public bool AnalysisTryMap(AnalysisShot shot)
    {
        if (shot.Mapped) return true;
        if (shot.Staging == null) return false;
        Result hr = Ctx.Map(shot.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource map);
        if (hr.Failure)
        {
            if (hr == Vortice.DXGI.ResultCode.WasStillDrawing) return false;
            hr.CheckError();
            return false;
        }
        shot.Data = map.DataPointer; shot.RowPitch = (int)map.RowPitch; shot.Mapped = true;
        return true;
    }

    /// <summary>Test hook: make <paramref name="src"/> the oriented picture (replacing what the live pipeline left there; the
    /// caller pokes a re-convert afterwards) and render it at <paramref name="output"/>. Needs no capture and no swap chain.</summary>
    internal Bitmap? RenderFromBitmap(Bitmap src, Size output, ScalingQuality quality, float sharpness, int nativeH, int filter)
    {
        lock (FrameLock)
        {
            EnsureOriented(src.Size);
            BitmapData bd = src.LockBits(new Rectangle(Point.Empty, src.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Ctx.UpdateSubresource(_oriented!, 0, null, bd.Scan0, (uint)bd.Stride, 0); _orientedVer++; }
            finally { src.UnlockBits(bd); }
            _lastAlpha = -1; _curA = _curB = _meA = _meB = 0;
            return RenderOffscreen(output, quality, sharpness, nativeH, filter);
        }
    }

    /// <summary>Test hook: only the prescale pass of <paramref name="src"/> to <paramref name="to"/> with <paramref name="filter"/>, read back.</summary>
    internal Bitmap? ReadPrescaleOnly(Bitmap src, Size to, int filter)
    {
        if (_psPre == null) return null;
        lock (FrameLock)
        {
            EnsureOriented(src.Size);
            BitmapData bd = src.LockBits(new Rectangle(Point.Empty, src.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Ctx.UpdateSubresource(_oriented!, 0, null, bd.Scan0, (uint)bd.Stride, 0); _orientedVer++; }
            finally { src.UnlockBits(bd); }
            DrawPrescale(src.Size, to, filter);
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            return ReadBack(_pre!, new Rectangle(Point.Empty, to));
        }
    }

    /// <summary>Test hook: GPU milliseconds per frame (compose at <paramref name="output"/>) without and with the prescale, and of
    /// the prescale pass alone, for the picture last given to <see cref="RenderFromBitmap"/> / the live frame.</summary>
    internal bool PrescaleBench(Bitmap? source, Size output, ScalingQuality quality, float sharpness, int nativeH, int filter, int frames,
        out double[] plain, out double[] withPre, out double[] passOnly)
    {
        plain = withPre = passOnly = Array.Empty<double>();
        if (source != null)
        {
            EnsureOriented(source.Size);
            BitmapData bd = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { Ctx.UpdateSubresource(_oriented!, 0, null, bd.Scan0, (uint)bd.Stride, 0); _orientedVer++; }
            finally { source.UnlockBits(bd); }
            _lastAlpha = -1; _curA = _curB = _meA = _meB = 0;
        }
        if (_oriented == null || _psPre == null) return false;
        Size src = _orientedSize;
        double scale = Math.Min((double)output.Width / src.Width, (double)output.Height / src.Height);
        var dst = new Rectangle(0, 0, Math.Max(1, (int)Math.Round(src.Width * scale)), Math.Max(1, (int)Math.Round(src.Height * scale)));
        Texture2DDescription d = new()
        {
            Width = (uint)output.Width, Height = (uint)output.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        using ID3D11Texture2D target = Device.CreateTexture2D(d);
        using ID3D11RenderTargetView targetRtv = Device.CreateRenderTargetView(target);
        using ID3D11Texture2D inter = Device.CreateTexture2D(d);
        using ID3D11RenderTargetView interRtv = Device.CreateRenderTargetView(inter);
        using ID3D11ShaderResourceView interSrv = Device.CreateShaderResourceView(inter);
        try
        {
            plain = TimeGpu(() => Compose(targetRtv, output, dst, quality, false, sharpness, interRtv, interSrv, 0), frames);
            withPre = TimeGpu(() => Compose(targetRtv, output, dst, quality, false, sharpness, interRtv, interSrv, nativeH, filter), frames);
            int nw = Math.Max(2, (int)Math.Round((double)src.Width * nativeH / src.Height));
            passOnly = TimeGpu(() => DrawPrescale(src, new Size(nw, nativeH), filter), frames);
        }
        finally { Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>()); }
        return true;
    }

    /// <summary>Read back what was last presented: dst∩client when cropping, else all of it.</summary>
    public Bitmap? ReadComposed(bool cropToVideo)
    {
        if (_lastComposed == null || !_composedValid) return null;
        var full = new Rectangle(Point.Empty, _lastComposedSize);
        Rectangle r = full;
        if (cropToVideo && _composedDst.Width > 0 && _composedDst.Height > 0)
        {
            Rectangle d = Rectangle.Intersect(_composedDst, full);
            if (d.Width > 0 && d.Height > 0) r = d;
        }
        return ReadBack(_lastComposed, r);
    }

    private Bitmap ReadBack(ID3D11Texture2D tex, Rectangle region)
    {
        using ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)region.Width, Height = (uint)region.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        Ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0,
            new Box(region.Left, region.Top, 0, region.Right, region.Bottom, 1));

        MappedSubresource map = Ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int row = region.Width * 4;
                for (int y = 0; y < region.Height; y++)
                    RtlMoveMemory(bd.Scan0 + y * bd.Stride, map.DataPointer + y * (int)map.RowPitch, (UIntPtr)row);
            }
            finally { bmp.UnlockBits(bd); }
        }
        catch { bmp.Dispose(); throw; }
        finally { Ctx.Unmap(staging, 0); }
        return bmp;
    }

    /// <summary>Render the current oriented frame at <paramref name="output"/> size into a
    /// temporary target (aspect fit) and read it back. Must be called after a Convert().</summary>
    public Bitmap? RenderOffscreen(Size output, ScalingQuality quality, float sharpness, int nativeH = 0, int filter = -1)
    {
        if (_oriented == null || output.Width <= 0 || output.Height <= 0) return null;
        Size src = _orientedSize;
        double scale = Math.Min((double)output.Width / src.Width, (double)output.Height / src.Height);
        int dw = Math.Max(1, (int)Math.Round(src.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Rectangle((output.Width - dw) / 2, (output.Height - dh) / 2, dw, dh);

        Texture2DDescription d = new()
        {
            Width = (uint)output.Width, Height = (uint)output.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        using ID3D11Texture2D target = Device.CreateTexture2D(d);
        using ID3D11RenderTargetView targetRtv = Device.CreateRenderTargetView(target);
        using ID3D11Texture2D inter = Device.CreateTexture2D(d);
        using ID3D11RenderTargetView interRtv = Device.CreateRenderTargetView(inter);
        using ID3D11ShaderResourceView interSrv = Device.CreateShaderResourceView(inter);

        Compose(targetRtv, output, dst, quality, pixelPerfect: false, sharpness, interRtv, interSrv, nativeH, filter);
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        return ReadBack(target, new Rectangle(Point.Empty, output));
    }

    /// <summary>Benchmark (<c>--gpu-bench</c>): render the current source frame <paramref name="frames"/>
    /// times at <paramref name="output"/> size into off-screen targets - convert, scale/FSR and the
    /// copy that PresentFrame also makes - and return the GPU milliseconds of each, measured with the
    /// same timestamp code as the live display. Frames are kept several deep in the GPU queue (as a
    /// real stream keeps them) and only the results are waited for. Must run on the render thread;
    /// null when there is no frame or no timestamp support.</summary>
    public GpuBenchResult? GpuBench(Size output, ScalingQuality quality, float sharpness, int frames,
        int rotation, bool mirror, VideoAdjustments adj, System.Windows.Forms.Padding crop, int nativeH = 0)
    {
        if (output.Width <= 0 || output.Height <= 0 || frames <= 0) return null;
        if (!Convert(rotation, mirror, adj, crop) || _orientedSrv == null) return null;
        Size src = _orientedSize;
        double scale = Math.Min((double)output.Width / src.Width, (double)output.Height / src.Height);
        int dw = Math.Max(1, (int)Math.Round(src.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Rectangle((output.Width - dw) / 2, (output.Height - dh) / 2, dw, dh);

        Texture2DDescription d = new()
        {
            Width = (uint)output.Width, Height = (uint)output.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        Texture2DDescription dCopy = d;
        dCopy.BindFlags = BindFlags.ShaderResource;
        using ID3D11Texture2D target = Device.CreateTexture2D(d);
        using ID3D11RenderTargetView targetRtv = Device.CreateRenderTargetView(target);
        using ID3D11Texture2D inter = Device.CreateTexture2D(d);
        using ID3D11RenderTargetView interRtv = Device.CreateRenderTargetView(inter);
        using ID3D11ShaderResourceView interSrv = Device.CreateShaderResourceView(inter);
        using ID3D11Texture2D copy = Device.CreateTexture2D(dCopy);
        GpuTimer timer;
        try { timer = new GpuTimer(Device, Ctx, 8); }
        catch { return null; }

        using (timer)
        {
            void OneFrame()
            {
                Convert(rotation, mirror, adj, crop);
                Compose(targetRtv, output, dst, quality, pixelPerfect: false, sharpness, interRtv, interSrv, nativeH);
                Ctx.CopyResource(copy, target);
            }

            for (int i = 0; i < 10; i++) OneFrame();   // warm-up: first use of every shader and texture
            Ctx.Flush();
            timer.Poll();
            timer.TakeSamples();
            Thread.Sleep(50);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int submitted = 0;
            while (submitted < frames && sw.ElapsedMilliseconds < 30000)
            {
                if (!timer.Begin())
                {
                    Ctx.Flush();
                    Thread.Sleep(0);
                    timer.Poll();
                    continue;
                }
                OneFrame();
                timer.End();
                Ctx.Flush();
                submitted++;
                timer.Poll();
            }
            while (timer.AnyPending && sw.ElapsedMilliseconds < 35000)
            {
                Ctx.Flush();
                Thread.Sleep(1);
                timer.Poll();
            }
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            return new GpuBenchResult(AdapterName, timer.TakeSamples().ToArray());
        }
    }

    // =====================================================================

    public void Dispose() => DisposeAll(releaseDevice: true);

    /// <summary>Release everything. With <paramref name="releaseDevice"/> false the device and
    /// context are left alone (the capture thread may still be inside them — leaking is the
    /// safe choice there).</summary>
    public void DisposeAll(bool releaseDevice)
    {
        try { lock (FrameLock) { Ctx?.ClearState(); Ctx?.Flush(); } } catch { }
        Beat.Dispose();
        InterpFree();
        A4kFree();
        FreeOverlays(); FreeCrisp(); StreamFree(); FreeHdrTextures(); _hdrKind = 0; _monHdr = null; _hdrCheckAt = 0;
        _interRtv?.Dispose(); _interSrv?.Dispose(); _inter?.Dispose();
        _lastComposed?.Dispose();
        _orientedRtv?.Dispose(); _orientedSrv?.Dispose(); _oriented?.Dispose();
        _preRtv?.Dispose(); _preSrv?.Dispose(); _pre?.Dispose(); _preRtv = null; _preSrv = null; _pre = null;
        _interRtv = null; _interSrv = null; _inter = null; _lastComposed = null;
        _orientedRtv = null; _orientedSrv = null; _oriented = null;
        _swap2?.Dispose(); _swap2 = null;
        DisposeComposition();
        _swap?.Dispose(); _swap = null;
        if (!releaseDevice) return;

        lock (FrameLock) { FreeRing(); DisposeFrameTextures(); DupFree(); }
        _gpu?.Dispose(); _gpu = null;
        DisposeNv();
        _vs?.Dispose(); _psConvert?.Dispose(); _psScale?.Dispose(); _psOverlay?.Dispose(); _blendOver?.Dispose(); _psEasu?.Dispose(); _psRcas?.Dispose();
        _psPre?.Dispose(); _psIbp?.Dispose(); _cbPre?.Dispose();
        for (int i = 0; i < 2; i++) { _pfRtv[i]?.Dispose(); _pfSrv[i]?.Dispose(); _pf[i]?.Dispose(); }
        _psPyr1?.Dispose(); _psPyrD?.Dispose(); _psMe?.Dispose(); _psMeCost?.Dispose(); _psCut?.Dispose(); _psMed?.Dispose(); _psSynth?.Dispose();
        _linear?.Dispose(); _point?.Dispose(); _raster?.Dispose();
        _cbConvert?.Dispose(); _cbScale?.Dispose(); _cbEasu?.Dispose(); _cbRcas?.Dispose(); _cbInterp?.Dispose();
        Ctx?.Dispose();
        Device?.Dispose();
        Marshal.FreeHGlobal(_scratch);
        Marshal.FreeHGlobal(_dupScratch);
    }
}
