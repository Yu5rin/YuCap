using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: くっきり（Anime4K）の効果。Section 5, after 「元の細かさに合わせた拡大」.
//  Pictures, metrics and the CPU reference are in A4kTest.cs / NativeResTest.cs; this file drives the live engine.
//  Wording is Japanese only (TestReport.cs). Numbers decide, not looks: (a) a known picture with a ground truth,
//  (b) the live picture, (c) GPU time against the refresh budget, then one plain recommendation line.
// =============================================================================================

public sealed partial class MainForm
{
    private async Task A4kStep(TestSection sec, List<CaptureMode> modes)
    {
        CaptureMode? mode = PickMode(modes, 1920, 1080, 60) ?? PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
        var itemSynth = new TestItem { Name = "くっきり（Anime4K）の効果（既知の絵: 720p を 1080p に引き伸ばしたもの → 4K）" };
        var itemLive = new TestItem { Name = "くっきり（Anime4K）の効果（実際の映像）" };
        var itemGpu = new TestItem { Name = "くっきり（Anime4K）の GPU 時間（4K 出力）" };
        var itemNv = new TestItem { Name = "AI 拡大（NVIDIA RTX）が働いているか（VSR あり / なし）" };
        var itemRec = new TestItem { Name = "おすすめの拡大設定（この映像・このモニター）" };
        sec.Items.Add(itemSynth); sec.Items.Add(itemLive); sec.Items.Add(itemGpu); sec.Items.Add(itemNv); sec.Items.Add(itemRec);
        // 自分専用モデル: only when the user has one (the setting's model, else the newest). Its rows join the comparisons below.
        string? userModel = UserModels.Resolve(_settings.A4kModel) ?? UserModels.List().Select(UserModels.Resolve).FirstOrDefault(x => x != null);
        TestItem? itemUser = userModel == null ? null : new TestItem { Name = "自分専用モデルの効果（学習に使っていない実際の絵）" };
        if (itemUser != null) sec.Items.Insert(sec.Items.IndexOf(itemLive) + 1, itemUser);
        var all = itemUser == null ? new[] { itemSynth, itemLive, itemGpu, itemNv, itemRec } : new[] { itemSynth, itemLive, itemUser, itemGpu, itemNv, itemRec };
        if (mode == null)
        {
            foreach (TestItem it in all) it.Summary = "映像のモードがありません";
            return;
        }
        if (_testCaption != null) _testCaption.Showing = false;
        EnsureEngine(false, mode);
        await Wait(SettleMs);
        if (!_video.SupportsNativeRes)
        {
            foreach (TestItem it in all)
            {
                it.Verdict = Verdict.Info;
                it.Summary = "Direct3D 描画のときだけ使える機能のため、試験しません";
            }
            return;
        }

        Bitmap? console = null;
        var rec = new A4kRecommendation();
        string? keepCustom = D3DRenderer.A4kCustomPath;
        try
        {
            A4kTest.UserModelPath = userModel;
            console = await A4kSynthetic(itemSynth, rec);
            await A4kLive(itemLive, rec);
            if (itemUser != null) await A4kUser(itemUser, userModel!);
            await A4kGpu(itemGpu, mode, console, rec);
            await NvVsrCheck(itemNv, rec);
            A4kRecommend(itemRec, rec);
        }
        finally { console?.Dispose(); A4kTest.UserModelPath = null; D3DRenderer.SetA4kCustom(keepCustom); }
    }

    /// <summary>自分専用モデル against the built-in network on the held-out frames of its training (never seen by the training), by the real GPU pipeline.
    /// "Better" only when both SSIM and PSNR are higher than くっきり's; otherwise it says it is not.</summary>
    private async Task A4kUser(TestItem item, string modelPath)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(modelPath);
            NativeResTest.RenderFn render = TestRender();
            var sw = Stopwatch.StartNew();
            int n = _tQuick ? 4 : 10;
            List<A4kUserTest.ValRow> rows = await Task.Run(() => A4kUserTest.Validation(render, modelPath, n, null));
            item.Num("モデル", name);
            if (rows.Count == 0)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "[情報] 学習に使っていない絵の一覧がモデルにないため、比べられません";
                item.Explanation = "モデルの .json に val_frames（学習に使わなかった絵）が書かれていると比べられます。tools/train_upscaler.py で学習すると自動で書かれます。";
                return;
            }
            foreach (A4kUserTest.ValRow r in rows) item.Num(r.Name, A4kUserTest.RowText(r));
            bool better = A4kUserTest.Better(rows, out A4kUserTest.ValRow? u, out A4kUserTest.ValRow? b);
            if (u == null || b == null) { item.Verdict = Verdict.Info; item.Summary = "[情報] 比較の絵を作れませんでした"; return; }
            item.Verdict = better ? Verdict.Ok : Verdict.Info;
            string diff = $"SSIM {b.Ssim:0.0000} → {u.Ssim:0.0000}、PSNR {b.Psnr:0.00} → {u.Psnr:0.00} dB";
            item.Summary = better
                ? $"自分専用モデルは、学習に使っていない絵で くっきり（Anime4K）より数字の上で上回りました（{diff}）"
                : $"自分専用モデルは、学習に使っていない絵で くっきり（Anime4K）を数字の上では上回りませんでした（{diff}）";
            item.Explanation = "学習に使わなかった本物の絵（元の大きさ）を半分に縮め、YuCap の実際の GPU 処理で 2 倍に拡大して、元の絵と比べています。SSIM と PSNR のどちらも上回ったときだけ「上回った」と書きます。" +
                "学習に使った絵と同じゲーム・同じ場面の続きで比べているため、別のゲームでも同じだけ良くなるとは限りません。絵が少ないと、上回らないか、場面が違うと悪くなることがあります（集めた絵を増やして学習し直してください）。" +
                $"（所要 {sw.Elapsed.TotalSeconds:0} 秒）";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }

    private sealed class A4kRecommendation
    {
        public double SsimStd, SsimFsr, SsimNative, SsimA4k, SsimNativeA4k, SsimRestFsr, SsimRestA4k;
        public double GpuRestoreMs;
        public double SsimNv = -1, GpuNvMs;
        public bool NvEffectiveKnown, NvEffective;
        public string Winner = "";
        public bool Synth;
        public int EstimatedHeight, SourceHeight;
        public double GpuAvg, GpuMax, BudgetMs, GpuInterpAvg, GpuInterpMax;
        public bool Gpu;
        public double LiveGradFsr, LiveGradA4k, LiveRingFsr, LiveRingA4k;
        public bool Live;
    }

    private async Task<Bitmap?> A4kSynthetic(TestItem item, A4kRecommendation rec)
    {
        try
        {
            NativeResTest.RenderFn render = TestRender();
            using Bitmap truth = await Task.Run(() => NativeResTest.MakeTruth());
            var sw = Stopwatch.StartNew();
            Bitmap? console = null;
            var runs = new List<(string Title, List<A4kTest.MethodRow> Rows)>();
            List<A4kTest.MethodRow> bil = await Task.Run(() => A4kTest.Synthetic(render, truth, null, NativeResTest.Kernel.Bilinear, out console));
            runs.Add(("バイリニアで引き伸ばし", bil));
            List<A4kTest.MethodRow>? cub = null;
            if (!_tQuick)
            {
                cub = await Task.Run(() => { var rr = A4kTest.Synthetic(render, truth, null, NativeResTest.Kernel.CatmullRom, out Bitmap c2, withIdeal: false); c2.Dispose(); return rr; });
                runs.Add(("バイキュービックで引き伸ばし", cub));
            }
            try
            {
                if (bil.Count < 5)
                {
                    item.Verdict = Verdict.Warn; item.Summary = "拡大した絵を作れませんでした（GPU での描画が失敗）";
                    console?.Dispose();
                    return null;
                }
                item.Num("方法", "4K の細かい絵（文字・細い線・縞・市松・円・グラデーション・写真のようなノイズ）を正解とし、それを 1280x720 に縮めた絵を「ゲームの描画」、1920x1080 に引き伸ばした絵を「ゲーム機の出力」とします。これを YuCap の実際の GPU 処理で 3840x2160 にして、正解と比べます。くっきりは Anime4K の中くらいのネットワーク（M）です");
                foreach ((string title, List<A4kTest.MethodRow> rows) in runs)
                    foreach (A4kTest.MethodRow r in rows)
                        item.Num($"{title} / {r.Name}", A4kTest.RowText(r));

                A4kTest.MethodRow? Find(List<A4kTest.MethodRow> rows, string name) => rows.FirstOrDefault(x => x.Name == name);
                A4kTest.MethodRow? std = Find(bil, "標準（バイリニア）"), fsr = Find(bil, "FSR"), nat = Find(bil, "元の細かさ + FSR"),
                    a4k = Find(bil, "くっきり（Anime4K）"), natA = Find(bil, "元の細かさ + くっきり");
                A4kTest.MethodRow? win = A4kTest.Winner(bil);
                rec.Synth = true;
                rec.SsimStd = std!.Ssim; rec.SsimFsr = fsr!.Ssim; rec.SsimNative = nat!.Ssim; rec.SsimA4k = a4k!.Ssim; rec.SsimNativeA4k = natA!.Ssim;
                rec.Winner = win?.Name ?? "";
                rec.SsimNv = Find(bil, "AI 拡大（NVIDIA RTX）")?.Ssim ?? -1;
                rec.SsimRestFsr = Find(bil, "ぼやけ取り + FSR")?.Ssim ?? 0; rec.SsimRestA4k = Find(bil, "ぼやけ取り + くっきり")?.Ssim ?? 0;
                if (cub != null)
                {
                    A4kTest.MethodRow? cf = Find(cub, "FSR"), ca = Find(cub, "くっきり（Anime4K）");
                    if (cf != null && ca != null)
                        item.Num("バイキュービックで引き伸ばしたとき（SSIM）", $"FSR {cf.Ssim:0.0000} → くっきり {ca.Ssim:0.0000}");
                }

                var (t1, t2) = NativeResTest.Tiles(truth);
                SaveA4kCrops(bil, truth, t1, Path.Combine(_rep.Folder, "a4k_text.png"));
                SaveA4kCrops(bil, truth, t2, Path.Combine(_rep.Folder, "a4k_edges.png"));
                item.Images.Add("a4k_text.png");
                item.Images.Add("a4k_edges.png");

                bool better = a4k.Ssim > fsr.Ssim && natA.Ssim > nat.Ssim;
                item.Verdict = better ? Verdict.Ok : Verdict.Warn;
                item.Summary = $"正解に一番近いのは「{win?.Name}」です（SSIM: 標準 {std.Ssim:0.0000} / FSR {fsr.Ssim:0.0000} / くっきり {a4k.Ssim:0.0000} / 元の細かさ+FSR {nat.Ssim:0.0000} / 元の細かさ+くっきり {natA.Ssim:0.0000}）";
                item.Explanation = "画像は左から FSR、元の細かさ + FSR、くっきり、元の細かさ + くっきり、正解（4K）です（480x270 を拡大せずに切り出し。1つ目は文字、2つ目は線の一番多い部分）。\n" +
                    "「輪郭の強さ」は細かい線のくっきり具合を正解と比べた比（1 が正解）、「輪郭の幅」は黒い四角の縁が白から黒へ変わる幅（小さいほどくっきり）、「行き過ぎ」は縁で明るさが正解の範囲を超えた分（輪＝ハロ）、「輪の出た画素」は縁の近くで正解の範囲を 4 段階より大きく超えた画素の割合です。正解より強く出すぎると輪の数字が大きくなります。\n" +
                    "くっきり（Anime4K）は、ニューラルネットで細い線を描き直す拡大です。ここで使う絵はコンピューターで作った文字・線・図形が中心のため、イラストやゲーム画面に近い一方、実際のゲーム機の引き伸ばし方とは違います（バイリニアとバイキュービックの 2 種類で確かめています）。" +
                    $"（所要 {sw.Elapsed.TotalSeconds:0} 秒）";
                return console;
            }
            finally
            {
                foreach (var (_, rows) in runs) foreach (A4kTest.MethodRow r in rows) r.Image?.Dispose();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
            return null;
        }
    }

    private static void SaveA4kCrops(List<A4kTest.MethodRow> rows, Bitmap truth, Rectangle tile, string path)
    {
        var panels = new List<Bitmap>(); var labels = new List<string>();
        foreach (var (label, key) in new[] { ("FSR", "FSR"), ("720p+FSR", "元の細かさ + FSR"), ("crisp", "くっきり（Anime4K）"), ("720p+crisp", "元の細かさ + くっきり") })
        {
            A4kTest.MethodRow? m = rows.FirstOrDefault(x => x.Name == key);
            if (m?.Image != null) { panels.Add(m.Image); labels.Add(label); }
        }
        panels.Add(truth); labels.Add("truth 4K");
        TestProbes.SaveStrip(panels.ToArray(), labels.ToArray(), tile, path);
    }

    private async Task A4kLive(TestItem item, A4kRecommendation rec)
    {
        try
        {
            using Bitmap? first = await WaitForDetailedPhoto();
            if (first == null)
            {
                item.Verdict = Verdict.Info;
                item.Summary = NoDetailSummary;
                item.Explanation = NoDetailWhy;
                return;
            }
            var res = await Task.Run(() => { float[] y = ResEstimate.LumaOf(first, out int w, out int h); return ResEstimate.Analyze(y, w, h); });
            int estV = Math.Min(res.Vertical.EstimateHeight, first.Height);
            rec.EstimatedHeight = estV; rec.SourceHeight = first.Height;
            Size outSize = _topt.Target.Bounds.Size;
            string? forced = Environment.GetEnvironmentVariable("YUCAP_TEST_OUTPUT");
            if (forced != null)
            {
                string[] wh = forced.ToLowerInvariant().Split('x');
                if (wh.Length == 2 && int.TryParse(wh[0], out int fw) && int.TryParse(wh[1], out int fhh) && fw > 0 && fhh > 0)
                {
                    outSize = new Size(fw, fhh);
                    item.Num("注意", $"環境変数 YUCAP_TEST_OUTPUT により、拡大先を {fw}x{fhh} にして比べています");
                }
            }
            item.Num("映像", $"{first.Width}x{first.Height}（細かさ {ResEstimate.Name(estV)} 相当）").Num("拡大先", $"{outSize.Width}x{outSize.Height}");
            if (outSize.Height <= first.Height || outSize.Width <= first.Width)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "[情報] このモニターは映像と同じか小さいため、拡大の比べようがありません";
                item.Explanation = "拡大の処理は、絵を映像より大きく出すときだけ働きます。4K のモニターで試験すると比べられます。";
                return;
            }
            List<A4kTest.LiveRow> rows = await Task.Run(() => A4kTest.Live(TestRender(), first, outSize, estV));
            try
            {
                if (rows.Count < 3)
                {
                    item.Verdict = Verdict.Warn; item.Summary = "拡大した絵を作れませんでした";
                    return;
                }
                double g0 = rows[0].Grad;
                foreach (A4kTest.LiveRow r in rows)
                    item.Num(r.Name, $"輪郭の強さ {r.Grad:0.000}（標準の {r.Grad / Math.Max(1e-9, g0):0.00} 倍） / 輪の出た画素 {r.RingPct:0.00}%（平均の行き過ぎ {r.RingExcess:0.0} 段階）");
                A4kTest.LiveRow fsr = rows[1], a4k = rows[2];
                rec.Live = true; rec.LiveGradFsr = fsr.Grad / g0; rec.LiveGradA4k = a4k.Grad / g0; rec.LiveRingFsr = fsr.RingPct; rec.LiveRingA4k = a4k.RingPct;
                Rectangle? tile = await Task.Run(() => TestProbes.BestTile(TestProbes.MeasureEdges(a4k.Image!), a4k.Image!.Size));
                if (tile is { } t)
                {
                    TestProbes.SaveStrip(rows.Select(r => r.Image!).ToArray(),
                        rows.Select(r => r.Name.Replace("標準", "standard").Replace("くっきり", "crisp").Replace("に縮めて", "+").Replace("相当", "")).ToArray(), t,
                        Path.Combine(_rep.Folder, "a4k_live.png"));
                    item.Images.Add("a4k_live.png");
                }
                bool sharper = a4k.Grad > fsr.Grad;
                item.Verdict = sharper ? Verdict.Ok : Verdict.Info;
                item.Summary = sharper
                    ? $"くっきりは FSR より輪郭が {a4k.Grad / Math.Max(1e-9, fsr.Grad):0.00} 倍くっきりします（輪の出た画素 {fsr.RingPct:0.0}% → {a4k.RingPct:0.0}%）"
                    : "この映像では、くっきりは FSR より輪郭がくっきりしませんでした";
                item.Explanation = "同じ 1 枚の絵を各方法で拡大して比べています。正解の絵がないため、輪郭の強さ（細かい線のくっきり具合）と「輪」の量だけを測っています。輪の量は標準（双線形）の拡大を基準にしているため、くっきりした絵ほど大きく出ます。数字は参考で、良し悪しは画像を見て決めてください。";
            }
            finally { foreach (A4kTest.LiveRow r in rows) r.Image?.Dispose(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }

    private async Task A4kGpu(TestItem item, CaptureMode mode, Bitmap? console, A4kRecommendation rec)
    {
        try
        {
            if (console == null)
            {
                item.Verdict = Verdict.Info; item.Summary = "[情報] 既知の絵を作れなかったため、測りません";
                return;
            }
            Size out4k = new(3840, 2160);
            int frames = _tQuick ? 120 : 300;
            double refresh = Math.Max(30, _topt.Target.RefreshHz);
            double budget = 1000.0 / refresh;
            // a second picture a few pixels away: the interpolation has something to move
            using var shifted = new Bitmap(console.Width, console.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(shifted)) { g.DrawImage(console, 7, 3, console.Width, console.Height); }

            (string Name, ScalingQuality Q, int Nh)[] cases =
            {
                ("標準", ScalingQuality.Standard, 0), ("FSR", ScalingQuality.Fsr, 0),
                ("くっきり", ScalingQuality.Anime4K, 0), ("元の細かさ（720p）+ くっきり", ScalingQuality.Anime4K, 720),
                ("ぼやけ取り + くっきり", ScalingQuality.Anime4K, -1),
            };
            if (_video.NvRtxSupported) cases = cases.Append(("AI 拡大（NVIDIA RTX）", ScalingQuality.NvRtx, 0)).ToArray();
            if (A4kTest.UserModelPath != null) cases = cases.Append(("自分専用モデル", ScalingQuality.Anime4K, 0)).ToArray();
            var avgNew = new Dictionary<string, double>();
            foreach ((string name, ScalingQuality q, int nh0) in cases)
            {
                int nh = Math.Max(0, nh0);
                bool userCase = name == "自分専用モデル";
                D3DRenderer.SetA4kCustom(userCase ? A4kTest.UserModelPath : null);
                D3DRenderer.RestoreForce = nh0 < 0;
                try {
                double[] fresh = Array.Empty<double>(), same = Array.Empty<double>(), ip = Array.Empty<double>();
                bool ok = await Task.Run(() => _video.BenchQuality(console, null, out4k, q, nh, frames, false, out fresh));
                if (!ok || fresh.Length == 0)
                {
                    item.Verdict = Verdict.Info; item.Summary = "[情報] この GPU では時間を測れませんでした（タイムスタンプ未対応など）";
                    return;
                }
                if (q == ScalingQuality.Anime4K) await Task.Run(() => _video.BenchQuality(console, null, out4k, q, nh, frames, true, out same));
                await Task.Run(() => _video.BenchQuality(console, shifted, out4k, q, nh, frames, false, out ip));
                string P(double[] v) => v.Length == 0 ? "n/a" : $"平均 {v.Average():0.00} ms / 最大 {v.Max():0.00} ms（1秒に {1000.0 / Math.Max(0.001, v.Average()):0} コマまで）";
                item.Num($"{name}: 新しい絵 1 コマ", P(fresh));
                if (same.Length > 0) item.Num($"{name}: 同じ絵の再表示（くっきりの結果を使い回し）", P(same));
                if (ip.Length > 0) item.Num($"{name}: フレーム補間オン（毎回新しい絵）", P(ip));
                avgNew[name] = fresh.Average();
                if (q == ScalingQuality.NvRtx) rec.GpuNvMs = fresh.Average();
                if (nh0 < 0) rec.GpuRestoreMs = fresh.Average() - avgNew.GetValueOrDefault("くっきり");
                if (q == ScalingQuality.Anime4K && nh == 0 && !userCase)
                {
                    rec.Gpu = true; rec.GpuAvg = fresh.Average(); rec.GpuMax = fresh.Max();
                    if (ip.Length > 0) { rec.GpuInterpAvg = ip.Average(); rec.GpuInterpMax = ip.Max(); }
                    rec.BudgetMs = budget;
                }
                } finally { D3DRenderer.RestoreForce = false; }
            }
            item.Num("出力", "3840x2160 / 映像 1080p の絵（変換の時間は含みません）")
                .Num($"画面の 1 回の更新（{refresh:0}Hz）", $"{budget:0.0} ms");
            double worst = Math.Max(rec.GpuMax, rec.GpuInterpMax);
            bool fine = worst <= budget * 0.6;
            item.Verdict = fine ? Verdict.Ok : Verdict.Warn;
            item.Summary = fine
                ? $"くっきりの GPU 時間は新しい絵で平均 {rec.GpuAvg:0.00} ms（FSR は {avgNew.GetValueOrDefault("FSR"):0.00} ms）。更新 1 回 {budget:0.0} ms に対して余裕があります"
                : $"くっきりの GPU 時間は最大 {worst:0.00} ms で、更新 1 回 {budget:0.0} ms に対して余裕が少なくなります";
            item.Explanation = "くっきりは映像の新しい絵ごとに 1 回だけ実行し、同じ絵を再表示するときは結果を使い回します（フレーム補間オンのときは毎回新しい絵なので毎回実行）。表示の GPU 時間（「表示の計測」の項目）の中に含まれます。映像が大きいほど時間がかかります（約 0.8 ms／100 万画素）。出力が 3 倍以上で、2 段にしても時間に収まるときだけ 2 段で拡大します。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }

    /// <summary>One plain line: which setting wins by the numbers for this source and this monitor.</summary>
    private void A4kRecommend(TestItem item, A4kRecommendation r)
    {
        if (!r.Synth)
        {
            item.Verdict = Verdict.Info; item.Summary = "[情報] 比較の数字が揃わなかったため、おすすめを出せません";
            return;
        }
        bool stretched = r.Live && r.EstimatedHeight > 0 && r.EstimatedHeight < r.SourceHeight;
        string det = r.Live ? $"今の映像は {ResEstimate.Name(r.EstimatedHeight)} 相当の細かさ" + (stretched ? "（引き伸ばされています）" : "") : "映像の細かさは調べられませんでした";
        string pick;
        if (stretched)
            pick = $"元の細かさに合わせて拡大を「自動」にして、拡大の画質を「くっきり」にするのが数字の上で一番です（既知の絵の SSIM {r.SsimFsr:0.0000}（FSR）→ {r.SsimNativeA4k:0.0000}）";
        else
            pick = r.SsimA4k > r.SsimFsr
                ? $"拡大の画質を「くっきり」にするのが数字の上で一番です（既知の絵の SSIM {r.SsimFsr:0.0000}（FSR）→ {r.SsimA4k:0.0000}）"
                : $"拡大の画質は「高画質（FSR）」のままが数字の上で一番です（くっきりの SSIM {r.SsimA4k:0.0000}、FSR {r.SsimFsr:0.0000}）";
        string cost = r.Gpu
            ? $"。GPU は 1 コマ平均 {Math.Max(r.GpuAvg, r.GpuInterpAvg):0.0} ms で、更新 1 回の {r.BudgetMs:0.0} ms に対して" + (Math.Max(r.GpuMax, r.GpuInterpMax) <= r.BudgetMs * 0.6 ? "余裕があります" : "余裕が少なめです")
            : string.Empty;
        string sharp = r.Live && r.LiveRingA4k > r.LiveRingFsr + 2.0 ? "。輪（白や黒の縁取り）が気になるときは、シャープさを下げてください" : string.Empty;
        string rest = r.SsimRestA4k > 0
            ? $"。「ぼやけを取ってから拡大」は、既知の絵で FSR の SSIM {r.SsimFsr:0.0000} → {r.SsimRestFsr:0.0000}、くっきりの SSIM {r.SsimA4k:0.0000} → {r.SsimRestA4k:0.0000} と上がりますが、GPU が 1 コマ約 {r.GpuRestoreMs:0.0} ms 増え、縁に輪が出やすく、実際の映像では輪の出た画素が増えるため、既定はオフです（元の細かさに合わせて拡大が働くときは使いません）"
            : string.Empty;
        string nv = string.Empty;
        if (r.SsimNv >= 0)
        {
            double bestOther = Math.Max(r.SsimFsr, r.SsimA4k);
            nv = $"。AI 拡大（NVIDIA RTX）は SSIM {r.SsimNv:0.0000}" + (r.NvEffectiveKnown && !r.NvEffective ? "（ただし VSR が働いていないため、ふつうの拡大と同じ結果です。NVIDIA コントロールパネルの「RTX ビデオ強調」をオンにしてください）"
                : r.SsimNv > bestOther + 0.0005 ? $"で、数字の上では FSR・くっきりより上です" : $"で、FSR・くっきりを上回りません（FSR {r.SsimFsr:0.0000}、くっきり {r.SsimA4k:0.0000}）")
                + (r.GpuNvMs > 0 ? $"。GPU は 1 コマ平均 {r.GpuNvMs:0.0} ms" : string.Empty);
        }
        item.Verdict = Verdict.Info;
        item.Summary = "[おすすめ] " + det + "。" + pick + nv + cost + sharp + rest;
        item.Explanation = "比べた 5 通り（標準 / FSR / 元の細かさ + FSR / くっきり / 元の細かさ + くっきり）のうち、正解の絵に一番近い（SSIM が最大で、同点なら輪が少ない）ものを選んでいます。" +
            "好みや、ゲームごとの絵柄（写実的な絵では差が小さい）は数字に出ないため、最後は画像を見て決めてください。";
    }
}
