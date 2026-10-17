using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YuCap;

/// <summary>
/// Anime4K checks: (1) a small CPU reference that interprets the vendored GLSL directly (not the generated HLSL) and the
/// equivalence test against the GPU port, (2) the comparison tables shared by the test mode and the hidden CLI
/// <c>--a4k-check</c> (synthetic ground truth, real pictures, GPU time).
/// </summary>
internal static class A4kTest
{
    // =====================================================================================
    //  CPU reference: runs the mpv hook passes of Anime4K_Upscale_CNN_x2_<V>.glsl as written
    // =====================================================================================

    private sealed class Term { public string Tex = ""; public int Mode; public int Dx, Dy; public float[] M = new float[16]; }
    private sealed class GPass
    {
        public List<string> Binds = new();
        public string Save = "";
        public List<Term> Terms = new();
        public float[] Bias = new float[4];
        public bool D2S;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static List<GPass> ParseGlsl(string text)
    {
        var list = new List<GPass>();
        string[] blocks = Regex.Split(text, @"(?m)^//!DESC ").Skip(1).ToArray();
        foreach (string b in blocks)
        {
            var p = new GPass { D2S = b.Split('\n')[0].Contains("Depth-to-Space") };
            foreach (Match m in Regex.Matches(b, @"(?m)^//!BIND (\w+)")) p.Binds.Add(m.Groups[1].Value);
            p.Save = Regex.Match(b, @"(?m)^//!SAVE (\w+)").Groups[1].Value;
            var defs = new Dictionary<string, (string Tex, int Mode)>();
            foreach (Match m in Regex.Matches(b, @"(?m)^#define ((?:go|g)_\d+)(?:\(x_off, y_off\))? \((.*)\)\s*$"))
            {
                string body = m.Groups[2].Value;
                Match mm = Regex.Match(body, @"^max\((-?)\((\w+?)_tex(?:Off\(vec2\(x_off, y_off\)\)|\(\w+_pos\))\), 0\.0\)$");
                if (mm.Success) { defs[m.Groups[1].Value] = (mm.Groups[2].Value, mm.Groups[1].Value == "-" ? -1 : 1); continue; }
                mm = Regex.Match(body, @"^\(?(\w+?)_texOff\(vec2\(x_off, y_off\)\)\)?$");
                if (mm.Success) { defs[m.Groups[1].Value] = (mm.Groups[1].Value, 0); continue; }
                throw new InvalidDataException("unparsed define " + m.Value);
            }
            foreach (Match m in Regex.Matches(b, @"(?m)^\s*(?:vec4 )?result \+?= (mat4|vec4)\(([^)]*)\)(?: \* ((?:go|g)_\d+)(?:\(([-\d.]+), ([-\d.]+)\))?)?;"))
            {
                float[] nums = m.Groups[2].Value.Split(',').Select(x => float.Parse(x.Trim(), Inv)).ToArray();
                if (m.Groups[1].Value == "vec4") { p.Bias = nums; continue; }
                var (tex, mode) = defs[m.Groups[3].Value];
                p.Terms.Add(new Term
                {
                    Tex = tex, Mode = mode, M = nums,
                    Dx = m.Groups[4].Success ? (int)float.Parse(m.Groups[4].Value, Inv) : 0,
                    Dy = m.Groups[5].Success ? (int)float.Parse(m.Groups[5].Value, Inv) : 0,
                });
            }
            list.Add(p);
        }
        return list;
    }

    /// <summary>Run the network on <paramref name="rgb"/> (w*h*3, 0..1) as the mpv hook would: clamp-to-edge reads, CReLU, depth-to-space,
    /// bilinear base plus residual. Returns (2w)*(2h)*3 floats (not clamped).</summary>
    public static float[] Reference(string glslPath, float[] rgb, int w, int h)
    {
        List<GPass> passes = ParseGlsl(File.ReadAllText(glslPath));
        var tex = new Dictionary<string, float[]>();
        var main = new float[w * h * 4];
        for (int i = 0; i < w * h; i++) { main[i * 4] = rgb[i * 3]; main[i * 4 + 1] = rgb[i * 3 + 1]; main[i * 4 + 2] = rgb[i * 3 + 2]; main[i * 4 + 3] = 1f; }
        tex["MAIN"] = main;
        float[] result = Array.Empty<float>();
        foreach (GPass p in passes)
        {
            if (!p.D2S)
            {
                var o = new float[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float r0 = p.Bias[0], r1 = p.Bias[1], r2 = p.Bias[2], r3 = p.Bias[3];
                        foreach (Term t in p.Terms)
                        {
                            float[] src = tex[t.Tex];
                            int sx = Math.Clamp(x + t.Dx, 0, w - 1), sy = Math.Clamp(y + t.Dy, 0, h - 1);
                            int si = (sy * w + sx) * 4;
                            for (int j = 0; j < 4; j++)
                            {
                                float v = src[si + j];
                                if (t.Mode == 1) v = Math.Max(v, 0f); else if (t.Mode == -1) v = Math.Max(-v, 0f);
                                r0 += t.M[4 * j] * v; r1 += t.M[4 * j + 1] * v; r2 += t.M[4 * j + 2] * v; r3 += t.M[4 * j + 3] * v;
                            }
                        }
                        int oi = (y * w + x) * 4;
                        o[oi] = r0; o[oi + 1] = r1; o[oi + 2] = r2; o[oi + 3] = r3;
                    }
                tex[p.Save] = o;
            }
            else
            {
                int ow = w * 2, oh = h * 2;
                result = new float[ow * oh * 3];
                List<string> outs = p.Binds.Where(n => n != "MAIN").ToList();
                for (int Y = 0; Y < oh; Y++)
                    for (int X = 0; X < ow; X++)
                    {
                        double px = (X + 0.5) / ow, py = (Y + 0.5) / oh;
                        double fx = px * w - Math.Floor(px * w), fy = py * h - Math.Floor(py * h);
                        int ix = (int)(fx * 2), iy = (int)(fy * 2);
                        int sx = (int)Math.Floor(px * w), sy = (int)Math.Floor(py * h);
                        float[] c = new float[3];
                        for (int k = 0; k < 3; k++)
                        {
                            float[] t = tex[outs[Math.Min(k, outs.Count - 1)]];
                            c[k] = t[(sy * w + sx) * 4 + iy * 2 + ix];
                        }
                        // MAIN_tex(MAIN_pos): bilinear, texel centres at (i + 0.5) / w, edges clamped
                        double u = px * w - 0.5, v = py * h - 0.5;
                        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
                        double ax = u - x0, ay = v - y0;
                        for (int k = 0; k < 3; k++)
                        {
                            double Get(int xx, int yy) => main[(Math.Clamp(yy, 0, h - 1) * w + Math.Clamp(xx, 0, w - 1)) * 4 + k];
                            double bil = (Get(x0, y0) * (1 - ax) + Get(x0 + 1, y0) * ax) * (1 - ay) + (Get(x0, y0 + 1) * (1 - ax) + Get(x0 + 1, y0 + 1) * ax) * ay;
                            result[(Y * ow + X) * 3 + k] = (float)(bil + c[k]);
                        }
                    }
            }
        }
        return result;
    }

    public static string? FindGlsl(char variant)
    {
        string name = $"Anime4K_Upscale_CNN_x2_{variant}.glsl";
        string? env = Environment.GetEnvironmentVariable("YUCAP_A4K_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, name))) return Path.Combine(env, name);
        for (string? d = AppContext.BaseDirectory; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            string f = Path.Combine(d, "third_party", "Anime4K", name);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Cut a <paramref name="n"/>x<paramref name="n"/> block out of a picture (as 8-bit, like the GPU input).</summary>
    public static Bitmap Crop(Bitmap src, int x, int y, int n)
    {
        var b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b)) g.DrawImage(src, new Rectangle(0, 0, n, n), new Rectangle(x, y, n, n), GraphicsUnit.Pixel);
        return b;
    }

    private static float[] RgbOf(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var res = new float[w * h * 3];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    res[(y * w + x) * 3] = row[x * 4 + 2] / 255f; res[(y * w + x) * 3 + 1] = row[x * 4 + 1] / 255f; res[(y * w + x) * 3 + 2] = row[x * 4] / 255f;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return res;
    }

    public sealed record EquivResult(char Variant, int Stages, string Picture, double MaxDiff, double MeanDiff, double MaxRef, bool Half);

    /// <summary>GPU port against the CPU reference on <paramref name="crop"/> (RGB, GPU result compared before any clamping).</summary>
    internal static EquivResult? Equivalence(D3DRenderer r, char variant, Bitmap crop, string label, int stages, bool half)
    {
        string? glsl = FindGlsl(variant);
        if (glsl == null) return null;
        bool keepHalf = D3DRenderer.A4kHalf; char keepV = D3DRenderer.A4kVariantLetter;
        D3DRenderer.A4kHalf = half; D3DRenderer.A4kVariantLetter = variant;
        try
        {
            float[]? gpu = r.A4kReadRaw(crop, stages, out Size sz);
            if (gpu == null) return null;
            float[] cur = RgbOf(crop);
            int w = crop.Width, h = crop.Height;
            for (int st = 0; st < stages; st++) { cur = Reference(glsl, cur, w, h); w *= 2; h *= 2; }
            if (sz.Width != w || sz.Height != h) return null;
            double max = 0, sum = 0, mref = 0;
            for (int i = 0; i < w * h; i++)
                for (int k = 0; k < 3; k++)
                {
                    double d = Math.Abs(gpu[i * 4 + k] - cur[i * 3 + k]);
                    max = Math.Max(max, d); sum += d; mref = Math.Max(mref, Math.Abs(cur[i * 3 + k]));
                }
            return new EquivResult(variant, stages, label, max, sum / (w * h * 3), mref, half);
        }
        finally { D3DRenderer.A4kHalf = keepHalf; D3DRenderer.A4kVariantLetter = keepV; }
    }

    // =====================================================================================
    //  Comparison metrics
    // =====================================================================================

    internal sealed class MethodRow
    {
        public string Name = "";
        public double Psnr, Ssim, GradRatio, EdgeWidth, StepOvershoot, RingPct, RingExcess;
        public Bitmap? Image;
    }

    internal static MethodRow Evaluate(string name, Bitmap img, float[] truthLuma, double truthGrad)
    {
        float[] l = NativeResTest.Luma(img);
        NativeResTest.EdgeProfile ep = NativeResTest.MeasureStep(l, 3840, 2160);
        (double rp, double rx) = NativeResTest.Ringing(l, truthLuma, 3840, 2160);
        return new MethodRow
        {
            Name = name, Image = img,
            Psnr = NativeResTest.Psnr(l, truthLuma), Ssim = NativeResTest.Ssim(l, truthLuma, 3840, 2160),
            GradRatio = ResEstimate.MeanGradient(l, 3840, 2160) / truthGrad,
            EdgeWidth = ep.Width, StepOvershoot = ep.Overshoot, RingPct = rp, RingExcess = rx,
        };
    }

    public static string RowText(MethodRow r) =>
        $"PSNR {r.Psnr:0.00} dB / SSIM {r.Ssim:0.0000} / 輪郭の強さ {r.GradRatio:0.000}×（正解=1） / 輪郭の幅 {r.EdgeWidth:0.00}px / 行き過ぎ {r.StepOvershoot:0.0}% / 輪の出た画素 {r.RingPct:0.00}%";

    /// <summary>Synthetic ground truth: 4K truth -> 720p game -> 1080p console (<paramref name="upscaler"/>) -> 4K by each method.
    /// The caller disposes the rows' images.</summary>
    internal static List<MethodRow> Synthetic(NativeResTest.RenderFn render, Bitmap truth, Bitmap? console, NativeResTest.Kernel upscaler, out Bitmap console1080, bool withIdeal = true)
    {
        using Bitmap game = NativeResTest.Resample(truth, 1280, 720, NativeResTest.Kernel.Box);
        console1080 = console != null ? (Bitmap)console.Clone() : NativeResTest.Resample(game, 1920, 1080, upscaler);
        var out4k = new Size(3840, 2160);
        float[] tl = NativeResTest.Luma(truth);
        double tg = ResEstimate.MeanGradient(tl, 3840, 2160);
        var rows = new List<MethodRow>();
        (string, Bitmap?, ScalingQuality, int)[] specs =
        {
            ("標準（バイリニア）", console1080, ScalingQuality.Standard, 0),
            ("FSR", console1080, ScalingQuality.Fsr, 0),
            ("元の細かさ + FSR", console1080, ScalingQuality.Fsr, 720),
            ("くっきり（Anime4K）", console1080, ScalingQuality.Anime4K, 0),
            ("元の細かさ + くっきり", console1080, ScalingQuality.Anime4K, 720),
            ("【参考】本物の 720p から FSR", withIdeal ? game : null, ScalingQuality.Fsr, 0),
            ("【参考】本物の 720p から くっきり", withIdeal ? game : null, ScalingQuality.Anime4K, 0),
        };
        foreach (var (name, src, q, nh) in specs)
        {
            if (src == null) continue;
            Bitmap? img = render(src, out4k, q, nh, -1);
            if (img == null) continue;
            rows.Add(Evaluate(name, img, tl, tg));
        }
        return rows;
    }

    /// <summary>The method with the highest SSIM among the real ones (not the 【参考】 rows); ties within 0.001 go to fewer ringing pixels.</summary>
    internal static MethodRow? Winner(List<MethodRow> rows)
    {
        MethodRow? best = null;
        foreach (MethodRow r in rows.Where(r => !r.Name.StartsWith("【参考】")))
            if (best == null || r.Ssim > best.Ssim + 0.001 || (Math.Abs(r.Ssim - best.Ssim) <= 0.001 && r.RingPct < best.RingPct)) best = r;
        return best;
    }

    // ---- real pictures ----

    internal sealed class LiveRow
    {
        public string Name = "";
        public double Grad, RingPct, RingExcess;
        public Bitmap? Image;
    }

    /// <summary>One real picture through each method at <paramref name="output"/>; edge strength and ringing (against the bilinear enlargement).</summary>
    internal static List<LiveRow> Live(NativeResTest.RenderFn render, Bitmap source, Size output, int estV)
    {
        var rows = new List<LiveRow>();
        var specs = new List<(string, ScalingQuality, int)>
        {
            ("標準", ScalingQuality.Standard, 0), ("FSR", ScalingQuality.Fsr, 0), ("くっきり", ScalingQuality.Anime4K, 0),
        };
        if (estV > 0 && estV < source.Height)
        {
            specs.Add(($"{ResEstimate.Name(estV)} に縮めて FSR", ScalingQuality.Fsr, estV));
            specs.Add(($"{ResEstimate.Name(estV)} に縮めて くっきり", ScalingQuality.Anime4K, estV));
        }
        float[]? plain = null;
        foreach (var (name, q, nh) in specs)
        {
            Bitmap? img = render(source, output, q, nh, -1);
            if (img == null) continue;
            float[] l = NativeResTest.Luma(img);
            plain ??= l;
            (double rp, double rx) = NativeResTest.Ringing(l, plain, output.Width, output.Height);
            rows.Add(new LiveRow { Name = name, Image = img, Grad = ResEstimate.MeanGradient(l, output.Width, output.Height), RingPct = rp, RingExcess = rx });
        }
        return rows;
    }

    // =====================================================================================
    //  Headless CLI: --a4k-check <out> [--frames <dir>]
    // =====================================================================================

    public static string HeadlessCheck(string outDir, string? framesDir)
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
            sb.AppendLine("Anime4K v4.0.1 (4029bf7), GLSL passes of Anime4K_Upscale_CNN_x2_{S,M,L}");
            NativeResTest.RenderFn render = (src, o, q, nh, f) => r.RenderFromBitmap(src, o, q, 0.5f, nh, f);

            // ---------------- 1. equivalence ----------------
            sb.AppendLine();
            sb.AppendLine("=== 1. equivalence: GPU port vs CPU reference that interprets the original GLSL (64x64 crop -> 128x128; max |GPU - CPU| over RGB) ===");
            using Bitmap truth = NativeResTest.MakeTruth();
            using Bitmap game = NativeResTest.Resample(truth, 1280, 720, NativeResTest.Kernel.Box);
            using Bitmap console = NativeResTest.Resample(game, 1920, 1080, NativeResTest.Kernel.Bilinear);
            var crops = new List<(string, Bitmap)>
            {
                ("synthetic text+lines (console 1080p)", Crop(console, 70, 60, 64)),
                ("synthetic step/stripes (console 1080p)", Crop(console, 800, 400, 64)),
            };
            string? firstReal = framesDir != null && Directory.Exists(framesDir) ? Directory.GetFiles(framesDir, "t*_f0.png").OrderBy(x => x).FirstOrDefault() : null;
            Bitmap? real = null;
            if (firstReal != null)
            {
                real = new Bitmap(firstReal);
                using var rb = new Bitmap(real.Width, real.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(rb)) g.DrawImage(real, 0, 0, real.Width, real.Height);
                var es = TestProbes.MeasureEdges(rb);
                Rectangle t = TestProbes.BestTile(es, rb.Size) ?? new Rectangle(800, 400, 480, 270);
                crops.Add(($"real game frame {Path.GetFileName(firstReal)} (most detailed tile)", Crop(rb, Math.Clamp(t.X + 100, 0, rb.Width - 64), Math.Clamp(t.Y + 60, 0, rb.Height - 64), 64)));
            }
            double worst32 = 0;
            foreach (char v in "SML")
                foreach (var (label, crop) in crops)
                    foreach (bool half in new[] { false, true })
                    {
                        EquivResult? e = Equivalence(r, v, crop, label, 1, half);
                        if (e == null) { sb.AppendLine($"  {v} {label}: not run (GLSL file or GPU unavailable)"); continue; }
                        if (!half) worst32 = Math.Max(worst32, e.MaxDiff);
                        sb.AppendLine($"  {v} / {(half ? "float16" : "float32")} intermediates / {label}: max diff {e.MaxDiff:0.0e+0}, mean diff {e.MeanDiff:0.0e+0} (largest value {e.MaxRef:0.00})");
                    }
            using (Bitmap c32 = Crop(console, 70, 60, 32))
                foreach (bool half in new[] { false, true })
                {
                    EquivResult? e = Equivalence(r, 'M', c32, "synthetic text+lines 32x32", 2, half);
                    if (e != null) sb.AppendLine($"  M / {(half ? "float16" : "float32")} / two stages (32x32 -> 128x128) : max diff {e.MaxDiff:0.0e+0}, mean {e.MeanDiff:0.0e+0}");
                }
            sb.AppendLine($"  -> float32 worst case over all {(worst32 < 1e-3 ? "PASS" : "FAIL")} (limit 1e-3): {worst32:0.0e+0}");
            foreach (var (_, c) in crops) c.Dispose();
            real?.Dispose();

            // ---------------- 2. synthetic comparison ----------------
            foreach (char v in "SML")
            {
                D3DRenderer.A4kVariantLetter = v;
                foreach (var (up, nm) in new[] { (NativeResTest.Kernel.Bilinear, "bilinear"), (NativeResTest.Kernel.CatmullRom, "bicubic") })
                {
                    if (v != 'M' && up != NativeResTest.Kernel.Bilinear) continue;
                    sb.AppendLine();
                    sb.AppendLine($"=== 2. synthetic truth: 4K truth -> 720p -> 1080p console ({nm}) -> 4K; Anime4K variant {v} ===");
                    var rows = Synthetic(render, truth, null, up, out Bitmap con, v == 'M');
                    foreach (MethodRow row in rows) sb.AppendLine($"  {row.Name,-34}: {RowText(row)}");
                    if (v == 'M' && up == NativeResTest.Kernel.Bilinear)
                    {
                        var (t1, t2) = NativeResTest.Tiles(truth);
                        SaveStrip(rows, truth, t1, Path.Combine(outDir, "a4k_synth_text.png"));
                        SaveStrip(rows, truth, t2, Path.Combine(outDir, "a4k_synth_edges.png"));
                    }
                    MethodRow? w = Winner(rows);
                    sb.AppendLine($"  -> best by SSIM: {w?.Name}");
                    foreach (MethodRow row in rows) row.Image?.Dispose();
                    con.Dispose();
                }
            }
            D3DRenderer.A4kVariantLetter = 'M';

            // ---------------- 2b. final-stage options (variant M) ----------------
            sb.AppendLine();
            sb.AppendLine("=== 2b. options after the CNN (variant M, bilinear console) ===");
            {
                float[] tl = NativeResTest.Luma(truth);
                double tg = ResEstimate.MeanGradient(tl, 3840, 2160);
                void Opt(string name, Func<Bitmap?> f)
                {
                    using Bitmap? img = f();
                    if (img == null) { sb.AppendLine($"  {name}: n/a"); return; }
                    MethodRow row = Evaluate(name, img, tl, tg); row.Image = null;
                    sb.AppendLine($"  {name,-60}: {RowText(row)}");
                }
                foreach (float sh in new[] { 0f, 0.5f, 1f })
                    Opt($"1080p -> CNN x2 (exact 4K) -> RCAS sharpness {sh:0.0}", () => r.RenderFromBitmap(console, new Size(3840, 2160), ScalingQuality.Anime4K, sh, 0, -1));
                foreach (bool easu in new[] { false, true })
                {
                    D3DRenderer.A4kUseEasu = easu;
                    D3DRenderer.A4kBudgetMs = 3.0;
                    Opt($"720p-detail (prescale) -> CNN x1 -> {(easu ? "EASU" : "bilinear")} + RCAS 0.5", () => { D3DRenderer.A4kSecondStageRatio = 99; try { return r.RenderFromBitmap(console, new Size(3840, 2160), ScalingQuality.Anime4K, 0.5f, 720, -1); } finally { D3DRenderer.A4kSecondStageRatio = 2.99; } });
                }
                D3DRenderer.A4kUseEasu = false;
                D3DRenderer.A4kBudgetMs = 99;
                Opt("720p-detail (prescale) -> CNN x2 stages (2.99x rule, budget lifted) -> bilinear + RCAS 0.5", () => r.RenderFromBitmap(console, new Size(3840, 2160), ScalingQuality.Anime4K, 0.5f, 720, -1));
                D3DRenderer.A4kBudgetMs = 3.0;
                // smaller screen: 1080p -> 1440p (CNN to 2160 then down)
                using Bitmap truth1440 = NativeResTest.Resample(truth, 2560, 1440, NativeResTest.Kernel.Box);
                float[] t14 = NativeResTest.Luma(truth1440);
                sb.AppendLine("  1440p screen (truth = the 4K truth shrunk to 2560x1440), SSIM/PSNR:");
                foreach (var (name, q) in new[] { ("標準", ScalingQuality.Standard), ("FSR", ScalingQuality.Fsr), ("くっきり", ScalingQuality.Anime4K) })
                    foreach (bool easu in q == ScalingQuality.Anime4K ? new[] { false, true } : new[] { false })
                    {
                        D3DRenderer.A4kUseEasu = easu;
                        using Bitmap? img = r.RenderFromBitmap(console, new Size(2560, 1440), q, 0.5f, 0, -1);
                        if (img == null) continue;
                        float[] l = NativeResTest.Luma(img);
                        sb.AppendLine($"    {name}{(q == ScalingQuality.Anime4K ? (easu ? " (EASU)" : " (bilinear down)") : "")}: SSIM {NativeResTest.Ssim(l, t14, 2560, 1440):0.0000}, PSNR {NativeResTest.Psnr(l, t14):0.00} dB");
                    }
                D3DRenderer.A4kUseEasu = true;
            }

            // ---------------- 3. real frames ----------------
            if (framesDir != null && Directory.Exists(framesDir))
            {
                sb.AppendLine();
                sb.AppendLine("=== 3. real game frames (1080p capture) -> 3840x2160: edge strength (mean gradient) and ringing (vs bilinear) ===");
                string[] files = Directory.GetFiles(framesDir, "t*_f0.png").OrderBy(x => x).Take(6).ToArray();
                var gsum = new Dictionary<string, (double G, double R, int N)>();
                int idx = 0;
                foreach (string f in files)
                {
                    using var raw = new Bitmap(f);
                    using var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                    ResEstimate.FrameResult fr = ResEstimate.Analyze(NativeResTest.Luma(bmp), bmp.Width, bmp.Height);
                    int estV = Math.Min(fr.Vertical.EstimateHeight, bmp.Height);
                    sb.AppendLine($"  {Path.GetFileName(f)}: estimated detail {ResEstimate.Name(estV)}");
                    List<LiveRow> rows = Live(render, bmp, new Size(3840, 2160), estV);
                    double g0 = rows.Count > 0 ? rows[0].Grad : 1;
                    foreach (LiveRow row in rows)
                    {
                        sb.AppendLine($"    {row.Name,-24}: edge strength {row.Grad:0.000} ({row.Grad / Math.Max(1e-9, g0):0.00}x bilinear), ring pixels {row.RingPct:0.00}% (mean excess {row.RingExcess:0.0})");
                        gsum.TryGetValue(row.Name.Contains("に縮めて") ? row.Name.Replace(ResEstimate.Name(estV), "N") : row.Name, out var cur);
                        gsum[row.Name.Contains("に縮めて") ? row.Name.Replace(ResEstimate.Name(estV), "N") : row.Name] = (cur.G + row.Grad / Math.Max(1e-9, g0), cur.R + row.RingPct, cur.N + 1);
                    }
                    if (idx++ == 0 && rows.Count >= 3)
                    {
                        Rectangle? tile = TestProbes.BestTile(TestProbes.MeasureEdges(rows[2].Image!), new Size(3840, 2160));
                        if (tile is { } t) TestProbes.SaveStrip(rows.Select(x => x.Image!).ToArray(), rows.Select(x => x.Name.Replace("標準", "standard").Replace("くっきり", "crisp").Replace("に縮めて", "+").Replace("相当", "")).ToArray(), t, Path.Combine(outDir, "a4k_live.png"));
                    }
                    foreach (LiveRow row in rows) row.Image?.Dispose();
                }
                sb.AppendLine("  average over the frames:");
                foreach (var kv in gsum) sb.AppendLine($"    {kv.Key,-24}: edge strength {kv.Value.G / kv.Value.N:0.00}x bilinear, ring pixels {kv.Value.R / kv.Value.N:0.00}%");

                // ---------------- 4. GPU time (real 1080p frame + the triple for interpolation) ----------------
                string? f0 = files.FirstOrDefault();
                string f1 = f0 != null ? f0.Replace("_f0.png", "_f1.png") : "";
                if (f0 != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== 4. GPU time per presented frame, 1080p -> 3840x2160 (convert not included) ===");
                    using var a0 = new Bitmap(f0);
                    using var a = new Bitmap(a0.Width, a0.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(a)) g.DrawImage(a0, 0, 0, a0.Width, a0.Height);
                    Bitmap? b = null;
                    if (File.Exists(f1))
                    {
                        using var b0 = new Bitmap(f1);
                        b = new Bitmap(b0.Width, b0.Height, PixelFormat.Format32bppArgb);
                        using Graphics g = Graphics.FromImage(b); g.DrawImage(b0, 0, 0, b0.Width, b0.Height);
                    }
                    var o4k = new Size(3840, 2160);
                    string Avg(double[] v) => v.Length == 0 ? "n/a" : $"avg {v.Average():0.00} ms, max {v.Max():0.00} ms -> sustains {1000.0 / Math.Max(0.001, v.Average()):0} fps";
                    foreach (var (name, q, nh, ch) in new[]
                    {
                        ("標準", ScalingQuality.Standard, 0, 'M'), ("FSR", ScalingQuality.Fsr, 0, 'M'),
                        ("くっきり S", ScalingQuality.Anime4K, 0, 'S'), ("くっきり M", ScalingQuality.Anime4K, 0, 'M'), ("くっきり L", ScalingQuality.Anime4K, 0, 'L'),
                        ("元の細かさ + くっきり M (720p)", ScalingQuality.Anime4K, 720, 'M'),
                    })
                    {
                        D3DRenderer.A4kVariantLetter = ch;
                        double[] fresh = r.QualityBench(a, null, o4k, q, 0.5f, nh, 300, false);
                        double[] rep = q == ScalingQuality.Anime4K ? r.QualityBench(a, null, o4k, q, 0.5f, nh, 300, true) : fresh;
                        sb.AppendLine($"  {name,-32} new frame : {Avg(fresh)}");
                        if (q == ScalingQuality.Anime4K) sb.AppendLine($"  {"",-32} same frame: {Avg(rep)} (CNN result reused)");
                        if (b != null)
                        {
                            double[] ip = r.QualityBench(a, b, o4k, q, 0.5f, nh, 300, false);
                            sb.AppendLine($"  {"",-32} interpolation on, every refresh a new synthesised picture: {Avg(ip)}");
                        }
                    }
                    D3DRenderer.A4kVariantLetter = 'M';
                    b?.Dispose();
                    sb.AppendLine("  (144 Hz = 6.94 ms per refresh; 60 fps source = 16.7 ms per source frame)");
                }
            }
            else sb.AppendLine("\n(no --frames directory: real-picture comparison and GPU timing skipped)");
        }
        catch (Exception ex) { sb.AppendLine("FAILED: " + ex); }
        finally { try { r?.DisposeAll(true); } catch { } }
        return sb.ToString();
    }

    private static void SaveStrip(List<MethodRow> rows, Bitmap truth, Rectangle tile, string path)
    {
        var panels = new List<Bitmap>(); var labels = new List<string>();
        foreach (var (name, key) in new[] { ("FSR", "FSR"), ("720p+FSR", "元の細かさ + FSR"), ("Crisp", "くっきり（Anime4K）"), ("720p+Crisp", "元の細かさ + くっきり") })
        {
            MethodRow? m = rows.FirstOrDefault(x => x.Name == key);
            if (m?.Image != null) { panels.Add(m.Image); labels.Add(name); }
        }
        panels.Add(truth); labels.Add("truth 4K");
        TestProbes.SaveStrip(panels.ToArray(), labels.ToArray(), tile, path);
    }
}
