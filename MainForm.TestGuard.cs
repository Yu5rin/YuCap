using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: 負荷の自動調整 (GPU budget guard). Section "表示の計測", after the interpolation cases.
//  The 1080p60 / くっきり / フレーム補間 case normally fits its 144 Hz budget, so the guard stays out of it. To see its decisions the
//  same case is run once more with the budget it compares with scaled down (QualityGuard.BudgetScale), i.e. as on a GPU 2.5x slower.
// =============================================================================================

public sealed partial class MainForm
{
    private async Task GuardStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "負荷の自動調整（1080p60 / くっきり / フレーム補間）" };
        sec.Items.Add(item);
        CaptureMode? mode = PickMode(modes, 1920, 1080, 60);
        if (mode == null || !_video.SupportsInterpolation) { item.Verdict = Verdict.Info; item.Summary = "[情報] 1080p60 のモードか Direct3D 描画がないため、試験しません"; return; }
        if (_testCaption != null) _testCaption.Showing = false;
        double panel = _topt.Target.RefreshHz;
        try
        {
            _settings.ScalingQuality = "anime4k";
            _settings.Interpolation = true;
            EnsureEngine(false, mode);
            await Wait(SettleMs + 2000);
            string Gpu(List<RenderStats> st) => st.Count == 0 ? "n/a" : $"GPU 平均 {st.Average(s => s.GpuMsAvg):0.00} ms / 最大 {st.Max(s => s.GpuMsMax):0.00} ms, 表示 {st.Average(s => s.PresentedFps):0.0} fps";
            List<RenderStats> before = await CollectStats(3000);
            item.Num("普通の条件（予算 = 更新 1 回）", Gpu(before) + (_video.GuardStatus != null ? "（調整が働いています）" : "（調整は働いていません）"));

            // Pretend the GPU is slower: the budget is scaled so the measured GPU time is ~110% of it, whatever the panel's refresh is
            // (a fixed 0.4 never reached the 85% trigger on a 60 Hz panel, whose refresh budget is already 2.4x longer than at 144 Hz).
            double avgBefore = before.Count > 0 ? before.Average(s => s.GpuMsAvg) : 0;
            double fullBudget = 1000.0 / Math.Max(24, panel > 0 ? panel : 60);
            float scale = avgBefore > 0.05 ? (float)Math.Clamp(avgBefore / 1.1 / fullBudget, 0.01, 0.4) : 0.4f;
            QualityGuard.BudgetScale = scale;
            try
            {
                await Wait(7000);
                List<RenderStats> during = await CollectStats(3000);
                string stat = _video.GuardStatus ?? "（調整なし）";
                item.Num($"遅い GPU を想定（予算を {scale:0.##} 倍に）", Gpu(during) + " / 状態: " + stat);
                item.Num("調整の経過", _video.GuardLog.Count == 0 ? "なし" : string.Join(" / ", _video.GuardLog));
                bool acted = _video.GuardLog.Count > 0;
                double fpsBefore = before.Count > 0 ? before.Average(s => s.PresentedFps) : 0, fpsDuring = during.Count > 0 ? during.Average(s => s.PresentedFps) : 0;
                item.Verdict = acted && fpsDuring >= panel * 0.97 ? Verdict.Ok : Verdict.Warn;
                item.Summary = acted
                    ? $"予算を超えたとき、約 2 秒で軽くする方向に動きました（{stat}）。表示は {fpsBefore:0.0} → {fpsDuring:0.0} fps（モニター {panel:0} Hz）"
                    : "予算を超えても調整は働きませんでした";
                item.Explanation = "GPU の時間が更新 1 回の 85% を約 2 秒超えると、くっきり（M）→ くっきり（S）→ FSR の順に軽くし、60% を下回る状態が約 10 秒続くと 1 段ずつ戻します。フレーム補間は止めません。設定は書き換えません。実際の GPU はこの映像では余裕があるため、ここでは比べる予算を小さくして、判断だけを確かめています。";
            }
            finally { QualityGuard.BudgetScale = 1f; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Info; item.Summary = "この項目は実行できませんでした。"; item.Explanation = "理由: " + ex.Message;
        }
        finally { _settings.Interpolation = false; try { _video.Interpolation = false; } catch { } }
    }
}
