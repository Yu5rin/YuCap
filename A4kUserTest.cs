using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YuCap;

/// <summary>
/// 自分専用モデル in the test code: the user's model compared with the built-in ones on pictures it was NOT trained on, and the
/// equivalence of its exported shader with the PyTorch reference.
/// <list type="bullet">
/// <item><see cref="Validation"/>: the held-out frames listed in <c>models\&lt;name&gt;.json</c> (<c>val_frames</c>, written by tools/train_upscaler.py;
/// training never saw them) are shrunk to half size (box filter, like a capture of a half-size source), enlarged x2 by the real GPU pipeline
/// with each method, and compared with the original frame: PSNR, SSIM, edge strength against the original, ringing.</item>
/// <item><see cref="DumpRaw"/>: the raw float output of one network (built-in letter or a model file) for one picture, for the comparison with PyTorch.</item>
/// </list>
/// </summary>
internal static class A4kUserTest
{
    public sealed class ValRow
    {
        public string Name = "";
        public double Psnr, Ssim, Grad, RingPct;
        public int Frames;
    }

    /// <summary>The paths of the held-out frames of a model (from its .json), existing files only.</summary>
    public static List<string> ValFrames(string modelPath)
    {
        string meta = Path.ChangeExtension(modelPath, ".json");
        var list = new List<string>();
        try
        {
            if (!File.Exists(meta)) return list;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(meta, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("val_frames", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in arr.EnumerateArray())
                    if (e.GetString() is { } p && File.Exists(p)) list.Add(p);
        }
        catch (Exception ex) { Log.Info("user model test: " + ex.Message.Split('\n')[0]); }
        return list;
    }

    private static Bitmap Load32(string path)
    {
        using var raw = new Bitmap(path);
        int w = raw.Width / 2 * 2, h = raw.Height / 2 * 2;
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(b);
        g.DrawImage(raw, new Rectangle(0, 0, w, h), new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
        return b;
    }

    /// <summary>Held-out frames -> half size -> x2 with each method -> compared with the original. <paramref name="render"/> is the real GPU pipeline
    /// (<c>RenderFromBitmap</c>); the user model is selected through <see cref="D3DRenderer.SetA4kCustom"/> for its row only.</summary>
    public static List<ValRow> Validation(NativeResTest.RenderFn render, string modelPath, int maxFrames, StringBuilder? log)
    {
        string? keep = D3DRenderer.A4kCustomPath;
        var rows = new Dictionary<string, ValRow>();
        var order = new List<string>();
        List<string> frames = ValFrames(modelPath).Take(maxFrames).ToList();
        try
        {
            foreach (string f in frames)
            {
                using Bitmap hr = Load32(f);
                int w = hr.Width, h = hr.Height;
                using Bitmap lr = NativeResTest.Resample(hr, w / 2, h / 2, NativeResTest.Kernel.Box);
                float[] truth = NativeResTest.Luma(hr);
                double tg = ResEstimate.MeanGradient(truth, w, h);
                foreach (var (name, q, custom) in new[]
                {
                    ("標準（バイリニア）", ScalingQuality.Standard, false), ("FSR", ScalingQuality.Fsr, false),
                    ("くっきり（Anime4K）", ScalingQuality.Anime4K, false), ("自分専用モデル", ScalingQuality.Anime4K, true),
                })
                {
                    D3DRenderer.SetA4kCustom(custom ? modelPath : null);
                    using Bitmap? img = render(lr, new Size(w, h), q, 0, -1);
                    if (img == null || img.Size != new Size(w, h)) continue;
                    float[] l = NativeResTest.Luma(img);
                    (double rp, _) = NativeResTest.Ringing(l, truth, w, h);
                    if (!rows.TryGetValue(name, out ValRow? r)) { rows[name] = r = new ValRow { Name = name }; order.Add(name); }
                    r.Psnr += NativeResTest.Psnr(l, truth); r.Ssim += NativeResTest.Ssim(l, truth, w, h);
                    r.Grad += ResEstimate.MeanGradient(l, w, h) / Math.Max(1e-9, tg); r.RingPct += rp; r.Frames++;
                }
                log?.AppendLine($"  {Path.GetFileName(f)} ({w}x{h}) done");
            }
        }
        finally { D3DRenderer.SetA4kCustom(keep); }
        var res = new List<ValRow>();
        foreach (string n in order)
        {
            ValRow r = rows[n];
            if (r.Frames == 0) continue;
            r.Psnr /= r.Frames; r.Ssim /= r.Frames; r.Grad /= r.Frames; r.RingPct /= r.Frames;
            res.Add(r);
        }
        return res;
    }

    public static string RowText(ValRow r) =>
        $"PSNR {r.Psnr:0.00} dB / SSIM {r.Ssim:0.0000} / 輪郭の強さ {r.Grad:0.000}×（元の絵=1） / 輪の出た画素 {r.RingPct:0.00}%（{r.Frames} 枚の平均）";

    /// <summary>The user model beats the built-in network on held-out frames: higher SSIM and higher PSNR.</summary>
    public static bool Better(List<ValRow> rows, out ValRow? user, out ValRow? builtIn)
    {
        user = rows.FirstOrDefault(r => r.Name == "自分専用モデル"); builtIn = rows.FirstOrDefault(r => r.Name == "くっきり（Anime4K）");
        return user != null && builtIn != null && user.Ssim > builtIn.Ssim + 0.0005 && user.Psnr > builtIn.Psnr + 0.05;
    }

    /// <summary>Raw (unclamped, float32 intermediates) x2 output of <paramref name="net"/> ("S", "M", "L" or the path of a model file) for the picture
    /// <paramref name="png"/>, written as little-endian float32 RGB (H*W*3 of the doubled picture) to <paramref name="outFile"/>. Needs the GPU only.</summary>
    public static string DumpRaw(string net, string png, string outFile, bool half = false)
    {
        D3DRenderer? r = null;
        bool keepHalf = D3DRenderer.A4kHalf; char keepV = D3DRenderer.A4kVariantLetter; string? keepC = D3DRenderer.A4kCustomPath;
        try
        {
            r = new D3DRenderer();
            r.CreateDevice();
            r.CreateShaders();
            D3DRenderer.A4kHalf = half;     // false: float32 intermediates (exactness); true: what the viewer runs
            if (net.Length == 1) { D3DRenderer.SetA4kCustom(null); D3DRenderer.A4kVariantLetter = char.ToUpperInvariant(net[0]); }
            else D3DRenderer.SetA4kCustom(Path.GetFullPath(net));
            using Bitmap src = Load32(png);
            float[]? raw = r.A4kReadRaw(src, 1, out Size sz);
            if (raw == null) return "GPU run failed";
            var rgb = new float[sz.Width * sz.Height * 3];
            for (int i = 0; i < sz.Width * sz.Height; i++) { rgb[i * 3] = raw[i * 4]; rgb[i * 3 + 1] = raw[i * 4 + 1]; rgb[i * 3 + 2] = raw[i * 4 + 2]; }
            var bytes = new byte[rgb.Length * 4];
            Buffer.BlockCopy(rgb, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(outFile, bytes);
            return $"wrote {outFile}: {sz.Width}x{sz.Height} float32 RGB";
        }
        catch (Exception ex) { return "FAILED: " + ex; }
        finally
        {
            D3DRenderer.A4kHalf = keepHalf; D3DRenderer.A4kVariantLetter = keepV; D3DRenderer.SetA4kCustom(keepC);
            try { r?.DisposeAll(true); } catch { }
        }
    }
}
