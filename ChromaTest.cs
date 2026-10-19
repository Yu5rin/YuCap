using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace YuCap;

/// <summary>
/// 「色の境目」: how well the convert pass restores colour at coloured edges. A sharp RGB picture with coloured text, lines
/// and bars is the ground truth; it is encoded to 4:2:0 NV12 the way a card does (three ways of subsampling the chroma, limited
/// range BT.709), converted by the REAL convert pass (plain bilinear chroma, then the luma-guided modes) and compared with the
/// truth in chroma (PSNR over the whole picture and around edges) and by the width of the colour step at bar edges.
/// Used by the test mode and by the hidden CLI <c>--chroma-check</c>.
/// </summary>
internal static class ChromaTest
{
    public enum Siting
    {
        /// <summary>MPEG-2: chroma on the even luma column, filtered [1 2 1]/4 horizontally; the two rows averaged.</summary>
        LeftFilter,
        /// <summary>Chroma point-sampled on the even column (a 4:2:2 YUY2 line), then the two rows averaged (YUY2 -> NV12).</summary>
        LeftPoint,
        /// <summary>Plain 2x2 box average (centre siting): not what the shader assumes; shows how it copes with a different card.</summary>
        CenterBox,
    }

    public static string SitingName(Siting s) => s switch
    {
        Siting.LeftFilter => "NV12（色を左寄せ・なめらかに間引き）",
        Siting.LeftPoint => "YUY2 → NV12（色を間引くだけ）",
        _ => "NV12（色を 2x2 平均・中央寄せ）",
    };

    public static string SitingNameEn(Siting s) => s switch
    {
        Siting.LeftFilter => "NV12 left-sited, filtered",
        Siting.LeftPoint => "YUY2->NV12 point-sampled",
        _ => "NV12 centre-sited box",
    };

    // ------------------------------------------------------------------ the picture

    private static readonly Color[] Saturated =
    {
        Color.FromArgb(255, 0, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 0, 255), Color.FromArgb(255, 255, 0),
        Color.FromArgb(0, 255, 255), Color.FromArgb(255, 0, 255), Color.FromArgb(255, 128, 0), Color.FromArgb(255, 255, 255),
    };

    /// <summary>Bars: left edge x of region, y of rows, bar width. Boundaries at x0 + k*BarW (and +1 in the shifted copy).</summary>
    public const int BarW = 120;
    public static readonly Rectangle BarsA = new(0, 560, 960, 80);      // boundaries on even columns
    public static readonly Rectangle BarsB = new(1, 660, 959, 80);      // boundaries on odd columns
    public static readonly Rectangle BandsA = new(20, 760, 80, 300);    // horizontal bands: boundaries on even rows (vertical edge test)

    public static Bitmap MakeTruth(int w = 1920, int h = 1080)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;   // anti-aliased like a console's UI text
        g.Clear(Color.Black);

        // ---- 1. game UI text on dark: top left ----
        string[] words = { "HP 128/256", "SCORE 004500", "Level up!", "MENU START", "ゲームの文字 赤", "COMBO x12", "ITEM", "READY" };
        int y = 14;
        foreach (float size in new[] { 13f, 17f, 23f, 31f })
        {
            using var font = new Font("Yu Gothic UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            int x = 14;
            for (int i = 0; i < 4; i++)
            {
                using var br = new SolidBrush(Saturated[(i + (int)size) % 7]);
                g.DrawString(words[(i + (int)size / 6) % words.Length], font, br, x, y);
                x += (int)(size * 8.5f);
            }
            y += (int)(size * 1.6f);
        }
        // thin coloured lines on black
        for (int k = 0; k < 6; k++)
        {
            using var pen = new Pen(Saturated[k], 1 + (k % 2));
            g.DrawLine(pen, 20, 320 + k * 14, 440, 320 + k * 14);
            g.DrawLine(pen, 480 + k * 22, 310, 480 + k * 22, 410);
        }
        // a framed gauge: coloured fill, white frame, dark background
        using (var pen = new Pen(Color.White, 2)) g.DrawRectangle(pen, 20, 420, 300, 24);
        using (var br = new SolidBrush(Color.FromArgb(220, 30, 30))) g.FillRectangle(br, 22, 422, 190, 20);
        using (var br = new SolidBrush(Color.FromArgb(30, 80, 255))) g.FillRectangle(br, 212, 422, 108, 20);
        using (var pen = new Pen(Color.FromArgb(0, 200, 0), 1)) g.DrawEllipse(pen, 380, 410, 90, 90);
        using (var pen = new Pen(Color.FromArgb(255, 200, 0), 2)) g.DrawEllipse(pen, 500, 410, 90, 90);

        // ---- 2. text on light and on colour: top right ----
        using (var br = new SolidBrush(Color.FromArgb(240, 240, 240))) g.FillRectangle(br, 960, 0, 480, 270);
        using (var br = new SolidBrush(Color.FromArgb(20, 40, 160))) g.FillRectangle(br, 1440, 0, 480, 270);
        using (var br = new SolidBrush(Color.FromArgb(200, 40, 40))) g.FillRectangle(br, 960, 270, 480, 270);
        using (var br = new SolidBrush(Color.FromArgb(40, 160, 60))) g.FillRectangle(br, 1440, 270, 480, 270);
        Color[] fg = { Color.FromArgb(220, 0, 0), Color.FromArgb(255, 235, 0), Color.FromArgb(255, 255, 255), Color.FromArgb(255, 255, 0) };
        Color[] fg2 = { Color.FromArgb(0, 60, 220), Color.FromArgb(0, 255, 255), Color.FromArgb(0, 0, 0), Color.FromArgb(255, 80, 255) };
        for (int q = 0; q < 4; q++)
        {
            int bx = 960 + (q % 2) * 480, by = (q / 2) * 270;
            int ty = by + 12;
            foreach (float size in new[] { 14f, 20f, 28f })
            {
                using var font = new Font("Yu Gothic UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
                using var br1 = new SolidBrush(fg[q]); using var br2 = new SolidBrush(fg2[q]);
                g.DrawString("Red/Blue text 色の境目", font, br1, bx + 12, ty);
                g.DrawString("0123456789 ABC", font, br2, bx + 12, ty + size * 1.4f);
                ty += (int)(size * 2.9f);
            }
        }

        // ---- 3. colour bars with sharp edges, two parities ----
        for (int k = 0; k * BarW < BarsA.Width; k++)
        {
            using var br = new SolidBrush(Saturated[k % Saturated.Length]);
            g.FillRectangle(br, BarsA.X + k * BarW, BarsA.Y, BarW, BarsA.Height);
        }
        g.FillRectangle(Brushes.Black, BarsB.X - 1, BarsB.Y, BarsB.Width + 1, BarsB.Height);
        for (int k = 0; k * BarW + 1 < BarsB.Width + 1; k++)
        {
            using var br = new SolidBrush(Saturated[(k + 3) % Saturated.Length]);
            g.FillRectangle(br, BarsB.X + k * BarW, BarsB.Y, BarW, BarsB.Height);
        }
        // horizontal bands (vertical step test) and chequers
        for (int k = 0; k * 40 < BandsA.Height; k++)
        {
            using var br = new SolidBrush(Saturated[(k + 1) % 7]);
            g.FillRectangle(br, BandsA.X, BandsA.Y + k * 40, BandsA.Width, 40);
        }
        int cx = 140;
        foreach (int cell in new[] { 1, 2, 3, 4, 8 })
        {
            for (int yy = 0; yy < 60; yy += cell)
                for (int xx = 0; xx < 90; xx += cell)
                {
                    Color c = ((xx / cell + yy / cell) & 1) == 0 ? Color.FromArgb(255, 0, 0) : Color.FromArgb(0, 200, 255);
                    using var br = new SolidBrush(c);
                    g.FillRectangle(br, cx + xx, 760 + yy + (cell == 8 ? 0 : 0), cell, cell);
                }
            cx += 100;
        }
        // coloured dots, 1..6 px
        for (int d = 1; d <= 6; d++)
        {
            using var br = new SolidBrush(Saturated[d % 7]);
            g.FillEllipse(br, 150 + d * 40, 840, d, d);
            g.FillRectangle(br, 150 + d * 40, 880, d, d);
        }
        // red text on blue, blue on red, thin: the isoluminant-ish worst case
        using (var br = new SolidBrush(Color.FromArgb(0, 0, 160))) g.FillRectangle(br, 150, 930, 330, 60);
        using (var br = new SolidBrush(Color.FromArgb(160, 0, 0))) g.FillRectangle(br, 490, 930, 330, 60);
        using (var font = new Font("Yu Gothic UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            using var r = new SolidBrush(Color.FromArgb(255, 60, 60)); using var b = new SolidBrush(Color.FromArgb(60, 60, 255));
            g.DrawString("RED on BLUE 123", font, r, 156, 945);
            g.DrawString("BLUE on RED 456", font, b, 496, 945);
        }

        // ---- 4. smooth: hue ramp and a soft colourful field ("photo"): bottom right ----
        var px = new int[960 * 540];
        var rnd = new Random(7);
        double[,] ph = new double[8, 5];
        for (int i = 0; i < 8; i++) for (int j = 0; j < 5; j++) ph[i, j] = rnd.NextDouble() * 6.28;
        for (int yy = 0; yy < 540; yy++)
            for (int xx = 0; xx < 960; xx++)
            {
                double r, gg, bb;
                if (yy < 140)
                {
                    HsvToRgb(xx / 960.0 * 360.0, 1.0, 0.3 + 0.7 * (yy / 140.0), out r, out gg, out bb);
                }
                else
                {
                    double u = xx / 960.0, v = (yy - 140) / 400.0;
                    r = 0.5 + 0.35 * Math.Sin(6.28 * (1.5 * u + 0.5 * v) + ph[0, 0]) + 0.1 * Math.Sin(40 * u + ph[1, 1]);
                    gg = 0.5 + 0.35 * Math.Sin(6.28 * (1.0 * u - 1.2 * v) + ph[2, 1]) + 0.1 * Math.Sin(53 * v + ph[3, 2]);
                    bb = 0.5 + 0.35 * Math.Sin(6.28 * (2.0 * v + 0.7 * u) + ph[4, 3]) + 0.1 * Math.Sin(31 * (u + v) + ph[5, 4]);
                    double n = (rnd.NextDouble() - 0.5) * 0.04;
                    r += n; gg += n; bb += n;
                }
                px[yy * 960 + xx] = Pack(r, gg, bb);
            }
        BitmapData bd = bmp.LockBits(new Rectangle(960, 540, 960, 540), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int yy = 0; yy < 540; yy++) Marshal.Copy(px, yy * 960, bd.Scan0 + yy * bd.Stride, 960); }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    private static int Pack(double r, double g, double b) =>
        unchecked((int)0xFF000000) | (Q(r) << 16) | (Q(g) << 8) | Q(b);

    private static int Q(double v) => Math.Clamp((int)Math.Round(v * 255.0), 0, 255);

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s, hh = h / 60.0, x = c * (1 - Math.Abs(hh % 2 - 1)), m = v - c;
        (r, g, b) = hh switch { < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x), < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x) };
        r += m; g += m; b += m;
    }

    // ------------------------------------------------------------------ colour space

    /// <summary>BT.709 luma and chroma (Cb, Cr in -0.5..0.5) of a picture, in full precision.</summary>
    public sealed class Ycc
    {
        public int W, H;
        public float[] Y = Array.Empty<float>(), Cb = Array.Empty<float>(), Cr = Array.Empty<float>();
        public float[] R = Array.Empty<float>(), G = Array.Empty<float>(), B = Array.Empty<float>();
    }

    public static Ycc ToYcc(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var o = new Ycc { W = w, H = h, Y = new float[w * h], Cb = new float[w * h], Cr = new float[w * h], R = new float[w * h], G = new float[w * h], B = new float[w * h] };
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    float b = row[x * 4] / 255f, g = row[x * 4 + 1] / 255f, r = row[x * 4 + 2] / 255f;
                    int i = y * w + x;
                    float yy = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    o.R[i] = r; o.G[i] = g; o.B[i] = b;
                    o.Y[i] = yy; o.Cb[i] = (b - yy) / 1.8556f; o.Cr[i] = (r - yy) / 1.5748f;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return o;
    }

    public sealed class Encoded { public int W, H; public byte[] Y = Array.Empty<byte>(), UV = Array.Empty<byte>(); }

    /// <summary>RGB -> limited-range BT.709 NV12 with the chosen chroma subsampling.</summary>
    public static Encoded Encode(Ycc t, Siting siting)
    {
        int w = t.W, h = t.H, cw = w / 2, ch = h / 2;
        var e = new Encoded { W = w, H = h, Y = new byte[w * h], UV = new byte[w * h / 2] };
        for (int i = 0; i < w * h; i++) e.Y[i] = (byte)Math.Clamp((int)Math.Round(16 + 219 * t.Y[i]), 0, 255);
        for (int j = 0; j < ch; j++)
            for (int i = 0; i < cw; i++)
            {
                double cb = 0, cr = 0;
                switch (siting)
                {
                    case Siting.LeftFilter:
                        for (int dy = 0; dy < 2; dy++)
                        {
                            int yy = 2 * j + dy;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                double wt = dx == 0 ? 0.5 : 0.25;
                                int xx = Math.Clamp(2 * i + dx, 0, w - 1);
                                cb += wt * 0.5 * t.Cb[yy * w + xx]; cr += wt * 0.5 * t.Cr[yy * w + xx];
                            }
                        }
                        break;
                    case Siting.LeftPoint:
                        for (int dy = 0; dy < 2; dy++)
                        {
                            cb += 0.5 * t.Cb[(2 * j + dy) * w + 2 * i]; cr += 0.5 * t.Cr[(2 * j + dy) * w + 2 * i];
                        }
                        break;
                    default:
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                cb += 0.25 * t.Cb[(2 * j + dy) * w + 2 * i + dx]; cr += 0.25 * t.Cr[(2 * j + dy) * w + 2 * i + dx];
                            }
                        break;
                }
                e.UV[j * w + 2 * i] = (byte)Math.Clamp((int)Math.Round(128 + 224 * cb), 0, 255);
                e.UV[j * w + 2 * i + 1] = (byte)Math.Clamp((int)Math.Round(128 + 224 * cr), 0, 255);
            }
        return e;
    }

    // ------------------------------------------------------------------ metrics

    public sealed class Row
    {
        public string Name = "";
        public double ChromaPsnr, ChromaPsnrEdge, RgbPsnr, SmoothPsnr, WidthEvenH, WidthOddH, WidthV;
        public Bitmap? Image;
    }

    /// <summary>10%-90% rise distance (pixels) of the larger chroma step across each boundary of a bar region, mean of the rows.</summary>
    private static double StepWidth(Ycc c, Rectangle bars, bool horizontalEdges, int firstBoundary, int pitch, int count)
    {
        double sum = 0; int n = 0;
        for (int k = 0; k < count; k++)
        {
            int b = firstBoundary + k * pitch;
            int lines = horizontalEdges ? bars.Width : bars.Height;
            for (int l = 8; l < lines - 8; l += 4)
            {
                // sample the profile 6 px each side of the boundary (clean of the next edge: pitch >= 40)
                double[] pc = new double[12], pb = new double[12];
                for (int d = -6; d < 6; d++)
                {
                    int x = horizontalEdges ? bars.X + l : b + d;
                    int y = horizontalEdges ? b + d : bars.Y + l;
                    if (x < 0 || y < 0 || x >= c.W || y >= c.H) goto next;
                    pc[d + 6] = c.Cb[y * c.W + x]; pb[d + 6] = c.Cr[y * c.W + x];
                }
                {
                    double[] p = Math.Abs(pc[11] - pc[0]) >= Math.Abs(pb[11] - pb[0]) ? pc : pb;
                    double a = p[0], z = p[11];
                    if (Math.Abs(z - a) < 0.15) goto next;
                    // normalised 0..1 profile; first crossing of 0.1 and of 0.9 by linear interpolation
                    double[] q = p.Select(v => (v - a) / (z - a)).ToArray();
                    double Cross(double level)
                    {
                        for (int i = 0; i < 11; i++)
                            if ((q[i] - level) * (q[i + 1] - level) <= 0 && q[i] != q[i + 1]) return i + (level - q[i]) / (q[i + 1] - q[i]);
                        return double.NaN;
                    }
                    double lo = Cross(0.1), hi = Cross(0.9);
                    if (double.IsNaN(lo) || double.IsNaN(hi)) goto next;
                    sum += hi - lo; n++;
                }
            next:;
            }
        }
        return n == 0 ? double.NaN : sum / n;
    }

    public static Row Measure(string name, Bitmap img, Ycc truth, bool[] edgeZone, bool[] smoothZone)
    {
        Ycc c = ToYcc(img);
        double se = 0, ee = 0, ss = 0; long n = 0, ne = 0, ns = 0;
        for (int i = 0; i < c.W * c.H; i++)
        {
            double d1 = c.Cb[i] - truth.Cb[i], d2 = c.Cr[i] - truth.Cr[i];
            double e = d1 * d1 + d2 * d2;
            se += e; n += 2;
            if (edgeZone[i]) { ee += e; ne += 2; }
            if (smoothZone[i]) { ss += e; ns += 2; }
        }
        double rgbMse = 0;
        for (int i = 0; i < c.W * c.H; i++)
        {
            double a = c.R[i] - truth.R[i], b = c.G[i] - truth.G[i], d = c.B[i] - truth.B[i];
            rgbMse += a * a + b * b + d * d;
        }
        rgbMse /= 3.0 * c.W * c.H;
        double Db(double mse) => 10 * Math.Log10(1.0 / Math.Max(mse, 1e-12));
        return new Row
        {
            Name = name, Image = img,
            ChromaPsnr = Db(se / n), ChromaPsnrEdge = Db(ee / Math.Max(1, ne)), SmoothPsnr = Db(ss / Math.Max(1, ns)), RgbPsnr = Db(rgbMse),
            WidthEvenH = StepWidth(c, BarsA, false, BarsA.X + BarW, BarW, 7),
            WidthOddH = StepWidth(c, BarsB, false, BarsB.X + BarW, BarW, 7),
            WidthV = StepWidth(c, BandsA, true, BandsA.Y + 40, 40, 6),
        };
    }

    /// <summary>Pixels within 2 px of a chroma edge of the truth (edge zone) and pixels with no chroma change within 3 px (smooth zone).</summary>
    public static void Zones(Ycc t, out bool[] edge, out bool[] smooth)
    {
        int w = t.W, h = t.H;
        var g = new bool[w * h];
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                int i = y * w + x;
                double dx = Math.Max(Math.Abs(t.Cb[i + 1] - t.Cb[i]), Math.Abs(t.Cr[i + 1] - t.Cr[i]));
                double dy = Math.Max(Math.Abs(t.Cb[i + w] - t.Cb[i]), Math.Abs(t.Cr[i + w] - t.Cr[i]));
                if (dx > 0.04 || dy > 0.04) g[i] = true;
            }
        edge = Dilate(g, w, h, 2);
        var near = Dilate(g, w, h, 4);
        smooth = new bool[w * h];
        for (int i = 0; i < smooth.Length; i++) smooth[i] = !near[i];
    }

    private static bool[] Dilate(bool[] src, int w, int h, int r)
    {
        var tmp = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int last = -1000;
            for (int x = 0; x < w; x++) { if (src[y * w + x]) last = x; tmp[y * w + x] = x - last <= r; }
            last = 100000;
            for (int x = w - 1; x >= 0; x--) { if (src[y * w + x]) last = x; if (last - x <= r) tmp[y * w + x] = true; }
        }
        var res = new bool[w * h];
        for (int x = 0; x < w; x++)
        {
            int last = -1000;
            for (int y = 0; y < h; y++) { if (tmp[y * w + x]) last = y; res[y * w + x] = y - last <= r; }
            last = 100000;
            for (int y = h - 1; y >= 0; y--) { if (tmp[y * w + x]) last = y; if (last - y <= r) res[y * w + x] = true; }
        }
        return res;
    }

    public delegate Bitmap? ConvertFn(Encoded e, int mode);

    public static readonly (string Name, int Mode)[] Modes =
    {
        ("今まで（バイリニア）", 0), ("色を左寄せで補間（輝度なし）", 1), ("輝度で重み付け（バイラテラル）", 2), ("輝度から色を推定（既定）", 3),
    };

    public static string RowText(Row r) =>
        $"色の PSNR {r.ChromaPsnr:0.00} dB / 境目の近く {r.ChromaPsnrEdge:0.00} dB / なめらかな所 {r.SmoothPsnr:0.00} dB / RGB の PSNR {r.RgbPsnr:0.00} dB / 縦線の色の境目の幅 {r.WidthEvenH:0.00}px（奇数列 {r.WidthOddH:0.00}px）/ 横線の境目の幅 {r.WidthV:0.00}px";

    public static void SaveCrops(List<Row> rows, Bitmap truth, Rectangle tile, int zoom, string path, string[] labels)
    {
        var panels = new List<Bitmap>(); var lb = new List<string>();
        for (int i = 0; i < rows.Count; i++) { if (rows[i].Image != null) { panels.Add(rows[i].Image!); lb.Add(labels[i]); } }
        panels.Add(truth); lb.Add("truth");
        const int head = 26, gap = 8;
        int n = panels.Count;
        using var outBmp = new Bitmap((tile.Width * zoom) * n + gap * (n - 1), tile.Height * zoom + head, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(outBmp);
        g.Clear(Color.FromArgb(24, 24, 24));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
        for (int i = 0; i < n; i++)
        {
            int x = i * (tile.Width * zoom + gap);
            g.DrawString(lb[i], font, Brushes.White, x + 4, 3);
            g.DrawImage(panels[i], new Rectangle(x, head, tile.Width * zoom, tile.Height * zoom), tile, GraphicsUnit.Pixel);
        }
        outBmp.Save(path, ImageFormat.Png);
    }

    // ------------------------------------------------------------------ headless

    /// <summary>All sitings x all modes on the synthetic picture.</summary>
    public static Dictionary<Siting, List<Row>> RunSynthetic(Bitmap truthBmp, ConvertFn convert, Ycc truth, out bool[] edge, out bool[] smooth, Action<string>? log = null)
    {
        Zones(truth, out edge, out smooth);
        var res = new Dictionary<Siting, List<Row>>();
        foreach (Siting s in Enum.GetValues<Siting>())
        {
            Encoded enc = Encode(truth, s);
            var rows = new List<Row>();
            foreach ((string name, int mode) in Modes)
            {
                Bitmap? img = convert(enc, mode);
                if (img == null) continue;
                rows.Add(Measure(name, img, truth, edge, smooth));
            }
            res[s] = rows;
        }
        return res;
    }

    /// <summary>Development aid (--chroma-check --sweep): the tunables of mode 3 over a small grid.</summary>
    public static string Sweep(D3DRenderer r)
    {
        var sb = new StringBuilder();
        using Bitmap truthBmp = MakeTruth();
        Ycc truth = ToYcc(truthBmp);
        Zones(truth, out bool[] edge, out bool[] smooth);
        var encs = Enum.GetValues<Siting>().Select(s => Encode(truth, s)).ToArray();
        var keepT = D3DRenderer.ChromaTune; int keepV = D3DRenderer.ChromaVariant;
        void Run(int mode, int variant, System.Numerics.Vector4 tune)
        {
            D3DRenderer.ChromaTune = tune; D3DRenderer.ChromaVariant = variant;
            double pe = 0, ps = 0, pr = 0, w = 0; string per = "";
            foreach (Encoded e in encs)
            {
                using Bitmap? img = r.ConvertSynthetic(e.Y, e.UV, e.W, e.H, VideoAdjustments.Neutral, new YuvInfo(1, false), mode, 0, out _);
                if (img == null) return;
                Row row = Measure("", img, truth, edge, smooth); row.Image = null;
                pe += row.ChromaPsnrEdge; ps += row.SmoothPsnr; pr += row.RgbPsnr; w += (row.WidthEvenH + row.WidthOddH + row.WidthV) / 3;
                per += $" {row.ChromaPsnrEdge:0.00}";
            }
            sb.AppendLine($"mode {mode} var {variant} tune ({tune.X:0.##},{tune.Y:0.#},{tune.Z:0.##},{tune.W:0.#####}): edge {pe / 3:0.00} smooth {ps / 3:0.00} rgb {pr / 3:0.00} width {w / 3:0.00}  [per siting{per}]");
        }
        foreach (int v in new[] { 0, 1 }) Run(3, v, keepT);
        foreach (float x in new[] { 0f, 0.5f, 1f, 2f, 4f }) Run(3, keepV, new(x, keepT.Y, keepT.Z, keepT.W));
        foreach (float z in new[] { 0.25f, 0.5f, 1f, 2f, 4f }) Run(3, keepV, new(keepT.X, keepT.Y, z, keepT.W));
        foreach (float wv in new[] { 0.00005f, 0.0002f, 0.0005f, 0.001f, 0.003f }) Run(3, keepV, new(keepT.X, keepT.Y, keepT.Z, wv));
        foreach (float y in new[] { 50f, 100f, 150f, 300f, 600f }) Run(2, keepV, new(keepT.X, y, keepT.Z, keepT.W));
        D3DRenderer.ChromaTune = keepT; D3DRenderer.ChromaVariant = keepV;
        return sb.ToString();
    }

    public static string HeadlessCheck(string outDir, string? framesDir, bool sweep = false)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        D3DRenderer? r = null;
        try
        {
            r = new D3DRenderer();
            r.CreateDevice();
            r.CreateShaders();
            sb.AppendLine("adapter: " + r.AdapterName);
            if (sweep) { sb.Append(Sweep(r)); return sb.ToString(); }
            using Bitmap truthBmp = MakeTruth();
            truthBmp.Save(Path.Combine(outDir, "chroma_truth.png"), ImageFormat.Png);
            Ycc truth = ToYcc(truthBmp);
            ConvertFn conv = (e, mode) => r.ConvertSynthetic(e.Y, e.UV, e.W, e.H, VideoAdjustments.Neutral, new YuvInfo(1, false), mode, 0, out _);
            var all = RunSynthetic(truthBmp, conv, truth, out _, out _);
            foreach ((Siting s, List<Row> rows) in all)
            {
                sb.AppendLine();
                sb.AppendLine($"=== synthetic truth, {SitingNameEn(s)} (limited range BT.709, 1920x1080) ===");
                foreach (Row row in rows)
                    sb.AppendLine($"  {row.Name,-22}: chroma PSNR {row.ChromaPsnr:0.00} dB, near edges {row.ChromaPsnrEdge:0.00} dB, smooth areas {row.SmoothPsnr:0.00} dB, RGB PSNR {row.RgbPsnr:0.00} dB, step width even/odd/vertical {row.WidthEvenH:0.00}/{row.WidthOddH:0.00}/{row.WidthV:0.00} px");
                if (s == Siting.LeftFilter)
                {
                    foreach (Rectangle tile in new[] { new Rectangle(14, 14, 200, 112), new Rectangle(1000, 12, 200, 112), new Rectangle(20, 300, 200, 112), new Rectangle(100, 925, 200, 112), new Rectangle(140, 750, 200, 112), new Rectangle(100, 555, 160, 90) })
                    {
                        string nm = $"chroma_crop_{tile.X}_{tile.Y}.png";
                        SaveCrops(rows, truthBmp, tile, 4, Path.Combine(outDir, nm), rows.Select(x => x.Name).ToArray());
                    }
                }
                foreach (Row row in rows) row.Image?.Dispose();
            }
            // cost
            sb.AppendLine();
            sb.AppendLine("=== GPU time of the convert pass (ms per draw, 1080p / 4K, NV12 source) ===");
            foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
            {
                using Bitmap tb = w == 1920 ? (Bitmap)truthBmp.Clone() : Upscale(truthBmp, w, h);
                Encoded enc = Encode(ToYcc(tb), Siting.LeftFilter);
                foreach ((string name, int mode) in Modes)
                {
                    r.ConvertSynthetic(enc.Y, enc.UV, w, h, VideoAdjustments.Neutral, new YuvInfo(1, false), mode, 400, out double[] ms)?.Dispose();
                    sb.AppendLine($"  {w}x{h} {name,-22}: avg {(ms.Length > 0 ? ms.Average() : double.NaN):0.000} ms, median {(ms.Length > 0 ? Median(ms) : double.NaN):0.000} ms, max {(ms.Length > 0 ? ms.Max() : double.NaN):0.000} ms (n={ms.Length})");
                }
            }
            if (framesDir != null && Directory.Exists(framesDir))
            {
                sb.AppendLine();
                sb.AppendLine("=== real frames re-encoded to NV12 (the pictures already went through a 4:2:0 card once; see notes) ===");
                foreach (string f in Directory.GetFiles(framesDir, "*.png").OrderBy(x => x).Take(8))
                {
                    using var raw = new Bitmap(f);
                    using var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                    if ((bmp.Width & 1) != 0 || (bmp.Height & 1) != 0) continue;
                    sb.AppendLine(RealLine(conv, bmp, Path.GetFileName(f)));
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
        finally { try { r?.DisposeAll(true); } catch { } }
        return sb.ToString();
    }

    private static double Median(double[] v) { var s = v.OrderBy(x => x).ToArray(); return s[s.Length / 2]; }

    public static Bitmap Upscale(Bitmap src, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, 0, 0, w, h);
        return b;
    }

    /// <summary>How a real picture changes when the new mode replaces the bilinear one: mean chroma change, share of pixels changed by more
    /// than 0.02 (about 4 levels of 255 in chroma), and the largest overshoot beyond the range of the 2x2 chroma samples nearby.</summary>
    internal static string RealLine(ConvertFn conv, Bitmap frame, string name)
    {
        Ycc t = ToYcc(frame);
        Encoded enc = Encode(t, Siting.LeftFilter);
        var yuv = new YuvInfo(1, false);
        using Bitmap? b0 = conv(enc, 0);
        using Bitmap? b3 = conv(enc, 3);
        if (b0 == null || b3 == null) return $"  {name}: not run";
        Ycc c0 = ToYcc(b0), c3 = ToYcc(b3);
        Zones(t, out bool[] edge, out _);
        double sumAll = 0, sumEdge = 0; long changed = 0, nEdge = 0, n = 0;
        double e0 = 0, e3 = 0; long ne = 0;
        for (int i = 0; i < t.W * t.H; i++)
        {
            double d = Math.Max(Math.Abs(c3.Cb[i] - c0.Cb[i]), Math.Abs(c3.Cr[i] - c0.Cr[i]));
            sumAll += d; n++;
            if (d > 0.02) changed++;
            if (edge[i])
            {
                sumEdge += d; nEdge++;
                double a0 = c0.Cb[i] - t.Cb[i], b0v = c0.Cr[i] - t.Cr[i], a3 = c3.Cb[i] - t.Cb[i], b3v = c3.Cr[i] - t.Cr[i];
                e0 += a0 * a0 + b0v * b0v; e3 += a3 * a3 + b3v * b3v; ne += 2;
            }
        }
        double Db(double mse) => 10 * Math.Log10(1.0 / Math.Max(mse, 1e-12));
        return $"  {name}: mean chroma change {sumAll / n:0.0000} (near edges {sumEdge / Math.Max(1, nEdge):0.0000}), pixels changed >0.02: {100.0 * changed / n:0.00}% ; vs the picture itself near edges: bilinear {Db(e0 / Math.Max(1, ne)):0.00} dB, new {Db(e3 / Math.Max(1, ne)):0.00} dB";
    }
}
