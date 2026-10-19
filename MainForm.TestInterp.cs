using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// =============================================================================================
//  テストモード: フレーム補間の画質（既知の動き・実際の映像）と、重ねた表示による更新の低下
//  Part of section 3 of the report (right after the 補間 cases); wording is Japanese only (TestReport.cs).
// =============================================================================================

public sealed partial class MainForm
{
    /// <summary>The mode frame interpolation is tested with: the fastest source the monitor refreshes clearly above
    /// (the setting only works from refresh >= 1.1 x fps), 1080p first, then NV12, then the smallest picture.</summary>
    private static CaptureMode? PickInterpMode(List<CaptureMode> modes, double refreshHz)
    {
        if (refreshHz <= 0) return null;
        var ok = modes.Where(m => m.Fps > 0 && m.Fps * 1.1 <= refreshHz).ToList();
        if (ok.Count == 0) return null;
        int fps = ok.Max(m => m.Fps);
        return ok.Where(m => m.Fps == fps)
            .OrderByDescending(m => m.Height == 1080 ? 1 : 0)
            .ThenByDescending(m => m.Compression == "NV12" ? 1 : 0)
            .ThenBy(m => (long)m.Width * m.Height).First();
    }

    /// <summary>The mode for the live check: games mostly render at 60 fps or less, so a 60 fps (or the highest rate up to
    /// 60) mode is preferred, because at 120 fps capture a 60 fps game shows every picture twice and the 30 fps one
    /// four times. Falls back to the usual pick when the monitor cannot refresh clearly above any of those.</summary>
    private static CaptureMode? PickLiveInterpMode(List<CaptureMode> modes, double refreshHz)
    {
        if (refreshHz <= 0) return null;
        var ok = modes.Where(m => m.Fps >= 24 && m.Fps <= 60 && m.Fps * 1.1 <= refreshHz).ToList();
        if (ok.Count == 0) return PickInterpMode(modes, refreshHz);
        int fps = ok.Max(m => m.Fps);
        return ok.Where(m => m.Fps == fps)
            .OrderByDescending(m => m.Height == 1080 ? 1 : 0)
            .ThenByDescending(m => m.Compression == "NV12" ? 1 : 0)
            .ThenBy(m => (long)m.Width * m.Height).First();
    }

    private void StopInterpForTest()
    {
        if (_settings.Interpolation) { _settings.Interpolation = false; try { _video.Interpolation = false; } catch { } }
    }

    // =============================================================================================
    //  A-1. Known motion
    // =============================================================================================

    private async Task InterpSynthStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "フレーム補間の画質: 既知の動きでの検算" };
        if (_video is not D3DVideoEngine eng)
        {
            item.Summary = "Direct3D の映像ではないため確認できませんでした";
            sec.Items.Add(item);
            return;
        }
        CaptureMode? mode = PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
        if (_testCaption != null) _testCaption.Showing = false;
        Bitmap? still = null;
        bool noDetail = false;
        try
        {
            if (mode != null)
            {
                _settings.Interpolation = false;
                EnsureEngine(false, mode);
                await Wait(SettleMs);
                using (Bitmap? probe = await WaitForDetailedPhoto()) noDetail = probe == null;
                Size res = _video.DisplayResolution;
                Size outSize = res.Width >= 640 && res.Height >= 360 && res.Width <= 2560 ? res : new Size(1920, 1080);
                if (!noDetail) still = await Task.Run(() => _video.RenderOffscreen(outSize, ScalingQuality.Standard));
            }
            // The verdict comes from a built-in picture (the same every time, with unambiguous detail everywhere), so it
            // does not depend on what the source happens to show; the picture taken from the video is a reference.
            const string file = "interp_synth.png", fileStill = "interp_synth_video.png";
            InterpCheck.SynthResult r = await Task.Run(() => InterpCheck.RunSynthetic(eng, null, Path.Combine(_rep.Folder, file)));
            InterpCheck.SynthResult? rs = still == null ? null
                : await Task.Run(() => InterpCheck.RunSynthetic(eng, still, Path.Combine(_rep.Folder, fileStill)));
            if (rs is { Ok: true, UsedStill: false }) rs = null;       // the video's picture was too flat: nothing to add
            if (!r.Ok)
            {
                item.Verdict = Verdict.Ng;
                item.Summary = "補間を試せませんでした";
                item.Explanation = "理由: " + r.Failure;
                sec.Items.Add(item);
                return;
            }

            double blkBlend = r.BlkBlend;
            bool texOk = r.BgTexN >= 50;
            int vecN = texOk ? r.BgTexN : r.BgN, vecOk = texOk ? r.BgTexOk : r.BgOk;
            double vecFrac = vecN > 0 ? (double)vecOk / vecN : 0;
            item.Num("使った絵（判定用）", $"試験用に作った絵（文字・図形・模様を並べたもの）  {r.Size.Width}x{r.Size.Height}（細かさ {r.Detail:0.0}）")
                .Num("動かし方", $"全体を右へ {InterpCheck.GlobalDx}・上へ {-InterpCheck.GlobalDy} ピクセル、四角い部分だけ左へ {-InterpCheck.BlockDx}・下へ {InterpCheck.BlockDy} ピクセル")
                .Num("背景の PSNR（補間 / 重ねただけ）", $"{r.BgPsnr:0.0} dB / {r.BgBlend:0.0} dB")
                .Num("動く四角の PSNR（補間 / 重ねただけ）", $"{r.BlkPsnr:0.0} dB / {blkBlend:0.0} dB")
                .Num("四角の縁（外側・内側 10 ピクセル）の PSNR（補間 / 重ねただけ）", $"{r.BdPsnr:0.0} dB / {r.BdBlend:0.0} dB")
                .Num("動きの検出に使われたもの", D3DRenderer.MotionLabel)
                .Num("動きの向きが合っていた割合（背景）", $"{Pct(vecFrac)}（{vecOk}/{vecN} ブロック、誤差 1 ピクセル以内。" + (texOk ? "模様のある所だけ" : "全部") + $"。全ブロックでは {r.BgOk}/{r.BgN}）")
                .Num("動きの向きが合っていた割合（四角）", $"{Pct(r.BlkN > 0 ? (double)r.BlkOk / r.BlkN : 0)}（{r.BlkOk}/{r.BlkN} ブロック、平均の誤差 {r.BlkErr:0.00} ピクセル）")
                .Num("真っ黒になったブロック / 異常な値の動き", $"{r.BadBlocks} 個 / {r.BadVectors} 個");
            InterpCheck.CutResult cut = await Task.Run(() => InterpCheck.RunSceneCut(eng));
            bool cutOk = cut.Ok && cut.Detected && cut.Clean && r.CutShare < 0.07;
            item.Num("場面が切り替わったとき（無関係な 2 コマ）", cut.Ok
                ? $"{(cut.Detected ? "切り替わりを検出" : "切り替わりを検出できず")}（うまく合わなかったブロックの割合 {Pct(cut.Share)}）。出力は 2 コマの重ね合わせと{(cut.Clean ? "同じ" : "違う")}（平均の差 {cut.ErrNearA:0.00} / {cut.ErrNearB:0.00}）。動く絵では誤検出 {(r.CutShare < 0.07 ? "なし" : "あり")}（{Pct(r.CutShare)}）"
                : "試せませんでした: " + cut.Failure);
            InterpHudCheck.Result hud = await Task.Run(() => InterpHudCheck.RunSynthetic(eng, null));
            bool hudOk = hud.Ok && hud.HudPsnr >= 40;
            item.Num("動かない表示（HUD・文字）の保護（背景が動く絵の上に固定の枠と文字）", hud.Ok
                ? $"枠と文字の PSNR {hud.HudPsnr:0.0} dB（重ねただけ {hud.HudBlend:0.0} dB）、枠のまわり {hud.RingPsnr:0.0} dB（重ねただけ {hud.RingBlend:0.0} dB）、文字のくっきり度 {hud.HudSharp:0.00}（1 が元通り）"
                : "試せませんでした: " + hud.Failure);
            item.Images.Add(file);
            if (noDetail) item.Num("（参考）映像から取った絵での結果", _probeCardMessage ? "映像がカード自身のメッセージ画面だったため、映像からは取りませんでした" : "映像が真っ黒（または一色）だったため、映像からは取りませんでした");
            if (rs is { Ok: true })
            {
                bool tx = rs.BgTexN >= 50;
                double vf = (tx ? rs.BgTexN : rs.BgN) > 0 ? (double)(tx ? rs.BgTexOk : rs.BgOk) / (tx ? rs.BgTexN : rs.BgN) : 0;
                item.Num("（参考）映像から取った絵での結果", $"背景 {rs.BgPsnr:0.0} dB（重ねただけ {rs.BgBlend:0.0}）、四角 {rs.BlkPsnr:0.0} dB（重ねただけ {rs.BlkBlend:0.0}）、背景の動きが合った割合 {Pct(vf)}、真っ黒なブロック {rs.BadBlocks} 個（細かさ {rs.Detail:0.0}）");
                item.Images.Add(fileStill);
            }

            var why = new List<string>();
            Verdict v;
            bool bad = r.BadBlocks > 0 || r.BadVectors > 0;
            if (bad || r.BgPsnr < 28 || r.BgPsnr < r.BgBlend + 3 || r.BlkPsnr < blkBlend || vecFrac < 0.60) v = Verdict.Ng;
            else if (r.BgPsnr >= 33 && r.BgPsnr >= r.BgBlend + 6 && vecFrac >= 0.85 && r.BlkPsnr >= blkBlend + 3) v = Verdict.Ok;
            else v = Verdict.Warn;
            if (bad) why.Add("補間した絵に真っ黒なブロックや異常な動きの値がありました。補間の処理がおかしくなっています。");
            if (r.BgPsnr < 28) why.Add($"背景が正解とかなりずれています（{r.BgPsnr:0.0} dB、28 dB 未満）。");
            else if (r.BgPsnr < 33) why.Add($"背景は正解にやや届いていません（{r.BgPsnr:0.0} dB、33 dB 未満）。");
            if (r.BgPsnr < r.BgBlend + 6) why.Add("背景が「2コマを重ねただけ」の絵と大差ありません。動きを追えていない可能性があります。");
            if (r.BlkPsnr < blkBlend) why.Add("動く四角の部分が「重ねただけ」より悪くなっています。");
            else if (r.BlkPsnr < blkBlend + 3) why.Add("動く四角の部分の改善が小さいです。");
            if (vecFrac < 0.85) why.Add($"動きの向きが合っていた割合が {Pct(vecFrac)} で、85% に届いていません。");
            if (!hudOk && v == Verdict.Ok) v = Verdict.Warn;
            if (!hudOk) why.Add("動かない表示（枠や文字）が、背景の動きに引きずられてにじんでいます。");
            if (!cutOk && v == Verdict.Ok) v = Verdict.Warn;
            if (!cutOk) why.Add("場面の切り替わり（まったく違う 2 コマ）で、2 コマの重ね合わせを出す動きになっていません。");
            if (v == Verdict.Ok) why.Insert(0, "背景も動く四角も、正解に近い絵を作れています。");
            if (rs is { Ok: true })
                why.Add("「（参考）映像から取った絵」は、いま映っている映像の 1 コマで同じ検算をしたものです。文字の行が並ぶ画面のように同じ模様が繰り返される絵は、どこへ動いたのか決まらず、補間が苦手とする（誰がやっても難しい）ため、判定には使わず参考にとどめています。実際の映像での出来は、次の「実際の映像での検算」で判定します。");
            why.Add("調べ方: 絵を全体で一定の距離だけ動かし、一部の四角だけ別の向きに動かした「次のコマ」を作ります。2 つの真ん中の絵は正確に分かるので、補間で作った絵と比べました。PSNR は正解との近さを表す数字で、大きいほど近く、何もしない場合の目安は 10〜15 dB、見分けがつかないのは 40 dB 以上です。" +
                "OK の目安は、背景 33 dB 以上かつ「重ねただけ」より 6 dB 以上よい、四角が「重ねただけ」より 3 dB 以上よい、動きの向きが 85% 以上合っている、真っ黒なブロックがない、です。NG は背景 28 dB 未満・「重ねただけ」より 3 dB 未満の改善・四角が「重ねただけ」より悪い・動きの合う割合 60% 未満・黒いブロックあり、のいずれかです。" +
                "模様のない平らな所は、動きが決まらない（どこに動いても同じに見える）ため、動きの向きの数字は模様のある所だけで数えています。画像は左から「2コマを重ねただけ」「補間」「正解」です（四角の端の付近を切り出しています）。");
            item.Verdict = v;
            item.Summary = v switch
            {
                Verdict.Ok => "動きを正しく読み取り、正解に近い絵を作れています",
                Verdict.Warn => "少し注意が必要です",
                _ => "問題があります",
            };
            item.Explanation = string.Join("\n", why);
            sec.Items.Add(item);
        }
        finally { still?.Dispose(); }
    }

    // =============================================================================================
    //  A-2. Hold-out on live frames
    // =============================================================================================

    private async Task InterpLiveStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "フレーム補間の画質: 実際の映像での検算" };
        double panel = _topt.Target.RefreshHz;
        CaptureMode? mode = PickLiveInterpMode(modes, panel);
        if (mode == null)
        {
            item.Summary = "このモニターではフレーム補間が使えないため試験しません";
            item.Explanation = $"フレーム補間は、モニターの更新が映像のコマ数の 1.1 倍以上のときだけ働きます。このモニター（{panel:0} Hz）では、どの映像モードでも条件を満たしません。";
            sec.Items.Add(item);
            return;
        }
        if (_video is not D3DVideoEngine eng)
        {
            item.Summary = "Direct3D の映像ではないため確認できませんでした";
            sec.Items.Add(item);
            return;
        }

        // Anything over the video would make Windows compose the monitor; keep the caption away while frames are taken.
        if (_testCaption != null) _testCaption.Showing = false;
        Bitmap? strip = null;
        try
        {
            _settings.Interpolation = true;
            EnsureEngine(false, mode);
            await Wait(SettleMs + 500);
            InterpolationStatus? st = _video.InterpolationState;
            item.Num("映像", $"{ModeLabel(mode)}（モニターの更新 {panel:0} Hz）");
            using (Bitmap? probe = await WaitForDetailedPhoto())
            {
                if (probe == null && st is { Active: true })
                {
                    item.Summary = NoDetailSummary;
                    item.Explanation = NoDetailWhy;
                    sec.Items.Add(item);
                    return;
                }
            }
            if (st is not { Active: true })
            {
                item.Summary = "フレーム補間が働かなかったため確認できませんでした";
                item.Explanation = "理由: " + InterpIdleText(st);
                sec.Items.Add(item);
                return;
            }

            // About half a second of frames is kept in the ring. Every 250 ms the new frames are read, repeated pictures
            // are dropped, and one triple of consecutive DIFFERENT, evenly spaced pictures is taken from the newest ones.
            const int pollMs = 250, runMs = 4200, maxTries = 16;
            double srcFpsNow = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
            eng.DebugSetRingExtra((int)Math.Ceiling(srcFpsNow * 0.6) + 4);
            await Wait(500);

            var triples = new List<InterpCheck.Triple>();
            var live = new InterpCheck.LiveFrames();
            int tried = 0, pickIndex = 0, lost = 0;
            long after = 0;
            double bestMoving = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < runMs && tried < maxTries)
            {
                await Wait(pollMs);
                var fresh = new List<D3DRenderer.ScanFrame>();
                long a0 = after;
                bool scanned = await Task.Run(() => eng.DebugInterpScan(a0, fresh));
                if (!scanned) continue;
                foreach (D3DRenderer.ScanFrame f in fresh) { live.Add(f.Seq, f.Smooth, f.W, f.H, f.Luma); after = Math.Max(after, f.Seq); }
                if (!live.TryPick(ref pickIndex, out var u0, out var u1, out var u2)) continue;

                double bm = bestMoving;
                (InterpCheck.Triple? T, Bitmap? Strip) got = await Task.Run<(InterpCheck.Triple?, Bitmap?)>(() =>
                {
                    if (!eng.DebugInterpHoldout(u0.Seq, u1.Seq, u2.Seq, out Bitmap? f0, out Bitmap? f1, out Bitmap? f2, out Bitmap? mid))
                        return (null, null);
                    try
                    {
                        InterpCheck.Triple t = InterpCheck.Analyze(f0!, f1!, f2!, mid!, bm, out Bitmap? sp);
                        return (t, sp);
                    }
                    finally { f0?.Dispose(); f1?.Dispose(); f2?.Dispose(); mid?.Dispose(); }
                });
                if (got.T == null) { lost++; continue; }
                tried++;
                triples.Add(got.T);
                if (got.Strip != null) { strip?.Dispose(); strip = got.Strip; bestMoving = got.T.Moving; }
            }
            eng.DebugSetRingExtra(0);

            var (windows, evenWindows) = live.CountWindows();
            double gapMs = live.MedianGapMs();
            double uniqueFps = gapMs > 0 ? 1000.0 / gapMs : 0;
            item.Num("映像のコマ（約 4 秒）", $"{live.Captured} コマのうち、前と違う絵は {live.Unique.Count} コマ、前と同じ絵の繰り返しは {live.Repeats} コマ" +
                (uniqueFps > 0 ? $"（絵が変わる速さの目安 約 {uniqueFps:0} 回/秒）" : ""))
                .Num("連続する 3 つの違う絵のうち、間隔が揃っていた組", windows > 0 ? $"{evenWindows} / {windows}（真ん中が本当の中間になるのは、前後の間隔が ±25% 以内のとき）" : "なし（違う絵が 3 つ続かなかった）");

            int still = triples.Count(t => t.Moving < 0.01);
            var moving = triples.Where(t => t.Moving >= 0.01).ToList();
            int dup = moving.Count(t => t.Dup);
            var used = moving.Where(t => !t.Dup).ToList();
            item.Num("調べた絵の組", $"{triples.Count} 組（違う絵が連続する 3 コマ × 約 4 秒）")
                .Num("動きがなかった組 / 判定に使えなかった組 / 判定に使った組", $"{still} / {dup} / {used.Count}");
            if (moving.Count > 0)
                item.Num("絵が動いた範囲（中央値）", $"画面の {Pct(InterpCheck.Median(moving.Select(t => t.Moving)))}");

            // why too few pairs could be used, from what was measured (shown when the verdict cannot be given)
            string Reasons()
            {
                var r = new List<string>();
                if (live.Unique.Count < 3)
                    r.Add($"4 秒の間に絵が変わったのは {live.Unique.Count} 回だけでした（映像がほぼ止まっていました）。");
                else
                {
                    if (windows > 0 && evenWindows * 2 < windows)
                        r.Add($"絵が変わる間隔が不揃いでした（間隔が揃っていたのは {evenWindows}/{windows} 組）。ゲームの描画の間隔が一定でないか、映像のコマ数と描画の回数が割り切れない（例: 45 fps のゲームを 60 fps で出力）ときに起きます。真ん中の絵が本当の中間にならない組は使いません。");
                    if (still > 0) r.Add($"動きのない組が {still} 組ありました（画面の一部しか変わらない場面や、止まった場面）。");
                    if (dup > 0) r.Add($"前後のどちらかと同じ絵だった組が {dup} 組ありました。");
                    if (lost > 0) r.Add($"保管場所から取り出せなかった組が {lost} 組ありました。");
                }
                if (r.Count == 0) r.Add("条件に合う組が十分に集まりませんでした。");
                return string.Join("\n", r);
            }

            if (triples.Count == 0)
            {
                item.Summary = live.Unique.Count < 3 ? "映像がほぼ止まっていたため判定できません（ゲームなど動きのある映像を映して試験してください）"
                    : "間隔の揃った違う 3 コマを取れなかったため判定できません";
                item.Explanation = Reasons() + "\n実際の映像での補間の検算は、映像のコマ数と同じ回数で描画しているゲーム（60 fps の映像なら 60 fps のゲーム）で、動きのある場面を映すと判定できます。";
            }
            else if (used.Count < 5)
            {
                item.Summary = moving.Count == 0 ? "映像に動きがなかったため判定できません（ゲームの画面など動きのある映像を映して試験すると判定できます）"
                    : $"判定に使える組が {used.Count} 組と少なかったため、判定しません";
                item.Explanation = $"判定に使えた組は {used.Count} 組で、判定に必要な 5 組（目標は 10 組以上）に届きませんでした。\n" + Reasons() +
                    "\n補間の良し悪しは、動いている絵でないと比べられません。OK とも NG とも言わずに「情報」としています。映像のコマ数と同じ回数で描画しているゲームで、動きのある場面を映してもう一度試験してください。";
            }
            else
            {
                double pI = InterpCheck.Median(used.Select(t => t.InterpPsnr)), pB = InterpCheck.Median(used.Select(t => t.BlendPsnr)), pR = InterpCheck.Median(used.Select(t => t.RepeatPsnr));
                double aI = InterpCheck.Median(used.Select(t => t.InterpAll)), aB = InterpCheck.Median(used.Select(t => t.BlendAll)), aR = InterpCheck.Median(used.Select(t => t.RepeatAll));
                double mI = InterpCheck.Median(used.Select(t => t.InterpMae)), mB = InterpCheck.Median(used.Select(t => t.BlendMae)), mR = InterpCheck.Median(used.Select(t => t.RepeatMae));
                item.Num("動いた部分の PSNR（補間 / 重ね合わせ / 前のコマの繰り返し）", $"{pI:0.0} / {pB:0.0} / {pR:0.0} dB")
                    .Num("動いた部分の平均の誤差（同じ順、0〜255）", $"{mI:0.0} / {mB:0.0} / {mR:0.0}")
                    .Num("画面全体の PSNR（同じ順）", $"{aI:0.0} / {aB:0.0} / {aR:0.0} dB");
                double best = Math.Max(pB, pR);
                Verdict v = pI >= best - 0.3 ? Verdict.Ok : pI >= best - 1.5 ? Verdict.Warn : Verdict.Ng;
                var why = new List<string>();
                if (pI >= best + 0.5) why.Add($"補間の絵は、本当の真ん中のコマに「重ね合わせ」より {pI - pB:0.0} dB、「前のコマの繰り返し」より {pI - pR:0.0} dB 近く、はっきり良くなっています。");
                else if (v == Verdict.Ok) why.Add("補間の絵は、重ね合わせ・繰り返しと同じくらいか、それ以上に本当の真ん中のコマに近いです。この映像では差が小さいだけで、悪くはなっていません。");
                else if (v == Verdict.Warn) why.Add($"補間の絵が、重ね合わせか繰り返しのよい方より {best - pI:0.0} dB 離れています。軽い不具合の範囲ですが、この映像は動きが速い・画面が切り替わるなど補間の苦手な場面だった可能性もあります。");
                else why.Add($"補間の絵が、重ね合わせか繰り返しのよい方より {best - pI:0.0} dB も本当の真ん中のコマから離れています。補間しないほうがよいくらいです。");
                why.Add($"調べ方: 実際の映像から、違う絵が連続し時間の間隔が揃った 3 コマ（F0・F1・F2）を取り、F0 と F2 だけから、実際の補間処理で真ん中の絵を作って、本当の F1 と比べました。比べた相手は「F0 と F2 を半分ずつ重ねただけ」と「F0 をそのまま出す（繰り返し）」です。動いた部分（F0 と F2 で絵が変わった所）だけで数え、組ごとの数字の中央値で判定しています。補間は実際の使用（1 コマ間隔）より遠い 2 コマ間隔で作っているため、実際より少し厳しい条件です。" +
                    "OK は補間が重ね合わせ・繰り返しのよい方と同じ（差 0.3 dB 以内）かそれ以上、注意は 1.5 dB 以内の劣り、NG はそれ以上の劣りです。PSNR は大きいほど本当の絵に近い数字です。画像は左から「前のコマの繰り返し」「重ね合わせ」「補間」「実際のコマ」で、一番動いた場面の一部です。");
                if (still + dup > 0) why.Add($"動きのなかった {still} 組と、前後のどちらかと同じ絵だった {dup} 組は数えていません。");
                why.Add($"絵が変わらない繰り返しのコマ（{live.Repeats} コマ）は先に取り除き、違う絵が連続して、時間の間隔が揃った（±25% 以内）3 コマだけを使っています。判定に使えた組は {used.Count} 組です。" +
                    (used.Count < 10 ? "目標の 10 組には届かなかったため、結果はやや不確かです。\n" + Reasons() : ""));
                item.Verdict = v;
                item.Summary = v switch
                {
                    Verdict.Ok => "補間は、重ね合わせや繰り返しと同じかそれ以上に本物へ近い絵を作れています",
                    Verdict.Warn => "少し注意が必要です",
                    _ => "問題があります",
                };
                item.Explanation = string.Join("\n", why);
                if (strip != null)
                {
                    const string file = "interp_live.png";
                    strip.Save(Path.Combine(_rep.Folder, file), System.Drawing.Imaging.ImageFormat.Png);
                    item.Images.Add(file);
                }
            }
            sec.Items.Add(item);
        }
        finally { strip?.Dispose(); StopInterpForTest(); }
    }

    // =============================================================================================
    //  A-3. Interpolation that follows the game's own frame rate
    // =============================================================================================

    private async Task InterpCadenceStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "ゲームのコマ数に合わせた補間" };
        double panel = _topt.Target.RefreshHz;
        CaptureMode? mode = PickLiveInterpMode(modes, panel);
        if (mode == null)
        {
            item.Summary = "このモニターではフレーム補間が使えないため試験しません";
            item.Explanation = $"フレーム補間は、モニターの更新が映像のコマ数の 1.1 倍以上のときだけ働きます。このモニター（{panel:0} Hz）では、どの映像モードでも条件を満たしません。";
            sec.Items.Add(item);
            return;
        }
        if (_video is not D3DVideoEngine eng)
        {
            item.Summary = "Direct3D の映像ではないため確認できませんでした";
            sec.Items.Add(item);
            return;
        }
        if (_testCaption != null) _testCaption.Showing = false;
        try
        {
            _settings.Interpolation = true;
            EnsureEngine(false, mode);
            await Wait(SettleMs + 500);
            InterpolationStatus? st = _video.InterpolationState;
            item.Num("映像", $"{ModeLabel(mode)}（モニターの更新 {panel:0} Hz）");
            if (st is not { Active: true })
            {
                item.Summary = "フレーム補間が働かなかったため確認できませんでした";
                item.Explanation = "理由: " + InterpIdleText(st);
                sec.Items.Add(item);
                return;
            }

            // 2 s of frames stay in the ring; the game's frame rate is read about 20 times in the meantime
            double fps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
            int keep = (int)Math.Min(120, Math.Ceiling(fps * 2.0));
            eng.DebugSetRingExtra(keep + 4);
            var rates = new List<double>();
            int stillSamples = 0, samples = 0;
            for (int i = 0; i < 12; i++)
            {
                await Wait(250);
                if (i < 4) continue;
                samples++;
                if (_video.PictureStill) stillSamples++;
                else if (_video.GameFpsReliable && _video.GameFps > 0) rates.Add(_video.GameFps);
            }
            var frames = new List<D3DRenderer.TimelineFrame>();
            bool got = eng.DebugTimeline(keep, frames, out double nominalMs, out double periodMs, out double extraMs, out double latMs);
            eng.DebugSetRingExtra(0);
            double inputFps = _video.Stats?.InputFps ?? fps;
            double game = rates.Count > 0 ? InterpCheck.Median(rates) : 0;
            double margin = _video.VideoPacingMarginMs;

            // FPS だけ must show the rate the interpolation really presents (not the game's own), and agree with the status bar.
            {
                var pit = new TestItem { Name = "FPS だけの数字（補間中）" };
                FpsPanelContent(out string pBig, out string pSmall, out bool pInterp);
                double presented = _video.Stats?.PresentedFps ?? 0;
                string bar = InterpMetric(out _) ?? "";
                pit.Num("パネルの数字", $"{pBig}（2行目: {(pSmall.Length == 0 ? "なし" : pSmall)}）")
                   .Num("画面に出ているコマ数", $"{presented:0.0} fps").Num("ゲームのコマ数", game > 0 ? $"{game:0.0} fps" : "不明")
                   .Num("ステータスバーの補間", bar.Length == 0 ? "（なし）" : bar);
                bool numOk = double.TryParse(pBig, out double pv) && Math.Abs(pv - presented) <= Math.Max(2, presented * 0.03);
                bool barOk = bar.Contains("→" + pBig);
                if (pInterp && numOk && barOk) { pit.Verdict = Verdict.Ok; pit.Summary = "補間中は、画面に出ているコマ数が表示され、ステータスバーと一致しています"; }
                else
                {
                    pit.Verdict = Verdict.Ng;
                    pit.Summary = !pInterp ? "補間が働いているのに、パネルは補間中の表示になっていません" : !numOk ? "パネルの数字が、画面に出ているコマ数と一致しません" : "パネルとステータスバーの補間の数字が一致しません";
                }
                sec.Items.Add(pit);
            }

            if (stillSamples * 2 > samples || rates.Count == 0)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "映像がほぼ止まっていたため、ゲームのコマ数を測れませんでした";
                item.Num("ゲームのコマ数", "静止（絵が変わらない）");
                item.Explanation = "約 2 秒の間、絵がほとんど変わりませんでした。ゲームなど動きのある映像を映して試験してください。";
                sec.Items.Add(item);
                return;
            }
            item.Num("映像が届く速さ", $"{inputFps:0.0} fps")
                .Num("ゲームの実際のコマ数（新しい絵が出る速さ）", $"約 {game:0.0} fps（{rates.Count} 回の測定の中央値）");
            if (!got || frames.Count < 30)
            {
                item.Summary = "ゲームのコマ数は測れましたが、補間の様子は調べられませんでした";
                item.Explanation = "補間用の保管場所から十分な数のコマを取り出せませんでした。";
                sec.Items.Add(item);
                return;
            }
            item.Num("新しい絵の出方", CadenceCheck.DescribeCadence(frames, inputFps));

            bool repeats = game < inputFps * 0.9;
            CadenceCheck.SimResult oldR = CadenceCheck.Simulate(frames, panel, margin, nominalMs, extraMs, latMs, periodMs, false);
            CadenceCheck.SimResult newR = CadenceCheck.Simulate(frames, panel, margin, nominalMs, extraMs, latMs, periodMs, true);
            item.Num("繰り返しのコマ", $"{frames.Count(f => f.Dup)} / {frames.Count} コマ（直前と同じ絵）")
                .Num("動きが止まる回（従来 / 新しい絵だけで補間）", $"{Pct(oldR.StallShare)} / {Pct(newR.StallShare)}（{oldR.Analysed} 回の更新のうち、絵の進みが平均の 20% 未満だった割合）")
                .Num("絵の進みの不揃い（従来 / 新）", $"{oldR.Cv:0.00} / {newR.Cv:0.00}（更新ごとの進みの標準偏差 ÷ 平均、小さいほど滑らか）")
                .Num("1 回の更新での最大の進み（平均に対して、従来 / 新）", $"{oldR.Jump:0.0} 倍 / {newR.Jump:0.0} 倍")
                .Num("映像を保持する時間（従来 / 新）", $"{oldR.HoldMs:0} ms / {newR.HoldMs:0} ms（新しい絵 1 つ分の間隔と、繰り返しかどうかの判定を待つ分だけ長くなります）")
                .Num("繰り返しの判定が出るまで", $"最大 {latMs:0} ms");

            var why = new List<string>();
            if (!repeats)
            {
                item.Verdict = Verdict.Info;
                item.Summary = "ゲームが映像と同じコマ数で描画しているため、この補間は働きません";
                why.Add($"映像は {inputFps:0} fps、ゲームの新しい絵は {game:0} fps で、繰り返しのコマはほとんどありません。このときの補間は従来と同じ動きで、確認する項目がありません。");
                why.Add("30 fps や 40 fps で描画するゲームを 60 fps で取り込むと、同じ絵が続けて届きます。そのときにこの補間が働きます。");
            }
            else
            {
                Verdict v;
                if (newR.StallShare <= 0.05 && newR.StallShare <= oldR.StallShare * 0.5 && newR.Starved <= 2) v = Verdict.Ok;
                else if (newR.StallShare < oldR.StallShare) v = Verdict.Warn;
                else v = Verdict.Ng;
                item.Verdict = v;
                item.Summary = v switch
                {
                    Verdict.Ok => $"動きの止まる回が {Pct(oldR.StallShare)} から {Pct(newR.StallShare)} に減りました",
                    Verdict.Warn => $"動きの止まる回は減りましたが（{Pct(oldR.StallShare)} → {Pct(newR.StallShare)}）、まだ残っています",
                    _ => $"動きの止まる回が減りませんでした（{Pct(oldR.StallShare)} → {Pct(newR.StallShare)}）",
                };
                why.Add($"ゲームは約 {game:0} fps で描画していて、映像（{inputFps:0} fps）には同じ絵が続けて届きます。従来の補間は「同じ絵と同じ絵」の間を補間してしまい、動きが止まったあとに一気に進みました。新しい補間は繰り返しのコマを飛ばし、新しい絵どうしを、一定の間隔で補間します。");
                why.Add($"代わりに、新しい絵が次に届くまで待つ必要があるため、映像を保持する時間が {oldR.HoldMs:0} ms から {newR.HoldMs:0} ms に増えます（約 {newR.HoldMs - oldR.HoldMs:0} ms の遅れが増えます）。ゲームのコマ数が低いほど、増える分は大きくなります。");
                if (newR.Starved > 2) why.Add($"次の新しい絵がまだ届いておらず、前の絵のまま待った更新が {newR.Starved} 回ありました。");
            }
            why.Add($"調べ方: 約 2 秒分の実際のコマ（届いた時刻と、直前と同じ絵かどうか）を記録し、{panel:0} Hz のモニターで更新のたびにどの絵とどの絵の間を表示するかを、実際の選び方のプログラムで再現しました。表示している位置を「新しい絵の通し番号 + 次の絵への進み具合」で表し、更新ごとの進みを比べています（絵の中身は使わないため、動きの大きさの違いに左右されません）。毎回同じだけ進めば理想です。" +
                "OK は、動きの止まる回が 5% 以下で、従来の半分以下になったとき。注意は減ったがそれより多いとき。NG は減らなかったときです。");
            item.Explanation = string.Join("\n", why);
            sec.Items.Add(item);
        }
        finally { StopInterpForTest(); }
    }

    // =============================================================================================
    //  B. Updates lowered by something drawn over the video
    // =============================================================================================

    private async Task<(double Rate, double ObservedHz, double Judder)> PresentRate(int ms, double srcFps, double panel)
    {
        _video.BeginMeasure();
        long p0 = _video.FramesPresented;
        var sw = Stopwatch.StartNew();
        double rate = 0;
        RenderMeasurement? meas = null;
        try
        {
            await Wait(ms);
            double el = sw.Elapsed.TotalSeconds;
            rate = el > 0 ? (_video.FramesPresented - p0) / el : 0;
        }
        finally { meas = _video.EndMeasure(); }
        double hz = 0, judder = 0;
        try { if (meas != null) { var pa = TestProbes.AnalyzePacing(meas.Pacing, srcFps, panel); hz = pa.ObservedHz; judder = pa.JudderShare; } } catch { }
        return (rate, hz, judder);
    }

    private async Task OverlayCapStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "重ねた表示による更新の低下" };
        double panel = _topt.Target.RefreshHz;
        DisplayInfo? primary = Displays.List().FirstOrDefault(d => d.Primary);
        bool drawn = _video.SupportsOverlays;   // Direct3D: the overlays are drawn into the video, there is no window over it
        if (_topt.Target.Primary && !drawn)
        {
            item.Summary = "試験するモニターがメインのモニターなので、この低下は起きません";
            item.Explanation = "この低下は、メインではないモニターを、メインのモニターとは違う更新の速さで使っているときに起きます。試験するモニターがメインのため調べていません。";
            sec.Items.Add(item);
            return;
        }
        if (primary != null && Math.Abs(primary.RefreshHz - panel) <= 1 && !drawn)
        {
            item.Summary = "メインのモニターと更新の速さが同じなので、この低下は起きません";
            item.Num("更新の速さ", $"試験するモニター {panel:0} Hz / メインのモニター {primary.RefreshHz} Hz");
            item.Explanation = "この低下は、2 つのモニターの更新の速さが違うときに起きます。同じ速さなので調べていません。";
            sec.Items.Add(item);
            return;
        }

        CaptureMode? mode = PickInterpMode(modes, panel);
        bool interp = mode != null;
        mode ??= PickMode(modes, 1920, 1080, 0) ?? modes.FirstOrDefault();
        if (mode == null)
        {
            item.Summary = "映像のモードがないため調べられませんでした";
            sec.Items.Add(item);
            return;
        }

        if (_testCaption != null) _testCaption.Showing = false;
        InfoMode wasInfo = CurrentInfoMode;
        try
        {
            _settings.Interpolation = interp;
            EnsureEngine(false, mode);
            if (wasInfo != InfoMode.Off) SetInfoMode(InfoMode.Off);      // the baseline needs nothing over the video
            await Wait(SettleMs);
            double srcFps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : mode.Fps;
            bool interpOn = interp && _video.InterpolationState is { Active: true };
            item.Num("映像", ModeLabel(mode) + (interpOn ? "（フレーム補間オン）" : "（フレーム補間なし）"))
                .Num("更新の速さ", $"試験するモニター {panel:0} Hz / メインのモニター {(primary != null ? primary.RefreshHz + " Hz" : "不明")}");
            if (interp && !interpOn)
                item.Num("注意", "フレーム補間が働かなかったため、補間なしで測っています");
            double expect = interpOn ? panel : Math.Min(srcFps, panel);

            bool crispable = _video.SupportsCrispness;
            if (crispable) _video.CrispnessSampling = false;
            var clear = await PresentRate(2000, srcFps, panel);
            ToggleInfo();                      // the real info display, as in normal use
            // The くっきり度 sampling runs while the info display is up; one sample a second here (the normal
            // rate is one every 5 s) makes sure the measurement below contains several of them.
            int crispIntervalWas = _video.CrispnessIntervalMs;
            if (crispable) _video.CrispnessIntervalMs = 1000;
            UpdateMetricLabels();
            await Wait(700);
            var over = await PresentRate(drawn ? 4000 : 2000, srcFps, panel);
            CrispnessReading? crisp = crispable ? _video.Crispness : null;
            if (crispable) _video.CrispnessIntervalMs = crispIntervalWas;
            ToggleInfo();
            UpdateMetricLabels();
            await Wait(700);
            var back = await PresentRate(1500, srcFps, panel);

            item.Num("何も重ねていないとき", $"{clear.Rate:0.0} fps" + (clear.ObservedHz > 0 ? $"（実測の更新 {clear.ObservedHz:0.0} Hz）" : ""))
                .Num("情報表示を重ねたとき", $"{over.Rate:0.0} fps" + (over.ObservedHz > 0 ? $"（実測の更新 {over.ObservedHz:0.0} Hz）" : ""))
                .Num("情報表示を消したあと", $"{back.Rate:0.0} fps" + (back.ObservedHz > 0 ? $"（実測の更新 {back.ObservedHz:0.0} Hz）" : ""))
                .Num("期待した数", $"{expect:0} fps");
            if (drawn)
            {
                item.Num("コマの出方の乱れ（何も重ねていないとき）", Pct(clear.Judder))
                    .Num("コマの出方の乱れ（情報表示とくっきり度の計測中）", Pct(over.Judder));
                if (crisp != null)
                    item.Num("くっきり度の計測", $"{crisp.Samples} 回 ／ 1 回あたり描画スレッドを使った時間 最大 {crisp.RenderMsMax:0.00} ms（目標 2 ms 未満）");
                else if (crispable)
                    item.Num("くっきり度の計測", "この条件では計測が働きませんでした（映像が画面より大きくならない、または絵が平らなとき）");
            }

            if (clear.Rate < 5)
            {
                item.Verdict = Verdict.Warn;
                item.Summary = "映像が出ていなかったため比べられませんでした";
                item.Explanation = "何も重ねていない状態で映像のコマがほとんど表示されなかったため、重ねたときとの比較をしていません。";
            }
            else if (drawn)
            {
                bool judderBad = over.Judder > clear.Judder + 0.02;
                bool slow = crisp != null && crisp.RenderMsMax >= 2.0;
                if (over.Rate < clear.Rate * 0.9)
                {
                    item.Verdict = Verdict.Ng;
                    item.Summary = $"映像の中に描いているのに、更新が {clear.Rate:0} から {over.Rate:0} fps に下がりました";
                    item.Explanation = "Direct3D の描画では、通知と情報表示を映像の中に描くため、重ねても更新は下がらないはずです。下がっているため、何か別の原因があります。";
                }
                else if (judderBad || slow)
                {
                    item.Verdict = Verdict.Warn;
                    item.Summary = judderBad ? "くっきり度の計測中にコマの出方が乱れました" : "くっきり度の計測が描画を 2 ms 以上止めました";
                    item.Explanation = $"更新は {clear.Rate:0} から {over.Rate:0} fps と下がっていませんが、" +
                        (judderBad ? $"コマの出方の乱れが {Pct(clear.Judder)} から {Pct(over.Judder)} に増えました。" : "") +
                        (slow ? $"計測 1 回が描画スレッドを最大 {crisp!.RenderMsMax:0.0} ms 使いました。" : "");
                }
                else
                {
                    item.Verdict = Verdict.Ok;
                    item.Summary = "映像の中に描くため、表示中も更新は下がりません";
                    item.Explanation = $"通知と情報表示は別のウィンドウではなく映像の中に描いているので、Windows が映像を作り直す必要がなく、重ねても {clear.Rate:0} fps から {over.Rate:0} fps とほぼ変わりませんでした。" +
                        "情報表示を出してくっきり度を 1 秒ごとに計測しているあいだも、コマの出方は変わりませんでした（" +
                        $"{Pct(clear.Judder)} → {Pct(over.Judder)}）。ふだんの計測は 5 秒ごとです。";
                }
            }
            else if (over.Rate >= clear.Rate * 0.9)
            {
                item.Verdict = Verdict.Ok;
                item.Summary = "重ねた表示があっても更新は下がりません";
                item.Explanation = $"情報表示を重ねても、表示されるコマ数は {clear.Rate:0} fps から {over.Rate:0} fps とほぼ変わりませんでした。";
            }
            else
            {
                item.Verdict = Verdict.Info;
                item.Summary = $"情報表示を重ねると、更新が {clear.Rate:0} から {over.Rate:0} fps に下がりました";
                item.Explanation =
                    "Windows は、映像の上に何かが重なっている間、このモニターをメインのモニターとほぼ同じ速さ" +
                    (primary != null ? $"（{primary.RefreshHz} Hz）" : "") +
                    "でしか更新しません。2 つのモニターの更新の速さが違う（" + $"{panel:0} Hz と " + (primary != null ? primary.RefreshHz + " Hz" : "不明") + "）ために起きる、Windows 側の仕組みです。\n" +
                    (back.Rate >= clear.Rate * 0.9
                        ? $"重ねた表示を消すと、{back.Rate:0} fps に戻りました（試験で確認済み）。\n"
                        : $"重ねた表示を消したあとも {back.Rate:0} fps のままでした（元に戻らない場合があります）。\n") +
                    "通知、情報表示、ほかのウィンドウ、タスクバーなど、映像の上に重なるものはすべて同じ影響があります。ゲームを全力の更新の速さで見たいときは、これらを映像の上に出さないでください。\n" +
                    "このモニターをメインのモニターにすると避けられる可能性がありますが、試験ではメインを切り替えて確かめていないため、未確認です。";
            }
            sec.Items.Add(item);
        }
        finally
        {
            if (CurrentInfoMode != wasInfo) SetInfoMode(wasInfo);
            if (_video.SupportsCrispness) _video.CrispnessIntervalMs = 5000;
            StopInterpForTest();
        }
    }
}
