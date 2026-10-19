using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace YuCap;

/// <summary>
/// NVIDIA RTX Video Super Resolution / RTX Video HDR through the D3D11 video processor. The driver exposes both as a stream
/// extension of ID3D11VideoContext::VideoProcessorSetStreamExtension (the same calls Chromium (swap_chain_presenter.cc) makes):
///   super resolution: GUID {d43ce1b3-1f4b-48ac-baee-c3c25375e6f7}, struct {version 1, method 2, enable}
///   TrueHDR:          GUID {fdd62bb4-620b-4fd7-9ab3-1e59d0d544b3}, struct {version 4, method 3, enable:1 + reserved:31};
///                     its support is read back as one UINT with the same GUID (1 = supported); it needs an HDR10 output.
/// The user must also have the RTX video enhancement switched on in the NVIDIA Control Panel - the call succeeds either way, so
/// whether the picture really changes is measured by D3DRenderer (VSR on versus off).
/// </summary>
internal sealed class NvVideoProc : IDisposable
{
    public static readonly Guid PpeGuid = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");
    public static readonly Guid TrueHdrGuid = new("fdd62bb4-620b-4fd7-9ab3-1e59d0d544b3");

    private readonly ID3D11VideoDevice _vd;
    private readonly ID3D11VideoContext _vc;
    private readonly ID3D11VideoContext1? _vc1;
    private ID3D11VideoProcessorEnumerator? _en;
    private ID3D11VideoProcessor? _vp;
    private (int iw, int ih, int ow, int oh) _key;
    private bool _stateKnown;
    private static int s_vsr = -1, s_hdr = -1;

    private ID3D11Texture2D? _inTex, _outTex;
    private ID3D11VideoProcessorInputView? _inView;
    private ID3D11VideoProcessorOutputView? _outView;

    public static string LastFailure = "";
    public static string LastHdrQuery = "";
    /// <summary>Experiment hooks (probe only): colour spaces given to the stream / output when HDR is on.</summary>
    public static ColorSpaceType? ExpStreamCs, ExpOutputCs;
    public static bool ExpNoColorSpace;
    /// <summary>True when the driver answers the TrueHDR support query with 1 (read once per processor).</summary>
    public bool HdrSupported { get; private set; }
    /// <summary>The last SetStreamExtension for super resolution returned success.</summary>
    public bool VsrCallOk { get; private set; } = true;

    private NvVideoProc(ID3D11VideoDevice vd, ID3D11VideoContext vc, ID3D11VideoContext1? vc1)
    { _vd = vd; _vc = vc; _vc1 = vc1; }

    /// <summary>NVIDIA adapter + video processor available; null otherwise (LastFailure says why).</summary>
    public static NvVideoProc? TryCreate(ID3D11Device dev, ID3D11DeviceContext ctx)
    {
        try
        {
            using (IDXGIDevice dx = dev.QueryInterface<IDXGIDevice>())
            using (IDXGIAdapter ad = dx.GetAdapter())
            {
                if (ad.Description.VendorId != 0x10DE) { LastFailure = "NVIDIA 以外の GPU です"; return null; }
            }
            ID3D11VideoDevice? vd = dev.QueryInterfaceOrNull<ID3D11VideoDevice>();
            ID3D11VideoContext? vc = ctx.QueryInterfaceOrNull<ID3D11VideoContext>();
            if (vd == null || vc == null) { vd?.Dispose(); vc?.Dispose(); LastFailure = "ビデオプロセッサを取得できません"; return null; }
            return new NvVideoProc(vd, vc, ctx.QueryInterfaceOrNull<ID3D11VideoContext1>());
        }
        catch (Exception ex) { LastFailure = ex.Message.Split('\n')[0]; return null; }
    }

    private void Prepare(int iw, int ih, int ow, int oh)
    {
        if (_vp != null && _key == (iw, ih, ow, oh)) return;
        DropViews();
        _vp?.Dispose(); _en?.Dispose(); _vp = null; _en = null;
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1),
            InputWidth = (uint)iw, InputHeight = (uint)ih, OutputWidth = (uint)ow, OutputHeight = (uint)oh,
            Usage = VideoUsage.PlaybackNormal,
        };
        _en = _vd.CreateVideoProcessorEnumerator(desc);
        _vp = _vd.CreateVideoProcessor(_en, 0);
        _key = (iw, ih, ow, oh);
        _stateKnown = false;
    }

    /// <summary>Let go of the cached input / output views: a view on the swap chain's back buffer would make ResizeBuffers fail (DXGI_ERROR_INVALID_CALL).</summary>
    public void DropViews()
    {
        _inView?.Dispose(); _outView?.Dispose(); _inView = null; _outView = null; _inTex = null; _outTex = null;
    }

    private bool SetExt(Guid g, uint version, uint method, uint enable)
    {
        IntPtr p = Marshal.AllocHGlobal(12);
        try
        {
            Marshal.WriteInt32(p, 0, (int)version); Marshal.WriteInt32(p, 4, (int)method); Marshal.WriteInt32(p, 8, (int)enable);
            Result r = _vc.VideoProcessorSetStreamExtension(_vp!, 0, g, 12, p);
            if (r.Failure) { LastFailure = $"拡張の設定に失敗 0x{r.Code:X8}"; return false; }
            return true;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private bool QueryHdr()
    {
        IntPtr p = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(p, 0);
            Result r = _vc.VideoProcessorGetStreamExtension(_vp!, 0, TrueHdrGuid, 4, p);
            int v = Marshal.ReadInt32(p);
            LastHdrQuery = $"GetStreamExtension(TrueHDR) -> 0x{r.Code:X8}, value {v}";
            return r.Success && v == 1;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>One video-processor blit of <paramref name="srcRect"/> of <paramref name="src"/> (BGRA, SDR) into the whole of <paramref name="dst"/>.
    /// With <paramref name="hdr"/> the output is HDR10 (dst must be R10G10B10A2) and the TrueHDR extension is applied.</summary>
    public bool Blit(ID3D11Texture2D src, Rectangle srcRect, ID3D11Texture2D dst, Size dstSize, bool vsr, bool hdr)
    {
        try
        {
            Texture2DDescription sd = src.Description;
            Prepare((int)sd.Width, (int)sd.Height, dstSize.Width, dstSize.Height);
            // The driver keeps the extension state per device context, not per processor: a second processor (the scaler and the HDR
            // conversion each have one) would otherwise inherit the other's setting. So the state last written is tracked process-wide
            // and written again whenever this blit wants something else (measured: with a stale "on" a later "off" changed nothing).
            int wv = vsr ? 1 : 0, wh = hdr ? 1 : 0;
            if (!_stateKnown) HdrSupported = QueryHdr();
            if (!_stateKnown || s_vsr != wv) { VsrCallOk = SetExt(PpeGuid, 1, 2, (uint)wv); s_vsr = wv; }
            if (HdrSupported && (!_stateKnown || s_hdr != wh)) { SetExt(TrueHdrGuid, 4, 3, (uint)wh); s_hdr = wh; }
            _stateKnown = true;
            if (_vc1 != null && !ExpNoColorSpace)
            {
                _vc1.VideoProcessorSetStreamColorSpace1(_vp!, 0, ExpStreamCs ?? ColorSpaceType.RgbFullG22NoneP709);
                _vc1.VideoProcessorSetOutputColorSpace1(_vp!, hdr ? (ExpOutputCs ?? ColorSpaceType.RgbFullG2084NoneP2020) : ColorSpaceType.RgbFullG22NoneP709);
            }
            if (!ReferenceEquals(_inTex, src))
            {
                _inView?.Dispose();
                _inView = _vd.CreateVideoProcessorInputView(src, _en!, new VideoProcessorInputViewDescription
                { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 } });
                _inTex = src;
            }
            if (!ReferenceEquals(_outTex, dst))
            {
                _outView?.Dispose();
                _outView = _vd.CreateVideoProcessorOutputView(dst, _en!, new VideoProcessorOutputViewDescription
                { ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 } });
                _outTex = dst;
            }
            var sr = new Vortice.RawRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom);
            var dr = new Vortice.RawRect(0, 0, dstSize.Width, dstSize.Height);
            _vc.VideoProcessorSetStreamSourceRect(_vp!, 0, true, sr);
            _vc.VideoProcessorSetStreamDestRect(_vp!, 0, true, dr);
            _vc.VideoProcessorSetOutputTargetRect(_vp!, true, dr);
            var st = new VideoProcessorStream { Enable = true, InputSurface = _inView };
            Result r = _vc.VideoProcessorBlt(_vp!, _outView!, 0, 1, new[] { st });
            if (r.Failure) { LastFailure = $"VideoProcessorBlt 0x{r.Code:X8}"; return false; }
            return true;
        }
        catch (Exception ex) { LastFailure = ex.Message.Split('\n')[0]; return false; }
    }

    public void Dispose()
    {
        DropViews();
        _vp?.Dispose(); _en?.Dispose(); _vc1?.Dispose(); _vc.Dispose(); _vd.Dispose();
        _vp = null; _en = null;
    }
}
