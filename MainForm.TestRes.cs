using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: 映像の実際の細かさ（入力の解像度の推定）。Section 2 of the report, after the capture cases.
//  The capture card does not say what the HDMI input is; this infers it from the picture (ResEstimate.cs).
//  Wording is Japanese only (TestReport.cs).
// =============================================================================================

public sealed partial class MainForm
{
    private async Task ResolutionStep(TestSection sec, List<CaptureMode> modes)
    {
        var picks = new List<CaptureMode>();
        void Add(CaptureMode? m) { if (m != null && !picks.Contains(m)) picks.Add(m); }
        Add(PickMode(modes, 1920, 1080, 60) ?? PickMode(modes, 1920, 1080, 0));
        Add(PickMode(modes, 2560, 1440, 60) ?? PickMode(modes, 2560, 1440, 0));
        Add(PickMode(modes, 3840, 2160, 30) ?? PickMode(modes, 3840, 2160, 0));
        if (picks.Count == 0) Add(modes.FirstOrDefault());
        foreach (CaptureMode m in picks)
        {
            try { await ResolutionCase(sec, m); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sec.Items.Add(new TestItem
                {
                    Name = $"映像の実際の細かさ（{ModeLabel(m)}）", Verdict = Verdict.Info,
                    Summary = "この項目は実行できませんでした。", Explanation = "理由: " + ex.Message,
                });
            }
        }
    }

    private async Task ResolutionCase(TestSection sec, CaptureMode mode)
    {
        var item = new TestItem { Name = $"映像の実際の細かさ（入力の解像度の推定）: {ModeLabel(mode)}" };
        // A caption over the video makes Windows compose the monitor; keep it away like the other measuring steps.
        if (_testCaption != null) _testCaption.Visible = false;
        _settings.Interpolation = false;
        EnsureEngine(false, mode);
        await Wait(SettleMs);
        double nominal = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
        await WaitStableRate(nominal);

        // Several stills at the source's own size; black / flat ones are skipped (retry for up to ~5 s for the first
        // usable one, then a few more for the median).
        var results = new List<ResEstimate.FrameResult>();
        Bitmap? best = null; double bestDetail = -1;
        int tried = 0;
        var sw = Stopwatch.StartNew();
        int firstAt = -1;
        try
        {
            while (results.Count < 5 && tried < 40)
            {
                if (results.Count == 0 && sw.ElapsedMilliseconds > 5000) break;
                if (results.Count > 0 && sw.ElapsedMilliseconds > firstAt + 3500) break;
                Bitmap? b = await Task.Run(() => _video.PhotoSnapshot());
                tried++;
                if (b == null) Log.Info($"res-estimate {ModeLabel(mode)}: attempt {tried} - no still");
                if (b != null)
                {
                    var (r, usable) = await Task.Run<(ResEstimate.FrameResult?, bool)>(() =>
                    {
                        float[] y = ResEstimate.LumaOf(b, out int w, out int h);
                        double det = ResEstimate.MeanGradient(y, w, h);
                        bool msg = ResEstimate.LooksLikeCardMessage(y, w, h);
                        Log.Info($"res-estimate {ModeLabel(mode)}: attempt {tried} - {w}x{h} detail {det:0.00}, mean luma {y.Average():0.0}{(msg ? ", card message" : "")}");
                        // the card's own message screen is sparse text on black: keep it so it can be named as such
                        if (det < ResEstimate.MinDetail && !msg) return (null, false);
                        return (ResEstimate.Analyze(y, w, h), true);
                    });
                    if (usable && r != null)
                    {
                        results.Add(r);
                        if (firstAt < 0) firstAt = (int)sw.ElapsedMilliseconds;
                        if (r.Detail > bestDetail) { best?.Dispose(); best = b; bestDetail = r.Detail; b = null; }
                    }
                    b?.Dispose();
                }
                await Wait(results.Count == 0 ? 250 : 400);
            }

            Size cap = _video.DisplayResolution;
            item.Num("カードが届けた映像", $"{ModeLabel(mode)}（{cap.Width}x{cap.Height}）");
            if (results.Count == 0)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "映像が暗すぎる（または平らすぎる）ため判定できません";
                item.Explanation = "約 5 秒の間に、細かい部分のある絵が取れませんでした（映像が真っ黒または一色のままでした）。文字や模様のある明るい画面を映してから、もう一度試験してください。";
                sec.Items.Add(item);
                return;
            }

            int fw = results[0].W, fh = results[0].H;
            int estV = ResEstimate.Median(results.Select(r => r.Vertical.EstimateHeight));
            int estH = ResEstimate.Median(results.Select(r => r.Horizontal.EstimateHeight));
            // "縦 N" is a count of lines (the candidate height), "横" the matching count of columns
            int lenH = (int)Math.Round((double)fw * estH / fh), lenV = estV;
            // The vertical estimate is the headline: soft horizontal filtering (the card's, the console's) makes the
            // horizontal one wobble between neighbouring sizes, so it is reported but does not decide.
            int agree = results.Count(r => r.Vertical.EstimateHeight == estV);
            string conf = results.Count >= 3 && agree * 10 >= results.Count * 8 ? "高い"
                : results.Count >= 2 && agree * 10 >= results.Count * 6 ? "ふつう" : "低い";
            string label = ResEstimate.Name(estV);
            string perFrame = string.Join(" / ", results.Select(r => r.Vertical.EstimateHeight == r.Horizontal.EstimateHeight
                ? ResEstimate.Name(r.Vertical.EstimateHeight) : $"横{r.Horizontal.EstimateHeight}・縦{r.Vertical.EstimateHeight}"));

            bool cardMsg = results.Count(r => r.CardMessage) * 2 >= results.Count;
            if (!cardMsg)
            {
                item.Num("推定した細かさ", $"横 {lenH}・縦 {lenV} 相当（{label}）" + (estH == estV ? "" : $"  ※横は {ResEstimate.Name(estH)} 相当と出ました（目安）"))
                    .Num("判定の確かさ", $"{conf}（使えた絵 {results.Count} 枚のうち {agree} 枚が縦方向で同じ判定: {perFrame}）");
                ResEstimate.FrameResult rb = results.OrderByDescending(r => r.Detail).First();
                string Basis(ResEstimate.AxisResult a) => string.Join(" / ", a.Cands.Zip(a.Nr, (c, n) => $"{ResEstimate.Name(c)}: {n:0.000}"));
                item.Num("判定の根拠（その大きさから作り直せなかった割合、縦方向）", Basis(rb.Vertical))
                    .Num("判定の根拠（同、横方向）", Basis(rb.Horizontal) +
                        $"（{ResEstimate.Threshold:0.00} より小さい最小の大きさが推定値。実際の 80% を超える大きさは {ResEstimate.NearNativeThreshold:0.00}）");
            }

            // YuCap's own enlargement to this monitor (data this run already has: capture size, monitor size, scaler)
            Size mon = _topt.Target.Bounds.Size;
            string scaler = _video.SupportsScalingQuality
                ? ((_testSaved?.ScalingQuality ?? _settings.ScalingQuality) == "fsr" ? "FSR（高画質）" : "標準")
                : "標準";
            bool enlarged = mon.Width > cap.Width || mon.Height > cap.Height;
            string upText = enlarged
                ? $"{cap.Width}x{cap.Height} → {mon.Width}x{mon.Height} に拡大して表示（{scaler}）"
                : (mon.Width < cap.Width || mon.Height < cap.Height
                    ? $"{cap.Width}x{cap.Height} → {mon.Width}x{mon.Height} に縮小して表示（{scaler}）"
                    : $"{cap.Width}x{cap.Height} のまま等倍で表示（拡大なし）");
            item.Num("YuCap の拡大", upText);

            // crop at 100% of the most detailed region
            if (best != null)
            {
                Bitmap bb = best;
                Rectangle? tile = await Task.Run(() => TestProbes.BestTile(TestProbes.MeasureEdges(bb), bb.Size));
                if (tile is { } t)
                {
                    string file = $"res_{ModeLabel(mode)}.png";
                    using (Bitmap crop = bb.Clone(t, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        crop.Save(Path.Combine(_rep.Folder, file), System.Drawing.Imaging.ImageFormat.Png);
                    item.Images.Add(file);
                }
            }

            var why = new List<string>();
            bool lower = estV < fh;
            if (cardMsg)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "映像がカード自身のメッセージ画面のようです（入力の細かさは判定できません）";
                why.Add("絵は黒地に白い文字だけで、キャプチャカードが入力を受け取れないときに自分で出す「入力非対応」のような画面の特徴と一致しました（画像を見て確かめてください）。この画面はカードが " +
                    $"{fw}x{fh} で描いたものなので、数字は入力の細かさを表していません。入力側（ゲーム機など）の出力が、このモードで受け取れる解像度・コマ数と合っていない可能性があります。");
            }
            else if (!lower)
            {
                item.Verdict = Verdict.Ok;
                item.Summary = $"入力の細かさをそのまま受け取れています（{label}）";
                why.Add($"絵の細かさは、届いた {cap.Width}x{cap.Height} の大きさいっぱいまであります。映像の入力が {fh}p 相当の細かさで届いていて、拡大された形跡はありません。");
            }
            else
            {
                item.Verdict = Verdict.Info;
                item.Summary = $"{cap.Width}x{cap.Height} で届いていますが、絵の細かさは {label} 相当です（確かさ: {conf}）";
                why.Add($"届いた映像は {cap.Width}x{cap.Height} ですが、中の絵は {label} 相当の細かさしかなく、{label} の絵を {fh}p に引き伸ばしたものと同じ性質でした（縦方向で判定）。" +
                    "つまり YuCap に届く前に（ゲーム機やゲームの描画の解像度、ゲーム機の出力設定、またはキャプチャカードの中で）絵が引き伸ばされています。無いはずの細かさは YuCap でも取り戻せません。FSR は引き伸ばしで甘くなった輪郭をくっきりさせる効果はあります。");
                why.Add("ゲーム機やゲームによっては、出力は 1080p でも、ゲーム自体は 720p などの低い解像度で描いて引き伸ばしていることがあり、その場合は「720p 相当」と出るのが正常です。ゲーム機の出力設定を変えても細かさは増えません。");
            }
            if (cap.Width < mon.Width || cap.Height < mon.Height)
                why.Add($"その映像を、YuCap はこのモニターの大きさ（{mon.Width}x{mon.Height}）まで拡大して表示しています（{scaler}）。");
            if (!cardMsg && estH < estV)
                why.Add($"横方向だけ縦より低く（{ResEstimate.Name(estH)} 相当）出ています。キャプチャカードや映像の出力が横方向をなめらかに（ぼかして）処理していると、横の細かさだけ低く測れ、枚ごとにも揺れます。このため判定は縦方向を基準にしています。");
            why.Add("調べ方: 映像をカードから届いた大きさのまま（拡大も縮小もせずに）数枚取り、横方向と縦方向に別々に調べました。各候補の大きさ（480p〜2160p 相当）まで縮めてから拡大し直し、元の絵をどれだけ再現できるかを見ます。ある大きさから引き伸ばされた絵は、その大きさに縮めて戻すとほぼ元どおりになり、本物の細かい部分がある絵は戻りません。再現できる最小の大きさを、その絵の本当の細かさとしています（枚数の中央値）。" +
                "キャプチャカードは、HDMI の入力が何の大きさかを教えてくれないため、絵から推定しています。ぼやけた絵や、細かい模様の少ない画面では、実際より低く出ることがあります。真っ黒・一色の絵は使っていません。下の画像は、絵の中で線が一番多い 480x270 の部分を、拡大せずそのまま切り出したものです。");
            item.Explanation = string.Join("\n", why);
            sec.Items.Add(item);
        }
        finally { best?.Dispose(); }
    }
}
