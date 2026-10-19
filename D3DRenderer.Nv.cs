using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

// NVIDIA RTX Video Super Resolution (a scaler) and the HDR display (RTX Video HDR through the video processor, or our own inverse
// tone map). Both are optional extras around Compose / PresentFrame: with the scaler not chosen and HDR off none of this runs.
internal sealed partial class D3DRenderer
{
    // ------------------------------------------------------------------ settings / shared state
    /// <summary>0 = off, 1 = RTX Video HDR (AI, falls back to 2), 2 = simple inverse tone map. Written by the engine.</summary>
    public static volatile int HdrModeRequested;
    /// <summary>Paper white (nits SDR white is shown at), peak (nits SDR white is expanded up to by the simple map) and the linear level where expansion starts.</summary>
    public static float HdrPaperWhite = 203f, HdrPeak = 600f, HdrKnee = 0.5f;
    /// <summary>VSR measured against the same picture with it off: 0 = not measured, 1 = changes the picture, -1 = identical (the control panel setting is off).</summary>
    public static volatile int NvEffective;
    public static volatile bool NvMeasureDue = true;
    public static string NvNote = "";
    public static double NvVsrDiff;
    /// <summary>The scaler could not run on this machine (not NVIDIA / no video processor / the blit failed); FSR is used instead.</summary>
    public static volatile bool NvVsrBroken;
    public static string NvBrokenWhy = "";

    /// <summary>What the HDR display shows now (0 SDR, 1 RTX Video HDR, 2 simple) and why not what was asked; written by the render thread.</summary>
    public static volatile string HdrSwapColorSpace = "G22 / sRGB (SDR)";
    public static volatile int HdrActiveKind;
    public static volatile string HdrActiveNote = "";

    private static int _nvAdapter = -1;
    /// <summary>An NVIDIA GPU is installed (DXGI adapter list; cached).</summary>
    public static bool NvAdapterPresent()
    {
        if (_nvAdapter >= 0) return _nvAdapter == 1;
        bool found = false;
        try
        {
            using IDXGIFactory1 f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; f.EnumAdapters1(i, out IDXGIAdapter1? a).Success && a != null; i++)
                using (a) if (a.Description1.VendorId == 0x10DE) found = true;
        }
        catch { }
        _nvAdapter = found ? 1 : 0;
        return found;
    }

    private IntPtr _presentHwnd;
    private NvVideoProc? _nvS, _nvH;
    private bool _nvSTried, _nvHTried;
    private ID3D11Texture2D? _nvOut;
    private ID3D11ShaderResourceView? _nvOutSrv;
    private Size _nvOutSize;

    /// <summary>The scaler's output is sized after the destination; true when <paramref name="srv"/> holds the picture at <paramref name="to"/>.</summary>
    private bool NvScale(ID3D11Texture2D src, Size srcSize, Size to, out ID3D11ShaderResourceView? srv)
    {
        srv = null;
        if (NvVsrBroken || to.Width <= srcSize.Width || to.Height <= srcSize.Height) return false;
        if (!_nvSTried)
        {
            _nvSTried = true;
            _nvS = NvVideoProc.TryCreate(Device, Ctx);
            if (_nvS == null) { NvVsrBroken = true; NvBrokenWhy = NvVideoProc.LastFailure; Log.Info("D3D: NVIDIA RTX scaler unavailable: " + NvBrokenWhy); return false; }
        }
        if (_nvS == null) return false;
        if (_nvOut == null || _nvOutSize != to)
        {
            _nvOutSrv?.Dispose(); _nvOut?.Dispose();
            _nvOut = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)to.Width, Height = (uint)to.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _nvOutSrv = Device.CreateShaderResourceView(_nvOut);
            _nvOutSize = to;
        }
        if (!_nvS.Blit(src, new Rectangle(Point.Empty, srcSize), _nvOut, to, vsr: true, hdr: false))
        {
            NvVsrBroken = true; NvBrokenWhy = NvVideoProc.LastFailure;
            Log.Info("D3D: NVIDIA RTX scaler failed, using FSR: " + NvBrokenWhy);
            return false;
        }
        srv = _nvOutSrv;
        if (NvMeasureDue) { NvMeasureDue = false; try { MeasureVsr(); } catch (Exception ex) { Log.Info("D3D: VSR check failed: " + ex.Message.Split('\n')[0]); } }
        return true;
    }

    // ------------------------------------------------------------------ is VSR doing anything?
    /// <summary>A fixed test picture with fine edges, text-like strokes and gradients (CPU made, BGRA).</summary>
    internal static byte[] NvTestPicture(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(30, 40, 60));
            using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(40, 90, 200), Color.FromArgb(230, 190, 60), 35f);
            g.FillRectangle(br, 0, h / 3, w, h / 3);
            using var pen = new Pen(Color.White, 1.3f);
            for (int i = 0; i < 40; i++) g.DrawLine(pen, i * w / 40, 0, w - i * w / 40, h / 3);
            using var f = new Font("Arial", Math.Max(8, h / 22f), FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("Super Resolution 1234567890 ABCxyz", f, Brushes.White, 6, h / 3 + 6);
            g.DrawString("edges and small text", f, Brushes.Black, 6, h / 3 + h / 10);
            g.DrawEllipse(pen, w / 5, 2 * h / 3, w / 3, h / 4);
            for (int i = 0; i < 24; i++) g.FillRectangle(i % 2 == 0 ? Brushes.White : Brushes.Black, w / 2 + i * 4, 2 * h / 3 + 4, 2, h / 4);
        }
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, buf, y * w * 4, w * 4);
            return buf;
        }
        finally { bmp.UnlockBits(d); }
    }

    private ID3D11Texture2D MakeTex(byte[] bgra, int w, int h, Format fmt = Format.B8G8R8A8_UNorm)
    {
        var tex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        var hd = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try { Ctx.UpdateSubresource(tex, 0, null, hd.AddrOfPinnedObject(), (uint)(w * 4), 0); }
        finally { hd.Free(); }
        return tex;
    }

    private byte[] ReadTexBytes(ID3D11Texture2D tex, int w, int h, int bpp)
    {
        var st = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = tex.Description.Format, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        using (st)
        {
            Ctx.CopyResource(st, tex);
            MappedSubresource m = Ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var buf = new byte[w * h * bpp];
                for (int y = 0; y < h; y++) Marshal.Copy(m.DataPointer + y * (int)m.RowPitch, buf, y * w * bpp, w * bpp);
                return buf;
            }
            finally { Ctx.Unmap(st, 0); }
        }
    }

    /// <summary>Runs the video processor on a test picture with super resolution on and off and compares the two results.
    /// Identical = the driver is not applying it (the control panel's RTX video enhancement is off). Returns the mean absolute difference (0..255).</summary>
    public double MeasureVsr()
    {
        const int W = 960, H = 540, OW = 1920, OH = 1080;
        _nvSTried = true;
        NvVideoProc? nv = _nvS ??= NvVideoProc.TryCreate(Device, Ctx);   // the scaler's own processor: a second one would see the first one's driver state
        if (nv == null) { NvEffective = 0; NvNote = NvVideoProc.LastFailure; return -1; }
        using ID3D11Texture2D src = MakeTex(NvTestPicture(W, H), W, H);
        var od = new Texture2DDescription
        {
            Width = OW, Height = OH, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        using ID3D11Texture2D on = Device.CreateTexture2D(od), off = Device.CreateTexture2D(od);
        bool ok = true;
        for (int i = 0; i < 4 && ok; i++) ok = nv.Blit(src, new Rectangle(0, 0, W, H), on, new Size(OW, OH), true, false);   // a few runs: the driver may need a moment to start the network
        ok = ok && nv.Blit(src, new Rectangle(0, 0, W, H), off, new Size(OW, OH), false, false);
        ok = ok && nv.Blit(src, new Rectangle(0, 0, W, H), on, new Size(OW, OH), true, false);
        if (!ok) { NvEffective = 0; NvNote = NvVideoProc.LastFailure; return -1; }
        byte[] a = ReadTexBytes(on, OW, OH, 4), b = ReadTexBytes(off, OW, OH, 4);
        long sum = 0;
        for (int i = 0; i < a.Length; i += 4) { sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]); }
        double mean = sum / (3.0 * (a.Length / 4));
        NvVsrDiff = mean;
        NvEffective = mean > 0.05 ? 1 : -1;
        NvNote = nv.VsrCallOk ? "" : "拡張の設定が失敗しました";
        return mean;
    }

    // ------------------------------------------------------------------ HDR display
    private ID3D11PixelShader? _psHdr, _psOverlayPq, _psCopy;
    private ID3D11Buffer? _cbHdr;
    private ID3D11Texture2D? _sdrTex;
    private ID3D11ShaderResourceView? _sdrSrv;
    private ID3D11RenderTargetView? _sdrRtv;
    private Size _hdrTexSize;
    private bool _overlayPq;
    private volatile int _hdrKind;               // 0 = SDR swap chain, 1 = RTX Video HDR, 2 = simple (the swap chain is HDR10)
    private bool _hdrRtxBroken;
    private long _hdrCheckAt;
    private Program.OutputHdr? _monHdr;
    private volatile string _hdrNote = "";

    private void CreateHdrShaders(D3DShaders.Bytecode b)
    {
        if (!b.HdrOk) return;
        try
        {
            _psHdr = Device.CreatePixelShader(b.Hdr);
            _psOverlayPq = Device.CreatePixelShader(b.HdrOverlay);
            _psCopy = Device.CreatePixelShader(b.HdrCopy);
            _cbHdr = Device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        }
        catch (Exception ex)
        {
            Log.Info("D3D: HDR shader could not be created: " + ex.Message.Split('\n')[0]);
            _psHdr?.Dispose(); _psOverlayPq?.Dispose(); _psCopy?.Dispose(); _cbHdr?.Dispose(); _psHdr = null; _psOverlayPq = null; _psCopy = null; _cbHdr = null;
        }
    }

    private void DisposeNv()
    {
        FreeHdrTextures();
        _psHdr?.Dispose(); _psOverlayPq?.Dispose(); _psCopy?.Dispose(); _cbHdr?.Dispose();
        _nvS?.Dispose(); _nvH?.Dispose(); _nvS = _nvH = null;
        _nvOutSrv?.Dispose(); _nvOut?.Dispose(); _nvOutSrv = null; _nvOut = null;
    }

    private void FreeHdrTextures()
    {
        _sdrRtv?.Dispose(); _sdrSrv?.Dispose(); _sdrTex?.Dispose();
        _sdrRtv = null; _sdrSrv = null; _sdrTex = null;
        _hdrTexSize = Size.Empty;
    }

    private void EnsureHdrTextures(Size client)
    {
        if (_sdrTex != null && _hdrTexSize == client) return;
        FreeHdrTextures();
        _sdrTex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)client.Width, Height = (uint)client.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _sdrSrv = Device.CreateShaderResourceView(_sdrTex); _sdrRtv = Device.CreateRenderTargetView(_sdrTex);
        _hdrTexSize = client;
    }

    /// <summary>What the HDR display is doing right now (any thread; for the info panel and the test mode).</summary>
    public HdrStatus HdrNow => new(_hdrKind, _monHdr?.HdrOn ?? false, _monHdr?.ColorSpace ?? "?", _hdrNote, _swapColorSpace, _nvH?.HdrSupported ?? false, _monHdr?.MaxLuminance ?? 0);
    public readonly record struct HdrStatus(int Kind, bool MonitorHdr, string MonitorColorSpace, string Note, string SwapColorSpace, bool RtxHdrSupported, float MonitorMaxNits);
    private string _swapColorSpace = "G22 / sRGB (SDR)";

    private static Program.OutputHdr? FindOutput(string deviceName)
    {
        try
        {
            List<Program.OutputHdr> all = Program.QueryOutputsHdr();
            return all.FirstOrDefault(o => string.Equals(o.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>The swap chain is R10G10B10A2 with the SDR colour space because the monitor is in Windows HDR mode and the user's HDR display is off.
    /// A composition swap chain of B8G8R8A8 on such a monitor is composed by DWM and held to the primary monitor's refresh rate (measured: 60 Hz on a
    /// 144 Hz TV beside a 59.94 Hz primary); a 10-bit one is presented at the monitor's own rate. The picture is the same sRGB codes, DWM maps them as for any SDR window.</summary>
    private volatile bool _wideSdr;
    private bool _wideFailed, _monHdrBusy;
    /// <summary>Only the composition (vsync) swap chain needs it: a window swap chain is not held back.</summary>
    private bool WideEligible => _psCopy != null && !_wideFailed && _swap != null && PresentMode == "composition vsync";

    /// <summary>Render thread, before every frame: switch the swap chain between SDR, 10-bit SDR (Windows HDR monitor) and HDR10 to match the request and the monitor.</summary>
    private void HdrUpdate()
    {
        int want = HdrModeRequested;
        bool wideOk = WideEligible;
        if (want == 0 && _hdrKind == 0 && !wideOk && !_wideSdr) return;
        long now = Environment.TickCount64;
        if (_hdrCheckAt == 0)
        {
            _hdrCheckAt = now;
            _monHdr = FindOutput(Displays.DeviceNameOfWindow(_presentHwnd));
        }
        else if (now - _hdrCheckAt > 1000 && !_monHdrBusy)
        {
            // the DXGI output walk takes milliseconds: not on the render thread (the result is picked up a moment later)
            _hdrCheckAt = now; _monHdrBusy = true;
            string dev = Displays.DeviceNameOfWindow(_presentHwnd);
            ThreadPool.QueueUserWorkItem(_ => { try { _monHdr = FindOutput(dev) ?? _monHdr; } finally { _monHdrBusy = false; } });
        }
        bool monHdr = _monHdr?.HdrOn ?? false;
        bool can = want != 0 && _psHdr != null && monHdr && _swap != null;
        int kind = !can ? 0 : (want == 1 && !_hdrRtxBroken ? 1 : 2);
        if (kind == 1 && !RtxHdrReady()) { kind = 2; _hdrRtxWhy = "このドライバー・GPU では RTX Video HDR を使えません"; }
        bool wide = kind == 0 && monHdr && wideOk;
        if (kind == _hdrKind && wide == _wideSdr) return;
        if (_hdrKind == 0 && kind != 0 && !SetSwapMode(2)) { _hdrNote = "HDR の出力に切り替えられませんでした"; kind = 0; HdrModeRequested = 0; return; }
        if (kind == 1 && !_hdrRtxChecked)
        {
            // the driver's HDR must really change the picture on the HDR10 back buffer (it does nothing for some setups): measure on versus off once
            _hdrRtxChecked = true;
            if (!RtxHdrEngaged()) { _hdrRtxBroken = true; kind = 2; _hdrRtxWhy = "RTX Video HDR がこの環境で働かないため（オン / オフで絵が変わらない）"; Log.Info("D3D: RTX Video HDR does not change the picture here - using the simple HDR"); }
            if (kind == _hdrKind) { HdrActiveKind = kind; HdrActiveNote = _hdrNote = _hdrRtxWhy + "、簡易の HDR にしています"; return; }
        }
        if (kind == 0 && (_hdrKind != 0 || wide != _wideSdr))
        {
            if (!SetSwapMode(wide ? 1 : 0) && wide) { _wideFailed = true; Log.Info("D3D: the 10-bit SDR swap chain could not be set up - staying with B8G8R8A8"); }
        }
        if (kind != 0) _hdrNote = kind == 1 ? "" : (want == 1 ? _hdrRtxWhy + "、簡易の HDR にしています" : "");
        else _hdrNote = want != 0 && !monHdr ? "このモニターの HDR がオフです" : "";
        _hdrKind = kind;
        HdrActiveKind = kind; HdrActiveNote = _hdrNote;
        Log.Info($"D3D: swap chain {_swapFormatDesc} (HDR display kind {kind}, requested {want}, monitor HDR {monHdr})");
    }

    private bool RtxHdrReady()
    {
        if (_nvH == null && !_nvHTried)
        {
            _nvHTried = true;
            _nvH = NvVideoProc.TryCreate(Device, Ctx);
        }
        if (_nvH == null) return false;
        if (!_nvHProbed)
        {
            _nvHProbed = true;
            // a first blit tells whether the driver supports TrueHDR (the support flag is read once the processor exists)
            try
            {
                using ID3D11Texture2D s = MakeTex(new byte[64 * 64 * 4], 64, 64);
                using ID3D11Texture2D o = Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = 64, Height = 64, MipLevels = 1, ArraySize = 1, Format = Format.R10G10B10A2_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                });
                if (!_nvH.Blit(s, new Rectangle(0, 0, 64, 64), o, new Size(64, 64), false, true) || !_nvH.HdrSupported) _hdrRtxBroken = true;
                if (_hdrRtxBroken) Log.Info("D3D: RTX Video HDR unavailable: " + NvVideoProc.LastFailure + " / " + NvVideoProc.LastHdrQuery);
            }
            catch { _hdrRtxBroken = true; }
        }
        return !_hdrRtxBroken;
    }
    private bool _nvHProbed, _hdrRtxChecked;
    private string _hdrRtxWhy = "";

    /// <summary>On the HDR10 back buffer: video-processor output with TrueHDR on versus off for a grey ramp. Identical = the driver does nothing.</summary>
    private bool RtxHdrEngaged()
    {
        try
        {
            int W = _swapSize.Width, H = _swapSize.Height;
            if (W <= 0 || H <= 0 || _nvH == null) return false;
            EnsureHdrTextures(_swapSize);
            var px = new byte[W * H * 4];
            for (int x = 0; x < W; x++) { byte c = (byte)(x * 255 / Math.Max(1, W - 1)); px[x * 4] = px[x * 4 + 1] = px[x * 4 + 2] = c; px[x * 4 + 3] = 255; }
            for (int y = 1; y < H; y++) Buffer.BlockCopy(px, 0, px, y * W * 4, W * 4);
            var h = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { Ctx.UpdateSubresource(_sdrTex!, 0, null, h.AddrOfPinnedObject(), (uint)(W * 4), 0); }
            finally { h.Free(); }
            byte[] off, on;
            using (ID3D11Texture2D bb = _swap!.GetBuffer<ID3D11Texture2D>(0))
            {
                var rect = new Rectangle(0, 0, W, H);
                bool ok = true;
                for (int i = 0; i < 3 && ok; i++) ok = _nvH.Blit(_sdrTex!, rect, bb, _swapSize, false, false);
                off = ReadTexBytes(bb, W, H, 4);
                for (int i = 0; i < 4 && ok; i++) ok = _nvH.Blit(_sdrTex!, rect, bb, _swapSize, false, true);
                if (!ok) return false;
                on = ReadTexBytes(bb, W, H, 4);
            }
            int diff = 0, n = 0;
            for (int x = 0; x < W; x += 16)
            {
                int o = ((H / 2) * W + x) * 4;
                uint a = BitConverter.ToUInt32(off, o), b = BitConverter.ToUInt32(on, o);
                n++;
                if (Math.Abs((int)(a & 0x3FF) - (int)(b & 0x3FF)) > 3 || Math.Abs((int)((a >> 10) & 0x3FF) - (int)((b >> 10) & 0x3FF)) > 3) diff++;
            }
            Log.Info($"D3D: RTX Video HDR check: {diff}/{n} samples differ between TrueHDR on and off");
            return diff > n / 20;
        }
        catch (Exception ex) { Log.Info("D3D: RTX Video HDR check failed: " + ex.Message.Split((char)10)[0]); return false; }
    }

    /// <summary>0 = B8G8R8A8 + sRGB (SDR), 1 = R10G10B10A2 + sRGB (SDR picture, Windows HDR monitor), 2 = R10G10B10A2 + HDR10 (G2084, BT.2020).</summary>
    private bool SetSwapMode(int mode)
    {
        if (_swap == null) return false;
        try
        {
            using IDXGISwapChain3? sc3 = _swap.QueryInterfaceOrNull<IDXGISwapChain3>();
            if (sc3 == null) return false;
            ColorSpaceType cs = mode == 2 ? ColorSpaceType.RgbFullG2084NoneP2020 : ColorSpaceType.RgbFullG22NoneP709;
            if (!sc3.CheckColorSpaceSupport(cs).HasFlag(SwapChainColorSpaceSupportFlags.Present)) { Log.Info("D3D: swap chain does not support " + cs); return false; }
            lock (FrameLock)
            {
                Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                Ctx.ClearState();
                _nvH?.DropViews();      // the video processor keeps an output view on the back buffer it blitted into last
                Ctx.Flush();
            }
            _swap.ResizeBuffers(0, (uint)_swapSize.Width, (uint)_swapSize.Height, mode == 0 ? Format.B8G8R8A8_UNorm : Format.R10G10B10A2_UNorm, _swapFlags).CheckError();
            sc3.SetColorSpace1(cs);
            _wideSdr = mode == 1;
            _swapFormatDesc = mode switch { 0 => "B8G8R8A8 / sRGB (SDR)", 1 => "R10G10B10A2 / sRGB (SDR picture; Windows HDR monitor)", _ => "R10G10B10A2 / G2084 BT.2020 (HDR10)" };
            _swapColorSpace = mode == 2 ? "G2084 / BT.2020 (HDR10), R10G10B10A2" : mode == 1 ? "G22 / sRGB (SDR), R10G10B10A2" : "G22 / sRGB (SDR)";
            HdrSwapColorSpace = _swapColorSpace;
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("D3D: switching the swap chain format failed: " + ex.Message.Split('\n')[0]);
            try { _swap.ResizeBuffers(0, (uint)_swapSize.Width, (uint)_swapSize.Height, Format.B8G8R8A8_UNorm, _swapFlags); } catch { }
            _wideSdr = false; _swapFormatDesc = "B8G8R8A8 / sRGB (SDR)";
            return false;
        }
    }
    private volatile string _swapFormatDesc = "B8G8R8A8 / sRGB (SDR)";
    /// <summary>The swap chain format and colour space in use (any thread; for the test mode).</summary>
    public string SwapFormatDesc => _swapFormatDesc;
    /// <summary>The 10-bit SDR swap chain is in use (the monitor is in Windows HDR mode, the HDR display is off).</summary>
    public bool WideSdrActive => _wideSdr;

    /// <summary>SDR picture -> 10-bit back buffer, codes unchanged.</summary>
    private void WideCopy(ID3D11ShaderResourceView srcSrv, ID3D11RenderTargetView outRtv, Size size)
    {
        SetupTriangle(outRtv, new Rectangle(Point.Empty, size));
        Ctx.PSSetShader(_psCopy);
        Ctx.PSSetSampler(0, _point);
        Ctx.PSSetShaderResource(0, srcSrv);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);
    }

    private void WriteHdrCb()
    {
        WriteCb(_cbHdr!, new Vector4(HdrPaperWhite, Math.Max(HdrPaperWhite, HdrPeak), HdrKnee, 0));
    }

    /// <summary>SDR picture (<paramref name="srcSrv"/>/<paramref name="srcTex"/>) -> HDR10 render target. kind 1 = video processor TrueHDR, else our shader.</summary>
    private void HdrConvert(int kind, ID3D11Texture2D srcTex, ID3D11ShaderResourceView srcSrv, ID3D11Texture2D outTex, ID3D11RenderTargetView outRtv, Size size)
    {
        if (kind == 1 && _nvH != null && _nvH.Blit(srcTex, new Rectangle(Point.Empty, size), outTex, size, false, true)) return;
        if (kind == 1) { _hdrRtxBroken = true; _hdrNote = "RTX Video HDR が失敗したため、簡易の HDR にしています"; _hdrKind = 2; HdrActiveKind = 2; HdrActiveNote = _hdrNote; Log.Info("D3D: RTX Video HDR blit failed: " + NvVideoProc.LastFailure); }
        WriteHdrCb();
        SetupTriangle(outRtv, new Rectangle(Point.Empty, size));
        Ctx.PSSetShader(_psHdr);
        Ctx.PSSetConstantBuffer(1, _cbHdr);
        Ctx.PSSetSampler(0, _point);
        Ctx.PSSetShaderResource(0, srcSrv);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!);
    }

    private volatile bool _hdrRtxReadback;
    private byte[]? _hdrRtxRaw;

    private Result PresentFrameHdr(Rectangle dst, ScalingQuality quality, bool pixelPerfect, float sharpness, int nativeH, OverlayLayer[]? overlays)
    {
        EnsureHdrTextures(_swapSize);
        _gpu?.Begin();
        Compose(_sdrRtv!, _swapSize, dst, quality, pixelPerfect, sharpness, _interRtv, _interSrv, nativeH);
        Ctx.CopyResource(_lastComposed!, _sdrTex!);          // stills stay SDR
        _composedDst = dst;
        _composedValid = true;
        using (ID3D11Texture2D bb = _swap!.GetBuffer<ID3D11Texture2D>(0))
        using (ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(bb))
        {
            // straight into the HDR10 back buffer: the driver applies RTX Video HDR to a video-processor output that is a swap-chain buffer
            if (_hdrKind != 0) HdrConvert(_hdrKind, _sdrTex!, _sdrSrv!, bb, rtv, _swapSize);
            else WideCopy(_sdrSrv!, rtv, _swapSize);
            _gpu?.End();
            if (_hdrKind == 1 && _hdrRtxReadback) { _hdrRtxReadback = false; _hdrRtxRaw = ReadTexBytes(bb, _swapSize.Width, _swapSize.Height, 4); }
            if (_hdrKind != 0 && overlays != null && overlays.Length > 0) { WriteHdrCb(); Ctx.PSSetConstantBuffer(1, _cbHdr); }
            _overlayPq = _hdrKind != 0;
            try { DrawOverlays(rtv, _swapSize, overlays); }
            finally { _overlayPq = false; }
        }
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        Result pr = _swap.Present(_syncInterval, _presentFlags);
        if (pr.Success) Volatile.Read(ref _pacing)?.Record(Beat.Mark());
        _gpu?.Poll();
        return pr;
    }

    // ------------------------------------------------------------------ test hooks
    /// <summary>Run a flat grey/white patch set (sRGB code values <paramref name="codes"/>) through the HDR conversion of <paramref name="kind"/> (1 RTX, 2 simple)
    /// into an R10G10B10A2 target and return the nits (PQ decoded, max of R/G/B) per patch; null when that path cannot run.</summary>
    internal float[]? HdrPatchProbe(int[] codes, int kind, out string why)
    {
        const int W = 256, H = 256;
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int c = codes[Math.Min(codes.Length - 1, x * codes.Length / W)];
                int o = (y * W + x) * 4;
                px[o] = px[o + 1] = px[o + 2] = (byte)c; px[o + 3] = 255;
            }
        float[]? all = HdrRun(px, W, H, kind, out why);
        if (all == null) return null;
        var res = new float[codes.Length];
        for (int i = 0; i < codes.Length; i++) res[i] = all[(H / 2) * W + (i * 2 + 1) * W / (codes.Length * 2)];
        return res;
    }

    /// <summary>The whole BGRA picture through the HDR conversion of <paramref name="kind"/>; returns the nits (max of R/G/B, PQ decoded) of every pixel.</summary>
    internal float[]? HdrRun(byte[] bgra, int W, int H, int kind, out string why, Format outFmt = Format.R10G10B10A2_UNorm, bool raw10 = false)
    {
        why = "";
        if (_psHdr == null) { why = "シェーダーがありません"; return null; }
        if (kind is 1 or 3 && !RtxHdrReady()) { why = "RTX Video HDR が使えません"; return null; }
        using ID3D11Texture2D src = MakeTex(bgra, W, H);
        using ID3D11ShaderResourceView srv = Device.CreateShaderResourceView(src);
        using ID3D11Texture2D outT = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)W, Height = (uint)H, MipLevels = 1, ArraySize = 1, Format = outFmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        using ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(outT);
        int saved = _hdrKind;
        _hdrKind = kind;
        try
        {
            if (kind is 1 or 3)
            {
                for (int i = 0; i < 3; i++) if (!_nvH!.Blit(src, new Rectangle(0, 0, W, H), outT, new Size(W, H), false, kind == 1)) { why = NvVideoProc.LastFailure; return null; }
            }
            else HdrConvert(2, src, srv, outT, rtv, new Size(W, H));
        }
        finally { _hdrKind = saved; Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>()); }
        var res = new float[W * H];
        if (raw10)
        {
            byte[] rr = ReadTexBytes(outT, W, H, 4);
            for (int i = 0; i < res.Length; i++) { uint v = BitConverter.ToUInt32(rr, i * 4); res[i] = Math.Max(v & 0x3FF, Math.Max((v >> 10) & 0x3FF, (v >> 20) & 0x3FF)); }
            return res;
        }
        if (outFmt == Format.R16G16B16A16_Float)
        {
            byte[] rf = ReadTexBytes(outT, W, H, 8);
            for (int i = 0; i < res.Length; i++)
                res[i] = 80f * Math.Max((float)BitConverter.ToHalf(rf, i * 8), Math.Max((float)BitConverter.ToHalf(rf, i * 8 + 2), (float)BitConverter.ToHalf(rf, i * 8 + 4)));
            return res;
        }
        byte[] raw = ReadTexBytes(outT, W, H, 4);
        for (int i = 0; i < res.Length; i++)
        {
            uint v = BitConverter.ToUInt32(raw, i * 4);
            float r = (v & 0x3FF) / 1023f, g = ((v >> 10) & 0x3FF) / 1023f, bl = ((v >> 20) & 0x3FF) / 1023f;
            res[i] = Math.Max(PqToNits(r), Math.Max(PqToNits(g), PqToNits(bl)));
        }
        return res;
    }

    /// <summary>Render thread: draw vertical grey bars (sRGB codes) over the whole swap-chain size through the live HDR path (the same call
    /// the stream of pictures uses), read the HDR10 back buffer before Present and return the nits of each bar (max of R/G/B, PQ decoded).</summary>
    internal float[]? HdrLiveCheck(int[] codes, out int kindUsed, out string why)
    {
        kindUsed = _hdrKind; why = "";
        if (_hdrKind == 0 || _swap == null || _swapSize.Width <= 0) { why = "HDR 表示になっていません"; return null; }
        int W = _swapSize.Width, H = _swapSize.Height;
        EnsureHdrTextures(_swapSize);
        var px = new byte[W * H * 4];
        for (int x = 0; x < W; x++)
        {
            byte c = (byte)codes[Math.Min(codes.Length - 1, x * codes.Length / W)];
            px[x * 4] = px[x * 4 + 1] = px[x * 4 + 2] = c; px[x * 4 + 3] = 255;
        }
        for (int y = 1; y < H; y++) Buffer.BlockCopy(px, 0, px, y * W * 4, W * 4);
        var h = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { Ctx.UpdateSubresource(_sdrTex!, 0, null, h.AddrOfPinnedObject(), (uint)(W * 4), 0); }
        finally { h.Free(); }
        byte[] raw;
        using (ID3D11Texture2D bb = _swap.GetBuffer<ID3D11Texture2D>(0))
        using (ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(bb))
        {
            for (int i = 0; i < 4; i++) HdrConvert(_hdrKind, _sdrTex!, _sdrSrv!, bb, rtv, _swapSize);
            raw = ReadTexBytes(bb, W, H, 4);
        }
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        kindUsed = _hdrKind;
        var res = new float[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            int x = (i * 2 + 1) * W / (codes.Length * 2);
            uint v = BitConverter.ToUInt32(raw, ((H / 2) * W + x) * 4);
            float r = (v & 0x3FF) / 1023f, g = ((v >> 10) & 0x3FF) / 1023f, bl = ((v >> 20) & 0x3FF) / 1023f;
            res[i] = Math.Max(PqToNits(r), Math.Max(PqToNits(g), PqToNits(bl)));
        }
        return res;
    }

    /// <summary>Render thread, 10-bit SDR swap chain only: draw vertical grey bars (sRGB codes) through the live path (<see cref="WideCopy"/>), read the back
    /// buffer before Present and return each bar as an 8-bit code (10-bit value / 1023 x 255). Null when the swap chain is not the 10-bit SDR one.</summary>
    internal float[]? WideLiveCheck(int[] codes, out string why)
    {
        why = "";
        if (!_wideSdr || _swap == null || _swapSize.Width <= 0) { why = "10bit の SDR 出力になっていません"; return null; }
        int W = _swapSize.Width, H = _swapSize.Height;
        EnsureHdrTextures(_swapSize);
        var px = new byte[W * H * 4];
        for (int x = 0; x < W; x++)
        {
            byte c = (byte)codes[Math.Min(codes.Length - 1, x * codes.Length / W)];
            px[x * 4] = px[x * 4 + 1] = px[x * 4 + 2] = c; px[x * 4 + 3] = 255;
        }
        for (int y = 1; y < H; y++) Buffer.BlockCopy(px, 0, px, y * W * 4, W * 4);
        var h = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { Ctx.UpdateSubresource(_sdrTex!, 0, null, h.AddrOfPinnedObject(), (uint)(W * 4), 0); }
        finally { h.Free(); }
        byte[] raw;
        using (ID3D11Texture2D bb = _swap.GetBuffer<ID3D11Texture2D>(0))
        using (ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(bb))
        {
            WideCopy(_sdrSrv!, rtv, _swapSize);
            raw = ReadTexBytes(bb, W, H, 4);
        }
        Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        var res = new float[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            int x = (i * 2 + 1) * W / (codes.Length * 2);
            uint v = BitConverter.ToUInt32(raw, ((H / 2) * W + x) * 4);
            res[i] = Math.Max(v & 0x3FF, Math.Max((v >> 10) & 0x3FF, (v >> 20) & 0x3FF)) / 1023f * 255f;
        }
        return res;
    }

    internal static float PqToNits(float e)
    {
        const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        double p = Math.Pow(Math.Clamp(e, 0f, 1f), 1.0 / m2);
        double y = Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1.0 / m1);
        return (float)(10000.0 * y);
    }
}
