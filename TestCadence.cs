using System;
using System.Collections.Generic;
using System.Linq;

namespace YuCap;

// Test mode: "ゲームのコマ数に合わせた補間". The live ring frames (arrival, repeat flag, timeline) are replayed through the REAL pair
// selection (PairSelect) at the display's refresh rate, once the former way (repeats are pairs) and once on the unique-picture timeline, and
// the position of the shown picture in units of game pictures is followed from refresh to refresh. Wording in the report is Japanese only.
internal static class CadenceCheck
{
    internal sealed class SimResult
    {
        public int Refreshes, Analysed, Starved;
        public double Cv, StallShare, Jump, HoldMs;
    }

    /// <param name="frames">Consecutive ring frames, oldest first.</param>
    /// <param name="hz">The display's refresh rate.</param>
    /// <param name="marginMs">The pacing margin the renderer holds frames back by.</param>
    /// <param name="nominalMs">One frame interval of the capture.</param>
    /// <param name="extraHoldMs">The hold-back the unique-picture timeline adds (measured live); 0 for the former timeline.</param>
    /// <param name="answerLatMs">How long after its arrival the repeat verdict of a frame is known.</param>
    /// <param name="periodMs">Interval of the unique pictures (for the cap on a long wait).</param>
    public static SimResult Simulate(IReadOnlyList<D3DRenderer.TimelineFrame> frames, double hz, double marginMs, double nominalMs,
        double extraHoldMs, double answerLatMs, double periodMs, bool uniqueAware)
    {
        var res = new SimResult();
        if (frames.Count < 20 || hz <= 0) return res;
        double hold = marginMs + nominalMs + (uniqueAware ? extraHoldMs : 0);
        res.HoldMs = hold;

        // picture number of every frame, from the final verdicts
        var pic = new int[frames.Count];
        int id = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0 && !frames[i].Dup) id++;
            pic[i] = id;
        }
        var picOfSeq = new Dictionary<long, int>();
        for (int i = 0; i < frames.Count; i++) picOfSeq[frames[i].Seq] = pic[i];

        const double us = 1000.0;                       // ticks of the simulation: microseconds
        double period = 1000.0 / hz;
        double t0 = frames[0].ArrivalMs, t1 = frames[^1].ArrivalMs;
        double from = t0 + hold + 150, to = t1 - 2.5 * Math.Max(nominalMs, periodMs > 0 ? periodMs : 0);
        var entries = new List<TimelineEntry>();
        var steps = new List<double>();
        double prevPos = double.NaN;
        for (double tau = t0; tau <= t1; tau += period)
        {
            entries.Clear();
            foreach (D3DRenderer.TimelineFrame f in frames)
            {
                if (f.ArrivalMs > tau) break;
                bool known = f.Resolved && f.ArrivalMs + answerLatMs <= tau;
                entries.Add(new TimelineEntry(f.Seq, (long)(f.SmoothMs * us), (long)((known ? f.UMs : f.SmoothMs) * us), known && f.Dup));
            }
            if (entries.Count == 0) continue;
            long t = (long)((tau - hold) * us);
            int a = PairSelect.Pick(entries, t, uniqueAware, (periodMs > 0 ? periodMs : nominalMs) * us, nominalMs * us, out int b, out double alpha);
            res.Refreshes++;
            if (a < 0) { prevPos = double.NaN; continue; }
            int pa = picOfSeq.GetValueOrDefault(entries[a].Seq);
            int pb = b >= 0 ? picOfSeq.GetValueOrDefault(entries[b].Seq) : pa;
            double pos = pa + (b >= 0 ? alpha : 0) * (pb - pa);
            if (tau >= from && tau <= to)
            {
                if (b < 0) res.Starved++;
                if (!double.IsNaN(prevPos)) steps.Add(pos - prevPos);
            }
            prevPos = pos;
        }
        res.Analysed = steps.Count;
        if (steps.Count >= 8)
        {
            double mean = steps.Average();
            double sd = Math.Sqrt(steps.Select(x => (x - mean) * (x - mean)).Average());
            res.Cv = mean > 1e-9 ? sd / mean : 0;
            res.StallShare = steps.Count(x => x < 0.2 * mean) / (double)steps.Count;
            res.Jump = mean > 1e-9 ? steps.Max() / mean : 0;
        }
        return res;
    }

    /// <summary>The usual number of frames between new pictures (median) and how many times the picture stood still far longer than that
    /// (a hitch or a loading screen, not the game's own rate).</summary>
    public static (double Typical, int Stalls, int LongestFrames) Gaps(IReadOnlyList<D3DRenderer.TimelineFrame> frames)
    {
        var gaps = new List<int>();
        int since = 0; bool started = false;
        foreach (D3DRenderer.TimelineFrame f in frames)
        {
            if (!f.Dup) { if (started) gaps.Add(since); started = true; since = 1; }
            else since++;
        }
        if (gaps.Count < 4) return (0, 0, 0);
        var sorted = gaps.OrderBy(g => g).ToList();
        double med = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
        int stalls = gaps.Count(g => g >= 6 && g > 2.5 * med);
        return (med, stalls, sorted[^1]);
    }

    /// <summary>How the new pictures follow each other, in frames: "2 コマおき（30 回/秒）" or "1 と 2 コマおきの交互" ...</summary>
    public static string DescribeCadence(IReadOnlyList<D3DRenderer.TimelineFrame> frames, double inputFps)
    {
        var gaps = new List<int>();
        int since = 0; bool started = false;
        foreach (D3DRenderer.TimelineFrame f in frames)
        {
            if (!f.Dup) { if (started) gaps.Add(since); started = true; since = 1; }
            else since++;
        }
        if (gaps.Count < 4) return "絵がほとんど変わらなかった";
        var groups = gaps.GroupBy(g => g).OrderByDescending(g => g.Count()).ToList();
        string main = string.Join(" と ", groups.Take(3).Select(g => $"{g.Key}").OrderBy(x => int.Parse(x)));
        double mean = gaps.Average();
        double fps = inputFps / mean;
        if (groups.Count == 1) return $"{groups[0].Key} コマおきに新しい絵（約 {fps:0} 回/秒）";
        if (groups[0].Count() * 10 >= gaps.Count * 8) return $"{groups[0].Key} コマおきが中心（{groups[0].Count()}/{gaps.Count} 回）、ときどき {string.Join("・", groups.Skip(1).Take(3).Select(g => g.Key))} コマおき（約 {fps:0} 回/秒）";
        return $"{main} コマおきが入り混じる（平均 {mean:0.0} コマおき、約 {fps:0} 回/秒）";
    }
}
