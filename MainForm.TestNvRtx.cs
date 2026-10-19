using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: NVIDIA RTX（AI 拡大 = Video Super Resolution）と HDR 表示。
//  拡大の比較そのもの（SSIM・GPU 時間）は A4kTest / MainForm.TestA4k.cs の表に「AI 拡大（NVIDIA RTX）」の行として入っている。
//  ここは (a) VSR が本当に絵を変えているか、(b) HDR 表示（Windows の HDR・スワップチェーンの色空間・RTX Video HDR・PQ の読み戻し）。
//  文言は日本語のみ（TestReport.cs）。
// =============================================================================================

public sealed partial class MainForm
{
    /// <summary>VSR あり / なしで同じテスト画像を拡大して差を見る。同じなら、ドライバーが VSR を適用していない（コントロールパネルの設定）。</summary>
    private async Task NvVsrCheck(TestItem item, A4kRecommendation rec)
    {
        try
        {
            if (!D3DRenderer.NvAdapterPresent())
            {
                item.Verdict = Verdict.Info; item.Summary = "[情報] NVIDIA の GPU ではないため、AI 拡大（NVIDIA RTX）は使えません（拡大の画質は FSR などになります）";
                return;
            }
            D3DRenderer.NvMeasureDue = true;
            double d = await Task.Run(() => _video.MeasureVsr());
            item.Num("方法", "960x540 のテスト画像（細い線・文字・縞）を、ビデオプロセッサーで 1920x1080 に拡大し、VSR をオンにした結果とオフにした結果の差（平均絶対差、0〜255）を見ます");
            if (d < 0)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "[情報] NVIDIA の拡張を呼べなかったため、VSR を試せませんでした";
                item.Explanation = "理由: " + (D3DRenderer.NvNote.Length > 0 ? D3DRenderer.NvNote : NvVideoProc.LastFailure) + "。この場合、拡大の画質で「AI 拡大（NVIDIA RTX）」を選んでも FSR になります。";
                return;
            }
            item.Num("VSR あり / なしの差", $"{d:0.000}");
            item.Num("使っている拡張", "ID3D11VideoContext::VideoProcessorSetStreamExtension（NVIDIA、super resolution）");
            rec.NvEffectiveKnown = true; rec.NvEffective = d > 0.05;
            if (d > 0.05)
            {
                item.Verdict = Verdict.Ok;
                item.Summary = $"VSR が働いています（オン / オフの差 {d:0.00}）";
                item.Explanation = "NVIDIA のドライバーが、ビデオプロセッサーの拡大に RTX ビデオ超解像を適用しています（NVIDIA コントロールパネルの「RTX ビデオ強調」がオンです）。";
            }
            else
            {
                item.Verdict = Verdict.Info;
                item.Summary = "[情報] VSR が働いていません（NVIDIA コントロールパネルの設定）";
                item.Explanation = "拡張の呼び出しは成功しましたが、オンとオフで絵が同じでした。NVIDIA コントロールパネル → 「ビデオ イメージ設定の調整」 → 「RTX ビデオ強調」 → 「Super Resolution」にチェックを入れ（品質は自動でよい）、もう一度試験してください。ドライバーが古い、または GeForce RTX 以外の GPU のときも働きません。";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
    }

    private static readonly int[] HdrCodes = { 0, 32, 64, 128, 180, 220, 255 };

    /// <summary>HDR 表示: モニターの HDR、スワップチェーンに設定した色空間、RTX Video HDR が使えるか、既知の SDR の灰色が PQ でどの明るさ（nits）になるか。</summary>
    private async Task HdrDisplayStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "HDR で表示（モニターの HDR・色空間・RTX Video HDR・PQ の読み戻し）", Verdict = Verdict.Info };
        sec.Items.Add(item);
        string savedMode = _settings.HdrMode;
        try
        {
            Program.OutputHdr? h = null;
            try { h = Program.QueryOutputsHdr().FirstOrDefault(x => string.Equals(x.DeviceName, _topt.Target.DeviceName, StringComparison.OrdinalIgnoreCase)); } catch { }
            bool tvHdr = h is { HasInfo: true, HdrOn: true };
            item.Num("試験するモニター", $"{_topt.Target.DeviceName}（Windows の HDR: {(h == null || !h.HasInfo ? "情報なし" : tvHdr ? $"オン、最大輝度 {h.MaxLuminance:0} nits" : "オフ")}）");
            bool nvGpu = D3DRenderer.NvAdapterPresent();
            item.Num("NVIDIA の GPU", nvGpu ? "あり（RTX Video HDR を試せます）" : "なし（RTX Video HDR は使えません。簡易の HDR のみ）");

            CaptureMode? mode = PickMode(modes, 1920, 1080, 60) ?? PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
            if (mode == null) { item.Summary = "映像のモードがありません"; return; }
            EnsureEngine(false, mode);
            await Wait(SettleMs);
            if (!_video.SupportsNativeRes)
            {
                item.Summary = "[情報] Direct3D 描画のときだけ使える機能のため、試験しません";
                return;
            }
            if (!tvHdr)
            {
                item.Summary = "[情報] このモニターの Windows HDR がオフのため、HDR 表示は試験できません";
                item.Explanation = "HDR 表示は、Windows でそのモニターの HDR をオンにしているときだけ使えます（オフのときはメニューの「HDR 表示」が選べず、SDR のままです）。\n" +
                    "オンにする手順: 設定 → システム → ディスプレイ → 対象のモニターを選ぶ → 「HDR を使う」をオン（ショートカット: Win + Alt + B）。そのあと、もう一度この試験をしてください。\n" +
                    "NVIDIA コントロールパネルの「RTX ビデオ強調 → ハイダイナミックレンジ」は、Windows の HDR がオンのモニターでだけ選べます。";
                return;
            }

            string where = Displays.DeviceNameOfWindow(_canvas.Handle);
            item.Num("YuCap の画面があるモニター", where.Length > 0 ? where : "不明");
            float paper = D3DRenderer.HdrPaperWhite, peak = D3DRenderer.HdrPeak;
            var verdicts = new List<(string Name, Verdict V, string Text)>();
            foreach (string md in new[] { "simple", "rtx" })
            {
                if (md == "rtx" && !nvGpu) continue;
                _settings.HdrMode = md;
                ApplyRenderOptions();
                int want = md == "rtx" ? 1 : 2;
                for (int i = 0; i < 12 && _video.HdrKind != want; i++) await Wait(500);
                int kind = _video.HdrKind;
                string tag = md == "rtx" ? "RTX Video HDR" : "簡易";
                item.Num($"{tag}: 実際の表示", kind == 0 ? "SDR のまま（HDR にならなかった）" : kind == 1 ? "RTX Video HDR（ビデオプロセッサー）" : "簡易（自前のシェーダー）");
                if (kind == 0) { verdicts.Add((tag, Verdict.Warn, "HDR 表示に切り替わりませんでした（" + (_video.HdrNote ?? "理由不明") + "）")); continue; }
                if (kind != want) item.Num($"{tag}: 切り替え先", "RTX Video HDR は使えず簡易になりました（" + (_video.HdrNote ?? "理由不明") + "）");
                item.Num($"{tag}: スワップチェーンの色空間", D3DRenderer.HdrSwapColorSpace);

                int usedKind = 0; string why = "";
                float[]? n = await Task.Run(() => _video.HdrLiveCheck(HdrCodes, out usedKind, out why));
                if (n == null) { verdicts.Add((tag, Verdict.Info, "読み戻せませんでした: " + why)); continue; }
                item.Num($"{tag}: 灰色 {string.Join(" / ", HdrCodes)}（sRGB の値）の明るさ", string.Join(" / ", n.Select(x => x < 1 ? $"{x:0.00}" : $"{x:0}")) + " nits");
                bool mono = true;
                for (int i = 1; i < n.Length; i++) mono &= n[i] >= n[i - 1] - 0.5f;
                float white = n[^1], mid = n[3], max = h!.MaxLuminance > 0 ? h.MaxLuminance : 1000f;
                bool clipped = white >= 9900f;
                bool expands = white > paper * 1.5f;
                bool tooHigh = white > max * 1.05f;
                // 簡易: 設計どおり（中間は紙白に比例、白は peak）。RTX: 値そのものは AI 任せなので、単調・白つぶれなし・モニターの最大輝度以内かを見る
                if (usedKind == 2)
                {
                    float expectMid = paper * 0.2159f;     // sRGB 128 -> 線形 0.2159
                    bool midOk = Math.Abs(mid - expectMid) <= expectMid * 0.1f;
                    bool whiteOk = Math.Abs(white - peak) <= peak * 0.1f;
                    bool ok = mono && midOk && whiteOk && !clipped && expands;
                    string txt = $"白 {white:0} nits（設計 {peak:0}）、中間の灰色 {mid:0.0} nits（紙白 {paper:0} nits から {expectMid:0.0}）" + (ok ? "" : " — 設計とずれています");
                    if (md == "rtx") verdicts.Add((tag, Verdict.Info, "[情報] RTX Video HDR は使えず、簡易になりました（" + (_video.HdrNote ?? "理由不明") + "）。簡易の結果: " + txt));
                    else verdicts.Add((tag, ok ? Verdict.Ok : Verdict.Warn, txt));
                }
                else
                {
                    bool ok = mono && !clipped && !tooHigh && white >= paper;
                    verdicts.Add((tag, ok ? Verdict.Ok : Verdict.Warn,
                        $"白 {white:0} nits、中間の灰色 {mid:0.0} nits（モニターの最大 {max:0} nits）" + (clipped ? " — 白が PQ の上限で潰れています" : tooHigh ? " — モニターの最大輝度を超えています" : "")));
                }
            }
            _settings.HdrMode = savedMode;
            ApplyRenderOptions();

            foreach (var v in verdicts) item.Num($"{v.Name} の判定", v.Text);
            Verdict worst = verdicts.Count == 0 ? Verdict.Info : verdicts.Any(v => v.V == Verdict.Warn) ? Verdict.Warn : verdicts.All(v => v.V == Verdict.Ok) ? Verdict.Ok : Verdict.Info;
            item.Verdict = worst;
            item.Summary = worst == Verdict.Ok ? "HDR 表示は意図どおりの明るさで出ています（" + string.Join(" / ", verdicts.Select(v => v.Name + " " + v.Text.Split('（')[0].Trim())) + "）"
                : (worst == Verdict.Info && verdicts.Count > 0 && verdicts.All(v => v.V != Verdict.Warn) ? "[情報] " : "") + "HDR 表示: " + string.Join(" / ", verdicts.Select(v => v.Name + ": " + v.Text.Replace("[情報] ", "")));
            item.Explanation = "表示を HDR10（R10G10B10A2、PQ、BT.2020）のスワップチェーンに切り替え、既知の SDR の灰色の帯をいまの表示の経路そのままで描き、画面に出す直前の HDR10 のバッファを読み戻して PQ を nits に直しています。" +
                $"簡易は、SDR の白を紙白（{paper:0} nits）の基準に置き、{D3DRenderer.HdrKnee:0.0}（線形）より明るい部分をなめらかに広げて SDR の白をおよそ {peak:0} nits まで持ち上げます（オーバーレイ・OSD は紙白のまま）。" +
                "RTX Video HDR は NVIDIA のドライバーが AI で SDR から HDR を作るため、数字は設計ではなく、潰れ・最大輝度の超過がないかだけを見ています。静止画の保存は SDR のままです。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
        finally
        {
            if (_settings.HdrMode != savedMode) { _settings.HdrMode = savedMode; ApplyRenderOptions(); }
        }
    }

    private static readonly int[] WideCodes = { 16, 64, 128, 192, 255 };

    /// <summary>Windows の HDR がオンのモニターでの出力形式と更新の速さ。B8G8R8A8 の合成スワップチェーンはこの条件でメインのモニターの更新（約 60 Hz）に抑えられるため、
    /// 10bit の SDR スワップチェーンで出している。何も重ねない状態の実測の更新が画面の更新に近いか、絵の値（SDR の灰色）が変わっていないかを見る。</summary>
    private async Task WideSdrStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "Windows の HDR がオンのモニターでの出力形式と更新の速さ", Verdict = Verdict.Info };
        sec.Items.Add(item);
        InfoMode wasInfo = CurrentInfoMode;
        try
        {
            string dev = _topt.Target.DeviceName;
            Program.OutputHdr? h = null;
            try { h = Program.QueryOutputsHdr().FirstOrDefault(x => string.Equals(x.DeviceName, dev, StringComparison.OrdinalIgnoreCase)); } catch { }
            bool hdrOn = h is { HasInfo: true, HdrOn: true };
            double panel = _topt.Target.RefreshHz;
            DisplayInfo? primary = Displays.List().FirstOrDefault(d => d.Primary);
            item.Num("試験するモニター", $"{dev}（Windows の HDR: {(h == null || !h.HasInfo ? "情報なし" : hdrOn ? "オン" : "オフ")}、更新 {panel:0} Hz、メインのモニター {(primary != null ? primary.RefreshHz + " Hz" : "不明")}）");
            float white = hdrOn ? Displays.SdrWhiteNits(dev) : 0;
            if (hdrOn) item.Num("Windows の「SDR コンテンツの明るさ」", white > 0 ? $"白 = {white:0} nits（設定 → システム → ディスプレイ → HDR）" : "読み取れませんでした");

            CaptureMode? mode = PickInterpMode(modes, panel);
            bool interp = mode != null;
            mode ??= PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
            if (mode == null) { item.Summary = "映像のモードがないため調べられませんでした"; return; }
            if (_testCaption != null) _testCaption.Showing = false;
            _settings.Interpolation = interp;
            EnsureEngine(false, mode);
            if (wasInfo != InfoMode.Off) SetInfoMode(InfoMode.Off);      // nothing over the video
            await Wait(SettleMs);
            if (!_video.SupportsNativeRes) { item.Summary = "[情報] Direct3D 描画のときだけ使える機能のため、試験しません"; return; }
            double srcFps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
            bool interpOn = interp && _video.InterpolationState is { Active: true };
            double expect = interpOn ? panel : Math.Min(srcFps, panel);
            item.Num("映像", ModeLabel(mode) + (interpOn ? "（フレーム補間オン）" : "（フレーム補間なし）"));
            for (int i = 0; i < 6 && hdrOn && !_video.WideSdrActive; i++) await Wait(500);
            string fmt = _video.SwapFormat;
            item.Num("スワップチェーンの形式", fmt.Length > 0 ? fmt : "不明（Direct3D ではない）");
            var rate = await PresentRate(3000, srcFps, panel);
            item.Num("何も重ねていないときの更新", $"{rate.Rate:0.0} fps" + (rate.ObservedHz > 0 ? $"（実測の更新 {rate.ObservedHz:0.0} Hz）" : "") + $"、期待 {expect:0} fps");

            if (!hdrOn)
            {
                item.Summary = $"[情報] このモニターの Windows HDR はオフです（出力は {(fmt.Length > 0 ? fmt : "?")}、更新 {rate.Rate:0.0} fps）";
                item.Explanation = "Windows の HDR がオンのモニターでだけ、10bit の SDR 出力に切り替わります（オフのときは従来どおり 8bit です）。HDR がオンのモニターでこの項目を試験すると、出力形式・更新の速さ・絵の値の確認ができます。";
                return;
            }

            bool wide = _video.WideSdrActive;
            string? rampText = null; bool rampOk = false;
            if (wide)
            {
                string why = "";
                float[]? got = await Task.Run(() => _video.WideLiveCheck(WideCodes, out why));
                if (got == null) rampText = "読み戻せませんでした: " + why;
                else
                {
                    double worst = 0;
                    for (int i = 0; i < got.Length; i++) worst = Math.Max(worst, Math.Abs(got[i] - WideCodes[i]));
                    rampOk = worst <= 1.0;
                    item.Num($"灰色 {string.Join(" / ", WideCodes)}（sRGB の値）の書き込み", string.Join(" / ", got.Select(x => x.ToString("0.0"))) + $"（最大のずれ {worst:0.00}）");
                    rampText = rampOk ? "SDR の絵の値は変わっていません" : $"SDR の絵の値が最大 {worst:0.0} ずれています";
                }
                if (white > 0)
                {
                    // Windows が sRGB の灰色を HDR の画面に置き直す明るさ（SDR の白 = 「SDR コンテンツの明るさ」、中間の灰色は線形 0.2159 倍）
                    item.Num("Windows が置く明るさ（計算）", $"白 {white:0} nits、中間の灰色（128）{white * 0.2159:0.0} nits、暗い灰色（64）{white * 0.0513:0.00} nits");
                }
            }

            bool rateOk = rate.Rate >= expect * 0.93;
            if (wide && rateOk && rampOk)
            {
                item.Verdict = Verdict.Ok;
                item.Summary = $"HDR モニターでも更新は落ちません（10bit の SDR 出力で {rate.Rate:0} fps、期待 {expect:0} fps）。{rampText}";
                item.Explanation = "Windows の HDR がオンのモニターでは、8bit の合成スワップチェーンがメインのモニターの更新（約 60 Hz）に抑えられ、144 Hz のモニターでも 60 fps 前後になります（実測）。" +
                    "このため YuCap は、そのモニターに表示しているあいだだけ、10bit（R10G10B10A2）のスワップチェーンで出します。絵は SDR のままで、色の値は変えていません" +
                    "（Windows が他の SDR のウィンドウと同じ明るさ・色に置き直します）。HDR がオフのモニター（メインなど）へ動かすと 8bit に戻ります。";
            }
            else if (!wide)
            {
                item.Verdict = Verdict.Warn;
                item.Summary = $"注意: 10bit の SDR 出力になっていません（{fmt}）。更新 {rate.Rate:0.0} fps";
                item.Explanation = (rate.Rate < expect * 0.93 && primary != null && primary.RefreshHz < panel - 5
                    ? $"Windows の HDR がオンだと、このモニターは約 {primary.RefreshHz} Hz で表示されます（メインが {primary.RefreshHz} Hz のため）。" : "") +
                    "低遅延モードなど、ウィンドウに直接出す描画ではこの切り替えを使いません。そのときの更新が画面の更新に近ければ問題ありません。";
            }
            else if (!rateOk)
            {
                item.Verdict = Verdict.Warn;
                item.Summary = $"注意: 10bit の SDR 出力でも更新が {rate.Rate:0.0} fps に留まりました（期待 {expect:0} fps）";
                item.Explanation = $"Windows の HDR がオンだと、このモニターは約 {(primary != null ? primary.RefreshHz.ToString() : "60")} Hz で表示されます（メインが {(primary != null ? primary.RefreshHz.ToString() : "60")} Hz のため）。" +
                    "対処: 設定 → システム → ディスプレイ で、このモニターの「HDR を使う」をオフにするか、このモニターをメインのモニターにしてください（Win + Alt + B で切り替えられます）。";
            }
            else
            {
                item.Verdict = Verdict.Warn;
                item.Summary = $"10bit の SDR 出力ですが、{rampText}";
                item.Explanation = "更新の速さは足りていますが、SDR の灰色の値が書き込みのあとで変わっています。";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
        finally
        {
            if (CurrentInfoMode != wasInfo) SetInfoMode(wasInfo);
            StopInterpForTest();
        }
    }
}
