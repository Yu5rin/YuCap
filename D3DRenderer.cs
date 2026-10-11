using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
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
internal sealed class D3DRenderer : IDisposable
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
    private ID3D11PixelShader? _psConvert, _psScale, _psEasu, _psRcas;
    private ID3D11SamplerState? _linear, _point;
    private ID3D11RasterizerState? _raster;
    private ID3D11Buffer? _cbConvert, _cbScale, _cbEasu, _cbRcas;
    private readonly IntPtr _scratch = Marshal.AllocHGlobal(256);

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
        public long Seq;                       // 0 = empty
        public void Free()
        {
            Nv12?.Dispose(); Nv12 = null; Y?.Dispose(); Y = null; UV?.Dispose(); UV = null;
            Seq = 0; W = H = 0;
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
        _psEasu = Device.CreatePixelShader(b.Easu);
        _psRcas = Device.CreatePixelShader(b.Rcas);

        _linear = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _point = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _raster = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _cbConvert = Device.CreateBuffer(new BufferDescription(96, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbScale = Device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbEasu = Device.CreateBuffer(new BufferDescription(96, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbRcas = Device.CreateBuffer(new BufferDescription(96, BindFlags.ConstantBuffer, ResourceUsage.Default));
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
                    throw new UserFacingException(L.T("このウィンドウは別の描画方式で使われています。映像ウィンドウを作り直す必要があります。"));
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
    public void SubmitGpuFrame(ID3D11Texture2D src, uint subresource, int w, int h, YuvInfo yuv, long arrival)
    {
        lock (FrameLock)
        {
            if (_ringNeed > 0)
            {
                Slot s = AcquireSlot(w, h, gpu: true);
                Ctx.CopySubresourceRegion(s.Nv12!, 0, 0, 0, 0, src, subresource, new Box(0, 0, 0, w, h, 1));
                Stamp(s, yuv, arrival);
                return;
            }
            EnsureFrameTextures(w, h, gpu: true);
            Ctx.CopySubresourceRegion(_nv12!, 0, 0, 0, 0, src, subresource, new Box(0, 0, 0, w, h, 1));
            _gpuFrame = true;
            _yuv = yuv;
            _hasFrame = true;
        }
    }

    /// <summary>CPU path: upload NV12 from memory (Y plane, then the interleaved UV plane).</summary>
    public void SubmitCpuFrame(IntPtr scan0, int pitch, int w, int h, YuvInfo yuv, long arrival)
    {
        lock (FrameLock)
        {
            if (_ringNeed > 0)
            {
                Slot s = AcquireSlot(w, h, gpu: false);
                Ctx.UpdateSubresource(s.Y!, 0, null, scan0, (uint)pitch, 0);
                Ctx.UpdateSubresource(s.UV!, 0, null, scan0 + pitch * h, (uint)pitch, 0);
                Stamp(s, yuv, arrival);
                return;
            }
            EnsureFrameTextures(w, h, gpu: false);
            Ctx.UpdateSubresource(_texY!, 0, null, scan0, (uint)pitch, 0);
            Ctx.UpdateSubresource(_texUV!, 0, null, scan0 + pitch * h, (uint)pitch, 0);
            _gpuFrame = false;
            _yuv = yuv;
            _hasFrame = true;
        }
    }

    // =====================================================================
    //  Video delay ring
    // =====================================================================

    /// <summary>Render thread: size the ring for <paramref name="delayMs"/> at <paramref name="fps"/>
    /// (fps x delay + 2 slots, so the frame that is due always still exists while newer ones
    /// keep arriving), or free it when the delay is 0. The ring only grows while a delay is
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
            int need = (int)Math.Ceiling((fps > 0 ? fps : 60) * delayMs / 1000.0) + 2;
            if (need == _ringNeed) return false;
            if (_ringNeed == 0) _shownSeq = _ringSeq;   // nothing in the ring is "pending" yet
            _ringNeed = need;
            return true;
        }
    }

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
        }
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
            }
        }
        return slot;
    }

    private void Stamp(Slot s, YuvInfo yuv, long arrival)
    {
        s.Yuv = yuv;
        s.Arrival = arrival;
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

    /// <summary>Make the newest frame whose arrival is at least the delay ago the current one
    /// (GPU-side copy into the frame textures that Convert reads). Older due frames that were
    /// never shown - because the render thread was waiting for vsync - are simply skipped, never
    /// lost before their turn: they stay in the ring until the newest due one replaces them.</summary>
    public bool AdvanceDelayed(long nowTicks, long delayTicks, out long arrival)
    {
        arrival = 0;
        lock (FrameLock)
        {
            Slot? best = null;
            foreach (Slot s in _ring)
                if (s.Seq > _shownSeq && s.Arrival <= nowTicks - delayTicks && (best == null || s.Seq > best.Seq))
                    best = s;
            if (best == null) return false;

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
        public int CropL, CropT, CropR, CropB;
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
    /// Returns false if there is no frame yet.</summary>
    public bool Convert(int rotation, bool mirror, VideoAdjustments adj, System.Windows.Forms.Padding crop)
    {
        lock (FrameLock)
        {
            if (!_hasFrame) return false;
            Size src = new(_fw, _fh);
            Rectangle keep = CropMath.Effective(src, crop);      // the sub-rectangle that is shown
            Size o = Oriented(keep.Size, rotation);
            if (_oriented == null || _orientedSize != o)
            {
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
            }

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
                Adj = new Vector4(adj.Brightness, adj.Contrast, 1f / adj.Gamma, adj.Saturation),
                CropL = keep.X, CropT = keep.Y,
            });

            Ctx.PSSetShaderResource(0, null!);
            SetupTriangle(_orientedRtv!, new Rectangle(0, 0, o.Width, o.Height));
            Ctx.PSSetShader(_psConvert);
            Ctx.PSSetConstantBuffer(0, _cbConvert);
            Ctx.PSSetSampler(0, _linear);
            Ctx.PSSetShaderResource(0, (_gpuFrame ? _nv12Y : _srvY)!);
            Ctx.PSSetShaderResource(1, (_gpuFrame ? _nv12UV : _srvUV)!);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
            Ctx.PSSetShaderResource(1, null!);
            return true;
        }
    }

    public Size OrientedSize => _orientedSize;

    /// <summary>Passes (b)+(c): clear the target black and scale the oriented picture into
    /// <paramref name="dst"/>. <paramref name="inter"/> is the intermediate FSR needs
    /// (same size as the target).</summary>
    private void Compose(ID3D11RenderTargetView target, Size targetSize, Rectangle dst,
        ScalingQuality quality, bool pixelPerfect, float sharpness,
        ID3D11RenderTargetView? interRtv, ID3D11ShaderResourceView? interSrv)
    {
        Ctx.PSSetShaderResource(0, null!);
        Ctx.OMSetRenderTargets(target);
        Ctx.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 1f));

        Rectangle clip = Rectangle.Intersect(dst, new Rectangle(Point.Empty, targetSize));
        if (clip.Width <= 0 || clip.Height <= 0 || dst.Width <= 0 || dst.Height <= 0 || _orientedSrv == null) return;

        Size src = _orientedSize;
        bool fsr = quality == ScalingQuality.Fsr && !pixelPerfect && interRtv != null && interSrv != null
                   && dst.Width > src.Width && dst.Height > src.Height;

        if (!fsr)
        {
            WriteCb(_cbScale!, new Vector4(dst.X, dst.Y, dst.Width, dst.Height));
            SetupTriangle(target, clip);
            Ctx.PSSetShader(_psScale);
            Ctx.PSSetConstantBuffer(0, _cbScale);
            Ctx.PSSetSampler(0, pixelPerfect ? _point : _linear);
            Ctx.PSSetShaderResource(0, _orientedSrv);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(0, null!);
            return;
        }

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
        Ctx.PSSetShaderResource(0, _orientedSrv);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);

        // RCAS: intermediate -> target.
        var rc = new uint[4];
        Fsr1.RcasCon(rc, (1f - Math.Clamp(sharpness, 0f, 1f)) * 2.0f);
        var rcas = new uint[24];
        Array.Copy(rc, 0, rcas, 0, 4);
        rcas[20] = unchecked((uint)clip.Left); rcas[21] = unchecked((uint)clip.Top);
        rcas[22] = unchecked((uint)clip.Right); rcas[23] = unchecked((uint)clip.Bottom);
        WriteCb(_cbRcas!, rcas);
        SetupTriangle(target, clip);
        Ctx.PSSetShader(_psRcas);
        Ctx.PSSetConstantBuffer(0, _cbRcas);
        Ctx.PSSetSampler(0, _linear);
        Ctx.PSSetShaderResource(0, interSrv!);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);
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
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        Ctx.ClearState();
        Ctx.Flush();
        _swap.ResizeBuffers(0, (uint)client.Width, (uint)client.Height, Format.Unknown,
            _swapFlags).CheckError();
        _swapSize = client;
        EnsureClientTextures(client);
    }

    /// <summary>Compose into the back buffer and present. Returns the Present result.</summary>
    public Result PresentFrame(Rectangle dst, ScalingQuality quality, bool pixelPerfect, float sharpness)
    {
        if (_swap == null) return Result.Fail;
        EnsureClientTextures(_swapSize);
        using (ID3D11Texture2D bb = _swap.GetBuffer<ID3D11Texture2D>(0))
        using (ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(bb))
        {
            Compose(rtv, _swapSize, dst, quality, pixelPerfect, sharpness, _interRtv, _interSrv);
            // Flip-model back buffers are undefined after Present, so keep our own copy
            // for Snapshot().
            Ctx.CopyResource(_lastComposed!, bb);
            _composedDst = dst;
            _composedValid = true;
        }
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        return _swap.Present(_syncInterval, _presentFlags);
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
    public Bitmap? RenderOffscreen(Size output, ScalingQuality quality, float sharpness)
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

        Compose(targetRtv, output, dst, quality, pixelPerfect: false, sharpness, interRtv, interSrv);
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        return ReadBack(target, new Rectangle(Point.Empty, output));
    }

    // =====================================================================

    public void Dispose() => DisposeAll(releaseDevice: true);

    /// <summary>Release everything. With <paramref name="releaseDevice"/> false the device and
    /// context are left alone (the capture thread may still be inside them — leaking is the
    /// safe choice there).</summary>
    public void DisposeAll(bool releaseDevice)
    {
        try { Ctx?.ClearState(); Ctx?.Flush(); } catch { }
        _interRtv?.Dispose(); _interSrv?.Dispose(); _inter?.Dispose();
        _lastComposed?.Dispose();
        _orientedRtv?.Dispose(); _orientedSrv?.Dispose(); _oriented?.Dispose();
        _interRtv = null; _interSrv = null; _inter = null; _lastComposed = null;
        _orientedRtv = null; _orientedSrv = null; _oriented = null;
        _swap2?.Dispose(); _swap2 = null;
        DisposeComposition();
        _swap?.Dispose(); _swap = null;
        if (!releaseDevice) return;

        lock (FrameLock) { FreeRing(); DisposeFrameTextures(); }
        _vs?.Dispose(); _psConvert?.Dispose(); _psScale?.Dispose(); _psEasu?.Dispose(); _psRcas?.Dispose();
        _linear?.Dispose(); _point?.Dispose(); _raster?.Dispose();
        _cbConvert?.Dispose(); _cbScale?.Dispose(); _cbEasu?.Dispose(); _cbRcas?.Dispose();
        Ctx?.Dispose();
        Device?.Dispose();
        Marshal.FreeHGlobal(_scratch);
    }
}
