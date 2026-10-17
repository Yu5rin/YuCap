using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace YuCap;

/// <summary>
/// Measurements for 「元の細かさに合わせて拡大」 (prescale to the picture's real detail, then FSR). Shared by the test mode and by
/// the headless CLI check (<c>--native-res-check</c>), both of which pass in a function that renders a given picture through the
/// real GPU pipeline. Ground truth is a detailed 4K picture; the "game" is that picture shrunk to 1280x720, the "console" enlarges
/// it to 1920x1080, and the pipeline then goes to 3840x2160 four ways: standard from 1080p, FSR from 1080p (today), prescale
/// to 720p + FSR (new), FSR directly from the true 720p (the best any such pipeline could do). Each is compared with the truth.
/// </summary>
internal static class NativeResTest
{
    /// <summary>Render <paramref name="source"/> to <c>Size</c> with the scaler, a prescale height (0 = none) and a prescale filter (-1 = default).</summary>
    internal delegate Bitmap? RenderFn(Bitmap source, Size output, ScalingQuality quality, int nativeH, int filter);

    // the step-edge panel in the truth picture (4K pixels): a black square on white, edges away from the 720p grid
    private const int PanelX0 = 40, PanelY0 = 1380, PanelX1 = 1240, PanelY1 = 2120;
    private const int SqX0 = 441, SqX1 = 841, SqY0 = 1581, SqY1 = 1981;

    // ------------------------------------------------------------------ pictures

    public static Bitmap MakeTruth(int w = 3840, int h = 2160)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(238, 238, 244), Color.FromArgb(206, 214, 226), 90f))
                g.FillRectangle(bg, 0, 0, w, h);
            var rnd = new Random(21);
            string[] words = { "YuCap", "Resolution", "capture", "Wii U", "Menu", "Settings", "0123456789", "Zelda", "Mario Kart", "Quick brown fox", "jumps over", "lazy dog", "Option", "Start" };
            int ty = 40;
            foreach (float px in new[] { 24f, 36f, 48f, 66f, 90f })
            {
                using var f = new Font("Arial", px, FontStyle.Regular, GraphicsUnit.Pixel);
                for (int line = 0; line < 2; line++)
                {
                    string txt = string.Join(" ", Enumerable.Range(0, 4).Select(_ => words[rnd.Next(words.Length)]));
                    g.DrawString(txt, f, Brushes.Black, 60, ty);
                    ty += (int)(px * 1.35);
                }
            }
            // thin lines (1 px of a 720p picture) at many angles
            using (var pen = new Pen(Color.FromArgb(20, 20, 70), 3f))
                for (int a = 0; a < 48; a++)
                {
                    double ang = a * Math.PI / 48;
                    float cx = 3000, cy = 430, rad = 380;
                    g.DrawLine(pen, cx - (float)Math.Cos(ang) * rad, cy - (float)Math.Sin(ang) * rad, cx + (float)Math.Cos(ang) * rad, cy + (float)Math.Sin(ang) * rad);
                }
            g.SmoothingMode = SmoothingMode.None;
            // stripes (3 px and 6 px) and a checkerboard with 3 px cells
            for (int i = 0; i < 100; i++)
                g.FillRectangle(i % 2 == 0 ? Brushes.Black : Brushes.White, 1400 + i * 3, 920, 3, 260);
            for (int i = 0; i < 50; i++)
                g.FillRectangle(i % 2 == 0 ? Brushes.Black : Brushes.White, 1400 + i * 6, 1220, 6, 120);
            for (int cy = 0; cy < 40; cy++)
                for (int cx = 0; cx < 80; cx++)
                    if ((cx + cy) % 2 == 0) g.FillRectangle(Brushes.Black, 1800 + cx * 3, 920 + cy * 3, 3, 3);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // circle outlines
            using (var pen = new Pen(Color.DarkRed, 3f))
                for (int i = 1; i <= 20; i++) g.DrawEllipse(pen, 2300 + i * 9, 900 + i * 6, 520 - i * 18, 420 - i * 12);
            // smooth colour: gradient fills and a soft shape
            using (var gb = new LinearGradientBrush(new Rectangle(3000, 900, 700, 360), Color.FromArgb(250, 190, 40), Color.FromArgb(30, 80, 200), 35f))
                g.FillRectangle(gb, 3000, 900, 700, 360);
            using (var gb = new LinearGradientBrush(new Rectangle(3060, 1300, 600, 400), Color.White, Color.FromArgb(40, 160, 90), 100f))
                g.FillEllipse(gb, 3060, 1300, 600, 400);
            // the step-edge panel: white, with a black square whose edges sit at 441/841 and 1581/1981
            g.SmoothingMode = SmoothingMode.None;
            using (var lightBr = new SolidBrush(Color.FromArgb(225, 225, 225)))
                g.FillRectangle(lightBr, PanelX0, PanelY0, PanelX1 - PanelX0, PanelY1 - PanelY0);
            using (var darkBr = new SolidBrush(Color.FromArgb(30, 30, 30)))
                g.FillRectangle(darkBr, SqX0, SqY0, SqX1 - SqX0, SqY1 - SqY0);
        }
        // a photo-like patch: three octaves of smooth colour noise plus grain
        PhotoPatch(bmp, 1400, 1480, 1500, 620);
        return bmp;
    }

    private static void PhotoPatch(Bitmap bmp, int x0, int y0, int pw, int ph)
    {
        var rnd = new Random(77);
        float[][] Lattice(int cell, int ch)
        {
            int nx = pw / cell + 3, ny = ph / cell + 3;
            var l = new float[nx * ny];
            for (int i = 0; i < l.Length; i++) l[i] = (float)rnd.NextDouble();
            return new[] { l, new float[] { nx, ny, cell } };
        }
        var oct = new[] { 96, 24, 6 };
        var amp = new[] { 1.0f, 0.5f, 0.25f };
        var chans = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            chans[c] = new float[pw * ph];
            for (int o = 0; o < 3; o++)
            {
                float[][] L = Lattice(oct[o], c);
                float[] vals = L[0]; int nx = (int)L[1][0], cell = oct[o];
                for (int y = 0; y < ph; y++)
                    for (int x = 0; x < pw; x++)
                    {
                        float fx = (float)x / cell, fy = (float)y / cell;
                        int ix = (int)fx, iy = (int)fy;
                        float tx = fx - ix, ty = fy - iy;
                        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                        float v00 = vals[iy * nx + ix], v10 = vals[iy * nx + ix + 1], v01 = vals[(iy + 1) * nx + ix], v11 = vals[(iy + 1) * nx + ix + 1];
                        chans[c][y * pw + x] += amp[o] * ((v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty);
                    }
            }
        }
        BitmapData bd = bmp.LockBits(new Rectangle(x0, y0, pw, ph), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[pw * 4];
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
                    double grain = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * 3.0;
                    for (int c = 0; c < 3; c++)
                        row[x * 4 + (2 - c)] = (byte)Math.Clamp((int)Math.Round(chans[c][y * pw + x] / 1.75f * 255f + grain), 0, 255);
                    row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(bd); }
    }

    // planar float RGB <-> bitmap
    private static float[][] ToPlanes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var p = new[] { new float[w * h], new float[w * h], new float[w * h] };
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, buf, y * w * 4, w * 4);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    p[0][y * w + x] = buf[o + 2]; p[1][y * w + x] = buf[o + 1]; p[2][y * w + x] = buf[o];
                }
            });
        }
        finally { bmp.UnlockBits(bd); }
        return p;
    }

    private static Bitmap FromPlanes(float[][] p, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[w * h * 4];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    buf[o + 2] = (byte)Math.Clamp((int)Math.Round(p[0][y * w + x]), 0, 255);
                    buf[o + 1] = (byte)Math.Clamp((int)Math.Round(p[1][y * w + x]), 0, 255);
                    buf[o] = (byte)Math.Clamp((int)Math.Round(p[2][y * w + x]), 0, 255);
                    buf[o + 3] = 255;
                }
            });
            for (int y = 0; y < h; y++) Marshal.Copy(buf, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // ------------------------------------------------------------------ CPU resampling (the simulated game / console)

    internal enum Kernel { Box, Bilinear, CatmullRom, Nearest }

    private static double K(Kernel k, double x)
    {
        x = Math.Abs(x);
        if (k == Kernel.Bilinear) return x < 1 ? 1 - x : 0;
        if (k == Kernel.CatmullRom)
        {
            if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
            if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
            return 0;
        }
        return 0;
    }

    private static (int start, double[] w)[] Weights(int sn, int dn, Kernel k)
    {
        var res = new (int, double[])[dn];
        double ratio = (double)sn / dn;
        for (int d = 0; d < dn; d++)
        {
            if (k == Kernel.Nearest)
            {
                res[d] = (Math.Clamp((int)Math.Floor((d + 0.5) * ratio), 0, sn - 1), new[] { 1.0 });
            }
            else if (k == Kernel.Box)
            {
                double a = d * ratio, b = (d + 1) * ratio;
                int i0 = (int)Math.Floor(a), i1 = Math.Min(sn - 1, (int)Math.Ceiling(b) - 1);
                var w = new double[i1 - i0 + 1];
                for (int i = i0; i <= i1; i++) w[i - i0] = Math.Max(0, Math.Min(b, i + 1) - Math.Max(a, i)) / ratio;
                res[d] = (i0, w);
            }
            else
            {
                double c = (d + 0.5) * ratio - 0.5;
                int sup = k == Kernel.Bilinear ? 1 : 2;
                int i0 = (int)Math.Floor(c) - sup + 1;
                var w = new double[sup * 2];
                double sum = 0;
                for (int j = 0; j < w.Length; j++) { w[j] = K(k, c - (i0 + j)); sum += w[j]; }
                for (int j = 0; j < w.Length; j++) w[j] /= sum;
                res[d] = (i0, w);   // indices outside the picture are clamped when applied
            }
        }
        return res;
    }

    private static float[] Resample(float[] src, int sw, int sh, int dw, int dh, Kernel k)
    {
        var wx = Weights(sw, dw, k);
        var wy = Weights(sh, dh, k);
        var tmp = new float[dw * sh];
        Parallel.For(0, sh, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                var (s, w) = wx[x];
                double v = 0;
                for (int j = 0; j < w.Length; j++) v += src[y * sw + Math.Clamp(s + j, 0, sw - 1)] * w[j];
                tmp[y * dw + x] = (float)v;
            }
        });
        var dst = new float[dw * dh];
        Parallel.For(0, dh, y =>
        {
            var (s, w) = wy[y];
            for (int x = 0; x < dw; x++)
            {
                double v = 0;
                for (int j = 0; j < w.Length; j++) v += tmp[Math.Clamp(s + j, 0, sh - 1) * dw + x] * w[j];
                dst[y * dw + x] = (float)Math.Clamp(v, 0, 255);
            }
        });
        return dst;
    }

    public static Bitmap Resample(Bitmap src, int dw, int dh, Kernel k)
    {
        float[][] p = ToPlanes(src);
        var q = new float[3][];
        for (int c = 0; c < 3; c++) q[c] = Resample(p[c], src.Width, src.Height, dw, dh, k);
        return FromPlanes(q, dw, dh);
    }

    // ------------------------------------------------------------------ metrics (luma, Rec.709, 0..255)

    public static float[] Luma(Bitmap b) => ResEstimate.LumaOf(b, out _, out _);

    public static double Psnr(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; s += d * d; }
        double mse = s / a.Length;
        return mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Mean SSIM over 8x8 windows (stride 4) of the luma.</summary>
    public static double Ssim(float[] a, float[] b, int w, int h)
    {
        const double C1 = 6.5025, C2 = 58.5225;
        int nx = (w - 8) / 4 + 1, ny = (h - 8) / 4 + 1;
        var sums = new double[ny];
        Parallel.For(0, ny, wy =>
        {
            int y0 = wy * 4;
            double acc = 0;
            for (int wx = 0; wx < nx; wx++)
            {
                int x0 = wx * 4;
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++)
                {
                    int o = (y0 + j) * w + x0;
                    for (int i = 0; i < 8; i++)
                    {
                        double va = a[o + i], vb = b[o + i];
                        sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb;
                    }
                }
                double ma = sa / 64, mb = sb / 64;
                double va2 = saa / 64 - ma * ma, vb2 = sbb / 64 - mb * mb, cov = sab / 64 - ma * mb;
                acc += ((2 * ma * mb + C1) * (2 * cov + C2)) / ((ma * ma + mb * mb + C1) * (va2 + vb2 + C2));
            }
            sums[wy] = acc;
        });
        return sums.Sum() / ((double)nx * ny);
    }

    public sealed class EdgeProfile { public double Width, Overshoot; }

    /// <summary>10%-90% width (pixels) and overshoot (% of the step height) of the black square's four edges in a 4K picture
    /// of the truth (averaged along each edge, so noise and anti-aliasing do not matter).</summary>
    public static EdgeProfile MeasureStep(float[] y, int w, int h)
    {
        if (w < PanelX1 || h < PanelY1) return new EdgeProfile { Width = double.NaN, Overshoot = double.NaN };
        double wsum = 0, osum = 0; int n = 0;
        // (horizontal profile at a vertical edge) x edge, rows 1700..1860 ; (vertical profile at a horizontal edge) y edge, columns 540..740
        foreach (var (edge, vertical, whiteFirst) in new[] { (SqX0, true, true), (SqX1, true, false), (SqY0, false, true), (SqY1, false, false) })
        {
            const int R = 24;
            var prof = new double[2 * R];
            for (int k = 0; k < 2 * R; k++)
            {
                double s = 0; int c = 0;
                if (vertical) for (int yy = 1700; yy < 1860; yy++) { s += y[yy * w + edge - R + k]; c++; }
                else for (int xx = 540; xx < 740; xx++) { s += y[(edge - R + k) * w + xx]; c++; }
                prof[k] = s / c;
            }
            if (!whiteFirst) Array.Reverse(prof);    // now always white (left) to black (right)
            double hi = Median(prof.Take(6)), lo = Median(prof.Skip(2 * R - 6));
            double step = hi - lo;
            if (step < 50) continue;
            double over = 0;
            foreach (double v in prof) over = Math.Max(over, Math.Max(v - hi, lo - v));
            // 90% and 10% crossings (white=1 .. black=0)
            double Cross(double level)
            {
                for (int k = 0; k < prof.Length - 1; k++)
                {
                    double a = (prof[k] - lo) / step, b = (prof[k + 1] - lo) / step;
                    if (a >= level && b < level) return k + (a - level) / (a - b);
                }
                return double.NaN;
            }
            double x90 = Cross(0.9), x10 = Cross(0.1);
            if (double.IsNaN(x90) || double.IsNaN(x10)) continue;
            wsum += x10 - x90; osum += 100.0 * over / step; n++;
        }
        return n == 0 ? new EdgeProfile { Width = double.NaN, Overshoot = double.NaN } : new EdgeProfile { Width = wsum / n, Overshoot = osum / n };
    }

    private static double Median(IEnumerable<double> v)
    {
        double[] a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    /// <summary>Ringing: where the reference picture has a strong edge nearby (its 9x9 range is at least 40 levels), the
    /// share of pixels of <paramref name="cand"/> that leave that range by more than 4 levels, and the mean excess of those.
    /// The reference is the 4K truth in the synthetic experiment (anything outside the truth's own range is a halo), and the plain
    /// bilinear enlargement for a live picture (which cannot overshoot, but is softer than the truth, so a sharper result also
    /// scores higher there - compare the two live numbers with that in mind).</summary>
    public static (double Percent, double MeanExcess) Ringing(float[] cand, float[] plain, int w, int h)
    {
        const int R = 4;
        var mn = new float[w * h]; var mx = new float[w * h];
        var tmin = new float[w * h]; var tmax = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = Math.Max(0, x - R); i <= Math.Min(w - 1, x + R); i++) { float v = plain[y * w + i]; if (v < lo) lo = v; if (v > hi) hi = v; }
                tmin[y * w + x] = lo; tmax[y * w + x] = hi;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int j = Math.Max(0, y - R); j <= Math.Min(h - 1, y + R); j++) { float a = tmin[j * w + x], b = tmax[j * w + x]; if (a < lo) lo = a; if (b > hi) hi = b; }
                mn[y * w + x] = lo; mx[y * w + x] = hi;
            }
        });
        long region = 0, bad = 0; double excess = 0;
        object gate = new();
        Parallel.For(0, h, () => (0L, 0L, 0.0), (y, _, acc) =>
        {
            long rg = acc.Item1, bd = acc.Item2; double ex = acc.Item3;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (mx[i] - mn[i] < 40) continue;
                rg++;
                double over = Math.Max(cand[i] - mx[i] - 4, mn[i] - 4 - cand[i]);
                if (over > 0) { bd++; ex += over; }
            }
            return (rg, bd, ex);
        }, acc => { lock (gate) { region += acc.Item1; bad += acc.Item2; excess += acc.Item3; } });
        return (region == 0 ? 0 : 100.0 * bad / region, bad == 0 ? 0 : excess / bad);
    }

    // ------------------------------------------------------------------ the synthetic experiment

    internal sealed class Row
    {
        public string Name = "";
        public double Psnr, Ssim, GradRatio, EdgeWidth, StepOvershoot, RingPct, RingExcess;
    }

    internal sealed class Setup
    {
        public Bitmap Truth = null!, Game720 = null!, Console1080 = null!;
        public float[] TruthLuma = null!;
        public float[] PlainLuma = null!;       // standard (bilinear) from 1080p, the ringing reference
        public Bitmap?[] Renders = new Bitmap?[4];   // a standard, b FSR from 1080, c prescale+FSR, d FSR from true 720
        public Row[] Rows = new Row[4];
        public void Dispose()
        {
            Game720.Dispose(); Console1080.Dispose();   // the truth belongs to the caller
            foreach (Bitmap? b in Renders) b?.Dispose();
        }
    }

    /// <summary>One console type (<paramref name="upscaler"/>), four renderings, metrics for each. The caller disposes the result.</summary>
    internal static Setup RunOne(RenderFn render, Bitmap truth, Kernel upscaler, int filter = -1)
        => RunOne(render, null, truth, upscaler, filter);

    /// <param name="console">A ready-made console picture (a copy is kept), or null to make it with <paramref name="upscaler"/>.</param>
    internal static Setup RunOne(RenderFn render, Bitmap? console, Bitmap truth, Kernel upscaler, int filter = -1)
    {
        var s = new Setup { Truth = truth };
        s.Game720 = Resample(truth, 1280, 720, Kernel.Box);
        s.Console1080 = console != null ? (Bitmap)console.Clone() : Resample(s.Game720, 1920, 1080, upscaler);
        var out4k = new Size(3840, 2160);
        s.Renders[0] = render(s.Console1080, out4k, ScalingQuality.Standard, 0, filter);
        s.Renders[1] = render(s.Console1080, out4k, ScalingQuality.Fsr, 0, filter);
        s.Renders[2] = render(s.Console1080, out4k, ScalingQuality.Fsr, 720, filter);
        s.Renders[3] = render(s.Game720, out4k, ScalingQuality.Fsr, 0, filter);
        s.TruthLuma = Luma(truth);
        if (s.Renders.Any(r => r == null)) return s;
        s.PlainLuma = Luma(s.Renders[0]!);
        double tg = ResEstimate.MeanGradient(s.TruthLuma, 3840, 2160);
        string[] names = { "標準（1080p から）", "FSR（1080p から・今まで）", "720p に縮めてから FSR（新）", "FSR（本物の 720p から・理想）" };
        for (int i = 0; i < 4; i++)
        {
            float[] l = i == 0 ? s.PlainLuma : Luma(s.Renders[i]!);
            EdgeProfile ep = MeasureStep(l, 3840, 2160);
            (double rp, double rx) = Ringing(l, s.TruthLuma, 3840, 2160);
            s.Rows[i] = new Row
            {
                Name = names[i], Psnr = Psnr(l, s.TruthLuma), Ssim = Ssim(l, s.TruthLuma, 3840, 2160),
                GradRatio = ResEstimate.MeanGradient(l, 3840, 2160) / tg, EdgeWidth = ep.Width, StepOvershoot = ep.Overshoot,
                RingPct = rp, RingExcess = rx,
            };
        }
        return s;
    }

    public static string RowText(Row r) =>
        $"PSNR {r.Psnr:0.00} dB / SSIM {r.Ssim:0.0000} / 輪郭の強さ {r.GradRatio:0.000}×（正解=1） / 輪郭の幅 {r.EdgeWidth:0.00}px / 行き過ぎ {r.StepOvershoot:0.0}% / 輪の出た画素 {r.RingPct:0.00}%";

    /// <summary>Whether (c) beats (b): better SSIM, edge strength not further from the truth's, and no more ringing.</summary>
    public static bool Better(Setup s, out string why)
    {
        Row b = s.Rows[1], c = s.Rows[2];
        bool ssim = c.Ssim > b.Ssim;
        bool sharp = Math.Abs(c.GradRatio - 1) <= Math.Abs(b.GradRatio - 1) || c.EdgeWidth < b.EdgeWidth;
        bool ring = c.RingPct <= b.RingPct + 1.0 && (double.IsNaN(c.StepOvershoot) || c.StepOvershoot <= b.StepOvershoot + 1.5);
        why = $"SSIM {(ssim ? "良" : "悪")}（{b.Ssim:0.0000}→{c.Ssim:0.0000}） / 輪郭 {(sharp ? "良" : "悪")}（強さ {b.GradRatio:0.000}→{c.GradRatio:0.000}、幅 {b.EdgeWidth:0.00}→{c.EdgeWidth:0.00}px） / 輪 {(ring ? "同等以下" : "増えた")}（{b.RingPct:0.00}%→{c.RingPct:0.00}%、行き過ぎ {b.StepOvershoot:0.0}%→{c.StepOvershoot:0.0}%）";
        return ssim && sharp && ring;
    }

    /// <summary>Strip of crops (a standard, b FSR, c prescale+FSR, d true 720p, truth) at <paramref name="tile"/>.</summary>
    public static void SaveCrops(Setup s, Rectangle tile, string path)
    {
        var panels = new List<Bitmap>(); var labels = new List<string>();
        panels.Add(s.Renders[1]!); labels.Add("b FSR(1080p)");
        panels.Add(s.Renders[2]!); labels.Add("c 720p+FSR");
        panels.Add(s.Renders[3]!); labels.Add("d FSR(true 720p)");
        panels.Add(s.Truth); labels.Add("truth 4K");
        TestProbes.SaveStrip(panels.ToArray(), labels.ToArray(), tile, path);
    }

    /// <summary>A text-heavy tile and the most-edged tile of the truth.</summary>
    public static (Rectangle Text, Rectangle Edges) Tiles(Bitmap truth)
    {
        var text = new Rectangle(60, 330, 480, 270);
        Rectangle? best = TestProbes.BestTile(TestProbes.MeasureEdges(truth), truth.Size);
        return (text, best ?? new Rectangle(1400, 900, 480, 270));
    }

    // ------------------------------------------------------------------ live picture

    internal sealed class LiveResult
    {
        public double GradStd, GradFsr, GradPre, RingFsr, RingPre, ExcessFsr, ExcessPre;
        public Bitmap? Std, Fsr, Pre;
        public void Dispose() { Std?.Dispose(); Fsr?.Dispose(); Pre?.Dispose(); }
    }

    internal static LiveResult RunLive(RenderFn render, Bitmap source, Size output, int nativeH)
    {
        var r = new LiveResult
        {
            Std = render(source, output, ScalingQuality.Standard, 0, -1),
            Fsr = render(source, output, ScalingQuality.Fsr, 0, -1),
            Pre = render(source, output, ScalingQuality.Fsr, nativeH, -1),
        };
        if (r.Std == null || r.Fsr == null || r.Pre == null) return r;
        float[] ls = Luma(r.Std), lf = Luma(r.Fsr), lp = Luma(r.Pre);
        int w = output.Width, h = output.Height;
        r.GradStd = ResEstimate.MeanGradient(ls, w, h);
        r.GradFsr = ResEstimate.MeanGradient(lf, w, h);
        r.GradPre = ResEstimate.MeanGradient(lp, w, h);
        (r.RingFsr, r.ExcessFsr) = Ringing(lf, ls, w, h);
        (r.RingPre, r.ExcessPre) = Ringing(lp, ls, w, h);
        return r;
    }

    // ------------------------------------------------------------------ headless CLI

    /// <summary>Hidden CLI <c>--native-res-check</c>: no capture card, only the GPU. Compares the prescale filters on the synthetic
    /// pipeline (against the true 720p for the prescale pass alone, and against the 4K truth for the whole chain) and writes a text
    /// report plus crop images to <paramref name="outDir"/>.</summary>
    public static string HeadlessCheck(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        D3DRenderer? r = null;
        try
        {
            r = new D3DRenderer();
            r.CreateDevice();
            r.CreateShaders();
            if (!r.PrescaleAvailable) return "prescale shader unavailable\n";
            sb.AppendLine("adapter: " + r.AdapterName);
            RenderFn render = (src, o, q, nh, f) => r.RenderFromBitmap(src, o, q, 0.5f, nh, f);
            string[] fnames = { "box (area)", "tent", "Catmull-Rom", "Lanczos3", "back-projection" };
            using Bitmap truth = MakeTruth();
            truth.Save(Path.Combine(outDir, "truth.png"), ImageFormat.Png);
            using Bitmap game = Resample(truth, 1280, 720, Kernel.Box);
            float[] gl = Luma(game);
            foreach (var (up, noise) in new[] { (Kernel.Bilinear, 0.0), (Kernel.CatmullRom, 0.0), (Kernel.Nearest, 0.0), (Kernel.Bilinear, 2.0) })
            {
                sb.AppendLine();
                sb.AppendLine($"=== console enlarges 720p -> 1080p with {up}{(noise > 0 ? $" + capture noise sigma {noise}" : "")} ===");
                using Bitmap con0 = Resample(game, 1920, 1080, up);
                using Bitmap con = noise > 0 ? AddNoise(con0, noise) : (Bitmap)con0.Clone();
                {
                    ResEstimate.FrameResult fr = ResEstimate.Analyze(Luma(con), 1920, 1080);
                    sb.AppendLine($"estimator (the one 自動 uses) on this 1080p picture: vertical {fr.Vertical.EstimateHeight}p, horizontal {fr.Horizontal.EstimateHeight}p (truth: 720p)");
                }
                sb.AppendLine("prescale pass alone: 1080p console picture -> 1280x720, compared with the true 720p game picture");
                for (int f = 0; f < 5; f++)
                {
                    using Bitmap? pre = r.ReadPrescaleOnly(con, new Size(1280, 720), f);
                    if (pre == null) continue;
                    float[] pl = Luma(pre);
                    sb.AppendLine($"  {fnames[f],-16}: PSNR {Psnr(pl, gl):0.00} dB, SSIM {Ssim(pl, gl, 1280, 720):0.0000}");
                }
                {
                    sb.AppendLine("  back-projection clamp margin sweep (whole chain, c = prescale 720p + FSR):");
                    foreach (float cl in new[] { -1f, 0f, 0.1f, 0.2f, 0.35f, 0.5f })
                    {
                        D3DRenderer.BackProjectionClamp = cl;
                        Setup sc = RunOne(render, con, truth, up, D3DRenderer.FilterBackProjection);
                        Row c = sc.Rows[2];
                        sb.AppendLine($"    clamp {(cl < 0 ? "off" : cl.ToString("0.00"))}: SSIM {c.Ssim:0.0000}, edge strength {c.GradRatio:0.000}, width {c.EdgeWidth:0.00}px, overshoot {c.StepOvershoot:0.0}%, ring pixels {c.RingPct:0.00}%");
                        sc.Dispose();
                    }
                    D3DRenderer.BackProjectionClamp = 0.0f;
                }
                if (up == Kernel.Bilinear && noise == 0)
                {
                    sb.AppendLine("  back-projection iterations (prescale pass alone / whole chain SSIM):");
                    foreach (int it in new[] { 1, 2, 3, 4, 6, 8 })
                    {
                        D3DRenderer.BackProjectionIterations = it;
                        using Bitmap? pre = r.ReadPrescaleOnly(con, new Size(1280, 720), D3DRenderer.FilterBackProjection);
                        Setup sc = RunOne(render, truth, up, D3DRenderer.FilterBackProjection);
                        sb.AppendLine($"    {it} iterations: PSNR {Psnr(Luma(pre!), gl):0.00} dB; chain SSIM {sc.Rows[2].Ssim:0.0000}, edge strength {sc.Rows[2].GradRatio:0.000}, width {sc.Rows[2].EdgeWidth:0.00}px");
                        sc.Dispose();
                    }
                    D3DRenderer.BackProjectionIterations = 3;
                }
                for (int f = 0; f < 5; f++)
                {
                    Setup s = RunOne(render, con, truth, up, f);
                    try
                    {
                        sb.AppendLine($"  whole chain, prescale filter {fnames[f]}:");
                        int[] show = f == 4 ? new[] { 0, 1, 2, 3 } : new[] { 2 };
                        foreach (int ri in show) sb.AppendLine("    " + s.Rows[ri].Name + ": " + RowText(s.Rows[ri]));
                        bool ok = Better(s, out string why);
                        sb.AppendLine($"    -> new vs today: {(ok ? "BETTER" : "not better")} ({why})");
                        if (f == 4)
                        {
                            var (t1, t2) = Tiles(truth);
                            SaveCrops(s, t1, Path.Combine(outDir, $"crops_text_{up}{(noise > 0 ? "_noise" : "")}.png"));
                            SaveCrops(s, t2, Path.Combine(outDir, $"crops_edges_{up}{(noise > 0 ? "_noise" : "")}.png"));
                        }
                    }
                    finally { s.Dispose(); }
                }
            }

            // GPU time at 4K output
            using (Bitmap con2 = Resample(game, 1920, 1080, Kernel.Bilinear))
            {
                for (int f = 0; f < 5; f++)
                {
                    if (!r.PrescaleBench(con2, new Size(3840, 2160), ScalingQuality.Fsr, 0.5f, 720, f, 300, out double[] plain, out double[] with, out double[] pass))
                    { sb.AppendLine("bench unavailable"); break; }
                    sb.AppendLine($"GPU 4K FSR, filter {fnames[f]}: without {Avg(plain)} ms, with prescale {Avg(with)} ms, prescale pass alone {Avg(pass)} ms");
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
        finally { try { r?.DisposeAll(true); } catch { } }
        return sb.ToString();
    }

    internal static Bitmap AddNoise(Bitmap src, double sigma)
    {
        float[][] p = ToPlanes(src);
        var rnd = new Random(5);
        for (int i = 0; i < p[0].Length; i++)
        {
            double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
            float n = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sigma);
            p[0][i] += n; p[1][i] += n; p[2][i] += n;
        }
        return FromPlanes(p, src.Width, src.Height);
    }

    public static string Avg(double[] v) => v.Length == 0 ? "n/a" : $"{v.Average():0.000} (max {v.Max():0.000}, n={v.Length})";
}
