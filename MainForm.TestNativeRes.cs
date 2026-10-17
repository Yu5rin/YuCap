using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: 元の細かさに合わせた拡大の効果。Section 5 (FSR の効き), after the FSR comparisons.
//  The pictures and metrics are in NativeResTest.cs; this file only drives the live engine.
//  Wording is Japanese only (TestReport.cs).
// =============================================================================================

public sealed partial class MainForm
{
    private async Task NativeResStep(TestSection sec, List<CaptureMode> modes)
    {
        CaptureMode? mode = PickMode(modes, 1920, 1080, 60) ?? PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
        var itemSynth = new TestItem { Name = "元の細かさに合わせた拡大の効果（既知の絵: 720p を 1080p に引き伸ばしたもの → 4K）" };
        var itemLive = new TestItem { Name = "元の細かさに合わせた拡大の効果（実際の映像）" };
        var itemGpu = new TestItem { Name = "元の細かさに合わせた拡大の GPU 時間（4K 出力）" };
        sec.Items.Add(itemSynth); sec.Items.Add(itemLive); sec.Items.Add(itemGpu);
        if (mode == null)
        {
            foreach (TestItem it in new[] { itemSynth, itemLive, itemGpu }) it.Summary = "映像のモードがありません";
            return;
        }
        if (_testCaption != null) _testCaption.Showing = false;
        EnsureEngine(false, mode);
        await Wait(SettleMs);
        if (!_video.SupportsNativeRes)
        {
            foreach (TestItem it in new[] { itemSynth, itemLive, itemGpu })
            {
                it.Verdict = Verdict.Info;
                it.Summary = "Direct3D 描画のときだけ使える機能のため、試験しません";
            }
            return;
        }

        Bitmap? console = null;
        try
        {
            console = await NativeResSynthetic(itemSynth);
            await NativeResLive(itemLive, mode);
            await NativeResGpu(itemGpu, mode, console);
        }
        finally { console?.Dispose(); }
    }

    private NativeResTest.RenderFn TestRender() =>
        (src, o, q, nh, f) => _video.RenderBitmap(src, o, q, nh, f);

    /// <summary>The known-picture experiment. Returns the simulated console picture (1080p, bilinear) for the GPU timing, or null.</summary>
    private async Task<Bitmap?> NativeResSynthetic(TestItem item)
    {
        try
        {
            NativeResTest.RenderFn render = TestRender();
            using Bitmap truth = await Task.Run(() => NativeResTest.MakeTruth());
            var sw = Stopwatch.StartNew();
            NativeResTest.Setup sb = await Task.Run(() => NativeResTest.RunOne(render, truth, NativeResTest.Kernel.Bilinear));
            NativeResTest.Setup? sc = null;
            try
            {
                sc = await Task.Run(() => NativeResTest.RunOne(render, truth, NativeResTest.Kernel.CatmullRom));
                if (sb.Rows.Any(r => r == null) || sc.Rows.Any(r => r == null))
                {
                    item.Verdict = Verdict.Warn; item.Summary = "拡大した絵を作れませんでした（GPU での描画が失敗）";
                    return null;
                }
                item.Num("方法", "4K の細かい絵（文字・細い線・縞・市松・円・グラデーション・写真のようなノイズ）を正解とし、それを 1280x720 に縮めた絵を「ゲームの描画」、1920x1080 に引き伸ばした絵を「ゲーム機の出力」とします。これを YuCap の実際の GPU 処理で 3840x2160 にして、正解と比べます");
                foreach ((string title, NativeResTest.Setup s) in new[] { ("バイリニアで引き伸ばし", sb), ("バイキュービックで引き伸ばし", sc) })
                    foreach (NativeResTest.Row r in s.Rows)
                        item.Num($"{title} / {r.Name}", NativeResTest.RowText(r));

                bool okB = NativeResTest.Better(sb, out string whyB);
                bool okC = NativeResTest.Better(sc, out string whyC);
                item.Num("新方式と今までの比較（バイリニア）", (okB ? "新方式が良い: " : "新方式は良くない: ") + whyB);
                item.Num("新方式と今までの比較（バイキュービック）", (okC ? "新方式が良い: " : "新方式は良くない: ") + whyC);

                var (t1, t2) = NativeResTest.Tiles(truth);
                NativeResTest.SaveCrops(sb, t1, Path.Combine(_rep.Folder, "nativeres_text.png"));
                NativeResTest.SaveCrops(sb, t2, Path.Combine(_rep.Folder, "nativeres_edges.png"));
                item.Images.Add("nativeres_text.png");
                item.Images.Add("nativeres_edges.png");

                item.Verdict = okB ? Verdict.Ok : Verdict.Warn;
                item.Summary = okB
                    ? $"元の細かさに合わせて縮めてから FSR にすると、今までより正解に近くなります（SSIM {sb.Rows[1].Ssim:0.0000} → {sb.Rows[2].Ssim:0.0000}、バイキュービックでは {sc.Rows[1].Ssim:0.0000} → {sc.Rows[2].Ssim:0.0000}）"
                    : "元の細かさに合わせて縮めても、今までより正解に近くはなりませんでした";
                item.Explanation = "画像は左から b 今まで（1080p から FSR）、c 新方式（720p に縮めてから FSR）、d 理想（本物の 720p から FSR）、正解（4K）です（480x270 を拡大せずに切り出し。1つ目は文字、2つ目は線の一番多い部分）。\n" +
                    "「輪郭の強さ」は細かい線のくっきり具合を正解と比べた比（1 が正解）、「輪郭の幅」は黒い四角の縁が白から黒へ変わる幅（小さいほどくっきり）、「行き過ぎ」は縁で明るさが正解の範囲を超えた分（輪＝ハロ）、「輪の出た画素」は縁の近くで正解の範囲を 4 段階より大きく超えた画素の割合です。「良い」は、SSIM が上がり、輪郭が正解に近づき（または幅が狭まり）、輪が今までより 1 ポイント・1.5 ポイントを超えて増えないことです。\n" +
                    "縮める処理は、ゲーム機の引き伸ばしを元に戻すように（バックプロジェクション）作り直し、元の絵の周りの明るさの範囲を超えないようにしています。単に縮めるだけの方法は、引き伸ばしでぼやけた絵をそのまま縮めるので今までより悪くなります（NativeResTest の比較で確認）。ゲーム機が引き伸ばしにどの方式を使っているかは分からないため、2 種類で確かめています。方式が大きく違うと効果は小さくなります。" +
                    $"（所要 {sw.Elapsed.TotalSeconds:0} 秒）";
                return (Bitmap)sb.Console1080.Clone();
            }
            finally { sc?.Dispose(); sb.Dispose(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
            return null;
        }
    }

    /// <summary>The live picture: estimate its real detail; if below the capture size, render the same frame both ways at the monitor size.</summary>
    private async Task NativeResLive(TestItem item, CaptureMode mode)
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
            // up to three usable frames: the vertical estimate is the median
            var results = new List<ResEstimate.FrameResult>();
            results.Add(await Task.Run(() => { float[] y = ResEstimate.LumaOf(first, out int w, out int h); return ResEstimate.Analyze(y, w, h); }));
            for (int i = 0; i < 2; i++)
            {
                await Wait(300);
                using Bitmap? b = await Task.Run(() => _video.PhotoSnapshot());
                if (b == null) continue;
                var r = await Task.Run<ResEstimate.FrameResult?>(() =>
                {
                    float[] y = ResEstimate.LumaOf(b, out int w, out int h);
                    if (ResEstimate.LooksLikeCardMessage(y, w, h) || ResEstimate.MeanGradient(y, w, h) < ResEstimate.MinDetail) return null;
                    return ResEstimate.Analyze(y, w, h);
                });
                if (r != null) results.Add(r);
            }
            int fh = first.Height;
            int estV = Math.Min(ResEstimate.Median(results.Select(r => r.Vertical.EstimateHeight)), fh);
            int agree = results.Count(r => r.Vertical.EstimateHeight == estV);
            item.Num("映像", $"{ModeLabel(mode)}（{first.Width}x{first.Height}）")
                .Num("推定した細かさ", $"{ResEstimate.Name(estV)} 相当（使えた絵 {results.Count} 枚のうち {agree} 枚が同じ判定）");
            if (estV >= fh)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "[情報] 映像は届いた大きさいっぱいの細かさがあるため、縮めて拡大する対象になりません";
                item.Explanation = "入力が引き伸ばされていない映像では「元の細かさに合わせて拡大」は何もしません（自動のときは縮めずにそのまま表示します）。720p のゲームを 1080p で出力しているような映像を映すと、効果を調べられます。";
                return;
            }

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
            item.Num("拡大先", $"{outSize.Width}x{outSize.Height}");
            if (outSize.Height <= estV || outSize.Width <= first.Width * estV / fh)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "[情報] このモニターは縮めた大きさ以下のため、比べる意味がありません";
                item.Explanation = $"元の細かさ（{ResEstimate.Name(estV)} 相当）よりモニターが小さいか同じなので、縮める処理は使われません。4K のモニターで試験すると比べられます。";
                return;
            }

            NativeResTest.LiveResult lr = await Task.Run(() => NativeResTest.RunLive(TestRender(), first, outSize, estV));
            try
            {
                if (lr.Std == null || lr.Fsr == null || lr.Pre == null)
                {
                    item.Verdict = Verdict.Warn; item.Summary = "拡大した絵を作れませんでした";
                    return;
                }
                item.Num("輪郭の強さ（標準・縮めない）", $"{lr.GradStd:0.000}")
                    .Num("輪郭の強さ（FSR・縮めない・今まで）", $"{lr.GradFsr:0.000}")
                    .Num($"輪郭の強さ（{ResEstimate.Name(estV)} に縮めてから FSR・新）", $"{lr.GradPre:0.000}（今までの {(lr.GradFsr > 0 ? lr.GradPre / lr.GradFsr : 0):0.00} 倍）")
                    .Num("輪の出た画素（今まで）", $"{lr.RingFsr:0.00}%（平均の行き過ぎ {lr.ExcessFsr:0.0} 段階）")
                    .Num("輪の出た画素（新）", $"{lr.RingPre:0.00}%（平均の行き過ぎ {lr.ExcessPre:0.0} 段階）");
                Rectangle? tile = await Task.Run(() => TestProbes.BestTile(TestProbes.MeasureEdges(lr.Pre), lr.Pre.Size));
                if (tile is { } t)
                {
                    string file = $"nativeres_live.png";
                    TestProbes.SaveStrip(new[] { lr.Std, lr.Fsr, lr.Pre }, new[] { "standard", "FSR (today)", $"{ResEstimate.Name(estV)}+FSR (new)" }, t, Path.Combine(_rep.Folder, file));
                    item.Images.Add(file);
                }
                bool sharper = lr.GradPre > lr.GradFsr * 1.0;
                bool ring = lr.RingPre <= lr.RingFsr + 3.0;
                item.Verdict = sharper && ring ? Verdict.Ok : Verdict.Info;
                item.Summary = sharper
                    ? $"{ResEstimate.Name(estV)} 相当の映像を、縮めてから FSR にすると輪郭が {lr.GradPre / Math.Max(1e-9, lr.GradFsr):0.00} 倍くっきりします" + (ring ? "" : "（ただし輪が増えます）")
                    : "縮めても、今までより輪郭はくっきりしませんでした";
                item.Explanation = "同じ 1 枚の絵を 3 通りで拡大して比べています（左から 標準 / FSR / 縮めてから FSR）。正解の絵がないため、ここでは輪郭の強さと「輪」の量だけを測っています。輪の量は標準（双線形）の拡大を基準にしているため、くっきりした絵ほど大きく出ます。数字は参考で、良し悪しは画像を見て決めてください。";
            }
            finally { lr.Dispose(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }

    /// <summary>GPU time of the extra pass at 4K output (the known 1080p picture, FSR), and of the whole frame with and without it.</summary>
    private async Task NativeResGpu(TestItem item, CaptureMode mode, Bitmap? console)
    {
        try
        {
            if (console == null)
            {
                item.Verdict = Verdict.Info; item.Summary = "[情報] 既知の絵を作れなかったため、測りません";
                return;
            }
            Size out4k = new(3840, 2160);
            double[] plain = Array.Empty<double>(), with = Array.Empty<double>(), pass = Array.Empty<double>();
            bool ok = await Task.Run(() => _video.BenchPrescale(console, out4k, ScalingQuality.Fsr, 720, -1, 300, out plain, out with, out pass));
            if (!ok || plain.Length == 0 || with.Length == 0)
            {
                item.Verdict = Verdict.Info; item.Summary = "[情報] この GPU では時間を測れませんでした（タイムスタンプ未対応など）";
                return;
            }
            double nominal = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
            double budget = 1000.0 / Math.Max(1.0, nominal);
            double extra = with.Average() - plain.Average();
            item.Num("出力", "3840x2160 / FSR / 1080p の絵（720p に縮める）")
                .Num("1コマの GPU 時間（縮めない）", $"平均 {plain.Average():0.00} ms / 最大 {plain.Max():0.00} ms")
                .Num("1コマの GPU 時間（縮める）", $"平均 {with.Average():0.00} ms / 最大 {with.Max():0.00} ms")
                .Num("縮める処理だけ", $"平均 {pass.Average():0.00} ms / 最大 {pass.Max():0.00} ms")
                .Num("増える時間（平均）", $"{extra:0.00} ms")
                .Num("1コマの時間（映像 " + $"{nominal:0}fps）", $"{budget:0.0} ms");
            bool fine = with.Max() <= budget * 0.8 && extra <= budget * 0.25;
            item.Verdict = fine ? Verdict.Ok : Verdict.Warn;
            item.Summary = fine
                ? $"縮める処理で GPU 時間は平均 {extra:0.00} ms 増えますが、1コマ {budget:0.0} ms に対して余裕があります"
                : $"縮める処理で GPU 時間が平均 {extra:0.00} ms 増え、1コマ {budget:0.0} ms に対して余裕が少なくなります";
            item.Explanation = "縮める処理は、映像の大きさ（ここでは 1080p）で決まる固定の負荷で、出力が 4K でも 1080p でも変わりません。入力が 4K のときは約 4 倍になります。表示の GPU 時間（「表示の計測」の項目）の中に含まれます。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }
}
