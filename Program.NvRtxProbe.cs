using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YuCap;

// Developer probe: --nvrtx-probe <out.txt>
//   NVIDIA RTX Video Super Resolution / RTX Video HDR through the video processor: does the extension work here, does VSR change the
//   picture, which monitors have Windows HDR on, and what the SDR -> HDR10 conversions give for a few known grey levels.
internal static partial class Program
{
    private static void RunNvRtxProbe(string outPath)
    {
        var log = new List<string>();
        D3DRenderer? r = null;
        try
        {
            r = new D3DRenderer();
            r.CreateDevice();
            r.CreateShaders();
            log.Add("adapter: " + r.AdapterName + " / NVIDIA adapter present: " + D3DRenderer.NvAdapterPresent());
            foreach (OutputHdr o in QueryOutputsHdr())
                log.Add($"output {o.DeviceName}: {o.Width}x{o.Height} colorSpace={o.ColorSpace} windowsHDR={(o.HdrOn ? "ON" : "off")} maxLum={o.MaxLuminance:0} nits");
            if (Environment.GetEnvironmentVariable("NVPROBE_SECOND") == "1")
            {
                // a first video processor with VSR on stays alive while MeasureVsr makes a second one
                NvVideoProc? first = NvVideoProc.TryCreate(r.Device, r.Ctx);
                using Vortice.Direct3D11.ID3D11Texture2D a = r.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription { Width = 640, Height = 360, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default, BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource });
                using Vortice.Direct3D11.ID3D11Texture2D b = r.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription { Width = 1280, Height = 720, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default, BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource });
                log.Add("first VP blit with VSR on: " + first!.Blit(a, new System.Drawing.Rectangle(0, 0, 640, 360), b, new System.Drawing.Size(1280, 720), true, false));
            }
            double d = r.MeasureVsr();
            log.Add($"VSR on vs off (960x540 -> 1920x1080 test picture): mean abs diff {d:0.000} / 255 -> effective={D3DRenderer.NvEffective} note='{D3DRenderer.NvNote}' lastFailure='{NvVideoProc.LastFailure}'");
            int[] codes = { 0, 32, 64, 128, 180, 220, 255 };
            foreach (int kind in new[] { 2, 1 })
            {
                float[]? n = r.HdrPatchProbe(codes, kind, out string why);
                log.Add($"HDR kind {kind} ({(kind == 1 ? "RTX Video HDR via video processor" : "simple shader")}): " +
                    (n == null ? "unavailable: " + why + " / " + NvVideoProc.LastHdrQuery + " / " + NvVideoProc.LastFailure : string.Join("  ", codes.Select((c, i) => $"{c}->{n[i]:0.0}nits"))));
            }
            byte[] pic = D3DRenderer.NvTestPicture(960, 540);
            foreach (int kind in new[] { 2, 1 })
            {
                float[]? n = r.HdrRun(pic, 960, 540, kind, out string why);
                if (n == null) { log.Add($"picture kind {kind}: unavailable: {why}"); continue; }
                float[] sorted = n.OrderBy(x => x).ToArray();
                log.Add($"picture kind {kind}: nits median {sorted[sorted.Length / 2]:0.0}, p90 {sorted[sorted.Length * 9 / 10]:0.0}, p99 {sorted[sorted.Length * 99 / 100]:0.0}, max {sorted[^1]:0.0}, clipped(>=9990) {n.Count(x => x >= 9990) * 100.0 / n.Length:0.00}%");
            }
            // experiments: which colour description makes the driver's HDR give sane nits
            void Exp(string name, Vortice.DXGI.Format fmt, Vortice.DXGI.ColorSpaceType? stream, Vortice.DXGI.ColorSpaceType? outc, bool none)
            {
                NvVideoProc.ExpStreamCs = stream; NvVideoProc.ExpOutputCs = outc; NvVideoProc.ExpNoColorSpace = none;
                float[]? n = r.HdrRun(pic, 960, 540, 1, out string why, fmt);
                if (n == null) { log.Add($"exp {name}: unavailable: {why}"); return; }
                float[] sorted = n.OrderBy(x => x).ToArray();
                log.Add($"exp {name}: nits median {sorted[sorted.Length / 2]:0.0}, p90 {sorted[sorted.Length * 9 / 10]:0.0}, p99 {sorted[sorted.Length * 99 / 100]:0.0}, max {sorted[^1]:0.0}");
            }
            Exp("pq10 stream=G22 out=PQ2020", Vortice.DXGI.Format.R10G10B10A2_UNorm, null, null, false);
            Exp("pq10 no colour space", Vortice.DXGI.Format.R10G10B10A2_UNorm, null, null, true);
            Exp("pq10 stream=G22 out=PQ709", Vortice.DXGI.Format.R10G10B10A2_UNorm, null, Vortice.DXGI.ColorSpaceType.RgbFullG2084NoneP2020, false);
            Exp("fp16 stream=G22 out=scRGB", Vortice.DXGI.Format.R16G16B16A16_Float, null, Vortice.DXGI.ColorSpaceType.RgbFullG10NoneP709, false);
            Exp("fp16 no colour space", Vortice.DXGI.Format.R16G16B16A16_Float, null, null, true);
            foreach (int kind in new[] { 3, 1 })
            {
                var px = new byte[256 * 256 * 4];
                int[] cc = { 0, 32, 64, 128, 180, 220, 255 };
                for (int i = 0; i < 256 * 256; i++) { int c = cc[Math.Min(6, (i % 256) * 7 / 256)]; px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = (byte)c; px[i * 4 + 3] = 255; }
                float[]? n = r.HdrRun(px, 256, 256, kind, out string why, Vortice.DXGI.Format.R10G10B10A2_UNorm, true);
                log.Add($"raw 10-bit code, VP {(kind == 1 ? "with TrueHDR" : "without TrueHDR")}: " + (n == null ? why : string.Join("  ", cc.Select((c, i) => $"{c}->{n[128 * 256 + (i * 2 + 1) * 256 / 14]:0}"))));
            }
            if (Environment.GetEnvironmentVariable("NVPROBE_NV12") == "1")
            {
                // does TrueHDR engage for a video-like (NV12, studio range) input?
                const int W = 256, H = 256;
                int[] cc = { 0, 32, 64, 128, 180, 220, 255 };
                var nv12 = new byte[W * H * 3 / 2];
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) nv12[y * W + x] = (byte)(16 + 219 * cc[Math.Min(6, x * 7 / W)] / 255);
                for (int i = W * H; i < nv12.Length; i++) nv12[i] = 128;
                using var src = r.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.NV12, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default, BindFlags = Vortice.Direct3D11.BindFlags.None });
                var hnd = System.Runtime.InteropServices.GCHandle.Alloc(nv12, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { r.Ctx.UpdateSubresource(src, 0, null, hnd.AddrOfPinnedObject(), W, 0); } finally { hnd.Free(); }
                foreach (bool hdr in new[] { false, true })
                {
                    using var dstT = r.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.R10G10B10A2_UNorm, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default, BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource });
                    NvVideoProc.ExpStreamCs = Vortice.DXGI.ColorSpaceType.YcbcrStudioG22LeftP709;
                    using var vp = NvVideoProc.TryCreate(r.Device, r.Ctx)!;
                    bool ok = false; for (int i = 0; i < 3; i++) ok = vp.Blit(src, new System.Drawing.Rectangle(0, 0, W, H), dstT, new System.Drawing.Size(W, H), false, hdr);
                    var st = r.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.R10G10B10A2_UNorm, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Staging, CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read });
                    r.Ctx.CopyResource(st, dstT);
                    var m = r.Ctx.Map(st, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    var vals = new List<string>();
                    for (int i = 0; i < 7; i++) { uint v = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(m.DataPointer + (H / 2) * (int)m.RowPitch + ((i * 2 + 1) * W / 14) * 4); float rr = (v & 0x3FF) / 1023f, gg = ((v >> 10) & 0x3FF) / 1023f, bb = ((v >> 20) & 0x3FF) / 1023f; vals.Add($"{cc[i]}->code {Math.Max(v & 0x3FF, Math.Max((v >> 10) & 0x3FF, (v >> 20) & 0x3FF))} ({D3DRenderer.PqToNits(Math.Max(rr, Math.Max(gg, bb))):0}nits)"); }
                    r.Ctx.Unmap(st, 0); st.Dispose();
                    log.Add($"NV12 input, TrueHDR {(hdr ? "ON" : "off")} ok={ok}: " + string.Join("  ", vals) + " " + NvVideoProc.LastFailure);
                }
                NvVideoProc.ExpStreamCs = null;
            }
            NvVideoProc.ExpStreamCs = null; NvVideoProc.ExpOutputCs = null; NvVideoProc.ExpNoColorSpace = false;
        }
        catch (Exception ex) { log.Add("FAILED: " + ex); }
        File.WriteAllLines(outPath, log);
        foreach (string l in log) Console.WriteLine(l);
    }
}
