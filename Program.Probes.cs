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
//   --vblank-probe <out.txt>   vblank period and swap-chain present rate of every display (also with something drawn over the video)
//   --restart-stress <out.txt> [N]   restart the Direct3D preview N times; does video always come back?
//   --interp-probe <out.txt> [--mode 4k30] [--stills <dir>]   frame interpolation on the live device
//   --interp-synth <out.txt> --in <picture.png> [--scale 2] [--stills <dir>]   interpolation on a known motion
// Both run the Direct3D engine in a real (small) window on the live capture device.
internal static partial class Program
{
    /// <summary>--display for the probes that need a fast monitor (--interp-probe): the part of the monitor's name to open the window on.</summary>
    private static string? _probeDisplay;

    private static Form NewProbeWindow(string title, out VideoBox canvas)
    {
        DisplayInfo? di = string.IsNullOrEmpty(_probeDisplay) ? null : Displays.List().FirstOrDefault(d => d.FriendlyName.Contains(_probeDisplay, StringComparison.OrdinalIgnoreCase));
        var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = di == null ? new Point(80, 80) : new Point(di.Bounds.Left + 80, di.Bounds.Top + 80),
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
    //  --restart-stress
    // =====================================================================

    /// <summary>Quick "is there a picture" test on one snapshot: true when the image is not uniform.</summary>
    private static bool SnapshotHasPicture(IVideoEngine engine)
    {
        using Bitmap? a = engine.Snapshot();
        if (a == null) return false;
        int minL = 255, maxL = 0;
        for (int gy = 1; gy <= 4; gy++)
            for (int gx = 1; gx <= 4; gx++)
            {
                Color c = a.GetPixel(a.Width * gx / 5, a.Height * gy / 5);
                int l = (c.R + c.G + c.B) / 3;
                minL = Math.Min(minL, l); maxL = Math.Max(maxL, l);
            }
        return maxL - minL >= 8;
    }

    private static void RunRestartStress(string outPath, int count, bool fullscreen, bool fast)
    {
        var log = new List<string>();
        Form form = NewProbeWindow("YuCap restart-stress", out VideoBox canvas);
        IVideoEngine engine = VideoEngines.Create("d3d");
        var rng = new Random(12345);

        form.Shown += (_, _) =>
        {
            try
            {
                log.Add($"YuCap {Application.ProductVersion} - restart stress, {count} iterations (Direct3D)"
                    + (fullscreen ? ", borderless fullscreen" : "") + (fast ? ", fast extra restarts" : ""));
                if (fullscreen)
                {
                    // Same state as the self-test's restart: borderless, screen-sized, topmost.
                    form.FormBorderStyle = FormBorderStyle.None;
                    form.Bounds = Screen.FromControl(form).Bounds;
                    form.TopMost = true;
                    Application.DoEvents();
                }
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Attach(canvas.Handle);
                engine.Start(dev, null);
                PumpFor(500);
                List<CaptureMode> modes = engine.GetModes();
                CaptureMode? Find(int w, int h, int fps) => modes.Where(m => m.Width == w && m.Height == h && m.Fps == fps)
                    .OrderBy(m => m.Compression == "NV12" ? 0 : 1).FirstOrDefault();
                CaptureMode?[] cycle = { Find(1920, 1080, 120), Find(2560, 1440, 60) };
                log.Add("modes under test: " + string.Join(" / ", cycle.Select(m => m == null ? "default" : $"{m.Compression} {m.Width}x{m.Height}@{m.Fps}")));
                log.Add(string.Empty);

                var firstRx = new List<double>(); var firstPresent = new List<double>(); var firstPicture = new List<double>();
                int failures = 0, noPresent = 0;
                for (int i = 1; i <= count; i++)
                {
                    CaptureMode? mode = cycle[(i / 2) % 2];   // changes every other iteration
                    Log.Info($"STRESS iteration {i}/{count} start");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string err = "";
                    try
                    {
                        if (fast)
                        {
                            // Restart again before the previous session has shown anything.
                            for (int extra = rng.Next(1, 4); extra > 0; extra--)
                            {
                                canvas.ResetHandle();
                                engine.Attach(canvas.Handle);
                                engine.Start(dev, mode);
                                PumpFor(rng.Next(0, 500));
                            }
                        }
                        canvas.ResetHandle();
                        engine.Attach(canvas.Handle);
                        engine.Start(dev, mode);
                        Rectangle scr = canvas.ClientRectangle;
                        engine.SetVideoRect(new Rectangle(0, 0, scr.Width, scr.Height));
                    }
                    catch (Exception ex) { err = ex.Message.Split((char)10)[0]; }
                    long startMs = sw.ElapsedMilliseconds;
                    sw.Restart();
                    double tRx = -1, tPres = -1, tPic = -1;
                    while (err == "" && sw.ElapsedMilliseconds < 3000)
                    {
                        PumpFor(50);
                        if (tRx < 0 && engine.FramesReceived > 0) tRx = sw.ElapsedMilliseconds;
                        if (tPres < 0 && engine.FramesPresented > 0) tPres = sw.ElapsedMilliseconds;
                        if (SnapshotHasPicture(engine)) { tPic = sw.ElapsedMilliseconds; break; }
                    }
                    bool ok = err == "" && tPic >= 0;
                    if (ok && VerifyVideo(engine).StartsWith("NOT RENDERING")) ok = false;
                    double lateRecovery = -1;
                    if (!ok && err == "")
                    {
                        // Does it ever come back? (A black picture that never recovers is the bug.)
                        var rs = System.Diagnostics.Stopwatch.StartNew();
                        while (rs.ElapsedMilliseconds < 5000) { PumpFor(50); if (SnapshotHasPicture(engine)) { lateRecovery = 3000 + rs.ElapsedMilliseconds; break; } }
                    }
                    if (!ok) failures++;
                    if (tPres < 0) noPresent++;
                    if (tRx >= 0) firstRx.Add(tRx);
                    if (tPres >= 0) firstPresent.Add(tPres);
                    if (tPic >= 0) firstPicture.Add(tPic);
                    string line = $"#{i,3} {(ok ? "ok  " : "FAIL")} mode={(mode == null ? "default" : $"{mode.Width}x{mode.Height}@{mode.Fps}")}" +
                        $" start={startMs}ms firstRx={tRx:0}ms firstPresent={tPres:0}ms firstPicture={tPic:0}ms" +
                        $" margin={engine.VideoPacingMarginMs:0.0}ms rx={engine.FramesReceived} presented={engine.FramesPresented}" +
                        $" renderLoopPresented={(engine.FramesPresented > 0 ? "yes" : "NO")}" +
                        (err != "" ? " error=" + err : "") + (lateRecovery >= 0 ? $" recoveredAfter={lateRecovery:0}ms" : (ok ? "" : " NEVER-RECOVERED"));
                    log.Add(line);
                    Log.Info("STRESS " + line);
                }

                log.Add(string.Empty);
                string Pct(List<double> v)
                {
                    var t = v.OrderBy(x => x).ToList();
                    return t.Count == 0 ? "n/a" : $"p50={Percentile(t, 0.5)} p90={Percentile(t, 0.9)} max={Percentile(t, 1.0)} ms";
                }
                log.Add($"SUMMARY failures={failures}/{count}  never-presented={noPresent}");
                log.Add("  first frame received: " + Pct(firstRx));
                log.Add("  first present:        " + Pct(firstPresent));
                log.Add("  first picture:        " + Pct(firstPicture));
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
    //  --interp-probe
    // =====================================================================

    /// <summary>Frame interpolation on the live device in a small window: a baseline without it, then with it
    /// (presented fps, GPU time, pacing, activation), and stills of A, the synthesised middle and B.</summary>
    private static void RunInterpProbe(string outPath, CaptureMode? wanted, string? stillsDir)
    {
        var log = new List<string>();
        Form form = NewProbeWindow("YuCap interp-probe", out VideoBox canvas);
        var engine = new D3DVideoEngine();

        form.Shown += (_, _) =>
        {
            try
            {
                log.Add($"YuCap {Application.ProductVersion} - frame interpolation probe");
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Attach(canvas.Handle);
                engine.Start(dev, null);
                PumpFor(800);
                List<CaptureMode> modes = engine.GetModes();
                CaptureMode? mode = wanted == null ? null : modes
                    .Where(m => m.Width == wanted.Width && m.Height == wanted.Height && m.Fps == wanted.Fps)
                    .OrderBy(m => m.Compression == "NV12" ? 0 : 1).FirstOrDefault();
                if (wanted != null && mode == null) { log.Add("mode not offered: " + wanted); return; }
                engine.Start(dev, mode);
                engine.SetVideoRect(new Rectangle(0, 0, canvas.ClientSize.Width, canvas.ClientSize.Height));
                log.Add($"device: {dev.Name}  mode: {engine.CurrentMode?.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height}@{engine.NegotiatedFps}");
                log.Add($"window: {canvas.ClientSize.Width}x{canvas.ClientSize.Height} on {Screen.FromControl(form).DeviceName}, refresh {Displays.RefreshHzOfWindow(form.Handle)} Hz, present mode {engine.PresentMode}");
                log.Add(string.Empty);

                foreach (bool on in new[] { false, true })
                {
                    engine.Interpolation = on;
                    PumpFor(2500);
                    InterpolationStatus? st = engine.InterpolationState;
                    log.Add($"=== interpolation {(on ? "ON" : "OFF")}: state = {(st == null ? "?" : $"active={st.Active} idle={st.Idle} source={st.SourceFps:0.00}fps refresh={st.RefreshHz:0}Hz")}");
                    engine.BeginMeasure();
                    long rx0 = engine.FramesReceived, pr0 = engine.FramesPresented, dr0 = engine.FramesDropped;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    PumpFor(6000);
                    double el = sw.Elapsed.TotalSeconds;
                    RenderMeasurement? meas = engine.EndMeasure();
                    log.Add($"  received {(engine.FramesReceived - rx0) / el:0.00} fps, presented {(engine.FramesPresented - pr0) / el:0.00} fps, dropped {engine.FramesDropped - dr0}");
                    if (engine.Stats is { } rs) log.Add($"  stats: in={rs.InputFps:0.0} presented={rs.PresentedFps:0.0} gpu avg={rs.GpuMsAvg:0.00}ms max={rs.GpuMsMax:0.00}ms");
                    log.Add($"  video delay actual {engine.VideoDelayActualMs:0.0}ms (pacing margin {engine.VideoPacingMarginMs:0.0}ms)");
                    if (meas != null)
                    {
                        double[] g = meas.GpuMs.OrderBy(x => x).ToArray();
                        if (g.Length > 0)
                            log.Add($"  GPU per present: n={g.Length} avg={g.Average():0.00}ms p50={g[g.Length / 2]:0.00} p95={g[Math.Clamp((int)Math.Ceiling(0.95 * g.Length) - 1, 0, g.Length - 1)]:0.00} max={g[^1]:0.00}ms");
                        double srcFps = engine.NegotiatedFps;
                        double hz = Displays.RefreshHzOfWindow(form.Handle);
                        TestProbes.PacingAnalysis p = TestProbes.AnalyzePacing(meas.Pacing, on && st is { Active: true } ? hz : srcFps, hz);
                        log.Add($"  pacing ({p.Source}): frames={p.Frames} mean={p.MeanMs:0.00}ms std={p.StdMs:0.00}ms p5..p95={p.P5Ms:0.0}..{p.P95Ms:0.0}ms judder={p.JudderShare:P0} observedHz={p.ObservedHz:0.00} sameRefresh={p.SameRefreshShare:P1}");
                        foreach (var h in p.Histogram) log.Add($"    {h.Label}: {h.Text}");
                    }
                    log.Add(string.Empty);

                    if (on && stillsDir != null)
                    {
                        Directory.CreateDirectory(stillsDir);
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            if (engine.DebugInterpStills(0.5, out Bitmap? a, out Bitmap? mid, out Bitmap? b))
                            {
                                using (a) using (mid) using (b)
                                {
                                    a!.Save(Path.Combine(stillsDir, "A.png"), ImageFormat.Png);
                                    mid!.Save(Path.Combine(stillsDir, "mid.png"), ImageFormat.Png);
                                    b!.Save(Path.Combine(stillsDir, "B.png"), ImageFormat.Png);
                                    log.Add($"stills saved to {stillsDir} ({a.Width}x{a.Height})");
                                }
                                break;
                            }
                            PumpFor(100);
                        }
                    }
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

    // =====================================================================
    //  --interp-synth
    // =====================================================================

    internal static int[] ReadPixels(Bitmap bmp)
    {
        var px = new int[bmp.Width * bmp.Height];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length); }
        finally { bmp.UnlockBits(bd); }
        return px;
    }

    internal static Bitmap MakeBitmap(int[] px, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length); }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    /// <summary>out(x, y) = src(x - dx, y - dy), edge pixels repeated.</summary>
    internal static int[] ShiftPixels(int[] src, int w, int h, double dx, double dy)
    {
        var o = new int[src.Length];
        int ix = (int)Math.Round(dx), iy = (int)Math.Round(dy);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                o[y * w + x] = src[Math.Clamp(y - iy, 0, h - 1) * w + Math.Clamp(x - ix, 0, w - 1)];
        return o;
    }

    internal static double Psnr(int[] p, int[] q, int w, Rectangle r)
    {
        double se = 0; long n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                int a = p[y * w + x], b = q[y * w + x];
                for (int sh = 0; sh < 24; sh += 8) { int d = ((a >> sh) & 255) - ((b >> sh) & 255); se += d * d; n++; }
            }
        double mse = se / Math.Max(1, n);
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>The interpolation pipeline on a known motion: a picture and a copy of it moved by a global vector
    /// with one block moved differently. Reports how close the motion field and the synthesised picture are to the
    /// truth, plus GPU time per stage at the picture's size (<c>--scale</c> enlarges it, e.g. to 4K).</summary>
    private static void RunInterpSynth(string outPath, string inPng, string? stillsDir, double scale)
    {
        var log = new List<string>();
        Form form = NewProbeWindow("YuCap interp-synth", out VideoBox canvas);
        var engine = new D3DVideoEngine();
        form.Shown += (_, _) =>
        {
            try
            {
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Attach(canvas.Handle);
                engine.Start(dev, null);
                engine.Interpolation = true;
                PumpFor(1000);

                using Bitmap src = new Bitmap(inPng);
                int w = (int)Math.Round(src.Width * scale) & ~1, h = (int)Math.Round(src.Height * scale) & ~1;
                using var a0 = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(a0))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, w, h);
                }
                int[] A = ReadPixels(a0);
                log.Add($"picture {w}x{h} (from {Path.GetFileName(inPng)}, scale {scale})");

                double gdx = 14 * scale, gdy = -8 * scale;             // background motion A -> B
                var blk = new Rectangle(w * 36 / 100, h * 38 / 100, w * 20 / 100, h * 28 / 100);
                double bdx = -40 * scale, bdy = 12 * scale;            // the block's own motion
                int[] B = ShiftPixels(A, w, h, gdx, gdy);
                int[] blockShift = ShiftPixels(A, w, h, bdx, bdy);
                for (int y = blk.Top; y < blk.Bottom; y++)
                    for (int x = blk.Left; x < blk.Right; x++) B[y * w + x] = blockShift[y * w + x];

                double alpha = 0.5;
                int[] gt = ShiftPixels(A, w, h, gdx * alpha, gdy * alpha);                 // truth at alpha, background
                int[] gtBlock = ShiftPixels(A, w, h, bdx * alpha, bdy * alpha);
                var blkMid = new Rectangle(blk.Left + (int)Math.Round(bdx * alpha), blk.Top + (int)Math.Round(bdy * alpha), blk.Width, blk.Height);
                for (int y = blkMid.Top; y < blkMid.Bottom; y++)
                    for (int x = blkMid.Left; x < blkMid.Right; x++) gt[y * w + x] = gtBlock[y * w + x];

                using Bitmap bA = MakeBitmap(A, w, h), bB = MakeBitmap(B, w, h);
                bool ok = engine.DebugInterpSynthetic(bA, bB, alpha, 60, out Bitmap? mid, out float[] field, out Size fs, out string timing);
                if (!ok || mid == null) { log.Add("synthetic run failed (interpolation unavailable?)"); return; }
                using (mid)
                {
                    log.Add("GPU: " + timing);
                    int[] M = ReadPixels(mid);
                    int m = (int)(24 * Math.Max(1, scale));
                    var bgRect = new Rectangle(m, m, w - 2 * m, h * 30 / 100 - m);              // top band: background only
                    var blkRect = new Rectangle(blkMid.Left + m, blkMid.Top + m, blkMid.Width - 2 * m, blkMid.Height - 2 * m);
                    log.Add($"synthesised vs truth (alpha {alpha}): background PSNR {Psnr(M, gt, w, bgRect):0.0} dB, moving block PSNR {Psnr(M, gt, w, blkRect):0.0} dB");
                    // for scale: how good is just blending A and B, and just showing A
                    var blend = new int[A.Length];
                    for (int i = 0; i < A.Length; i++)
                    {
                        int r = 0;
                        for (int sh = 0; sh < 24; sh += 8) r |= ((((A[i] >> sh) & 255) + ((B[i] >> sh) & 255)) / 2) << sh;
                        blend[i] = r | unchecked((int)0xFF000000);
                    }
                    log.Add($"  (a plain 50% blend of A and B scores: background {Psnr(blend, gt, w, bgRect):0.0} dB, block {Psnr(blend, gt, w, blkRect):0.0} dB)");

                    // the field: block centres at bp*fx + bp/2 (source pixels; bp = 8, or 16 for a wide source)
                    int nBg = 0, okBg = 0, nBk = 0, okBk = 0; double eBg = 0, eBk = 0;
                    int bp = D3DRenderer.InterpBlockPx(new Size(w, h));
                    for (int fy = 0; fy < fs.Height; fy++)
                        for (int fx = 0; fx < fs.Width; fx++)
                        {
                            int cx = fx * bp + bp / 2, cy = fy * bp + bp / 2;
                            double vx = field[(fy * fs.Width + fx) * 2], vy = field[(fy * fs.Width + fx) * 2 + 1];
                            var inner = Rectangle.Inflate(blk, -m, -m);
                            var outer = Rectangle.Inflate(blk, m * 2, m * 2);
                            if (inner.Contains(cx, cy))
                            {
                                nBk++; double e = Math.Max(Math.Abs(vx - bdx), Math.Abs(vy - bdy)); eBk += e; if (e <= 1.0 * Math.Max(1, scale)) okBk++;
                            }
                            else if (!outer.Contains(cx, cy) && cx > m && cy > m && cx < w - m && cy < h - m)
                            {
                                nBg++; double e = Math.Max(Math.Abs(vx - gdx), Math.Abs(vy - gdy)); eBg += e; if (e <= 1.0 * Math.Max(1, scale)) okBg++;
                            }
                        }
                    log.Add($"field {fs.Width}x{fs.Height}: background truth ({gdx:0.#},{gdy:0.#}) -> {okBg}/{nBg} blocks within 1 px (mean error {eBg / Math.Max(1, nBg):0.00} px); " +
                            $"block truth ({bdx:0.#},{bdy:0.#}) -> {okBk}/{nBk} within 1 px (mean error {eBk / Math.Max(1, nBk):0.00} px)");

                    if (stillsDir != null)
                    {
                        Directory.CreateDirectory(stillsDir);
                        bA.Save(Path.Combine(stillsDir, "synA.png"), ImageFormat.Png);
                        bB.Save(Path.Combine(stillsDir, "synB.png"), ImageFormat.Png);
                        mid.Save(Path.Combine(stillsDir, "synMid.png"), ImageFormat.Png);
                        using Bitmap gtb = MakeBitmap(gt, w, h);
                        gtb.Save(Path.Combine(stillsDir, "synTruth.png"), ImageFormat.Png);
                        log.Add("stills saved to " + stillsDir);
                    }
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
