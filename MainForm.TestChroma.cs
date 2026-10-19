using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: 色の境目（色のにじみを抑える）. Section 5 (after the Anime4K step).
//  A sharp known picture is encoded to NV12 the way a card does it, converted by the real convert pass and compared with the truth.
// =============================================================================================

public sealed partial class MainForm
{
    private async Task ChromaStep(TestSection sec, List<CaptureMode> modes)
    {
        CaptureMode? mode = PickMode(modes, 1920, 1080, 60) ?? PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
        var item = new TestItem { Name = "色の境目（色のにじみを抑える）" };
        sec.Items.Add(item);
        if (mode == null) { item.Summary = "映像のモードがありません"; return; }
        if (_testCaption != null) _testCaption.Showing = false;
        EnsureEngine(false, mode);
        await Wait(SettleMs);
        if (!_video.SupportsNativeRes)
        {
            item.Verdict = Verdict.Info;
            item.Summary = "Direct3D 描画のときだけ使える機能のため、試験しません";
            return;
        }
        try
        {
            using Bitmap truthBmp = await Task.Run(() => ChromaTest.MakeTruth());
            ChromaTest.Ycc truth = await Task.Run(() => ChromaTest.ToYcc(truthBmp));
            ChromaTest.ConvertFn conv = (e, m) => _video.ConvertSynthetic(e.Y, e.UV, e.W, e.H, VideoAdjustments.Neutral, 1, false, m, 0, out _);
            var all = await Task.Run(() => ChromaTest.RunSynthetic(truthBmp, conv, truth, out _, out _));
            item.Num("方法", "色のはっきりした文字・線・色の帯（1920x1080）を正解とし、カードと同じように NV12（4:2:0、色は縦横とも半分）へ変換して、YuCap の実際の変換処理で RGB に戻し、正解と比べます。色の間引き方は 3 通り（MPEG-2 の左寄せ・なめらか / YUY2 から NV12 / 2x2 平均の中央寄せ）で試します。");
            var rowsMain = all[ChromaTest.Siting.LeftFilter];
            foreach ((ChromaTest.Siting s, List<ChromaTest.Row> rows) in all)
                foreach (ChromaTest.Row r in rows)
                    item.Num($"{ChromaTest.SitingName(s)} / {r.Name}", ChromaTest.RowText(r));
            ChromaTest.Row? bil = rowsMain.FirstOrDefault(r => r.Name.StartsWith("今まで")), cfl = rowsMain.FirstOrDefault(r => r.Name.Contains("既定"));
            if (bil == null || cfl == null)
            {
                item.Verdict = Verdict.Warn; item.Summary = "変換した絵を作れませんでした（GPU での描画が失敗）";
                return;
            }
            double worstGain = all.Values.Min(rows => rows.First(r => r.Name.Contains("既定")).ChromaPsnrEdge - rows.First(r => r.Name.StartsWith("今まで")).ChromaPsnrEdge);
            double smoothLoss = all.Values.Max(rows => rows.First(r => r.Name.StartsWith("今まで")).SmoothPsnr - rows.First(r => r.Name.Contains("既定")).SmoothPsnr);
            foreach (Rectangle tile in new[] { new Rectangle(14, 14, 200, 112), new Rectangle(100, 925, 200, 112) })
            {
                string nm = $"chroma_{tile.X}_{tile.Y}.png";
                var tl = rowsMain.Where(r => r.Image != null).ToList();
                ChromaTest.SaveCrops(tl, truthBmp, tile, 4, Path.Combine(_rep.Folder, nm), new[] { "bilinear (before)", "left-sited bilinear", "bilateral", "chroma-from-luma (new)" }.Take(tl.Count).ToArray());
                item.Images.Add(nm);
            }

            // real picture: how much it changes
            string real = "";
            using (Bitmap? first = await WaitForDetailedPhoto())
            {
                if (first != null && (first.Width & 1) == 0 && (first.Height & 1) == 0)
                {
                    string line = await Task.Run(() => ChromaTest.RealLine(conv, first, "実際の映像"));
                    item.Num("実際の映像（NV12 に直してから、今までと新しい方法で戻した差）", line.Trim());
                    real = "";
                }
                else real = "（実際の映像は細部が少ないため、実映像での比較は省きました）";
            }
            // cost
            double[] ms0 = Array.Empty<double>(), ms3 = Array.Empty<double>();
            ChromaTest.Encoded enc = await Task.Run(() => ChromaTest.Encode(truth, ChromaTest.Siting.LeftFilter));
            await Task.Run(() => { _video.ConvertSynthetic(enc.Y, enc.UV, enc.W, enc.H, VideoAdjustments.Neutral, 1, false, 0, 300, out ms0)?.Dispose(); });
            await Task.Run(() => { _video.ConvertSynthetic(enc.Y, enc.UV, enc.W, enc.H, VideoAdjustments.Neutral, 1, false, 3, 300, out ms3)?.Dispose(); });
            double a0 = ms0.Length > 0 ? ms0.Average() : double.NaN, a3 = ms3.Length > 0 ? ms3.Average() : double.NaN;
            item.Num("変換の GPU 時間（1080p）", $"今まで {a0:0.000} ms / 新しい方法 {a3:0.000} ms（増える分 {a3 - a0:0.000} ms）");

            bool better = worstGain > 1.0 && smoothLoss < 2.0;
            item.Verdict = better ? Verdict.Ok : Verdict.Warn;
            item.Summary = $"境目の近くの色は PSNR {bil.ChromaPsnrEdge:0.0} → {cfl.ChromaPsnrEdge:0.0} dB、色の境目の幅は {bil.WidthEvenH:0.0} → {cfl.WidthEvenH:0.0} px（正解は約 0.8 px）。増える GPU 時間は 1 コマ {a3 - a0:0.00} ms {real}";
            item.Explanation = "カードの映像（NV12）は、明るさは細かく、色は縦横とも半分の細かさしかありません。今までは色を単純に伸ばしていたため、赤い文字などの縁に色がにじんでいました。" +
                "新しい方法は、細かい明るさの動きから色の変わり目を推定して、色の境目をくっきりさせます。なめらかなグラデーションや写真のような絵は今までと変わりません（「なめらかな所」の PSNR）。" +
                "実映像は、すでにカードで色が半分の細かさになっているため、正解がなく、変化の量だけを見ています。";
            foreach (var rows in all.Values) foreach (ChromaTest.Row r in rows) r.Image?.Dispose();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }
}
