using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

// Hidden developer command for calibrating and checking the same-picture pass (the game frame rate):
//   --dup-probe [file.csv] [--seconds 12] [--display name] [--interp] [--off]
// runs the Direct3D engine on the capture device for some seconds and writes one line per captured frame (the four numbers the
// GPU reduced the thumbnail difference to, and the verdict), then a summary with the live game frame rate, the answer delay and the GPU time of the pass.
internal static partial class Program
{
    private static string? _dupProbeComp;
    private static CaptureMode? _dupProbeMode;

    private static void RunDupProbe(string outPath, int seconds, bool interp, bool off)
    {
        var log = new List<string>();
        var rows = new List<string> { "frame,t_ms,mean,max,c1,c2,same" };
        Form form = NewProbeWindow("YuCap dup-probe", out VideoBox canvas);
        var engine = new D3DVideoEngine();
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
                log.Add("modes: " + string.Join(", ", modes.Select(m => $"{m.Compression} {m.Width}x{m.Height}@{m.Fps}")));
                CaptureMode? mode = _dupProbeMode != null ? modes.Where(m => m.Width == _dupProbeMode.Width && m.Height == _dupProbeMode.Height && m.Fps == _dupProbeMode.Fps).FirstOrDefault() : null;
                mode ??= modes.Where(m => m.Width == 1920 && m.Height == 1080 && m.Fps == 60)
                    .OrderBy(m => _dupProbeComp != null && m.Compression.Equals(_dupProbeComp, StringComparison.OrdinalIgnoreCase) ? 0 : m.Compression == "NV12" ? 1 : 2).FirstOrDefault();
                engine.Start(dev, mode);
                engine.SetVideoRect(new Rectangle(0, 0, canvas.ClientSize.Width, canvas.ClientSize.Height));
                engine.Interpolation = interp;
                PumpFor(1500);
                log.Add($"device {dev.Name}, mode {engine.CurrentMode?.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height}@{engine.NegotiatedFps}");
                D3DRenderer? r = engine.DebugRenderer;
                if (r == null) { log.Add("no renderer"); return; }
                log.Add("adapter " + r.AdapterName);
                log.Add($"dup available {r.DupAvailable}; interpolation {(engine.InterpolationState is { } st ? $"active={st.Active} idle={st.Idle}" : "?")}");
                if (off) r.DupEnabled = false;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long n = 0, sameN = 0;
                var means = new List<double>(); var maxes = new List<double>();
                lock (rows)
                {
                    r.DupSink = (fn, arrival, sum, max, c1, c2, same) =>
                    {
                        double mean = sum / 64.0 / DupMath.ThumbPixels;
                        lock (rows)
                        {
                            rows.Add($"{fn},{(arrival - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0},{mean:0.0000},{max / 64.0:0.000},{c1},{c2},{(same ? 1 : 0)}");
                            n++; if (same) sameN++;
                            means.Add(mean); maxes.Add(max / 64.0);
                        }
                    };
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int sec = 0;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    PumpFor(1000);
                    sec++;
                    log.Add($"t={sec}s in={(engine.Stats?.InputFps ?? 0):0.0} gameFps={engine.GameFps:0.0} reliable={engine.GameFpsReliable} still={engine.PictureStill} uniquePeriod={r.UniquePeriodMs:0.0}ms answerLat={r.AnswerLatencyMs:0.0}ms extraHold={r.CadenceExtraHoldMs():0.0}ms");
                }
                r.DupSink = null;
                lock (rows)
                {
                    log.Add($"frames answered {n}, same {sameN} ({(n > 0 ? 100.0 * sameN / n : 0):0.0}%)");
                    var sm = means.OrderBy(x => x).ToList(); var sx = maxes.OrderBy(x => x).ToList();
                    string Q(List<double> v, double p) => v.Count == 0 ? "n/a" : v[Math.Clamp((int)Math.Ceiling(p * v.Count) - 1, 0, v.Count - 1)].ToString("0.0000");
                    log.Add("mean diff (levels) p5/25/50/75/90/95/99/max: " + string.Join(" / ", new[] { 0.05, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99, 1.0 }.Select(p => Q(sm, p))));
                    log.Add("max  diff (levels) p5/25/50/75/90/95/99/max: " + string.Join(" / ", new[] { 0.05, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99, 1.0 }.Select(p => Q(sx, p))));
                }
                foreach ((int part, string name) in new[] { (0, "whole pass"), (1, "clear only") })
                {
                    double[] bench = r.DupBench(300, part);
                    if (bench.Length == 0) continue;
                    var b = bench.OrderBy(x => x).ToList();
                    log.Add($"GPU ms, {name} (n={b.Count}): median {b[b.Count / 2]:0.0000} p95 {b[(int)(b.Count * 0.95)]:0.0000} max {b[^1]:0.0000}");
                }
                if (engine.Stats is { } gs) log.Add($"frame GPU avg {gs.GpuMsAvg:0.00} ms max {gs.GpuMsMax:0.00} ms");
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { engine.Dispose(); } catch { }
                try { lock (rows) File.WriteAllLines(outPath, rows); File.WriteAllLines(Path.ChangeExtension(outPath, ".txt"), log); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }
}
