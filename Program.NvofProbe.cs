using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

// Developer probe: --nvof-probe <out.txt>
//   Does the NVIDIA Optical Flow engine work here, which sign / scale / accuracy does it deliver on a known shift, and how long does it take?
internal static partial class Program
{
    private static void RunNvofProbe(string outPath)
    {
        var log = new List<string>();
        try
        {
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? dev, out _, out ID3D11DeviceContext? ctx).CheckError();
            using (dev) using (ctx)
            {
                using (IDXGIDevice dx = dev!.QueryInterface<IDXGIDevice>())
                {
                    dx.GetAdapter(out IDXGIAdapter? ad);
                    log.Add("adapter: " + ad!.Description.Description);
                    ad.Dispose();
                }
                foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
                {
                    using Bitmap pat = InterpCheck.MakePattern(w, h);
                    int[] A = ReadPixels(pat);
                    int[] B = ShiftPixels(A, w, h, 14, -8);
                    using ID3D11Texture2D ta = MakeIn(dev!, w, h, A), tb = MakeIn(dev!, w, h, B);
                    foreach (int grid in new[] { 4, 2, 1 })
                        foreach (int perf in new[] { 5, 10, 20 })
                        {
                            NvOfSession? s = NvOfSession.TryCreate(dev!, ctx!, w, h, grid, true, perf);
                            if (s == null) { log.Add($"{w}x{h} grid {grid} perf {perf}: unavailable: {NvOfSession.LastFailure}"); continue; }
                            using (s)
                            {
                                IntPtr ra = s.Register(ta), rb = s.Register(tb);
                                if (ra == IntPtr.Zero || rb == IntPtr.Zero) { log.Add($"{w}x{h} grid {grid} perf {perf}: register failed: {NvOfSession.LastFailure}"); continue; }
                                // input = A, reference = B
                                if (!s.Execute(ra, rb)) { log.Add($"{w}x{h} grid {grid} perf {perf}: execute failed: {NvOfSession.LastFailure}"); continue; }
                                string f1 = Describe(dev!, ctx!, s.FwdTex, s.OutSize.W, s.OutSize.H);
                                string b1 = s.BwdTex != null ? Describe(dev!, ctx!, s.BwdTex, s.OutSize.W, s.OutSize.H) : "-";
                                // timing (the readback of one pixel waits for the engine)
                                var sw = Stopwatch.StartNew();
                                int n = 40;
                                for (int i = 0; i < n; i++) { s.Execute(ra, rb); Sync(dev!, ctx!, s.FwdTex); }
                                double ms = sw.Elapsed.TotalMilliseconds / n;
                                log.Add($"{w}x{h} grid {grid} perf {perf}: out {s.OutSize.W}x{s.OutSize.H} caps {s.SupportedGrids:X}; input=A,ref=B fwd median {f1}; bwd median {b1}; {ms:0.00} ms/frame pair (both directions, incl. sync)");
                            }
                        }
                }
            }
        }
        catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
        File.WriteAllLines(outPath, log);
    }

    private static ID3D11Texture2D MakeIn(ID3D11Device dev, int w, int h, int[] px)
    {
        var hnd = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            return dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            }, new[] { new SubresourceData(hnd.AddrOfPinnedObject(), (uint)(w * 4)) });
        }
        finally { hnd.Free(); }
    }

    private static void Sync(ID3D11Device dev, ID3D11DeviceContext ctx, ID3D11Texture2D src)
    {
        using ID3D11Texture2D st = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R16G16_SInt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        ctx.CopySubresourceRegion(st, 0, 0, 0, 0, src, 0, new Vortice.Mathematics.Box(0, 0, 0, 1, 1, 1));
        ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        ctx.Unmap(st, 0);
    }

    private static string Describe(ID3D11Device dev, ID3D11DeviceContext ctx, ID3D11Texture2D src, int w, int h)
    {
        using ID3D11Texture2D st = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16_SInt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        ctx.CopyResource(st, src);
        MappedSubresource m = ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var xs = new List<double>(); var ys = new List<double>();
        try
        {
            var row = new short[w * 2];
            for (int y = h / 4; y < h * 3 / 4; y += 3)
            {
                Marshal.Copy(m.DataPointer + y * (int)m.RowPitch, row, 0, w * 2);
                for (int x = w / 4; x < w * 3 / 4; x += 3) { xs.Add(row[x * 2] / 32.0); ys.Add(row[x * 2 + 1] / 32.0); }
            }
        }
        finally { ctx.Unmap(st, 0); }
        xs.Sort(); ys.Sort();
        double within = 0;
        return $"({xs[xs.Count / 2]:0.00},{ys[ys.Count / 2]:0.00}) iqr x {xs[xs.Count * 3 / 4] - xs[xs.Count / 4]:0.00} y {ys[ys.Count * 3 / 4] - ys[ys.Count / 4]:0.00}{(within > 0 ? "" : "")}";
    }
}
