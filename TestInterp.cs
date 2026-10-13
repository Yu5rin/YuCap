using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace YuCap;

// Picture-quality checks of frame interpolation for the test mode (テストモード): a known-motion synthetic
// check and a hold-out check on live frames. Like the rest of the report, the wording that ends up in the
// report is Japanese only (see TestReport.cs); nothing here goes through L.T.

internal static class InterpCheck
{
    // ================================================================ shared pixel helpers

    private static int Ch(int px, int sh) => (px >> sh) & 255;

    private static int Luma(int px) => (Ch(px, 16) + 2 * Ch(px, 8) + Ch(px, 0)) >> 2;

    private static int[] Blend(int[] a, int[] b)
    {
        var o = new int[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int r = 0;
            for (int sh = 0; sh < 24; sh += 8) r |= ((Ch(a[i], sh) + Ch(b[i], sh) + 1) / 2) << sh;
            o[i] = r | unchecked((int)0xFF000000);
        }
        return o;
    }

    private static double PsnrOf(double mse) => mse <= 1e-9 ? 99 : Math.Min(99, 10 * Math.Log10(255.0 * 255.0 / mse));

    /// <summary>Mean squared / mean absolute error of p against q over the pixels where mask is true (null = all).</summary>
    private static (double Mse, double Mae) Errors(int[] p, int[] q, bool[]? mask)
    {
        double se = 0, ae = 0; long n = 0;
        for (int i = 0; i < p.Length; i++)
        {
            if (mask != null && !mask[i]) continue;
            for (int sh = 0; sh < 24; sh += 8)
            {
                int d = Ch(p[i], sh) - Ch(q[i], sh);
                se += d * d; ae += Math.Abs(d); n++;
            }
        }
        return n == 0 ? (0, 0) : (se / n, ae / n);
    }

    // ================================================================ a picture with plenty of detail

    /// <summary>A reproducible busy picture (gradient, random shapes, text-like edges) for when the live still is too flat.</summary>
    public static Bitmap MakePattern(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rnd = new Random(20260613);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(40, 60, 110), Color.FromArgb(200, 170, 90), 35f))
            g.FillRectangle(br, 0, 0, w, h);
        Color Rc(int a) => Color.FromArgb(a, rnd.Next(256), rnd.Next(256), rnd.Next(256));
        for (int i = 0; i < 320; i++)
        {
            int x = rnd.Next(-40, w), y = rnd.Next(-40, h), ww = rnd.Next(12, 220), hh = rnd.Next(12, 160);
            using var br = new SolidBrush(Rc(rnd.Next(110, 255)));
            switch (i % 3)
            {
                case 0: g.FillRectangle(br, x, y, ww, hh); break;
                case 1: g.FillEllipse(br, x, y, ww, hh); break;
                default:
                    using (var pen = new Pen(Rc(255), 1 + rnd.Next(4))) g.DrawLine(pen, x, y, x + ww * 2 - 200, y + hh * 2 - 150);
                    break;
            }
        }
        string[] words = { "YuCap", "frame", "motion", "interpolation", "ライブ", "映像", "0123456789", "ABCDEFG", "capture", "test", "フレーム", "{ }[]()<>", "x = y * 2", "Hello" };
        string[] fonts = { "Consolas", "Segoe UI", "Yu Gothic UI", "Arial" };
        for (int i = 0; i < 90; i++)
        {
            float size = 9 + rnd.Next(30);
            using var f = new Font(fonts[rnd.Next(fonts.Length)], size, rnd.Next(3) == 0 ? FontStyle.Bold : FontStyle.Regular);
            using var br = new SolidBrush(Rc(255));
            string t = string.Join(" ", Enumerable.Range(0, 1 + rnd.Next(4)).Select(_ => words[rnd.Next(words.Length)]));
            g.DrawString(t, f, br, rnd.Next(-20, w - 60), rnd.Next(-10, h - 20));
        }
        return bmp;
    }

    // ================================================================ 1. known motion

    internal sealed class SynthResult
    {
        public bool Ok;
        public string Failure = string.Empty;
        public bool UsedStill;
        public double Detail;                       // mean gradient of the picture used
        public Size Size;
        public double BgPsnr, BlkPsnr, BgBlend, BlkBlend;
        public int BgN, BgOk, BgTexN, BgTexOk, BlkN, BlkOk;
        public double BgErr, BlkErr;
        public int BadBlocks, BadVectors;
        public string Timing = string.Empty;
    }

    public const int GlobalDx = 14, GlobalDy = -8, BlockDx = -40, BlockDy = 12;

    /// <summary>Move a picture globally by (14,-8) and one rectangle by (-40,12), run the real pipeline at 0.5 and
    /// compare with the exactly known middle picture. Saves the blend | interpolated | truth crop to
    /// <paramref name="imagePath"/>.</summary>
    public static SynthResult RunSynthetic(D3DVideoEngine engine, Bitmap? still, string imagePath)
    {
        var res = new SynthResult();
        Bitmap? own = null;
        try
        {
            Bitmap src;
            double detail = still == null ? 0 : TestProbes.MeasureEdges(still).MeanGradient;
            if (still != null && detail >= 6.0 && still.Width >= 640 && still.Height >= 360)
            {
                src = still; res.UsedStill = true;
            }
            else
            {
                own = MakePattern(1920, 1080);
                src = own; res.UsedStill = false;
                detail = TestProbes.MeasureEdges(own).MeanGradient;
            }
            res.Detail = detail;
            int w = src.Width & ~1, h = src.Height & ~1;
            int[] A;
            using (var fit = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(fit)) g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
                A = Program.ReadPixels(fit);
            }
            res.Size = new Size(w, h);

            var blk = new Rectangle(w * 36 / 100, h * 38 / 100, w * 20 / 100, h * 28 / 100);
            int[] B = Program.ShiftPixels(A, w, h, GlobalDx, GlobalDy);
            int[] blockShift = Program.ShiftPixels(A, w, h, BlockDx, BlockDy);
            for (int y = blk.Top; y < blk.Bottom; y++)
                for (int x = blk.Left; x < blk.Right; x++) B[y * w + x] = blockShift[y * w + x];

            const double alpha = 0.5;
            int[] gt = Program.ShiftPixels(A, w, h, GlobalDx * alpha, GlobalDy * alpha);
            int[] gtBlock = Program.ShiftPixels(A, w, h, BlockDx * alpha, BlockDy * alpha);
            var blkMid = new Rectangle(blk.Left + (int)Math.Round(BlockDx * alpha), blk.Top + (int)Math.Round(BlockDy * alpha), blk.Width, blk.Height);
            for (int y = blkMid.Top; y < blkMid.Bottom; y++)
                for (int x = blkMid.Left; x < blkMid.Right; x++) gt[y * w + x] = gtBlock[y * w + x];

            using Bitmap bA = Program.MakeBitmap(A, w, h), bB = Program.MakeBitmap(B, w, h);
            if (!engine.DebugInterpSynthetic(bA, bB, alpha, 0, out Bitmap? mid, out float[] field, out Size fs, out string timing) || mid == null)
            {
                res.Failure = "補間の処理を始められませんでした（補間用のシェーダーを使えない可能性があります）";
                return res;
            }
            using (mid)
            {
                res.Timing = timing;
                int[] M = Program.ReadPixels(mid);
                int m = 24;
                var bgRect = new Rectangle(m, m, w - 2 * m, h * 30 / 100 - m);
                var blkRect = new Rectangle(blkMid.Left + m, blkMid.Top + m, blkMid.Width - 2 * m, blkMid.Height - 2 * m);
                int[] blend = Blend(A, B);
                res.BgPsnr = Program.Psnr(M, gt, w, bgRect);
                res.BlkPsnr = Program.Psnr(M, gt, w, blkRect);
                res.BgBlend = Program.Psnr(blend, gt, w, bgRect);
                res.BlkBlend = Program.Psnr(blend, gt, w, blkRect);

                // the field: one vector per block (8x8 source pixels, 16x16 for a wide source), centred at bp*fx + bp/2; "textured" = the block of A has real detail,
                // so a vector there is well defined (a flat block can match anywhere)
                var inner = Rectangle.Inflate(blk, -m, -m);
                var outer = Rectangle.Inflate(blk, m * 2, m * 2);
                int bp = D3DRenderer.InterpBlockPx(new Size(w, h));
                for (int fy = 0; fy < fs.Height; fy++)
                    for (int fx = 0; fx < fs.Width; fx++)
                    {
                        float vx = field[(fy * fs.Width + fx) * 2], vy = field[(fy * fs.Width + fx) * 2 + 1];
                        if (float.IsNaN(vx) || float.IsNaN(vy) || float.IsInfinity(vx) || float.IsInfinity(vy) || Math.Abs(vx) > w || Math.Abs(vy) > h) { res.BadVectors++; continue; }
                        int cx = fx * bp + bp / 2, cy = fy * bp + bp / 2;
                        if (inner.Contains(cx, cy))
                        {
                            res.BlkN++; double e = Math.Max(Math.Abs(vx - BlockDx), Math.Abs(vy - BlockDy)); res.BlkErr += e; if (e <= 1.0) res.BlkOk++;
                        }
                        else if (!outer.Contains(cx, cy) && cx > m && cy > m && cx < w - m && cy < h - m)
                        {
                            res.BgN++; double e = Math.Max(Math.Abs(vx - GlobalDx), Math.Abs(vy - GlobalDy)); res.BgErr += e;
                            bool good = e <= 1.0;
                            if (good) res.BgOk++;
                            if (CellDetail(A, w, h, fx * bp + bp / 2 - 4, fy * bp + bp / 2 - 4) >= 8.0) { res.BgTexN++; if (good) res.BgTexOk++; }
                        }
                    }
                if (res.BgN > 0) res.BgErr /= res.BgN;
                if (res.BlkN > 0) res.BlkErr /= res.BlkN;

                // blocks of the result that came out black although the truth is not
                for (int by = m; by + 16 <= h - m; by += 16)
                    for (int bx = m; bx + 16 <= w - m; bx += 16)
                    {
                        long sm = 0, st = 0;
                        for (int y = by; y < by + 16; y++)
                            for (int x = bx; x < bx + 16; x++) { sm += Luma(M[y * w + x]); st += Luma(gt[y * w + x]); }
                        if (st / 256 > 40 && sm / 256 < 12) res.BadBlocks++;
                    }

                int tw = Math.Min(400, w), th = Math.Min(225, h);
                int tx = Math.Clamp(blkMid.Left + blkMid.Width / 4 - tw / 2, 0, w - tw), ty = Math.Clamp(blkMid.Top + blkMid.Height / 4 - th / 2, 0, h - th);
                using Bitmap bBlend = Program.MakeBitmap(blend, w, h), bTruth = Program.MakeBitmap(gt, w, h);
                TestProbes.SaveStrip(new[] { bBlend, mid, bTruth }, new[] { "2コマを重ねただけ", "補間", "正解" },
                    new Rectangle(tx, ty, tw, th), imagePath);
                res.Ok = true;
            }
            return res;
        }
        catch (Exception ex) { res.Failure = ex.Message; return res; }
        finally { own?.Dispose(); }
    }

    /// <summary>Mean |dx|+|dy| of the luma over an 8x8 cell.</summary>
    private static double CellDetail(int[] px, int w, int h, int x0, int y0)
    {
        long sum = 0; int n = 0;
        for (int y = y0; y < Math.Min(h - 1, y0 + 8); y++)
            for (int x = x0; x < Math.Min(w - 1, x0 + 8); x++)
            {
                int l = Luma(px[y * w + x]);
                sum += Math.Abs(l - Luma(px[y * w + x + 1])) + Math.Abs(l - Luma(px[(y + 1) * w + x]));
                n++;
            }
        return n == 0 ? 0 : (double)sum / n;
    }

    // ================================================================ 2. live hold-out

    internal sealed class Triple
    {
        public double Moving;                  // share of pixels that changed between F0 and F2
        public bool Dup;                       // F1 equals F0 or F2 in the moving part (the game redraws less often than the video)
        public double InterpPsnr, BlendPsnr, RepeatPsnr;       // in the moving part
        public double InterpAll, BlendAll, RepeatAll;           // whole picture
        public double InterpMae, BlendMae, RepeatMae;           // in the moving part
    }

    /// <summary>Compare the interpolated middle picture with the real middle frame F1 (and a plain blend of F0/F2 and
    /// a repeat of F0). <paramref name="strip"/> is the four-panel example crop, built only when this triple moves
    /// more than <paramref name="bestMoving"/>.</summary>
    public static Triple Analyze(Bitmap f0, Bitmap f1, Bitmap f2, Bitmap mid, double bestMoving, out Bitmap? strip)
    {
        strip = null;
        int w = f0.Width, h = f0.Height;
        int[] p0 = Program.ReadPixels(f0), p1 = Program.ReadPixels(f1), p2 = Program.ReadPixels(f2), pm = Program.ReadPixels(mid);
        var mask = new bool[p0.Length];
        long mc = 0;
        for (int i = 0; i < p0.Length; i++)
        {
            int d = 0;
            for (int sh = 0; sh < 24; sh += 8) d = Math.Max(d, Math.Abs(Ch(p0[i], sh) - Ch(p2[i], sh)));
            if (d > 16) { mask[i] = true; mc++; }
        }
        var t = new Triple { Moving = (double)mc / p0.Length };
        if (t.Moving < 0.01) return t;

        int[] bl = Blend(p0, p2);
        t.Dup = Errors(p0, p1, mask).Mae < 0.5 || Errors(p2, p1, mask).Mae < 0.5;
        var (mi, mai) = Errors(pm, p1, mask); var (mb, mab) = Errors(bl, p1, mask); var (mr, mar) = Errors(p0, p1, mask);
        t.InterpPsnr = PsnrOf(mi); t.BlendPsnr = PsnrOf(mb); t.RepeatPsnr = PsnrOf(mr);
        t.InterpMae = mai; t.BlendMae = mab; t.RepeatMae = mar;
        t.InterpAll = PsnrOf(Errors(pm, p1, null).Mse); t.BlendAll = PsnrOf(Errors(bl, p1, null).Mse); t.RepeatAll = PsnrOf(Errors(p0, p1, null).Mse);

        if (!t.Dup && t.Moving > bestMoving)
        {
            // the 400x225 window (on a 40x25 grid) where the picture changed most
            const int cw = 40, ch = 25, nx = 10, ny = 9;
            int gx = w / cw, gy = h / ch;
            if (gx >= nx && gy >= ny)
            {
                var cells = new long[gx, gy];
                for (int y = 0; y < gy * ch; y++)
                    for (int x = 0; x < gx * cw; x++)
                        if (mask[y * w + x]) cells[x / cw, y / ch]++;
                long best = -1; int bx = 0, by = 0;
                for (int cx = 0; cx + nx <= gx; cx++)
                    for (int cy = 0; cy + ny <= gy; cy++)
                    {
                        long s = 0;
                        for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) s += cells[cx + i, cy + j];
                        if (s > best) { best = s; bx = cx; by = cy; }
                    }
                using Bitmap bBlend = Program.MakeBitmap(bl, w, h);
                string tmp = System.IO.Path.GetTempFileName();
                try
                {
                    TestProbes.SaveStrip(new[] { f0, bBlend, mid, f1 }, new[] { "前のコマの繰り返し", "2コマの重ね合わせ", "補間", "実際のコマ" },
                        new Rectangle(bx * cw, by * ch, nx * cw, ny * ch), tmp);
                    using var fromFile = new Bitmap(tmp);
                    strip = new Bitmap(fromFile);       // detach from the file
                }
                finally { try { System.IO.File.Delete(tmp); } catch { } }
            }
        }
        return t;
    }

    // ================================================================ 3. which live frames are new pictures

    /// <summary>
    /// Live frames in, evenly spaced triples of DIFFERENT pictures out. A game that renders at 30 or 60 fps and is
    /// captured at 60 or 120 fps delivers the same picture several times in a row; interpolation between two frames
    /// can only be judged against a real middle frame, so repeated pictures are dropped first and triples are built
    /// from consecutive unique pictures whose time gaps are about equal (then the middle one is a true midpoint).
    /// </summary>
    internal sealed class LiveFrames
    {
        public sealed class Frame { public long Seq, Ticks; public int W, H; }

        /// <summary>A gap may differ from the other by this share of their mean (+-25 %).</summary>
        public const double EvenTolerance = 0.25;

        public readonly List<Frame> Unique = new();
        public int Captured, Repeats;
        private byte[]? _lastLuma;

        /// <summary>Two pictures count as the same when almost no pixel changed by more than a few levels (capture
        /// noise is allowed, a moving object of a few pixels is not).</summary>
        public static bool SamePicture(byte[] a, byte[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return false;
            long sum = 0; int big = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = Math.Abs(a[i] - b[i]);
                sum += d;
                if (d > 6) big++;
            }
            return big <= Math.Max(8, a.Length / 20000) && sum < a.Length * 0.6;
        }

        /// <summary>Feed the frames in capture order. Returns true when the frame was a new picture. A frame is compared
        /// with the last picture KEPT, so a slow change that adds up over several frames still counts as new.</summary>
        public bool Add(long seq, long ticks, int w, int h, byte[] luma)
        {
            Captured++;
            if (_lastLuma != null && Unique.Count > 0 && Unique[^1].W == w && Unique[^1].H == h && SamePicture(_lastLuma, luma))
            {
                Repeats++;
                return false;
            }
            Unique.Add(new Frame { Seq = seq, Ticks = ticks, W = w, H = h });
            _lastLuma = luma;
            return true;
        }

        public static bool EvenlySpaced(long t0, long t1, long t2)
        {
            long d1 = t1 - t0, d2 = t2 - t1;
            if (d1 <= 0 || d2 <= 0) return false;
            return Math.Abs(d1 - d2) <= EvenTolerance * (d1 + d2) / 2.0;
        }

        /// <summary>The newest triple of consecutive unique pictures, starting at index <paramref name="minIndex"/> or
        /// later, with the same size and evenly spaced in time. On success <paramref name="minIndex"/> moves past it
        /// (triples never share a picture).</summary>
        public bool TryPick(ref int minIndex, out Frame f0, out Frame f1, out Frame f2)
        {
            f0 = f1 = f2 = null!;
            for (int i = Unique.Count - 3; i >= minIndex; i--)
            {
                Frame a = Unique[i], b = Unique[i + 1], c = Unique[i + 2];
                if (a.W != b.W || b.W != c.W || a.H != b.H || b.H != c.H) continue;
                if (!EvenlySpaced(a.Ticks, b.Ticks, c.Ticks)) continue;
                f0 = a; f1 = b; f2 = c;
                minIndex = i + 3;
                return true;
            }
            return false;
        }

        /// <summary>Over the whole stream: how many windows of three consecutive unique pictures exist, and how many of
        /// them are evenly spaced (the share a game with uneven frame pacing loses).</summary>
        public (int Windows, int Even) CountWindows()
        {
            int n = 0, e = 0;
            for (int i = 0; i + 2 < Unique.Count; i++)
            {
                n++;
                if (EvenlySpaced(Unique[i].Ticks, Unique[i + 1].Ticks, Unique[i + 2].Ticks)) e++;
            }
            return (n, e);
        }

        /// <summary>Median gap between consecutive unique pictures in ms (0 when fewer than two).</summary>
        public double MedianGapMs()
        {
            var g = new List<double>();
            for (int i = 1; i < Unique.Count; i++) g.Add((Unique[i].Ticks - Unique[i - 1].Ticks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            return g.Count == 0 ? 0 : Median(g);
        }
    }

    /// <summary>Self-check of the selection above with synthetic streams (no device): a 60 fps game, a 30 fps game
    /// captured at 60 and at 120 fps, a 45-in-60 (uneven) game, a static picture and a frozen-then-moving one.</summary>
    internal static string SelfCheckLiveFrames()
    {
        var sb = new System.Text.StringBuilder();
        long hz = System.Diagnostics.Stopwatch.Frequency;
        const int w = 96, h = 54;
        byte[] Pic(int step)
        {
            var p = new byte[w * h];
            int x0 = (step * 5) % (w - 12);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    p[y * w + x] = (byte)(((x + y) % 16) * 4 + (x >= x0 && x < x0 + 12 && y > 20 && y < 34 ? 150 : 0));
            return p;
        }
        bool allOk = true;
        void Run(string name, int captureFps, Func<int, int> gameStepOfCapture, int expectUniquePer100, bool expectTriples, bool expectNone = false)
        {
            var lf = new LiveFrames();
            int n = captureFps * 4, chunk = captureFps / 4;     // frames are read in 250 ms batches, like the real check
            int min = 0, picked = 0;
            bool evenAll = true;
            for (int k = 0; k < n; k++)
            {
                lf.Add(k + 1, (long)(k * hz / (double)captureFps), w, h, Pic(gameStepOfCapture(k)));
                if ((k + 1) % chunk == 0 && lf.TryPick(ref min, out var a, out var b, out var c))
                {
                    picked++;
                    evenAll &= LiveFrames.EvenlySpaced(a.Ticks, b.Ticks, c.Ticks) && a.Seq < b.Seq && b.Seq < c.Seq && a.Seq != b.Seq;
                }
            }
            var (win, even) = lf.CountWindows();
            double uniqueShare = 100.0 * lf.Unique.Count / n;
            bool ok = Math.Abs(uniqueShare - expectUniquePer100) <= 3 && (expectTriples ? picked >= 10 : picked == 0) && evenAll;
            allOk &= ok;
            sb.AppendLine($"{(ok ? "OK  " : "FAIL")} {name}: captured {lf.Captured}, unique {lf.Unique.Count} ({uniqueShare:0}%), repeats {lf.Repeats}, even windows {even}/{win}, triples picked {picked}, median gap {lf.MedianGapMs():0.0} ms");
        }
        Run("60 fps game at 60 fps capture", 60, k => k, 100, true);
        Run("30 fps game at 60 fps capture (30-in-60)", 60, k => k / 2, 50, true);
        Run("30 fps game at 120 fps capture", 120, k => k / 4, 25, true);
        Run("60 fps game at 120 fps capture", 120, k => k / 2, 50, true);
        // 40 fps in 60: new pictures alternate 1 and 2 captures apart (gaps 16.7 / 33.3 ms) -> never evenly spaced
        Run("40 fps game at 60 fps capture (uneven pacing)", 60, k => (k / 3) * 2 + Math.Min(k % 3, 1), 67, false);
        Run("static desktop", 60, k => 0, 0, false);
        sb.AppendLine(allOk ? "ALL OK" : "SOME FAILED");
        return sb.ToString();
    }

    public static double Median(IEnumerable<double> v)
    {
        double[] s = v.OrderBy(x => x).ToArray();
        if (s.Length == 0) return 0;
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}
