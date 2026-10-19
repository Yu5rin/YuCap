using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace YuCap;

// Hidden developer commands for judging frame interpolation on a stream whose pictures repeat (a 30 fps game in a 60 fps capture):
//   --interp-seq-capture <dir> [--frames 120] [--display name] [--size 960x540]
//        save a contiguous run of live frames (PNG) with the arrival times and the repeat flags the GPU found
//   --interp-seq-eval <dir> [--hz 144] [--margin 4] [--out file.txt] [--patterns real,2,1+2,1+1+2,3,1]
//        replay the saved run (and streams re-timed from its pictures with other repeat patterns) through the REAL ring, same-picture
//        pass and pair selection against a simulated clock at the display's refresh rate, once on the former timeline (repeats are
//        pairs) and once on the timeline of unique pictures, and measure how evenly the picture moves from refresh to refresh
internal static partial class Program
{
    // ------------------------------------------------------------------------------------ capture

    private static void RunInterpSeqCapture(string outDir, int frames, string display, Size size)
    {
        var log = new List<string>();
        Directory.CreateDirectory(outDir);
        _probeDisplay = display.Length > 0 ? display : null;
        Form form = NewProbeWindow("YuCap interp-seq-capture", out VideoBox canvas);
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
                CaptureMode? mode = modes.Where(m => m.Width == 1920 && m.Height == 1080 && m.Fps == 60)
                    .OrderBy(m => m.Compression == "NV12" ? 0 : 1).FirstOrDefault();
                engine.Start(dev, mode);
                engine.SetVideoRect(new Rectangle(0, 0, canvas.ClientSize.Width, canvas.ClientSize.Height));
                engine.Interpolation = true;
                PumpFor(1500);
                InterpolationStatus? st = engine.InterpolationState;
                log.Add($"device {dev.Name}, mode {engine.CurrentMode?.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height}@{engine.NegotiatedFps}");
                log.Add($"interpolation: {(st == null ? "?" : $"active={st.Active} idle={st.Idle} source={st.SourceFps:0.0}fps refresh={st.RefreshHz:0}Hz")}");
                if (st is not { Active: true }) { log.Add("interpolation is not active: no ring"); return; }
                engine.DebugSetRingExtra(frames + 6);
                PumpFor(frames * 1000 / Math.Max(1, engine.NegotiatedFps) + 1800);
                var dump = new List<D3DRenderer.DumpFrame>();
                if (!engine.DebugInterpDump(frames, size, dump) || dump.Count == 0) { log.Add("no frames in the ring"); return; }
                engine.DebugSetRingExtra(0);
                D3DRenderer? r = engine.DebugRenderer;
                long hz = System.Diagnostics.Stopwatch.Frequency;
                long a0 = dump[0].Arrival;
                var sb = new StringBuilder();
                sb.AppendLine("# idx seq arrival_ms smooth_ms gpu_dup resolved");
                int contiguous = 1;
                for (int i = 0; i < dump.Count; i++)
                {
                    D3DRenderer.DumpFrame f = dump[i];
                    if (i > 0 && f.Seq == dump[i - 1].Seq + 1) contiguous++;
                    f.Picture.Save(Path.Combine(outDir, $"f{i:000}.png"), ImageFormat.Png);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:0.000} {3:0.000} {4} {5}", i, f.Seq,
                        (f.Arrival - a0) * 1000.0 / hz, (f.Smooth - a0) * 1000.0 / hz, f.Dup ? 1 : 0, f.Resolved ? 1 : 0));
                    f.Picture.Dispose();
                }
                File.WriteAllText(Path.Combine(outDir, "seq.txt"), sb.ToString());
                int dupN = dump.Count(f => f.Dup);
                log.Add($"saved {dump.Count} frames ({contiguous} contiguous), GPU flagged {dupN} as repeats; span {(dump[^1].Arrival - a0) * 1000.0 / hz:0} ms");
                if (r != null) log.Add($"game fps {engine.GameFps:0.0} reliable {engine.GameFpsReliable}; unique period {r.UniquePeriodMs:0.0} ms; answer latency {r.AnswerLatencyMs:0.0} ms; extra hold {r.CadenceExtraHoldMs():0.0} ms; nominal {1000.0 / Math.Max(1, engine.Stats?.InputFps ?? 60):0.00} ms");
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { engine.Dispose(); } catch { }
                try { File.WriteAllLines(Path.Combine(outDir, "capture.txt"), log); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }

    // ------------------------------------------------------------------------------------ replay

    internal sealed class ReplayResult
    {
        public string Name = "";
        public int Refreshes, Analysed, Stalls;
        public double MeanMotion, Cv, StallShare, HoldMs;
        public int FramesDup, FramesDupFlagged, FramesWrongFlag;
        public double GameFps, PeriodMs, ExtraHoldMs;
        public double MaxMotionRatio;
        public List<double> Motion = new();
        // the same measures on the TIMELINE only: position of the shown picture in units of game pictures (picture number of A plus the
        // blend towards B), so the content's own motion cannot disturb them. Ideal: the same step at every refresh.
        public double PosCv, PosStallShare, PosJump;
        public int Starved;
        public List<string> Trace = new();
    }

    private static float[] Luma(Bitmap b, int div, out int w, out int h)
    {
        BitmapData bd = b.LockBits(new Rectangle(Point.Empty, b.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[bd.Stride * b.Height];
        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
        int stride = bd.Stride;
        b.UnlockBits(bd);
        w = b.Width / div; h = b.Height / div;
        var res = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double s = 0;
                for (int dy = 0; dy < div; dy++)
                    for (int dx = 0; dx < div; dx++)
                    {
                        int i = (y * div + dy) * stride + (x * div + dx) * 4;
                        s += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                    }
                res[y * w + x] = (float)(s / (div * div));
            }
        return res;
    }

    /// <summary>Replays <paramref name="frames"/> (arrival times in ms) through a headless renderer: a refresh every 1000/hz ms of simulated
    /// time shows the picture the interpolation makes for that instant. Returns how evenly that picture moved from refresh to refresh.</summary>
    internal static ReplayResult ReplayStream(string name, List<(Bitmap Pic, double ArrivalMs, int Unique)> frames, double hz, double marginMs, bool uniqueAware)
    {
        var res = new ReplayResult { Name = name };
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long Ticks(double ms) => (long)(ms * freq / 1000.0);
        using var r = new D3DRenderer();
        r.CreateDevice();
        r.CreateShaders();
        if (!r.InterpAvailable || !r.DupAvailable) throw new InvalidOperationException("interpolation or the same-picture pass is unavailable");
        r.UniqueAware = uniqueAware;
        var flagged = new Dictionary<long, bool>();
        r.DupSink = (fn, _, _, _, _, _, same) => flagged[fn] = same;
        long simNow = 0;
        r.ClockOverride = () => simNow;

        var iv = new List<double>();
        for (int i = 1; i < frames.Count; i++) iv.Add(frames[i].ArrivalMs - frames[i - 1].ArrivalMs);
        iv.Sort();
        double nominalMs = iv.Count > 0 ? iv[iv.Count / 2] : 1000.0 / 60;
        r.SetNominalMs(nominalMs);
        var smoother = new ArrivalSmoother();
        var gov = new HoldGovernor();

        double period = 1000.0 / hz;
        double tEnd = frames[^1].ArrivalMs + 250;
        int next = 0;
        long lastFrameNo = 0;
        var frameNoOfArrival = new List<(double Ms, long No)>();
        float[]? prev = null;
        int lw = 0, lh = 0;
        double holdMs = marginMs + nominalMs, lastHold = holdMs;
        double firstT = frames[0].ArrivalMs;
        var motionAt = new List<(double T, double M, double P, bool Starved)>();
        var uniqueOfFrameNo = new Dictionary<long, int>();
        double lastCad = 0;
        r.ConfigureRing((int)Math.Ceiling(holdMs), 60);
        r.SetInterpolation(true, Ticks(holdMs));
        for (double tau = firstT; tau <= tEnd; tau += period)
        {
            while (next < frames.Count && frames[next].ArrivalMs <= tau)
            {
                simNow = Ticks(frames[next].ArrivalMs);
                long sm = smoother.Track(simNow, nominalMs * freq / 1000.0);
                long no = r.ReplaySubmit(frames[next].Pic, simNow, sm);
                frameNoOfArrival.Add((frames[next].ArrivalMs, no));
                uniqueOfFrameNo[no] = frames[next].Unique;
                lastFrameNo = no;
                next++;
            }
            simNow = Ticks(tau);
            // the answer for a frame is taken at the first refresh after the GPU finished it (well under a millisecond)
            long upTo = 0;
            foreach (var (ms, no) in frameNoOfArrival) if (ms <= tau - 0.5) upTo = no;
            if (upTo > 0) r.DupResolveUpTo(upTo);

            double want = uniqueAware ? r.CadenceExtraHoldMs() : 0;
            lastCad = gov.Step(want, period);
            holdMs = marginMs + nominalMs + lastCad;
            lastHold = holdMs;
            r.ConfigureRing((int)Math.Ceiling(holdMs), 60);
            r.SetInterpolation(true, Ticks(holdMs));
            bool ok = r.InterpUpdate(Ticks(tau) - Ticks(holdMs), 0, false, VideoAdjustments.Neutral, default, false, out bool changed);
            res.Refreshes++;
            double m = 0;
            if (ok)
            {
                if (changed || prev == null)
                {
                    float[]? cur = r.ReplayPresentedLuma(4, out lw, out lh);
                    if (cur != null)
                    {
                        if (prev != null && prev.Length == cur.Length)
                        {
                            double s = 0;
                            for (int i = 0; i < cur.Length; i++) s += Math.Abs(cur[i] - prev[i]);
                            m = s / cur.Length;
                        }
                        prev = cur;
                    }
                }
            }
            (long pa, long pb, double pal) = r.ReplayLastPair();
            double pos = double.NaN;
            bool starved = false;
            if (ok && pa != 0 && uniqueOfFrameNo.TryGetValue(pa, out int ua))
            {
                int ub = pb != 0 && uniqueOfFrameNo.TryGetValue(pb, out int ubb) ? ubb : ua;
                pos = ua + Math.Max(0, pal) * (ub - ua);
                starved = pb == 0;
            }
            motionAt.Add((tau, m, pos, starved));
            res.Trace.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.00},{1},{2},{3:0.000},{4:0.000},{5:0.000},{6},{7:0.0}", tau, pa, pb, pal, pos, m, changed ? 1 : 0, holdMs));
        }

        // the part of the run that is in steady state: after the hold-back has filled, before the last pictures run out
        double from = firstT + lastHold + 400, to = frames[^1].ArrivalMs - 2.5 * Math.Max(nominalMs, 100.0 / 3);
        var winAll = motionAt.Where(x => x.T >= from && x.T <= to).ToList();
        var win = winAll.Select(x => x.M).ToList();
        res.Starved = winAll.Count(x => x.Starved);
        var steps = new List<double>();
        for (int i = 1; i < motionAt.Count; i++)
            if (motionAt[i].T >= from && motionAt[i].T <= to && !double.IsNaN(motionAt[i].P) && !double.IsNaN(motionAt[i - 1].P))
                steps.Add(motionAt[i].P - motionAt[i - 1].P);
        if (steps.Count > 4)
        {
            double pm = steps.Average();
            double psd = Math.Sqrt(steps.Select(x => (x - pm) * (x - pm)).Average());
            res.PosCv = pm > 1e-9 ? psd / pm : 0;
            res.PosStallShare = steps.Count(x => x < 0.2 * pm) / (double)steps.Count;
            res.PosJump = pm > 1e-9 ? steps.Max() / pm : 0;
        }
        res.Motion = win;
        res.Analysed = win.Count;
        if (win.Count > 4)
        {
            res.MeanMotion = win.Average();
            double sd = Math.Sqrt(win.Select(x => (x - res.MeanMotion) * (x - res.MeanMotion)).Average());
            res.Cv = res.MeanMotion > 1e-9 ? sd / res.MeanMotion : 0;
            res.Stalls = win.Count(x => x < 0.2 * res.MeanMotion);
            res.StallShare = res.Stalls / (double)win.Count;
            res.MaxMotionRatio = res.MeanMotion > 1e-9 ? win.Max() / res.MeanMotion : 0;
        }
        // did the pass find the repeats the stream was built from?
        for (int i = 1; i < frames.Count && i < frameNoOfArrival.Count; i++)
        {
            bool truth = frames[i].Unique == frames[i - 1].Unique;
            if (truth) res.FramesDup++;
            if (flagged.TryGetValue(frameNoOfArrival[i].No, out bool f))
            {
                if (f) res.FramesDupFlagged++;
                if (f != truth) res.FramesWrongFlag++;
            }
        }
        res.HoldMs = lastHold;
        res.GameFps = r.GameFps;
        res.PeriodMs = r.UniquePeriodMs;
        res.ExtraHoldMs = lastCad;
        return res;
    }

    // ------------------------------------------------------------------------------------ eval

    private static void RunInterpSeqEval(string dir, double hz, double margin, string outPath, string[] patterns, string? traceDir)
    {
        var sb = new StringBuilder();
        void Out(string s) { sb.AppendLine(s); }
        try
        {
            var lines = File.ReadAllLines(Path.Combine(dir, "seq.txt")).Where(l => !l.StartsWith('#') && l.Trim().Length > 0).Select(l => l.Split(' ')).ToList();
            var pics = new List<Bitmap>();
            var arr = new List<double>();
            int gpuDup = 0;
            foreach (string[] f in lines)
            {
                int idx = int.Parse(f[0], CultureInfo.InvariantCulture);
                using var fs = File.OpenRead(Path.Combine(dir, $"f{idx:000}.png"));
                using var tmp = new Bitmap(fs);
                pics.Add(new Bitmap(tmp));
                arr.Add(double.Parse(f[2], CultureInfo.InvariantCulture));
                if (f[4] == "1") gpuDup++;
            }
            Out($"sequence: {pics.Count} frames {pics[0].Width}x{pics[0].Height}, span {arr[^1] - arr[0]:0} ms, GPU flagged {gpuDup} repeats at capture; simulated display {hz:0} Hz, pacing margin {margin:0.0} ms");

            // the distinct pictures of the run, by exact luma equality with the previous frame
            var uniqIdx = new List<int>();
            float[]? last = null;
            for (int i = 0; i < pics.Count; i++)
            {
                float[] l = Luma(pics[i], 8, out _, out _);
                bool same = last != null && l.Length == last.Length && Enumerable.Range(0, l.Length).All(k => Math.Abs(l[k] - last[k]) < 0.01f);
                if (!same) uniqIdx.Add(i);
                last = l;
            }
            double medGap = uniqIdx.Count > 1 ? Enumerable.Range(1, uniqIdx.Count - 1).Select(k => arr[uniqIdx[k]] - arr[uniqIdx[k - 1]]).OrderBy(x => x).ElementAt((uniqIdx.Count - 1) / 2) : 0;
            Out($"distinct pictures: {uniqIdx.Count} of {pics.Count} frames (median gap {medGap:0.0} ms = {(medGap > 0 ? 1000 / medGap : 0):0.0} pictures/s)");
            Out("");
            Out("motion per refresh = mean absolute luma change of the presented picture from one refresh to the next (1/4 scale). Ideal: constant.");
            Out("CV = std/mean of that motion; stall = refresh whose motion is under 20% of the mean while the picture is moving; jump = largest motion / mean.");
            Out("");
            Out($"{"stream",-34}{"variant",-6}{"posCV",7}{"posSt%",8}{"posJmp",8}{"CV",7}{"stall%",8}{"jump",6}{"hold",7}{"gameFps",8}{"starved",8}  note");

            foreach (string pat in patterns)
            {
                List<(Bitmap, double, int)> stream = new();
                string label;
                if (pat == "real")
                {
                    int pid = -1, nextU = 0;
                    for (int i = 0; i < pics.Count; i++)
                    {
                        if (nextU < uniqIdx.Count && uniqIdx[nextU] == i) { pid++; nextU++; }
                        stream.Add((pics[i], arr[i], Math.Max(pid, 0)));
                    }
                    label = "real capture";
                }
                else
                {
                    // "2" = every picture twice; "1+2" = 1, 2, 1, 2...; "1@28/2@30" = 28 pictures once each, then 30 pictures twice each
                    double period = arr.Count > 1 ? (arr[^1] - arr[0]) / (arr.Count - 1) : 1000.0 / 60;
                    var rnd = new Random(7);
                    int fno = 0, u = 0;
                    var allCounts = new List<int>();
                    foreach (string seg in pat.Split('/'))
                    {
                        string[] part = seg.Split('@');
                        int[] counts = part[0].Split('+', ',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                        int n = part.Length > 1 ? int.Parse(part[1], CultureInfo.InvariantCulture) : uniqIdx.Count - u;
                        for (int k = 0; k < n && u < uniqIdx.Count; k++, u++)
                        {
                            int c = counts[k % counts.Length];
                            allCounts.Add(c);
                            for (int j = 0; j < c; j++)
                            {
                                stream.Add((pics[uniqIdx[u]], fno * period + (rnd.NextDouble() - 0.5) * 1.5, u));
                                fno++;
                            }
                        }
                    }
                    label = $"x[{pat}] ({1000 / (period * allCounts.Average()):0.0}/s)";
                }
                if (stream.Count < 30) { Out($"{label}: too few frames"); continue; }
                foreach (bool uniqueAware in new[] { false, true })
                {
                    ReplayResult rr = ReplayStream(label, stream, hz, margin, uniqueAware);
                    if (traceDir != null) File.WriteAllLines(Path.Combine(traceDir, $"trace_{pat.Replace("+", "_").Replace("/", "-").Replace("@", "n")}_{(uniqueAware ? "new" : "old")}.csv"), new[] { "t_ms,A,B,alpha,pos,motion,changed,hold" }.Concat(rr.Trace));
                    Out($"{label,-34}{(uniqueAware ? "new" : "old"),-6}{rr.PosCv,7:0.000}{rr.PosStallShare * 100,8:0.0}{rr.PosJump,8:0.0}{rr.Cv,7:0.00}{rr.StallShare * 100,8:0.0}{rr.MaxMotionRatio,6:0.0}{rr.HoldMs,7:0.0}{rr.GameFps,8:0.0}{rr.Starved,8}  refreshes {rr.Analysed}, repeats {rr.FramesDup} found {rr.FramesDupFlagged} wrong {rr.FramesWrongFlag}");
                }
            }
        }
        catch (Exception ex) { Out("EXCEPTION: " + ex); }
        File.WriteAllText(outPath, sb.ToString());
        Console.Out.Flush();
    }
}
