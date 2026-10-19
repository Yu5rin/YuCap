using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: 音ズレの自動補正 / 暗部補正  (section 7, 機能). Wording is Japanese only (TestReport.cs).
// =============================================================================================

public sealed partial class MainForm
{
    // ------------------------------------------------------------------ 音ズレの自動補正

    private async Task FeatureAvAuto(TestSection sec)
    {
        var item = new TestItem { Name = "音ズレの自動補正" };
        if (!_video.IsRunning || !_video.SupportsVideoDelay || !_audio.IsRunning)
        {
            item.Verdict = Verdict.Info;
            item.Summary = !_audio.IsRunning ? "音声が動いていないため、確認できませんでした" : "Direct3D の映像が動いていないため、確認できませんでした";
            item.Explanation = "自動補正は、映像の遅れ（Direct3D 描画のときだけ測れます）と音の遅れの差を見て合わせます。映像と音声の両方が動いているときに確認できます。";
            sec.Items.Add(item);
            return;
        }
        if (_testCaption != null) _testCaption.Showing = false;   // something on top changes how the screen time is read (see LatencyStep)
        try
        {
            // before: no correction, 3 s of the in-app difference
            _settings.AvAuto = false; _settings.AvOffsetMs = 0;
            _avCtl.SetAuto(0);
            ApplyAvSync();
            await Wait(Math.Max(SettleMs, 2500));
            double before = await AvDiffMedian(3000);

            // run auto
            _settings.AvAuto = true; _settings.AvOffsetMs = 0;
            AvAutoChanged(true);
            _avCtl.ResetStats();
            long under0 = _audio.Underruns;
            var sw = Stopwatch.StartNew();
            double convergedAt = double.NaN;
            var stepTimes = new List<double>(); var dropTimes = new List<double>();
            int lastSteps = 0; long lastUnder = under0;
            int limitMs = _tQuick ? 35000 : 45000;
            while (sw.ElapsedMilliseconds < limitMs)
            {
                await Wait(250);
                double t = sw.Elapsed.TotalSeconds;
                if (_avCtl.Steps != lastSteps) { lastSteps = _avCtl.Steps; stepTimes.Add(t); }
                long u = _audio.Underruns;
                if (u > lastUnder) { lastUnder = u; dropTimes.Add(t); }
                if (double.IsNaN(convergedAt) && _avCtl.Converged) convergedAt = sw.Elapsed.TotalSeconds;
                if (sw.ElapsedMilliseconds >= 10000 && _avCtl.Converged && Math.Abs(_audio.SlewPendingMs) < 0.5 && _avVideoCur == _avVideoTarget) break;
            }
            double runSec = sw.Elapsed.TotalSeconds;
            long drops = _audio.Underruns - under0;
            int steps = _avCtl.Steps;
            double autoMs = _avCtl.AutoMs;
            bool atLimit = _avCtl.AtLimit;
            await Wait(1500);
            double after = await AvDiffMedian(3000);

            // the audio side on its own: lengthen the sound's delay by 60 ms and take it back, both slewed (the run above may have
            // used the picture only). No steering is running here: auto is off.
            _settings.AvAuto = false; _settings.AvOffsetMs = 0; _avCtl.SetAuto(0); ApplyAvSync();
            await Wait(1500);
            double a0 = await AudioLatencyMedian(1500);
            long under1 = _audio.Underruns;
            var swA = Stopwatch.StartNew();
            _audio.SetExtraDelaySlewed(60);
            while (swA.ElapsedMilliseconds < 10000 && Math.Abs(_audio.SlewPendingMs) >= 0.5) await Wait(50);
            double upSec = swA.Elapsed.TotalSeconds;
            await Wait(1200);
            double a1 = await AudioLatencyMedian(1500);
            swA.Restart();
            _audio.SetExtraDelaySlewed(0);
            while (swA.ElapsedMilliseconds < 10000 && Math.Abs(_audio.SlewPendingMs) >= 0.5) await Wait(50);
            double downSec = swA.Elapsed.TotalSeconds;
            await Wait(1200);
            double a2 = await AudioLatencyMedian(1500);
            long audioDrops = _audio.Underruns - under1;
            bool audioSlewOk = !double.IsNaN(a0) && !double.IsNaN(a1) && !double.IsNaN(a2)
                && Math.Abs((a1 - a0) - 60) <= 12 && Math.Abs(a2 - a0) <= 12;
            item.Num("音を遅らせる側だけの確認（+60 ms を入れて戻す）", double.IsNaN(a1) ? "測定できません"
                : $"音の遅れ {a0:0} → {a1:0}（+{a1 - a0:0}）→ {a2:0} ms、延ばすのに {upSec:0.0} 秒・戻すのに {downSec:0.0} 秒、途切れ {(audioDrops == 0 ? "なし" : audioDrops + " 回")}");
            drops += audioDrops;

            bool measured = !double.IsNaN(before) && !double.IsNaN(after);
            item.Num("自動を入れる前の差（音 − 映像、YuCap の中）", double.IsNaN(before) ? "測定できません" : $"{before:+0.0;-0.0;0.0} ms")
                .Num("自動で合わせたあとの差", double.IsNaN(after) ? "測定できません" : $"{after:+0.0;-0.0;0.0} ms")
                .Num("自動部分の補正量", autoMs > 0.5 ? $"音を {autoMs:0} ms 遅らせる" : autoMs < -0.5 ? $"映像を {-autoMs:0} ms 遅らせる" : "なし")
                .Num("補正の回数", $"{steps} 回（1 回は最大 {AvAutoController.AcquireStep:0} ms、近くでは最大 {AvAutoController.TrackStep:0} ms。実際の変更は、音は 1.5% 以内の速さ・映像は 20 ms/秒で少しずつ）")
                .Num("収束までの時間", double.IsNaN(convergedAt) ? "収束しませんでした" : $"{convergedAt:0.0} 秒（全体 {runSec:0.0} 秒）")
                .Num("補正中の音の途切れ", drops == 0 ? "なし" : $"{drops} 回（開始から {string.Join("・", dropTimes.Select(x => x.ToString("0.0")))} 秒。補正を変えたのは {(stepTimes.Count == 0 ? "なし" : string.Join("・", stepTimes.Select(x => x.ToString("0.0"))))} 秒）");
            if (!measured)
            {
                item.Verdict = Verdict.Warn; item.Summary = "映像か音の遅れを測定できず、確認できませんでした";
            }
            else if (drops > 0)
            {
                item.Verdict = Verdict.Warn; item.Summary = $"補正中に音が {drops} 回途切れました（補正後の差 {after:+0.0;-0.0;0.0} ms）";
            }
            else if (Math.Abs(after) <= 10 && !audioSlewOk)
            {
                item.Verdict = Verdict.Warn; item.Summary = "映像側は合いましたが、音を遅らせる側の量が期待（+60 ms）からずれました";
            }
            else if (Math.Abs(after) <= 10)
            {
                item.Verdict = Verdict.Ok;
                item.Summary = $"自動で合わせると差が {before:+0.0;-0.0;0.0} ms から {after:+0.0;-0.0;0.0} ms になり、音の途切れもありません";
            }
            else
            {
                item.Verdict = Verdict.Warn;
                item.Summary = $"補正後の差が {after:+0.0;-0.0;0.0} ms で、±10 ms に収まりませんでした" + (atLimit ? "（補正できる範囲の端に達しました）" : "");
            }
            item.Explanation = "映像と音の「YuCap の中の遅れ」の差を 0.5 秒ごとに測り、直近 5 秒の中央値が ±8 ms の外に出たときだけ、少しずつ補正します（遠いときは 3 秒の中央値で先に大きく、近くでは最大 4 ms ずつ）。" +
                "補正は、音を遅らせる（音を 1.5% 以内の速さで 1 コマずつ繰り返して延ばす）か、映像を遅らせる（20 ms/秒）かで行い、つなぎ目の無音や音の切り取りは作りません。映像や音声の再開中・一時停止中・開始から 4 秒間は動かしません。" +
                "キャプチャカードやテレビ・スピーカーの中の遅れは測れないため、微調整（スライダー）で足します。";
        }
        finally
        {
            // the test baseline (neutral); RestoreAfterTest puts the user's own values back
            _settings.AvAuto = false; _settings.AvOffsetMs = 0;
            _avCtl.SetAuto(0);
            ApplyAvSync();
        }
        sec.Items.Add(item);
    }

    private async Task<double> AudioLatencyMedian(int ms)
    {
        var d = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (_audio.IsRunning && _audio.LatencyMs > 0) d.Add(_audio.LatencyMs);
            await Wait(50);
        }
        return d.Count < 5 ? double.NaN : AvAutoController.Median(d);
    }

    /// <summary>Median of (audio latency - video latency) over <paramref name="ms"/>, NaN when fewer than 10 readings.</summary>
    private async Task<double> AvDiffMedian(int ms)
    {
        var d = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            double v = _video.IsRunning ? _video.VideoLatencyMs : 0, a = _audio.IsRunning ? _audio.LatencyMs : 0;
            if (v > 0 && a > 0) d.Add(a - v);
            await Wait(100);
        }
        return d.Count < 10 ? double.NaN : AvAutoController.Median(d);
    }

    // ------------------------------------------------------------------ 暗部補正

    private static double[] RampLuma(Bitmap bmp, int row, out int[] g)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var y = new double[bmp.Width]; g = new int[bmp.Width];
            var line = new byte[bmp.Width * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + row * bd.Stride, line, 0, line.Length);
            for (int x = 0; x < bmp.Width; x++)
            {
                double b = line[x * 4], gg = line[x * 4 + 1], r = line[x * 4 + 2];
                y[x] = 0.2126 * r + 0.7152 * gg + 0.0722 * b;
                g[x] = (int)gg;
            }
            return y;
        }
        finally { bmp.UnlockBits(bd); }
    }

    private async Task FeatureShadow(TestSection sec)
    {
        var item = new TestItem { Name = "暗部補正（暗い場面を見やすく）" };
        if (!_video.IsRunning || !_video.SupportsAdjustments)
        {
            item.Verdict = Verdict.Warn; item.Summary = "Direct3D の映像が動いていないため、確認できませんでした";
            sec.Items.Add(item);
            return;
        }
        const int W = 256, H = 16;
        var luma = new byte[W * H]; var chroma = new byte[W * H / 2];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) luma[y * W + x] = (byte)x;   // full range: input level = x / 255
        Array.Fill(chroma, (byte)128);

        var outs = new Dictionary<int, (double[] Y, int[] G, Bitmap Img)>();
        try
        {
            for (int level = 0; level <= VideoAdjustments.MaxShadow; level++)
            {
                var adj = new VideoAdjustments(0f, 1f, 1f, 1f, level);
                Bitmap? bmp = await Task.Run(() => _video.ConvertSynthetic(luma, chroma, W, H, adj));
                if (bmp == null) { item.Verdict = Verdict.Warn; item.Summary = "階調の帯を GPU で描けませんでした"; sec.Items.Add(item); return; }
                double[] yv = RampLuma(bmp, H / 2, out int[] g);
                outs[level] = (yv, g, bmp);
            }

            double[] refY = outs[0].Y;
            int[] refG = outs[0].G;
            var failures = new List<string>();
            string[] names = { "オフ", "弱", "中", "強" };
            int[] probes = { 13, 26, 38, 51, 64 };   // 0.05 .. 0.25
            item.Num("方法", "0〜255 の階調の帯（グレー）を、実際の映像と同じ変換の処理（Direct3D のシェーダー）に通し、オフのときの出力と比べます。期待する持ち上げ量は y×k×(1−smoothstep(0.15, 0.5, y))（k は 弱 0.20・中 0.40・強 0.65）です");
            for (int level = 1; level <= VideoAdjustments.MaxShadow; level++)
            {
                var (yv, g, _) = outs[level];
                var parts = new List<string>();
                foreach (int p in probes)
                {
                    double yin = refY[p] / 255.0;
                    double expected = (VideoAdjustments.ShadowCurve((float)yin, level) - yin) * 255.0;
                    double got = yv[p] - refY[p];
                    parts.Add($"{p / 255.0:0.00}: +{got:0.0}（期待 +{expected:0.0}）");
                    if (Math.Abs(got - expected) > 1.5) failures.Add($"{names[level]} の入力 {p / 255.0:0.00} で持ち上げ量が期待と違う（+{got:0.0} / 期待 +{expected:0.0}）");
                }
                item.Num($"{names[level]}: 暗い所の持ち上げ（階調）", string.Join(" ／ ", parts));

                int hiDiff = 0; for (int x = 153; x < W; x++) hiDiff = Math.Max(hiDiff, Math.Abs(g[x] - refG[x]));   // 0.6 and above
                int blackDiff = Math.Abs(g[0] - refG[0]);
                int inversions = 0; int maxStep = 0;
                for (int x = 1; x < W; x++)
                {
                    int d = g[x] - g[x - 1];
                    if (d < 0) inversions++;
                    maxStep = Math.Max(maxStep, d);
                }
                item.Num($"{names[level]}: 明るい所（入力 0.6 以上）の変化", $"最大 {hiDiff} 階調（許容 1）")
                    .Num($"{names[level]}: 真っ黒（0）の変化", $"{blackDiff} 階調")
                    .Num($"{names[level]}: 階調の逆転 / 隣り合う階調の最大の飛び", $"{inversions} 箇所 / {maxStep} 階調（許容 2）");
                if (hiDiff > 1) failures.Add($"{names[level]} で明るい所が {hiDiff} 階調変わった");
                if (blackDiff > 0) failures.Add($"{names[level]} で真っ黒が {blackDiff} 階調変わった");
                if (inversions > 0) failures.Add($"{names[level]} で階調の逆転が {inversions} 箇所ある");
                if (maxStep > 2) failures.Add($"{names[level]} で隣り合う階調が {maxStep} 階調飛ぶ（縞が見える）");
                if (!(yv[64] > refY[64] + 0.5)) failures.Add($"{names[level]} で暗い所が持ち上がっていない");
            }
            bool order = outs[1].Y[40] < outs[2].Y[40] && outs[2].Y[40] < outs[3].Y[40];
            if (!order) failures.Add("弱 < 中 < 強 の順に持ち上がっていない");

            // the curve picture
            string file = "shadow_curve.png";
            try { SaveShadowChart(outs.ToDictionary(kv => kv.Key, kv => kv.Value.G), Path.Combine(_rep.Folder, file), names); item.Images.Add(file); }
            catch (Exception ex) { item.Num("グラフ", "作れませんでした: " + ex.Message); }

            // live picture before / after (info)
            string? liveNote = null;
            try
            {
                int level0 = _settings.ShadowBoost;
                _settings.ShadowBoost = 0; ApplyRenderOptions(); await Wait(500);
                Bitmap? a = await Task.Run(() => _video.PhotoSnapshot());
                _settings.ShadowBoost = 2; ApplyRenderOptions(); await Wait(500);
                Bitmap? b = await Task.Run(() => _video.PhotoSnapshot());
                _settings.ShadowBoost = level0; ApplyRenderOptions();
                if (a != null && b != null)
                {
                    (double meanA, double meanB, double darkA, double darkB) = DarkStats(a, b);
                    string lf = "shadow_live.png";
                    SaveBeforeAfter(a, b, Path.Combine(_rep.Folder, lf));
                    item.Images.Add(lf);
                    liveNote = $"左: オフ、右: 中。全体の平均の明るさ {meanA:0.0} → {meanB:0.0}、オフで暗かった所（明るさ 64 未満）の平均 {darkA:0.0} → {darkB:0.0}";
                }
                a?.Dispose(); b?.Dispose();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { liveNote = "実際の映像の比べは作れませんでした: " + ex.Message; }
            if (liveNote != null) item.Num("実際の映像", liveNote);

            if (failures.Count == 0)
            {
                item.Verdict = Verdict.Ok; item.Summary = "暗い所だけが期待どおりに持ち上がり、明るい所・真っ黒は変わらず、階調の逆転や縞もありません";
                item.Explanation = "階調の帯（0〜255）を弱・中・強で変換し、持ち上げ量が式どおり（誤差 1.5 階調以内）、入力 0.6 以上と真っ黒は 1 階調以内で変化なし、階調の逆転なし、隣り合う階調の飛びは最大 2 階調以内でした。" +
                    "処理は明るさ（輝度）だけを持ち上げ、色の差（色差）は動かさないので、色味は保たれます。画像は、入力 → 出力の曲線と階調の帯、そして実際の映像の比べです。";
            }
            else
            {
                item.Verdict = Verdict.Ng; item.Summary = $"{failures.Count} 件の食い違いがあります";
                item.Explanation = string.Join("\n", failures);
            }
        }
        finally { foreach (var v in outs.Values) v.Img.Dispose(); }
        sec.Items.Add(item);
    }

    /// <summary>Mean luma of both pictures, and of the places that were dark (luma below 64) in <paramref name="a"/>, in each.</summary>
    private static (double MeanA, double MeanB, double DarkA, double DarkB) DarkStats(Bitmap a, Bitmap b)
    {
        double sa = 0, sb = 0, da = 0, db = 0; int n = 0, dn = 0;
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        int sx = Math.Max(1, w / 128), sy = Math.Max(1, h / 128);
        for (int y = 0; y < h; y += sy)
            for (int x = 0; x < w; x += sx)
            {
                Color ca = a.GetPixel(x, y), cb = b.GetPixel(x, y);
                double la = 0.2126 * ca.R + 0.7152 * ca.G + 0.0722 * ca.B, lb = 0.2126 * cb.R + 0.7152 * cb.G + 0.0722 * cb.B;
                sa += la; sb += lb; n++;
                if (la < 64) { da += la; db += lb; dn++; }
            }
        return n == 0 ? (double.NaN, double.NaN, double.NaN, double.NaN)
            : (sa / n, sb / n, dn == 0 ? double.NaN : da / dn, dn == 0 ? double.NaN : db / dn);
    }

    private static void SaveBeforeAfter(Bitmap a, Bitmap b, string path)
    {
        int w = 480, h = Math.Max(1, a.Height * w / Math.Max(1, a.Width));
        using var img = new Bitmap(w * 2 + 6, h);
        using (Graphics g = Graphics.FromImage(img))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(a, new Rectangle(0, 0, w, h));
            g.DrawImage(b, new Rectangle(w + 6, 0, w, h));
        }
        img.Save(path, ImageFormat.Png);
    }

    private static void SaveShadowChart(Dictionary<int, int[]> g, string path, string[] names)
    {
        const int S = 400, M = 36;
        using var img = new Bitmap(S + M * 2 + 120, S + M * 2 + 90);
        using Graphics gr = Graphics.FromImage(img);
        gr.Clear(Color.White);
        gr.SmoothingMode = SmoothingMode.AntiAlias;
        using var axis = new Pen(Color.Black, 1);
        using var grid = new Pen(Color.LightGray, 1);
        using var font = new Font("Segoe UI", 8f);
        for (int i = 0; i <= 4; i++)
        {
            float p = M + S * i / 4f;
            gr.DrawLine(grid, p, M, p, M + S); gr.DrawLine(grid, M, p, M + S, p);
            gr.DrawString((i * 64).ToString(), font, Brushes.Black, p - 8, M + S + 2);
            gr.DrawString((256 - i * 64).ToString(), font, Brushes.Black, 4, M + S * i / 4f - 6);
        }
        gr.DrawRectangle(axis, M, M, S, S);
        Color[] cols = { Color.Gray, Color.FromArgb(0, 150, 60), Color.FromArgb(230, 140, 0), Color.FromArgb(200, 30, 30) };
        for (int level = 0; level < cols.Length; level++)
        {
            int[] v = g[level];
            using var pen = new Pen(cols[level], level == 0 ? 1 : 2);
            var pts = new PointF[v.Length];
            for (int x = 0; x < v.Length; x++) pts[x] = new PointF(M + S * x / 255f, M + S - S * Math.Min(255, v[x]) / 255f);
            gr.DrawLines(pen, pts);
            using var br = new SolidBrush(cols[level]);
            gr.DrawString(names[level], font, br, M + S + 10, M + 10 + level * 16);
        }
        gr.DrawString("input (x) -> output (y), 0-255", font, Brushes.Black, M, 6);
        // the ramps themselves, one strip per level
        for (int level = 0; level < cols.Length; level++)
        {
            int[] v = g[level];
            for (int x = 0; x < v.Length; x++)
            {
                using var br = new SolidBrush(Color.FromArgb(v[x], v[x], v[x]));
                gr.FillRectangle(br, M + S * x / 256f, M + S + 26 + level * 14, S / 256f + 1f, 12);
            }
            gr.DrawString(names[level], font, Brushes.Black, M + S + 10, M + S + 24 + level * 14);
        }
        img.Save(path, ImageFormat.Png);
    }
}
