using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

internal sealed partial class D3DRenderer
{
    /// <summary>Test hook (暗部補正 / adjustments): a synthetic full-range BT.709 NV12 picture (<paramref name="luma"/> w*h bytes,
    /// <paramref name="chroma"/> interleaved Cb,Cr of (w/2)*(h/2) pairs) through the REAL convert pass (the same pixel shader,
    /// constant buffer and 8-bit target as the live picture) with <paramref name="adj"/>, read back. The live frame textures are
    /// not touched. Must run on the render thread.</summary>
    internal Bitmap? ConvertSynthetic(byte[] luma, byte[] chroma, int w, int h, VideoAdjustments adj)
        => ConvertSynthetic(luma, chroma, w, h, adj, new YuvInfo(1, true), -1, 0, out _);

    /// <summary>The same with a chosen colour description (<paramref name="yuv"/>: matrix 1 = BT.709, 2 = BT.601; limited or full range),
    /// a forced chroma upsampling mode (<paramref name="chromaMode"/>, -1 = as set) and, with <paramref name="benchFrames"/> &gt; 0, the GPU
    /// milliseconds of that many convert draws of this picture.</summary>
    internal Bitmap? ConvertSynthetic(byte[] luma, byte[] chroma, int w, int h, VideoAdjustments adj, YuvInfo yuv, int chromaMode,
        int benchFrames, out double[] ms)
    {
        ms = Array.Empty<double>();
        if (_psConvert == null || w < 2 || h < 2 || (w & 1) != 0 || (h & 1) != 0) return null;
        if (luma.Length < w * h || chroma.Length < w * h / 2) return null;
        lock (FrameLock)
        {
            using ID3D11Texture2D ty = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            using ID3D11Texture2D tuv = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(w / 2), Height = (uint)(h / 2), MipLevels = 1, ArraySize = 1, Format = Format.R8G8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            using ID3D11ShaderResourceView sy = Device.CreateShaderResourceView(ty);
            using ID3D11ShaderResourceView suv = Device.CreateShaderResourceView(tuv);
            var hy = GCHandle.Alloc(luma, GCHandleType.Pinned);
            var hc = GCHandle.Alloc(chroma, GCHandleType.Pinned);
            try
            {
                Ctx.UpdateSubresource(ty, 0, null, hy.AddrOfPinnedObject(), (uint)w, 0);
                Ctx.UpdateSubresource(tuv, 0, null, hc.AddrOfPinnedObject(), (uint)w, 0);
            }
            finally { hy.Free(); hc.Free(); }
            var size = new Size(w, h);
            using ID3D11Texture2D target = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            using ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(target);

            // ConvertInto reads the frame textures and their colour description from the fields: lend it ours for this one draw.
            var saveY = _srvY; var saveUV = _srvUV; var saveGpu = _gpuFrame; var saveYuv = _yuv; int saveMode = ChromaModeOverride;
            try
            {
                _srvY = sy; _srvUV = suv; _gpuFrame = false; _yuv = yuv;
                ChromaModeOverride = chromaMode;
                ConvertInto(rtv, size, new Rectangle(Point.Empty, size), size, 0, false, adj);
                if (benchFrames > 0)
                    ms = TimeGpu(() => ConvertInto(rtv, size, new Rectangle(Point.Empty, size), size, 0, false, adj), benchFrames);
            }
            finally { _srvY = saveY; _srvUV = saveUV; _gpuFrame = saveGpu; _yuv = saveYuv; ChromaModeOverride = saveMode; }
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            return ReadBack(target, new Rectangle(Point.Empty, size));
        }
    }
}
