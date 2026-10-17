using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Estimates how fine the picture really is, from the picture alone. A capture card reports the size of the
/// frames it hands over, not the size of what the source rendered: a game drawn at 720p and enlarged to 1080p
/// by the console arrives as a 1920x1080 frame. Content enlarged from H0 lines is reconstructed almost exactly by
/// "shrink to H0, enlarge again", while real detail above H0 is not, so the smallest H0 that reconstructs the
/// picture is its effective resolution. Done separately for the horizontal and the vertical axis.
/// Also detects frames without detail (black or one colour), which no analysis of a still can use.
/// </summary>
internal static class ResEstimate
{
    public static readonly int[] Candidates = { 480, 576, 720, 900, 1080, 1440, 2160 };

    /// <summary>A candidate passes when the normalised residual stays below this. Calibrated by
    /// <see cref="SelfCheck"/> (enlarged 720p/480p/900p with several scalers and noise vs. native).</summary>
    public const double Threshold = 0.04;

    /// <summary>Candidates close to the native size (above 80% of it) must reconstruct almost perfectly: a
    /// real capture has a noise floor of about 0.02 (compression, the console's own scaler), which would otherwise
    /// make a native picture pass as "900p". Far below the native size the looser <see cref="Threshold"/> applies.</summary>
    public const double NearNativeThreshold = 0.01;

    public static double ThresholdFor(int candidate, int native) => candidate > native * 0.8 ? NearNativeThreshold : Threshold;

    /// <summary>Mean |dx|+|dy| of the luma (0..255) below which a frame is treated as black / flat.</summary>
    public const double MinDetail = 1.0;

    public sealed class AxisResult
    {
        public int[] Cands = Array.Empty<int>();     // heights tried, then the native height last
        public double[] Nr = Array.Empty<double>();  // normalised residual per candidate (native = 0)
        public int EstimateHeight;                   // in "lines of a frame of the same height" (e.g. 720)
        public int EstimateLength;                   // in pixels along this axis
    }

    public sealed class FrameResult
    {
        public int W, H;
        public double Detail;
        public bool CardMessage;
        public AxisResult Horizontal = new(), Vertical = new();
    }

    // ------------------------------------------------------------------ luma / detail

    public static float[] LumaOf(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var y = new float[w * h];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int j = 0; j < h; j++)
            {
                Marshal.Copy(bd.Scan0 + j * bd.Stride, row, 0, row.Length);
                for (int i = 0; i < w; i++)
                {
                    int o = i * 4;
                    y[j * w + i] = (0.2126f * row[o + 2] + 0.7152f * row[o + 1] + 0.0722f * row[o]);
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return y;
    }

    public static double MeanGradient(float[] y, int w, int h)
    {
        double sum = 0; long n = 0;
        for (int j = 1; j < h; j += 2)
            for (int i = 1; i < w; i++)
            {
                float v = y[j * w + i];
                sum += Math.Abs(v - y[j * w + i - 1]) + Math.Abs(v - y[(j - 1) * w + i]);
                n++;
            }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>True when the picture has enough detail to say something about its resolution. A card's own
    /// message screen (<see cref="LooksLikeCardMessage"/>) is reported through <paramref name="cardMessage"/> and
    /// does not count: it is not the source's picture.</summary>
    public static bool HasDetail(Bitmap bmp, out bool cardMessage)
    {
        float[] y = LumaOf(bmp, out int w, out int h);
        cardMessage = LooksLikeCardMessage(y, w, h);
        return !cardMessage && MeanGradient(y, w, h) >= MinDetail;
    }

    /// <summary>True when the picture is almost all black with some bright text or shapes: what a capture card
    /// draws itself when it has no usable input ("Input Not Supported" and the like). Such a picture is the card's own
    /// message, rendered at the capture size, and says nothing about the source.</summary>
    public static bool LooksLikeCardMessage(float[] y, int w, int h)
    {
        long n = 0, dark = 0, bright = 0;
        for (int j = 0; j < h; j += 3)
            for (int i = 0; i < w; i += 3)
            {
                float v = y[j * w + i];
                n++;
                if (v < 16) dark++; else if (v > 180) bright++;
            }
        if (n == 0) return false;
        double fd = (double)dark / n, fb = (double)bright / n;
        return fd >= 0.85 && fb >= 0.003 && fb <= 0.15;
    }

    // ------------------------------------------------------------------ resampling along one line

    private static void BoxDown(float[] src, int n, float[] dst, int m)
    {
        double ratio = (double)n / m;
        for (int j = 0; j < m; j++)
        {
            double a = j * ratio, b = (j + 1) * ratio;
            int i0 = (int)Math.Floor(a), i1 = Math.Min(n - 1, (int)Math.Ceiling(b) - 1);
            double s = 0;
            for (int i = i0; i <= i1; i++)
            {
                double lo = Math.Max(a, i), hi = Math.Min(b, i + 1);
                if (hi > lo) s += src[i] * (hi - lo);
            }
            dst[j] = (float)(s / ratio);
        }
    }

    private static void CubicUp(float[] src, int m, float[] dst, int n)
    {
        double r = (double)m / n;
        for (int i = 0; i < n; i++)
        {
            double x = (i + 0.5) * r - 0.5;
            int x0 = (int)Math.Floor(x);
            double t = x - x0;
            float p0 = src[Math.Clamp(x0 - 1, 0, m - 1)], p1 = src[Math.Clamp(x0, 0, m - 1)];
            float p2 = src[Math.Clamp(x0 + 1, 0, m - 1)], p3 = src[Math.Clamp(x0 + 2, 0, m - 1)];
            // Catmull-Rom
            double v = 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);
            dst[i] = (float)v;
        }
    }

    // ------------------------------------------------------------------ per-axis estimate

    /// <summary>Squared error left when the line is rebuilt from m samples: the best m-sample signal (found by
    /// iterative back-projection, so it fits whatever the enlarging filter was) enlarged again with a cubic filter
    /// (or by repeating samples when <paramref name="hold"/>), compared with the line.</summary>
    private static double Residual(float[] line, int n, int m, float[] low, float[] up, float[] resid, float[] back, bool hold)
    {
        if (hold)
        {
            // piecewise constant: cell j holds the pixels i with floor((i + 0.5) * m / n) == j; best value = their mean
            Array.Clear(low, 0, m);
            var cnt = new int[m];
            for (int i = 0; i < n; i++)
            {
                int j = Math.Min(m - 1, (int)((i + 0.5) * m / n));
                low[j] += line[i]; cnt[j]++;
            }
            double sh = 0;
            for (int i = 0; i < n; i++)
            {
                int j = Math.Min(m - 1, (int)((i + 0.5) * m / n));
                double d = line[i] - low[j] / Math.Max(1, cnt[j]);
                sh += d * d;
            }
            return sh;
        }
        BoxDown(line, n, low, m);
        for (int it = 0; it < 6; it++)
        {
            CubicUp(low, m, up, n);
            for (int i = 0; i < n; i++) resid[i] = line[i] - up[i];
            BoxDown(resid, n, back, m);
            for (int j = 0; j < m; j++) low[j] += back[j];
        }
        CubicUp(low, m, up, n);
        double s = 0;
        for (int i = 0; i < n; i++) { double d = line[i] - up[i]; s += d * d; }
        return s;
    }


    private static AxisResult AnalyzeAxis(float[] y, int w, int h, bool horizontal, int nativeHeight)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        var buf = new float[len];
        void Get(int line)
        {
            if (horizontal) Array.Copy(y, line * w, buf, 0, w);
            else for (int i = 0; i < h; i++) buf[i] = y[i * w + line];
        }

        // the lines with the most detail (flat lines say nothing and dilute the ratio)
        var energy = new double[lines];
        for (int l = 0; l < lines; l++)
        {
            Get(l);
            double e = 0;
            for (int i = 1; i < len; i++) { double d = buf[i] - buf[i - 1]; e += d * d; }
            energy[l] = e;
        }
        int[] order = Enumerable.Range(0, lines).OrderByDescending(l => energy[l]).ToArray();
        int take = Math.Max(1, (int)(lines * 0.4));
        int stride = Math.Max(1, take / 300);
        var chosen = new List<int>();
        for (int k = 0; k < take; k += stride) chosen.Add(order[k]);

        int[] cands = Candidates.Where(c => c < nativeHeight).ToArray();
        var err = new double[cands.Length];
        double norm = 0;
        var down = new float[len];
        var up = new float[len];
        var resid = new float[len];
        var back = new float[len];
        foreach (int l in chosen)
        {
            Get(l);
            for (int i = 1; i < len; i++) { double d = buf[i] - buf[i - 1]; norm += d * d; }
            for (int c = 0; c < cands.Length; c++)
            {
                int m = Math.Clamp((int)Math.Round((double)len * cands[c] / nativeHeight), 2, len - 1);
                err[c] += Math.Min(Residual(buf, len, m, down, up, resid, back, false), Residual(buf, len, m, down, up, resid, back, true));
            }
        }
        var res = new AxisResult { Cands = cands.Concat(new[] { nativeHeight }).ToArray() };
        res.Nr = new double[res.Cands.Length];
        for (int c = 0; c < cands.Length; c++) res.Nr[c] = norm > 0 ? err[c] / norm : 0;
        res.EstimateHeight = nativeHeight;
        for (int c = 0; c < cands.Length; c++)
            if (res.Nr[c] < ThresholdFor(cands[c], nativeHeight)) { res.EstimateHeight = cands[c]; break; }
        res.EstimateLength = (int)Math.Round((double)len * res.EstimateHeight / nativeHeight);
        return res;
    }

    public static FrameResult Analyze(float[] y, int w, int h)
    {
        var r = new FrameResult { W = w, H = h, Detail = MeanGradient(y, w, h), CardMessage = LooksLikeCardMessage(y, w, h) };
        r.Horizontal = AnalyzeAxis(y, w, h, true, h);
        r.Vertical = AnalyzeAxis(y, w, h, false, h);
        return r;
    }

    public static int Median(IEnumerable<int> v)
    {
        int[] a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    /// <summary>Name of a height such as 720p; heights between the usual ones are written as-is.</summary>
    public static string Name(int height) => height >= 2160 ? "4K" : height + "p";

    // ================================================================ calibration (hidden CLI: --res-estimate-check)

    private static float[] Scene(int w, int h, int seed)
    {
        double s = h / 1080.0;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(235, 235, 240));
            var rnd = new Random(seed);
            string[] words = { "YuCap", "Resolution", "capture", "Wii U", "Menu", "Settings", "0123456789", "Zelda", "Mario Kart", "Quick brown fox", "jumps over", "lazy dog", "Option", "Start" };
            // text lines of several sizes (sizes scale with the picture, line widths stay one pixel as in a native render)
            int ty = (int)(20 * s);
            foreach (float pt in new[] { 9f, 12f, 16f, 22f, 30f })
            {
                using var f = new Font("Arial", (float)(pt * s * 1.0), FontStyle.Regular, GraphicsUnit.Pixel);
                for (int line = 0; line < 3; line++)
                {
                    string txt = string.Join(" ", Enumerable.Range(0, 5).Select(_ => words[rnd.Next(words.Length)]));
                    g.DrawString(txt, f, Brushes.Black, (float)(30 * s), ty);
                    ty += (int)(pt * s * 1.5) + 2;
                }
            }
            // thin lines at many angles
            using (var pen = new Pen(Color.FromArgb(20, 20, 60), 1f))
                for (int a = 0; a < 40; a++)
                {
                    double ang = a * Math.PI / 40;
                    float cx = (float)(w * 0.72), cy = (float)(h * 0.3), rad = (float)(h * 0.22);
                    g.DrawLine(pen, cx - (float)Math.Cos(ang) * rad, cy - (float)Math.Sin(ang) * rad, cx + (float)Math.Cos(ang) * rad, cy + (float)Math.Sin(ang) * rad);
                }
            // fine stripes and a checkerboard
            for (int i = 0; i < 80; i++)
                g.FillRectangle(i % 2 == 0 ? Brushes.Black : Brushes.White, (int)(w * 0.55) + i * 2, (int)(h * 0.55), 1, (int)(h * 0.15));
            int cell = Math.Max(1, (int)Math.Round(2 * s * 0 + 2));
            for (int cy2 = 0; cy2 < 40; cy2++)
                for (int cx2 = 0; cx2 < 80; cx2++)
                    if ((cx2 + cy2) % 2 == 0) g.FillRectangle(Brushes.Black, (int)(w * 0.55) + cx2 * cell, (int)(h * 0.75) + cy2 * cell, cell, cell);
            // circles / shapes with one-pixel outlines
            using (var pen = new Pen(Color.DarkRed, 1f))
                for (int i = 1; i <= 20; i++) g.DrawEllipse(pen, (int)(w * 0.15) + i * 3, (int)(h * 0.65) + i * 2, (int)(h * 0.2) - i * 6, (int)(h * 0.2) - i * 4);
        }
        float[] y = LumaOf(bmp, out _, out _);
        // a fine texture patch at native grain
        var rn = new Random(seed + 7);
        int px = (int)(w * 0.05), py = (int)(h * 0.6), pw = (int)(w * 0.12), ph = (int)(h * 0.25);
        var tex = new float[pw * ph];
        for (int i = 0; i < tex.Length; i++) tex[i] = (float)(rn.NextDouble() * 255);
        for (int j = 1; j < ph - 1; j++)
            for (int i = 1; i < pw - 1; i++)
                y[(py + j) * w + px + i] = 0.5f * tex[j * pw + i] + 0.125f * (tex[j * pw + i - 1] + tex[j * pw + i + 1] + tex[(j - 1) * pw + i] + tex[(j + 1) * pw + i]);
        return y;
    }

    private enum Scaler { Nearest, Bilinear, CatmullRom, Bicubic075, Lanczos3 }

    private static double Kernel(Scaler k, double x)
    {
        x = Math.Abs(x);
        switch (k)
        {
            case Scaler.Bilinear: return x < 1 ? 1 - x : 0;
            case Scaler.CatmullRom:
            case Scaler.Bicubic075:
                {
                    double a = k == Scaler.CatmullRom ? -0.5 : -0.75;
                    if (x < 1) return (a + 2) * x * x * x - (a + 3) * x * x + 1;
                    if (x < 2) return a * x * x * x - 5 * a * x * x + 8 * a * x - 4 * a;
                    return 0;
                }
            case Scaler.Lanczos3:
                if (x < 1e-9) return 1;
                if (x >= 3) return 0;
                return 3 * Math.Sin(Math.PI * x) * Math.Sin(Math.PI * x / 3) / (Math.PI * Math.PI * x * x);
            default: return 0;
        }
    }

    private static float[] Enlarge(float[] src, int sw, int sh, int dw, int dh, Scaler k)
    {
        int support = k == Scaler.Lanczos3 ? 3 : k == Scaler.Bilinear ? 1 : 2;
        var tmp = new float[dw * sh];
        for (int x = 0; x < dw; x++)
        {
            double cx = (x + 0.5) * sw / dw - 0.5;
            int c0 = (int)Math.Floor(cx);
            var idx = new List<(int I, double W)>();
            if (k == Scaler.Nearest) idx.Add((Math.Clamp((int)Math.Floor((x + 0.5) * sw / dw), 0, sw - 1), 1));
            else
            {
                double sum = 0;
                for (int i = c0 - support + 1; i <= c0 + support; i++)
                {
                    double wgt = Kernel(k, cx - i);
                    if (wgt == 0) continue;
                    idx.Add((Math.Clamp(i, 0, sw - 1), wgt)); sum += wgt;
                }
                for (int q = 0; q < idx.Count; q++) idx[q] = (idx[q].I, idx[q].W / sum);
            }
            for (int j = 0; j < sh; j++)
            {
                double v = 0;
                foreach (var (i, wgt) in idx) v += src[j * sw + i] * wgt;
                tmp[j * dw + x] = (float)v;
            }
        }
        var dst = new float[dw * dh];
        for (int yy = 0; yy < dh; yy++)
        {
            double cy = (yy + 0.5) * sh / dh - 0.5;
            int c0 = (int)Math.Floor(cy);
            var idx = new List<(int J, double W)>();
            if (k == Scaler.Nearest) idx.Add((Math.Clamp((int)Math.Floor((yy + 0.5) * sh / dh), 0, sh - 1), 1));
            else
            {
                double sum = 0;
                for (int j = c0 - support + 1; j <= c0 + support; j++)
                {
                    double wgt = Kernel(k, cy - j);
                    if (wgt == 0) continue;
                    idx.Add((Math.Clamp(j, 0, sh - 1), wgt)); sum += wgt;
                }
                for (int q = 0; q < idx.Count; q++) idx[q] = (idx[q].J, idx[q].W / sum);
            }
            for (int x = 0; x < dw; x++)
            {
                double v = 0;
                foreach (var (j, wgt) in idx) v += tmp[j * dw + x] * wgt;
                dst[yy * dw + x] = (float)Math.Clamp(v, 0, 255);
            }
        }
        return dst;
    }

    private static void AddNoise(float[] y, double sigma, int seed)
    {
        if (sigma <= 0) return;
        var r = new Random(seed);
        for (int i = 0; i < y.Length; i++)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            double n = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sigma;
            y[i] = (float)Math.Clamp(Math.Round(y[i] + n), 0, 255);
        }
    }

    /// <summary>Runs the estimator on synthetic pictures whose true resolution is known and returns a text report.
    /// "ok" is appended to each line whose estimate equals the truth (both axes).</summary>
    public static string SelfCheck()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Threshold={Threshold} (near native, >80%: {NearNativeThreshold}), MinDetail={MinDetail}");
        // (captured height, true height, scaler)
        var cases = new List<(int Cap, int True, Scaler S)>();
        foreach (Scaler s in new[] { Scaler.Bilinear, Scaler.CatmullRom, Scaler.Bicubic075, Scaler.Lanczos3, Scaler.Nearest })
        {
            cases.Add((1080, 720, s));
            cases.Add((1080, 480, s));
        }
        cases.Add((1080, 900, Scaler.Bicubic075));
        cases.Add((1080, 576, Scaler.Bilinear));
        cases.Add((1080, 1080, Scaler.Bilinear));
        cases.Add((1440, 1440, Scaler.Bilinear));
        cases.Add((1440, 1080, Scaler.Bicubic075));
        cases.Add((1440, 720, Scaler.Lanczos3));
        cases.Add((2160, 2160, Scaler.Bilinear));
        cases.Add((2160, 1080, Scaler.Bilinear));
        cases.Add((2160, 1080, Scaler.Lanczos3));
        cases.Add((2160, 720, Scaler.Bicubic075));
        int total = 0, good = 0;
        foreach (double sigma in new[] { 0.0, 2.0 })
            foreach (var (cap, tr, sc) in cases)
            {
                int cw = cap * 16 / 9, tw = tr * 16 / 9;
                float[] src = Scene(tw, tr, 11);
                float[] y = tr == cap ? src : Enlarge(src, tw, tr, cw, cap, sc);
                AddNoise(y, sigma, 5);
                FrameResult r = Analyze(y, cw, cap);
                bool ok = r.Horizontal.EstimateHeight == tr && r.Vertical.EstimateHeight == tr;
                total++; if (ok) good++;
                sb.AppendLine($"noise {sigma:0.0} cap {cw}x{cap} true {tr}p via {sc,-10} -> H {r.Horizontal.EstimateHeight}p V {r.Vertical.EstimateHeight}p  {(ok ? "ok" : "MISS")}  detail {r.Detail:0.0}");
                sb.AppendLine("      nrH " + string.Join(" ", r.Horizontal.Cands.Zip(r.Horizontal.Nr, (c, n) => $"{c}:{n:0.000}")));
                sb.AppendLine("      nrV " + string.Join(" ", r.Vertical.Cands.Zip(r.Vertical.Nr, (c, n) => $"{c}:{n:0.000}")));
            }
        // a flat / black frame must be refused
        var flat = new float[1920 * 1080];
        sb.AppendLine($"black frame detail {MeanGradient(flat, 1920, 1080):0.00} -> {(MeanGradient(flat, 1920, 1080) < MinDetail ? "refused (ok)" : "USED (MISS)")}");
        Array.Fill(flat, 128f);
        sb.AppendLine($"flat frame detail {MeanGradient(flat, 1920, 1080):0.00} -> {(MeanGradient(flat, 1920, 1080) < MinDetail ? "refused (ok)" : "USED (MISS)")}");
        // a card's own "input not supported" screen (white text on black) must be recognised; a bright menu must not
        {
            using var bmp = new Bitmap(1920, 1080, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using var f = new Font("Arial", 60f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString("Input Not Supported", f, Brushes.White, 100, 100);
                g.DrawString("Check your input resolution", f, Brushes.White, 100, 200);
            }
            float[] ty = LumaOf(bmp, out int tw2, out int th2);
            bool msg = LooksLikeCardMessage(ty, tw2, th2);
            bool menu = LooksLikeCardMessage(Scene(1920, 1080, 11), 1920, 1080);
            sb.AppendLine($"card message screen -> {(msg ? "recognised (ok)" : "NOT recognised (MISS)")}; bright detailed scene -> {(menu ? "taken for a card message (MISS)" : "not flagged (ok)")}");
            total += 2; if (msg) good++; if (!menu) good++;
        }
        sb.AppendLine($"RESULT {good}/{total}");
        return sb.ToString();
    }
}
