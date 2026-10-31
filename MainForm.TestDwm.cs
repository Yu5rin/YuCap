using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

public sealed partial class MainForm
{
    /// <summary>A small topmost window that never takes the focus: stands in for a menu, a dialog or another program's window over the video.</summary>
    private sealed class NoActivateForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }   // NOACTIVATE | TOOLWINDOW
    }

    private string DescribeVideoOverlap(out int count)
    {
        List<VideoOverlap.Item> over = VideoOverlap.Find(FindForm()?.Handle ?? Handle, _canvas.RectangleToScreen(_canvas.ClientRectangle));
        count = over.Count;
        return VideoOverlap.Describe(over);
    }

    private async Task<double> PresentedRate(int ms)
    {
        long p0 = _video.FramesPresented;
        var sw = Stopwatch.StartNew();
        await Wait(ms);
        double el = sw.Elapsed.TotalSeconds;
        return el > 0 ? (_video.FramesPresented - p0) / el : 0;
    }

    /// <summary>Presented frames per second in <paramref name="bins"/> consecutive 250 ms bins.</summary>
    private async Task<double[]> RateBins(int bins)
    {
        var r = new double[bins];
        for (int i = 0; i < bins; i++) r[i] = await PresentedRate(250);
        return r;
    }

    /// <summary>Seconds until two consecutive bins reach 90 % of <paramref name="expect"/>; -1 = never.</summary>
    private static double RecoverySeconds(double[] bins, double expect)
    {
        for (int i = 0; i + 1 < bins.Length; i++) if (bins[i] >= expect * 0.9 && bins[i + 1] >= expect * 0.9) return i * 0.25;
        return -1;
    }

    private static string BinsText(double[] b) => string.Join(" ", b.Select(x => x.ToString("0")));

    private sealed record GateResult(double Rate, double WaitedSec, bool Recovered, bool Nudged, string Overlap);

    /// <summary>Before a display measurement: is the monitor presenting at its full rate? When not, wait up to 5 s (nudging the swap chain after 1.5 s if
    /// nothing overlaps the video) and say which windows overlap, so a low reading is explained and not blamed on YuCap.</summary>
    private async Task<GateResult> DisplayGate(double expect)
    {
        double r = await PresentedRate(1000);
        if (expect <= 5 || r >= expect * 0.9) return new GateResult(r, 0, true, false, string.Empty);
        string overlap = DescribeVideoOverlap(out int n);
        var sw = Stopwatch.StartNew();
        bool nudged = false;
        while (sw.Elapsed.TotalSeconds < 5)
        {
            if (!nudged && n == 0 && sw.Elapsed.TotalSeconds >= 1.5)
            {
                nudged = true;
                Log.Info($"test: display presents {r:0} fps, expected {expect:0}, nothing overlaps the video - nudging the swap chain");
                await Task.Run(() => _video.NudgeDisplay());
            }
            r = await PresentedRate(500);
            if (r >= expect * 0.9) return new GateResult(r, sw.Elapsed.TotalSeconds, true, nudged, n > 0 ? overlap : string.Empty);
        }
        overlap = DescribeVideoOverlap(out n);
        return new GateResult(r, sw.Elapsed.TotalSeconds, false, nudged, n > 0 ? overlap : string.Empty);
    }

    /// <summary>How fast the update comes back after something that overlapped the video went away (a menu, a dialog, another program's window), without and with the nudge.
    /// On a monitor that refreshes faster than the primary one, Windows composes the video while it is overlapped and holds it to the primary's rate.</summary>
    private async Task OverlapRecoveryStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "重なりが消えたあとの更新の戻り" };
        sec.Items.Add(item);
        double panel = _topt.Target.RefreshHz;
        DisplayInfo? primary = Displays.List().FirstOrDefault(d => d.Primary);
        if (primary == null || _topt.Target.Primary || panel < primary.RefreshHz + 10)
        {
            item.Verdict = Verdict.Info;
            item.Summary = "[情報] メインのモニターより十分速いモニターではないため、この低下は起きません";
            item.Num("更新の速さ", $"試験するモニター {panel:0} Hz / メインのモニター {(primary != null ? primary.RefreshHz + " Hz" : "不明")}");
            return;
        }
        CaptureMode? mode = PickInterpMode(modes, panel);
        bool interp = mode != null;
        mode ??= PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
        if (mode == null) { item.Summary = "映像のモードがないため調べられませんでした"; return; }
        if (_testCaption != null) _testCaption.Showing = false;
        InfoMode wasInfo = CurrentInfoMode;
        try
        {
            _settings.Interpolation = interp;
            EnsureEngine(false, mode);
            if (wasInfo != InfoMode.Off) SetInfoMode(InfoMode.Off);
            await Wait(SettleMs);
            if (!_video.SupportsNativeRes) { item.Summary = "[情報] Direct3D 描画のときだけ使える機能のため、試験しません"; return; }
            double srcFps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
            bool interpOn = interp && _video.InterpolationState is { Active: true };
            double expect = interpOn ? panel : Math.Min(srcFps, panel);
            item.Num("映像", ModeLabel(mode) + (interpOn ? "（フレーム補間オン）" : "（フレーム補間なし）"))
                .Num("更新の速さ", $"試験するモニター {panel:0} Hz / メインのモニター {primary.RefreshHz} Hz、期待 {expect:0} fps");
            if (expect <= primary.RefreshHz * 1.1)
            {
                item.Verdict = Verdict.Info;
                item.Summary = $"[情報] 映像が {expect:0} fps までしか出ない（メインのモニターと同じ程度）ため、この低下は見えません";
                return;
            }
            GateResult gate = await DisplayGate(expect);
            item.Num("重ねる前の更新", $"{gate.Rate:0.0} fps" + (gate.WaitedSec > 0 ? $"（戻るまで {gate.WaitedSec:0.0} 秒待ちました）" : ""));
            if (!gate.Recovered)
            {
                item.Verdict = Verdict.Warn;
                item.Summary = $"注意: 何も重ねていないのに更新が {gate.Rate:0} fps で、試験できませんでした";
                item.Explanation = gate.Overlap.Length > 0 ? "映像に重なっているウィンドウ: " + gate.Overlap : "映像に重なっているウィンドウは見つかりませんでした（Windows がこのモニターを合成のまま保っている可能性があります）。";
                return;
            }

            double rWithout = 0, rWith = 0, duringRate = 0;
            double[] binsA = Array.Empty<double>(), binsB = Array.Empty<double>();
            for (int pass = 0; pass < 2; pass++)
            {
                var ov = new NoActivateForm
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
                    BackColor = Color.Magenta, Opacity = 0.5,
                    Bounds = new Rectangle(_topt.Target.Bounds.X + 200, _topt.Target.Bounds.Y + 200, 300, 120),
                };
                ov.Show();
                await Wait(400);
                double during = await PresentedRate(1000);
                if (pass == 0) duringRate = during;
                ov.Close(); ov.Dispose();
                if (pass == 1) await Task.Run(() => _video.NudgeDisplay());
                double[] bins = await RateBins(24);
                double rec = RecoverySeconds(bins, expect);
                if (pass == 0) { binsA = bins; rWithout = rec; }
                else { binsB = bins; rWith = rec; }
                await Wait(1500);
            }
            item.Num("窓を重ねている間の更新", $"{duringRate:0.0} fps")
                .Num("窓を消したあとの更新（250 ms ごと、6 秒）", BinsText(binsA))
                .Num("元に戻るまで（そのまま）", rWithout < 0 ? "6 秒たっても戻りませんでした" : $"{rWithout:0.00} 秒")
                .Num("窓を消した直後にスワップチェーンを再設定したとき（250 ms ごと）", BinsText(binsB))
                .Num("元に戻るまで（再設定あり）", rWith < 0 ? "6 秒たっても戻りませんでした" : $"{rWith:0.00} 秒");
            if (rWithout >= 0 && rWithout <= 1.0)
            {
                item.Verdict = Verdict.Ok;
                item.Summary = $"重なりが消えると更新はすぐ戻ります（{rWithout:0.0} 秒、重なっている間は {duringRate:0} fps）";
                item.Explanation = $"メインのモニター（{primary.RefreshHz} Hz）より速いモニターでは、映像に何かが重なっている間、Windows が映像を合成して表示するため更新がメインのモニターの速さに抑えられます。重なりがなくなれば元の速さに戻ります。" +
                    "メニューやダイアログなど、YuCap 以外のウィンドウも同じです。";
            }
            else if (rWith >= 0 && rWith <= 1.0)
            {
                item.Verdict = Verdict.Warn;
                item.Summary = rWithout < 0 ? "注意: 重なりが消えても更新が戻りませんでした（スワップチェーンの再設定で戻ります）" : $"注意: 重なりが消えてから更新が戻るまで {rWithout:0.0} 秒かかりました（再設定で {rWith:0.0} 秒）";
                item.Explanation = "YuCap は、重なっているウィンドウがないのに更新がメインのモニターの速さのままだと、スワップチェーンを再設定して戻します。";
            }
            else
            {
                item.Verdict = Verdict.Warn;
                item.Summary = "注意: 重なりが消えたあとも、更新が戻りませんでした";
                item.Explanation = "スワップチェーンの再設定でも戻りませんでした。他のウィンドウやキャプチャ中のアプリが映像に重なっていないか確認してください。映像に重なっているウィンドウ: " + DescribeVideoOverlap(out _);
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
