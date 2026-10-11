using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Vortice.DXGI;

namespace YuCap;

// Diagnostic commands that answer hardware questions before the hardware is at hand:
//   --gpu-bench <out.txt>   can this GPU upscale to 4K at the source frame rate?
//   --hdr-probe <out.txt>   does the capture card deliver HDR, and is Windows HDR on?
// Both run the Direct3D engine in a real (small) window on the live capture device.
internal static partial class Program
{
    private static Form NewProbeWindow(string title, out VideoBox canvas)
    {
        var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            Size = new Size(960, 540),
            ShowInTaskbar = false,
            Text = title,
        };
        canvas = new VideoBox { Dock = DockStyle.Fill, BackColor = Color.Black };
        form.Controls.Add(canvas);
        return form;
    }

    private static string Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return "n/a";
        int i = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[i].ToString("0.00");
    }

    // =====================================================================
    //  --gpu-bench
    // =====================================================================

    private static void RunGpuBench(string outPath)
    {
        var log = new List<string>();
        Form form = NewProbeWindow("YuCap gpu-bench", out VideoBox canvas);
        IVideoEngine engine = VideoEngines.Create("d3d");

        form.Load += (_, _) =>
        {
            try
            {
                log.Add($"YuCap {Application.ProductVersion} - GPU benchmark: source frame -> 3840x2160 off-screen");
                log.Add("Each case renders ~120 frames back to back (convert + scale/FSR + copy, as in the live path)");
                log.Add("and reports GPU time per frame from D3D11 timestamp queries. Verdict uses the 95th percentile:");
                log.Add("ok = p95 <= 80% of the frame budget, TIGHT = above 80%, OVER = above the budget (1000 / source fps).");
                log.Add("Frames are not paced to the source rate, so a real stream (GPU clocks may drop between frames,");
                log.Add("the video processor and DWM share the GPU) can be somewhat slower than shown.");
                log.Add(string.Empty);
                int adapterLine = log.Count;

                engine.Attach(canvas.Handle);
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Start(dev, null);
                log.Add($"device: {dev.Name}");
                PumpFor(1500);
                List<CaptureMode> modes = engine.GetModes();
                log.Add("modes: " + string.Join(", ", modes.Select(m => $"{m.Compression} {m.Width}x{m.Height}@{m.Fps}")));
                log.Add(string.Empty);

                var output = new Size(3840, 2160);
                var cases = new (string Label, int W, int H, int WantFps, ScalingQuality[] Qualities)[]
                {
                    ("1080p120", 1920, 1080, 120, new[] { ScalingQuality.Fsr, ScalingQuality.Standard }),
                    ("1440p60", 2560, 1440, 60, new[] { ScalingQuality.Fsr, ScalingQuality.Standard }),
                    ("4K30", 3840, 2160, 30, new[] { ScalingQuality.Standard }),
                };
                string adapter = "?";
                foreach (var c in cases)
                {
                    log.Add($"=== {c.Label} ===");
                    CaptureMode? mode = modes.Where(m => m.Width == c.W && m.Height == c.H)
                        .OrderByDescending(m => m.Fps).FirstOrDefault();
                    if (mode == null) { log.Add("  skipped: the device offers no such mode"); log.Add(string.Empty); continue; }
                    try
                    {
                        engine.Start(dev, mode);
                        PumpFor(2000);
                        int fps = engine.NegotiatedFps > 0 ? engine.NegotiatedFps : mode.Fps;
                        double budget = 1000.0 / Math.Max(1, fps);
                        log.Add($"  source: {mode.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height} @{fps}fps"
                                + (fps < c.WantFps ? $"  (note: the device offers only {fps}fps here, not {c.WantFps})" : string.Empty));
                        if (engine.Stats is { } st0)
                            log.Add($"  stream: in={st0.InputFps:0}fps");
                        foreach (ScalingQuality q in c.Qualities)
                        {
                            GpuBenchResult? res = engine.GpuBench(output, q, 120);
                            string name = q == ScalingQuality.Fsr ? "FSR" : "Standard";
                            if (res == null || res.FrameMs.Length == 0)
                            {
                                log.Add($"  {name}: no result (no frame, or timestamp queries unavailable)");
                                continue;
                            }
                            adapter = res.Adapter;
                            var ms = res.FrameMs.OrderBy(x => x).ToList();
                            double avg = ms.Average(), max = ms[^1];
                            double p95 = double.Parse(Percentile(ms, 0.95));
                            string verdict = p95 > budget ? "OVER" : p95 > budget * 0.8 ? "TIGHT" : "ok";
                            log.Add($"  {name,-8} -> 3840x2160: frames={ms.Count} avg={avg:0.00}ms p95={p95:0.00}ms max={max:0.00}ms" +
                                    $"  budget={budget:0.00}ms ({fps}fps)  p95/budget={p95 / budget * 100:0}%  => {verdict}");
                        }
                    }
                    catch (Exception mex) { log.Add("  FAILED: " + mex.Message); }
                    log.Add(string.Empty);
                }
                log.Insert(adapterLine, "adapter: " + adapter);
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { engine.Dispose(); } catch { }
                try { File.WriteAllLines(outPath, log); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }

    // =====================================================================
    //  --hdr-probe
    // =====================================================================

    private static void RunHdrProbe(string outPath)
    {
        var log = new List<string>();
        Form form = NewProbeWindow("YuCap hdr-probe", out VideoBox canvas);
        IVideoEngine engine = VideoEngines.Create("d3d");

        form.Load += (_, _) =>
        {
            try
            {
                log.Add($"YuCap {Application.ProductVersion} - HDR probe");
                log.Add(string.Empty);
                log.Add("--- displays (Windows HDR state) ---");
                log.AddRange(DescribeDisplaysHdr());
                log.Add(string.Empty);

                engine.Attach(canvas.Handle);
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Start(dev, null);
                log.Add($"device: {dev.Name}");
                PumpFor(1000);

                List<CaptureMode> modes = engine.GetModes();
                CaptureMode? p010 = modes.Where(m => m.Compression == "P010")
                    .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Fps).FirstOrDefault();
                if (p010 == null)
                {
                    log.Add("no P010 mode offered; probing the current/largest mode instead");
                    log.Add("modes: " + string.Join(", ", modes.Select(m => $"{m.Compression} {m.Width}x{m.Height}@{m.Fps}")));
                }
                else
                {
                    log.Add($"mode: P010 {p010.Width}x{p010.Height}@{p010.Fps}");
                    engine.Start(dev, p010);
                }
                PumpFor(2000);
                log.Add($"running: {engine.CurrentMode?.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height} @{engine.NegotiatedFps}fps");
                log.Add(string.Empty);
                log.Add("--- source reader media types ---");
                log.Add(engine.SourceColorInfo ?? "(not available)");
                log.Add(string.Empty);
                log.Add("--- luminance of the frame as the renderer shows it (SDR path) ---");
                using (Bitmap? bmp = engine.PhotoSnapshot())
                {
                    if (bmp == null) log.Add("PhotoSnapshot failed (no frame?)");
                    else log.AddRange(DescribeLuminance(bmp));
                }
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { engine.Dispose(); } catch { }
                try { File.WriteAllLines(outPath, log); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }

    /// <summary>Min / mean / max luma (0..255, Rec.709 weights on the displayed RGB), the 1st and 99th
    /// percentile, and the fraction of pixels above 235 and at or below 16. A PQ-encoded picture
    /// shown as SDR looks washed out: few near-black pixels, low contrast, a high mean.</summary>
    internal static List<string> DescribeLuminance(Bitmap bmp)
    {
        var hist = new long[256];
        long n = 0;
        double sum = 0;
        int min = 255, max = 0;
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            const int step = 3;
            var row = new byte[bmp.Width * 4];
            for (int y = 0; y < bmp.Height; y += step)
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                for (int x = 0; x < bmp.Width; x += step)
                {
                    byte bl = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    int l = (int)Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * bl);
                    hist[l]++; sum += l; n++;
                    if (l < min) min = l;
                    if (l > max) max = l;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        if (n == 0) return new List<string> { "(empty frame)" };

        int Pct(double p)
        {
            long target = (long)Math.Ceiling(p * n), acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) return i; }
            return 255;
        }
        long above = 0, below = 0;
        for (int i = 236; i < 256; i++) above += hist[i];
        for (int i = 0; i <= 16; i++) below += hist[i];
        double mean = sum / n;
        int p1 = Pct(0.01), p99 = Pct(0.99);
        var lines = new List<string>
        {
            $"frame {bmp.Width}x{bmp.Height}, {n} pixels sampled",
            $"luma min={min} mean={mean:0.0} max={max}   p1={p1} p99={p99}",
            $"fraction > 235: {(double)above / n:P2}   fraction <= 16: {(double)below / n:P2}",
        };
        if (p1 > 40 && p99 < 235)
            lines.Add("hint: low contrast and few dark pixels - PQ/HLG HDR shown as SDR looks like this (a flat scene would too)");
        else if (below > n / 50)
            lines.Add("hint: real blacks are present - consistent with an SDR signal (or a PQ scene that really is dark)");
        return lines;
    }

    /// <summary>One monitor as DXGI sees it (Windows HDR state and luminance); shared by <c>--hdr-probe</c>
    /// and the test mode's environment report.</summary>
    internal sealed record OutputHdr(string Adapter, string DeviceName, int Width, int Height, string ColorSpace,
        bool HdrOn, uint BitsPerColor, float MaxLuminance, float MaxFullFrame, float MinLuminance, bool HasInfo);

    internal static List<OutputHdr> QueryOutputsHdr()
    {
        var list = new List<OutputHdr>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter != null; a++)
        {
            using (adapter)
            {
                string adapterName = adapter.Description1.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output != null; o++)
                {
                    using (output)
                    {
                        using IDXGIOutput6? o6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                        if (o6 == null)
                        {
                            var dd = output.Description.DesktopCoordinates;
                            list.Add(new OutputHdr(adapterName, output.Description.DeviceName, dd.Right - dd.Left, dd.Bottom - dd.Top,
                                "?", false, 0, 0, 0, 0, HasInfo: false));
                            continue;
                        }
                        OutputDescription1 d = o6.Description1;
                        bool hdr = d.ColorSpace.ToString().Contains("G2084", StringComparison.OrdinalIgnoreCase);
                        list.Add(new OutputHdr(adapterName, d.DeviceName,
                            d.DesktopCoordinates.Right - d.DesktopCoordinates.Left, d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top,
                            d.ColorSpace.ToString(), hdr, d.BitsPerColor, d.MaxLuminance, d.MaxFullFrameLuminance, d.MinLuminance, HasInfo: true));
                    }
                }
            }
        }
        return list;
    }

    private static List<string> DescribeDisplaysHdr()
    {
        var lines = new List<string>();
        try
        {
            string? lastAdapter = null;
            int outIdx = 0;
            foreach (OutputHdr h in QueryOutputsHdr())
            {
                if (h.Adapter != lastAdapter) { lines.Add($"adapter: {h.Adapter}"); lastAdapter = h.Adapter; outIdx = 0; }
                if (!h.HasInfo)
                {
                    lines.Add($"  output {outIdx}: {h.DeviceName}  (IDXGIOutput6 not available - no HDR information)");
                }
                else
                {
                    lines.Add($"  output {outIdx}: {h.DeviceName}  {h.Width}x{h.Height}" +
                              $"  colorSpace={h.ColorSpace}  windowsHDR={(h.HdrOn ? "ON" : "off")}  bitsPerColor={h.BitsPerColor}");
                    lines.Add($"      maxLuminance={h.MaxLuminance:0} nits  maxFullFrame={h.MaxFullFrame:0} nits  minLuminance={h.MinLuminance:0.0000} nits");
                }
                outIdx++;
            }
        }
        catch (Exception ex) { lines.Add("display query failed: " + ex.Message); }
        if (lines.Count == 0) lines.Add("(no adapters)");
        return lines;
    }
}
