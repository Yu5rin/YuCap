using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

namespace YuCap;

// HUD protection checks of frame interpolation (developer tool / test mode): a moving background under a static overlay,
// and the same on real footage (pixels that stay unchanged over several pictures).
internal static class InterpHudCheck
{
    internal sealed class Result
    {
        public bool Ok;
        public string Failure = string.Empty;
        public double HudPsnr, HudBlend, RingPsnr, RingBlend, HudSharp;     // overlay rectangles / the 16 px ring around them; HudSharp = edge energy of the result / of the truth
        public int N;
    }

    private static int Ch(int px, int sh) => (px >> sh) & 255;
    private static double PsnrOf(double mse) => mse <= 1e-9 ? 99 : Math.Min(99, 10 * Math.Log10(255.0 * 255.0 / mse));

    private static double MaskPsnr(int[] p, int[] q, bool[] mask)
    {
        double se = 0; long n = 0;
        for (int i = 0; i < p.Length; i++)
        {
            if (!mask[i]) continue;
            for (int sh = 0; sh < 24; sh += 8) { int d = Ch(p[i], sh) - Ch(q[i], sh); se += d * d; n++; }
        }
        return n == 0 ? 99 : PsnrOf(se / n);
    }

    private static double EdgeEnergy(int[] p, int w, int h, bool[] mask)
    {
        double s = 0; long n = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                if (!mask[y * w + x]) continue;
                int l(int xx, int yy) { int c = p[yy * w + xx]; return (Ch(c, 16) * 3 + Ch(c, 8) * 6 + Ch(c, 0)) / 10; }
                s += Math.Abs(l(x + 1, y) - l(x - 1, y)) + Math.Abs(l(x, y + 1) - l(x, y - 1)); n++;
            }
        return n == 0 ? 0 : s / n;
    }

    private static int[] Blend(int[] a, int[] b, double alpha)
    {
        var o = new int[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int r = 0;
            for (int sh = 0; sh < 24; sh += 8) r |= (int)Math.Round(Ch(a[i], sh) * (1 - alpha) + Ch(b[i], sh) * alpha) << sh;
            o[i] = r | unchecked((int)0xFF000000);
        }
        return o;
    }

    /// <summary>Moving background (14, -8 px per picture) under two static overlay panels with text; 5 pictures of history, then the
    /// middle of the last pair against the exactly known truth.</summary>
    public static Result RunSynthetic(D3DVideoEngine engine, string? savePath)
    {
        var res = new Result();
        try
        {
            const int w = 1920, h = 1080;
            using Bitmap bgBmp = InterpCheck.MakePattern(w, h);
            int[] bg = Program.ReadPixels(bgBmp);
            var rects = new[] { new Rectangle(70, 60, 520, 120), new Rectangle(1300, 860, 520, 140) };
            using var ov = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(ov))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var f = new Font("Yu Gothic UI", 24, FontStyle.Bold);
                int k = 0;
                foreach (Rectangle r in rects)
                {
                    using (var br = new SolidBrush(Color.FromArgb(255, 30, 40, 70))) g.FillRectangle(br, r);
                    using (var pen = new Pen(Color.FromArgb(255, 230, 230, 240), 2)) g.DrawRectangle(pen, r);
                    g.DrawString(k == 0 ? "現在の進行 HP 100/100" : "QUEST 3 / 5  ★★★", f, Brushes.White, r.Left + 14, r.Top + 16);
                    g.DrawString("0123456789 ABC xyz", f, Brushes.Yellow, r.Left + 14, r.Top + 62);
                    k++;
                }
            }
            int[] ovp = Program.ReadPixels(ov);
            int[] Compose(int[] b0)
            {
                var o = (int[])b0.Clone();
                for (int i = 0; i < o.Length; i++) if ((ovp[i] >> 24) != 0) o[i] = ovp[i] | unchecked((int)0xFF000000);
                return o;
            }
            var pics = new List<Bitmap>();
            for (int i = 0; i < 6; i++) pics.Add(Program.MakeBitmap(Compose(Program.ShiftPixels(bg, w, h, 14 * i, -8 * i)), w, h));
            try
            {
                if (!engine.DebugInterpChain(pics, 0.5, out Bitmap? mid) || mid == null) { res.Failure = "chain failed"; return res; }
                using (mid)
                {
                    int[] M = Program.ReadPixels(mid);
                    int[] truth = Compose(Program.ShiftPixels(bg, w, h, 14 * 4.5, -8 * 4.5));
                    int[] blend = Blend(Program.ReadPixels(pics[4]), Program.ReadPixels(pics[5]), 0.5);
                    var inMask = new bool[w * h]; var ringMask = new bool[w * h];
                    foreach (Rectangle r in rects)
                    {
                        var big = Rectangle.Inflate(r, 16, 16);
                        for (int y = Math.Max(0, big.Top); y < Math.Min(h, big.Bottom); y++)
                            for (int x = Math.Max(0, big.Left); x < Math.Min(w, big.Right); x++)
                                if (r.Contains(x, y)) inMask[y * w + x] = true; else ringMask[y * w + x] = true;
                    }
                    res.HudPsnr = MaskPsnr(M, truth, inMask); res.HudBlend = MaskPsnr(blend, truth, inMask);
                    res.RingPsnr = MaskPsnr(M, truth, ringMask); res.RingBlend = MaskPsnr(blend, truth, ringMask);
                    res.HudSharp = EdgeEnergy(M, w, h, inMask) / Math.Max(1e-6, EdgeEnergy(truth, w, h, inMask));
                    if (savePath != null)
                    {
                        Rectangle crop = Rectangle.Inflate(rects[0], 40, 40);
                        using Bitmap bt = Program.MakeBitmap(truth, w, h);
                        TestProbes.SaveStrip(new[] { pics[5], mid, bt }, new[] { "B", "result", "truth" }, crop, savePath);
                    }
                    res.Ok = true;
                }
            }
            finally { foreach (Bitmap b in pics) b.Dispose(); }
            return res;
        }
        catch (Exception ex) { res.Failure = ex.Message; return res; }
    }

    /// <summary>Real footage: pictures of a saved sequence (distinct pictures only). For each i, the middle of (u[i-1], u[i+1]) after the
    /// history u[i-5..i-1] against the real u[i], on the pixels that stay unchanged over u[i-3..i+1] and have an edge (HUD, text).</summary>
    public static Result RunReal(D3DVideoEngine engine, string seqDir, int maxCases)
    {
        var res = new Result();
        try
        {
            var lines = File.ReadAllLines(Path.Combine(seqDir, "seq.txt")).Where(l => !l.StartsWith('#') && l.Trim().Length > 0).Select(l => l.Split(' ')).ToList();
            var pics = new List<Bitmap>(); var lum = new List<float[]>(); var uniq = new List<int>();
            float[]? last = null;
            for (int i = 0; i < lines.Count; i++)
            {
                int idx = int.Parse(lines[i][0], CultureInfo.InvariantCulture);
                using var fs = File.OpenRead(Path.Combine(seqDir, $"f{idx:000}.png"));
                using var tmp = new Bitmap(fs);
                var bmp = new Bitmap(tmp);
                int[] px = Program.ReadPixels(bmp);
                var l = new float[px.Length];
                for (int k = 0; k < px.Length; k++) l[k] = (Ch(px[k], 16) * 0.299f + Ch(px[k], 8) * 0.587f + Ch(px[k], 0) * 0.114f);
                bool same = last != null && l.SequenceEqual(last);
                pics.Add(bmp); lum.Add(l);
                if (!same) uniq.Add(i);
                last = l;
            }
            int w = pics[0].Width, h = pics[0].Height;
            var psI = new List<double>(); var psB = new List<double>(); var sharp = new List<double>();
            for (int c = 6; c + 1 < uniq.Count && psI.Count < maxCases; c += 2)
            {
                int i = c;
                var chain = new List<Bitmap>();
                for (int k = i - 5; k <= i - 1; k++) chain.Add(pics[uniq[k]]);
                chain.Add(pics[uniq[i + 1]]);
                // mask: unchanged over u[i-3..i+1] (tolerance 4 levels of luma) and an edge in u[i]
                var mask = new bool[w * h];
                int n = 0;
                for (int p = w + 1; p < w * (h - 1) - 1; p++)
                {
                    bool st = true;
                    for (int k = i - 3; k <= i && st; k++) if (Math.Abs(lum[uniq[k + 1]][p] - lum[uniq[k]][p]) > 4) st = false;
                    if (!st) continue;
                    float[] lu = lum[uniq[i]];
                    float g = Math.Abs(lu[p + 1] - lu[p - 1]) + Math.Abs(lu[p + w] - lu[p - w]);
                    if (g > 24) { mask[p] = true; n++; }
                }
                if (n < 300) continue;
                if (!engine.DebugInterpChain(chain, 0.5, out Bitmap? mid) || mid == null) continue;
                using (mid)
                {
                    int[] M = Program.ReadPixels(mid), T = Program.ReadPixels(pics[uniq[i]]);
                    int[] B = Blend(Program.ReadPixels(pics[uniq[i - 1]]), Program.ReadPixels(pics[uniq[i + 1]]), 0.5);
                    psI.Add(MaskPsnr(M, T, mask)); psB.Add(MaskPsnr(B, T, mask));
                    sharp.Add(EdgeEnergy(M, w, h, mask) / Math.Max(1e-6, EdgeEnergy(T, w, h, mask)));
                }
            }
            foreach (Bitmap b in pics) b.Dispose();
            if (psI.Count == 0) { res.Failure = "no case with a static edge region"; return res; }
            psI.Sort(); psB.Sort(); sharp.Sort();
            res.N = psI.Count; res.HudPsnr = psI[psI.Count / 2]; res.HudBlend = psB[psB.Count / 2]; res.HudSharp = sharp[sharp.Count / 2];
            res.Ok = true;
            return res;
        }
        catch (Exception ex) { res.Failure = ex.Message; return res; }
    }
}
