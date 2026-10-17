using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

// Hidden developer commands for tuning frame interpolation on real footage (no test report involved):
//   --interp-capture <dir> [--count 20] [--seconds 150]   save evenly spaced triples F0,F1,F2 (PNG) of the live source
//   --interp-eval <dir> [--out file.txt] [--quick] [--show t01,t05]   run the real GPU pipeline on saved triples
//        (the middle of F0..F2 against the real F1, same metrics as the test mode), the known-motion check and the GPU time.
//        Shaders can be edited without rebuilding: set YUCAP_SHADER_DIR to the Shaders folder (compiled from disk at start).
internal static partial class Program
{
    private static void RunInterpCapture(string outDir, int count, int seconds, string display, double maxMoving)
    {
        var log = new List<string>();
        string logPath = Path.Combine(outDir, "capture.txt");
        Directory.CreateDirectory(outDir);
        Form form = NewProbeWindow("YuCap interp-capture", out VideoBox canvas);
        var engine = new D3DVideoEngine();
        form.Load += (_, _) =>
        {
            var all = Displays.List();
            DisplayInfo? di = all.FirstOrDefault(d => d.FriendlyName.Contains(display, StringComparison.OrdinalIgnoreCase) || d.DeviceName.EndsWith(display, StringComparison.OrdinalIgnoreCase));
            log.Add("displays: " + string.Join("; ", all.Select(d => d.MenuLabel())) + " -> " + (di?.MenuLabel() ?? "none matched"));
            if (di != null) form.Location = new Point(di.Bounds.Left + 80, di.Bounds.Top + 80);
        };
        form.Shown += (_, _) =>
        {
            try
            {
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Attach(canvas.Handle);
                engine.Start(dev, null);
                PumpFor(800);
                List<CaptureMode> modes = engine.GetModes();
                CaptureMode? mode = modes.Where(m => m.Width == 1920 && m.Height == 1080 && m.Fps == 60)
                    .OrderBy(m => m.Compression == "NV12" ? 0 : 1).FirstOrDefault()
                    ?? modes.Where(m => m.Fps <= 60 && m.Fps >= 24).OrderByDescending(m => m.Fps).ThenByDescending(m => m.Height == 1080 ? 1 : 0).FirstOrDefault();
                engine.Start(dev, mode);
                engine.SetVideoRect(new Rectangle(0, 0, canvas.ClientSize.Width, canvas.ClientSize.Height));
                engine.Interpolation = true;
                PumpFor(1500);
                log.Add($"device {dev.Name}, mode {engine.CurrentMode?.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height}@{engine.NegotiatedFps}");
                InterpolationStatus? st = engine.InterpolationState;
                log.Add($"interpolation: {(st == null ? "?" : $"active={st.Active} idle={st.Idle} source={st.SourceFps:0.0}fps refresh={st.RefreshHz:0}Hz")}; received {engine.FramesReceived}, presented {engine.FramesPresented}; window on {Screen.FromControl(form).DeviceName}");
                double fps = engine.NegotiatedFps > 0 ? engine.NegotiatedFps : 60;
                engine.DebugSetRingExtra((int)Math.Ceiling(fps * 0.6) + 4);
                PumpFor(600);

                var live = new InterpCheck.LiveFrames();
                int pick = 0, saved = 0, tried = 0, stillN = 0, dupN = 0;
                long after = 0; double lastSave = -99; var moves = new List<double>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (saved < count && sw.Elapsed.TotalSeconds < seconds)
                {
                    PumpFor(350);
                    var fresh = new List<D3DRenderer.ScanFrame>();
                    if (!engine.DebugInterpScan(after, fresh)) continue;
                    foreach (D3DRenderer.ScanFrame f in fresh) { live.Add(f.Seq, f.Smooth, f.W, f.H, f.Luma); after = Math.Max(after, f.Seq); }
                    if (!live.TryPick(ref pick, out var u0, out var u1, out var u2)) continue;
                    if (!engine.DebugInterpHoldout(u0.Seq, u1.Seq, u2.Seq, out Bitmap? f0, out Bitmap? f1, out Bitmap? f2, out Bitmap? mid)) continue;
                    try
                    {
                        tried++;
                        InterpCheck.Triple t = InterpCheck.Analyze(f0!, f1!, f2!, mid!, 1e9, out Bitmap? sp);
                        sp?.Dispose();
                        moves.Add(t.Moving);
                        if (t.Moving < 0.005) { stillN++; continue; }
                        if (t.Moving > maxMoving) continue;
                        if (saved > 0 && sw.Elapsed.TotalSeconds - lastSave < 3) continue;       // spread the triples over time
                        if (t.Dup) { dupN++; continue; }
                        saved++; lastSave = sw.Elapsed.TotalSeconds;
                        string b = Path.Combine(outDir, $"t{saved:00}");
                        f0!.Save(b + "_f0.png", ImageFormat.Png); f1!.Save(b + "_f1.png", ImageFormat.Png); f2!.Save(b + "_f2.png", ImageFormat.Png);
                        log.Add($"t{saved:00}: moving {t.Moving:P1}  interp {t.InterpPsnr:0.0} blend {t.BlendPsnr:0.0} repeat {t.RepeatPsnr:0.0} dB  (live pipeline, t={sw.Elapsed.TotalSeconds:0}s)");
                    }
                    finally { f0?.Dispose(); f1?.Dispose(); f2?.Dispose(); mid?.Dispose(); }
                }
                engine.DebugSetRingExtra(0);
                var (win, even) = live.CountWindows();
                log.Add("moving shares seen: " + string.Join(" ", moves.Select(m => (m * 100).ToString("0"))));
                log.Add($"saved {saved} triples; tried {tried} (still {stillN}, duplicate {dupN}); captured {live.Captured}, unique {live.Unique.Count}, even windows {even}/{win}, median gap {live.MedianGapMs():0.0} ms");
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { engine.Dispose(); } catch { }
                try { File.WriteAllLines(logPath, log); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }

    private sealed record EvalVariant(string Name);

    private static void RunInterpEval(string dir, string outPath, List<EvalVariant> variants, bool quickTiming, string[] show)
    {
        var log = new List<string>();
        Form form = NewProbeWindow("YuCap interp-eval", out VideoBox canvas);
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

                var files = Directory.GetFiles(dir, "t*_f0.png").OrderBy(x => x).ToList();
                var trip = new List<(Bitmap F0, Bitmap F1, Bitmap F2, string Name)>();
                foreach (string f0 in files)
                {
                    string b = f0.Substring(0, f0.Length - "_f0.png".Length);
                    trip.Add((new Bitmap(f0), new Bitmap(b + "_f1.png"), new Bitmap(b + "_f2.png"), Path.GetFileName(b)));
                }
                log.Add($"{trip.Count} triples from {dir}");
                string outDir = Path.Combine(dir, "eval");
                Directory.CreateDirectory(outDir);

                // reference (variant independent) numbers on the first variant's pass
                log.Add("variant | moving px PSNR interp/blend/repeat | MAE interp/blend/repeat | whole PSNR interp/blend/repeat | synth bg/block dB (blend) | GPU 1080p / 4K synth ms");
                foreach (EvalVariant v in variants)
                {
                    
                    var used = new List<InterpCheck.Triple>();
                    var perTriple = new List<string>();
                    int bestIdx = -1; double bestMov = 0;
                    for (int i = 0; i < trip.Count; i++)
                    {
                        var (f0, f1, f2, name) = trip[i];
                        if (!engine.DebugInterpSynthetic(f0, f2, 0.5, 0, out Bitmap? mid, out _, out _, out _) || mid == null) { log.Add("synthetic run failed"); continue; }
                        using (mid)
                        {
                            InterpCheck.Triple t = InterpCheck.Analyze(f0, f1, f2, mid, 1e9, out Bitmap? sp);
                            sp?.Dispose();
                            if (t.Moving >= 0.01 && !t.Dup) { used.Add(t); if (t.Moving > bestMov) { bestMov = t.Moving; bestIdx = i; } }
                            perTriple.Add($"    {name}: moving {t.Moving:P1} dup={t.Dup}  PSNR {t.InterpPsnr:0.0}/{t.BlendPsnr:0.0}/{t.RepeatPsnr:0.0}  MAE {t.InterpMae:0.0}/{t.BlendMae:0.0}/{t.RepeatMae:0.0}");
                        }
                    }
                    foreach (int bestIdx2 in show.Select(n => trip.FindIndex(x => x.Name == n)).Where(i => i >= 0).Append(bestIdx).Where(i => i >= 0).Distinct())
                    {
                        var (f0, f1, f2, tname) = trip[bestIdx2];
                        if (engine.DebugInterpSynthetic(f0, f2, 0.5, 0, out Bitmap? mid, out _, out _, out _) && mid != null)
                        {
                            using (mid)
                            {
                                InterpCheck.Triple t = InterpCheck.Analyze(f0, f1, f2, mid, 0, out Bitmap? sp);
                                try { sp?.Save(Path.Combine(outDir, v.Name + (bestIdx2 == bestIdx ? "" : "_" + tname) + ".png"), ImageFormat.Png); } finally { sp?.Dispose(); }
                                if (bestIdx2 != bestIdx) mid.Save(Path.Combine(outDir, v.Name + "_" + tname + "_mid.png"), ImageFormat.Png);
                            }
                        }
                    }
                    string Med(Func<InterpCheck.Triple, double> f) => used.Count == 0 ? "n/a" : InterpCheck.Median(used.Select(f)).ToString("0.0");
                    string Avg(Func<InterpCheck.Triple, double> f) => used.Count == 0 ? "n/a" : used.Average(f).ToString("0.0");

                    var syn = InterpCheck.RunSynthetic(engine, null, Path.Combine(outDir, v.Name + "_synth.png"));
                    string timing = "n/a";
                    if (!quickTiming)
                    {
                        string T(int w, int h)
                        {
                            using Bitmap pat = InterpCheck.MakePattern(w, h);
                            using Bitmap shifted = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                            int[] px = ReadPixels(pat);
                            using Bitmap b2 = MakeBitmap(ShiftPixels(px, w, h, 14, -8), w, h);
                            if (!engine.DebugInterpSynthetic(pat, b2, 0.5, 80, out Bitmap? m2, out _, out _, out string tm)) return "failed";
                            m2?.Dispose();
                            // "convert-less pyramid avg x; motion search avg y; synthesis avg z"
                            var mt = System.Text.RegularExpressions.Regex.Match(tm, @"motion search avg ([\d.]+).*synthesis avg ([\d.]+)");
                            var pt = System.Text.RegularExpressions.Regex.Match(tm, @"pyramid avg ([\d.]+)");
                            return mt.Success ? $"pyr {pt.Groups[1].Value} me {mt.Groups[1].Value} synth {mt.Groups[2].Value}" : tm;
                        }
                        timing = T(1920, 1080) + " | " + T(3840, 2160);
                        if (trip.Count > 0)
                        {
                            string TR(Bitmap a, Bitmap b)
                            {
                                if (!engine.DebugInterpSynthetic(a, b, 0.5, 80, out Bitmap? m2, out _, out _, out string tm)) return "failed";
                                m2?.Dispose();
                                var mt = System.Text.RegularExpressions.Regex.Match(tm, @"motion search avg ([\d.]+).*synthesis avg ([\d.]+)");
                                return mt.Success ? $"me {mt.Groups[1].Value} synth {mt.Groups[2].Value}" : tm;
                            }
                            Bitmap Up(Bitmap src)
                            {
                                var o = new Bitmap(3840, 2160, PixelFormat.Format32bppArgb);
                                using Graphics g = Graphics.FromImage(o);
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                                g.DrawImage(src, 0, 0, 3840, 2160);
                                return o;
                            }
                            // the same two real pictures, 1080p and enlarged to 4K (real motion: no early-outs from an exact shift)
                            int mid = trip.Count / 2;
                            // (adjacent frames F0, F1 = the pairs the live pipeline works on, 33 ms apart)
                            string t1 = TR(trip[mid].F0, trip[mid].F1);
                            using Bitmap u0 = Up(trip[mid].F0), u2 = Up(trip[mid].F1);
                            timing += " || real " + trip[mid].Name + " F0-F1: 1080p " + t1 + " | 4K " + TR(u0, u2);
                        }
                    }
                    log.Add($"{v.Name} n={used.Count} | median {Med(t => t.InterpPsnr)}/{Med(t => t.BlendPsnr)}/{Med(t => t.RepeatPsnr)} (mean {Avg(t => t.InterpPsnr)}/{Avg(t => t.BlendPsnr)}/{Avg(t => t.RepeatPsnr)}) | MAE med {Med(t => t.InterpMae)}/{Med(t => t.BlendMae)}/{Med(t => t.RepeatMae)} | whole {Med(t => t.InterpAll)}/{Med(t => t.BlendAll)}/{Med(t => t.RepeatAll)} | " +
                            $"synth bg {syn.BgPsnr:0.0} ({syn.BgBlend:0.0}) block {syn.BlkPsnr:0.0} ({syn.BlkBlend:0.0}) vec bg {syn.BgOk}/{syn.BgN} blk {syn.BlkOk}/{syn.BlkN} | {timing}");
                    foreach (string l in perTriple) log.Add(l);
                }
                                foreach (var t in trip) { t.F0.Dispose(); t.F1.Dispose(); t.F2.Dispose(); }
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

    private static void ParseInterpEvalArgs(string[] args, out string dir, out string outPath, out List<EvalVariant> variants, out bool quick, out string[] show)
    {
        dir = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_triples");
        outPath = Path.Combine(dir, "eval.txt");
        variants = new List<EvalVariant>();
        quick = false; show = Array.Empty<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--quick") quick = true;
            else if (args[i] == "--show" && i + 1 < args.Length) show = args[++i].Split(',');
        }
        if (variants.Count == 0) variants.Add(new EvalVariant("current"));
    }
}
