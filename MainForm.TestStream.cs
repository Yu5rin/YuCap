using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace YuCap;

/// <summary>Test mode: 配信用ウィンドウ and 信号なしの検出と省電力.</summary>
public sealed partial class MainForm
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private static string FgDescribe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "なし";
        var sb = new System.Text.StringBuilder(128);
        GetWindowText(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out uint pid);
        return $"「{sb}」(pid {pid}{(pid == (uint)Environment.ProcessId ? "・YuCap" : "")})";
    }

    private bool _testPowerSave;     // the 信号なし step measures with the power saving on

    // =============================================================================================
    //  配信用ウィンドウ
    // =============================================================================================

    private sealed record StreamWindowRun(double Judder, int Frames, double MainPerSec, double StreamPerSec, bool Ok, double InPerSec = 0, long Dropped = 0, string Timeline = "", double Stall = 0);

    private async Task<StreamWindowRun> StreamPacingWindow(int ms, double srcFps, double panel)
    {
        _video.BeginMeasure();
        long p0 = _video.FramesPresented, s0 = _video.StreamPresented, r0 = _video.FramesReceived, d0 = _video.FramesDropped;
        var sw = Stopwatch.StartNew();
        RenderMeasurement? meas = null;
        var tl = new List<long>();
        try
        {
            long prev = p0;
            for (int t = 0; t < ms; t += 500)
            {
                await Wait(500);
                long now = _video.FramesPresented; tl.Add(now - prev); prev = now;
            }
        }
        finally { meas = _video.EndMeasure(); }
        string tls = string.Join(",", tl);
        double el = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        double main = (_video.FramesPresented - p0) / el, stream = (_video.StreamPresented - s0) / el, inp = (_video.FramesReceived - r0) / el;
        if (meas == null) return new StreamWindowRun(0, 0, main, stream, false, inp, _video.FramesDropped - d0, tls);
        TestProbes.PacingAnalysis p = TestProbes.AnalyzePacing(meas.Pacing, srcFps, panel);
        // The usual "judder" (a frame held longer than 1.25 x the source interval) is not stable for 60 fps on a 144 Hz panel: the frames
        // alternate between 2 and 3 refreshes = 13.9 and 20.8 ms, and 20.8 ms is exactly the limit, so a hair of jitter moves the share by
        // 10 points from one run to the next. A real hitch is a frame held at least 1.5 x the source interval, which that cadence never reaches.
        double stall = StallShare(meas.Pacing, srcFps);
        return new StreamWindowRun(p.JudderShare, p.Frames, main, stream, p.Frames >= 20, inp, _video.FramesDropped - d0, tls, stall);
    }

    private static double StallShare(PacingCapture cap, double srcFps)
    {
        StatSample[] st = cap.Stats;
        double limit = 1.5 * 1000.0 / Math.Max(1.0, srcFps);
        int total = 0, stall = 0;
        for (int i = 1; i < st.Length; i++)
        {
            if ((long)st[i].PresentCount - st[i - 1].PresentCount != 1) continue;
            double ms = (st[i].SyncQpc - st[i - 1].SyncQpc) * 1000.0 / Stopwatch.Frequency;
            if (ms <= 0 || ms > 500) continue;
            total++;
            if (ms > limit) stall++;
        }
        return total > 0 ? (double)stall / total : 0;
    }

    /// <summary>Pixels of <paramref name="bmp"/> within a small colour distance of <paramref name="c"/>.</summary>
    private static long CountColor(Bitmap bmp, Color c)
    {
        BitmapData bd = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bd.Stride];
            long n = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bd.Stride);
                for (int x = 0; x < bmp.Width; x++)
                {
                    int i = x * 4;
                    if (Math.Abs(row[i + 2] - c.R) + Math.Abs(row[i + 1] - c.G) + Math.Abs(row[i] - c.B) < 60) n++;
                }
            }
            return n;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Mean absolute difference (0..255) of two pictures shrunk to 32x18, and the mean luma of the first.</summary>
    private static double ThumbDiff(Bitmap a, Bitmap b, out double meanA)
    {
        using var ta = new Bitmap(32, 18, PixelFormat.Format32bppArgb);
        using var tb = new Bitmap(32, 18, PixelFormat.Format32bppArgb);
        foreach ((Bitmap src, Bitmap dst) in new[] { (a, ta), (b, tb) })
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, 32, 18), new Rectangle(Point.Empty, src.Size), GraphicsUnit.Pixel);
            }
        double sum = 0, sa = 0;
        for (int y = 0; y < 18; y++)
            for (int x = 0; x < 32; x++)
            {
                Color ca = ta.GetPixel(x, y), cb = tb.GetPixel(x, y);
                sum += (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3.0;
                sa += (ca.R + ca.G + ca.B) / 3.0;
            }
        meanA = sa / (32 * 18);
        return sum / (32 * 18);
    }

    private async Task FeatureStream(TestSection sec, CaptureMode? mode)
    {
        var item = new TestItem { Name = "配信用ウィンドウ" };
        try { EnsureEngine(false, mode); }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng; item.Summary = "映像を開始できませんでした"; item.Explanation = ex.Message;
            sec.Items.Add(item);
            return;
        }
        if (!_video.SupportsStreamWindow)
        {
            item.Verdict = Verdict.Info; item.Summary = "[情報] この描画では使えません";
            sec.Items.Add(item);
            return;
        }
        string sizeBefore = _settings.StreamWindowSize;
        bool openBefore = _stream != null;
        if (openBefore) CloseStreamWindow(forget: false);
        _settings.Interpolation = false;
        ApplyRenderOptions();
        await Wait(SettleMs);
        double srcFps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : (mode?.Fps ?? 60);
        double panel = _topt.Target.RefreshHz;
        var why = new List<string>();
        Verdict v = Verdict.Ok;
        var notes = new List<string>();
        try
        {
            // ---- pacing without / with / without again (the second "without" tells how much two identical runs differ) ----
            int ms = RenderMeasureMs * 2;      // longer than the other steps: the judder share of a short run varies by a few points on its own
            StreamWindowRun b1 = await StreamPacingWindow(ms, srcFps, panel);

            _settings.StreamWindowSize = "1920x1080";
            IntPtr fgBefore = GetForegroundWindow();
            OpenStreamWindow();
            IntPtr fgNow = GetForegroundWindow();      // right after Show: what opening the window itself did, before anything else can move the focus
            // the first moments after a window appears: DWM and the window itself settle (one short hitch of the main window's
            // presents was seen here); they are looked at on their own and kept out of the comparison below
            var openTl = new List<long>();
            long po = _video.FramesPresented;
            for (int t = 0; t < 3500; t += 500) { await Wait(500); long nw = _video.FramesPresented; openTl.Add(nw - po); po = nw; }
            item.Num("開いた直後の 0.5 秒ごとの表示回数（3.5 秒間）", string.Join(",", openTl) + "（60 fps の映像なら 30 前後）");
            IntPtr fgAfter = GetForegroundWindow();
            bool streamIsFg = _stream != null && (fgNow == _stream.Handle || fgAfter == _stream.Handle);
            bool focusKept = fgBefore == fgNow && !streamIsFg;                       // judged on the moment of opening
            string fgLater = fgAfter == fgNow ? "" : "（開いた直後は動かず、その後 " + FgDescribe(fgAfter) + " が前に出ました。開いたことが原因ではありません）";
            Log.Info($"stream focus: before {FgDescribe(fgBefore)}, right after Show {FgDescribe(fgNow)}, after 3.5 s {FgDescribe(fgAfter)}");
            if (_stream == null) throw new InvalidOperationException("配信用ウィンドウを開けませんでした");
            Size want = new(1920, 1080);
            bool sizeOk = _stream.ClientSize == want;
            StreamWindowRun w = await StreamPacingWindow(ms, srcFps, panel);

            // ---- what the window shows ----
            using Bitmap? s1 = await Task.Run(() => _video.StreamSnapshot());
            bool snapOk = s1 != null && s1.Size == want;
            item.Num("窓の大きさ（指定 1920x1080）", $"{_stream.ClientSize.Width}x{_stream.ClientSize.Height}" + (sizeOk ? "" : "（違います）"))
                .Num("映像の大きさ（読み戻し）", s1 != null ? $"{s1.Width}x{s1.Height}" : "読み戻せませんでした")
                .Num("フォーカス", focusKept ? "開いても奪いませんでした" + fgLater : "開いたときにフォーカスが動きました（" + FgDescribe(fgBefore) + " → " + FgDescribe(fgNow) + "）")
                .Num("配信用ウィンドウに出したコマ数", $"{w.StreamPerSec:0.0} 回/秒（本体の表示 {w.MainPerSec:0.0} 回/秒）")
                .Num("配信用の描画にかかる CPU 時間（描画スレッド）", _video.StreamCpu is { N: > 0 } sc ? $"平均 {sc.Avg:0.00} ms／最大 {sc.Max:0.00} ms（{sc.N} 回）／ 内訳 {_video.StreamParts}" : "計れませんでした");
            if (!sizeOk) { v = Worst(v, Verdict.Ng); why.Add("窓の大きさが指定どおりになりませんでした。"); }
            if (!focusKept) { v = Worst(v, Verdict.Warn); why.Add("開いたときに別のウィンドウへフォーカスが移りました（ゲームの操作中に開くと困ります）。"); }
            if (!snapOk) { v = Worst(v, Verdict.Ng); why.Add("配信用ウィンドウの絵を読み戻せないか、大きさが違いました。"); }
            if (w.StreamPerSec < w.MainPerSec * 0.8) { v = Worst(v, Verdict.Warn); why.Add("配信用ウィンドウへ出したコマ数が、本体の表示より 2 割以上少なくなっています。"); }

            if (s1 != null && snapOk)
            {
                using Bitmap? main = await Task.Run(() => _video.Snapshot(cropToVideo: true));
                if (main != null)
                {
                    double diff = ThumbDiff(s1, main, out double meanS);
                    item.Num("本体の絵との違い（縮小して比べた平均）", $"{diff:0.0}（0 が同じ、255 が正反対）／ 配信用の明るさ {meanS:0}");
                    if (diff > 60) { v = Worst(v, Verdict.Ng); why.Add("配信用ウィンドウの絵が、本体に出ている絵と大きく違います。"); }
                    else why.Add("配信用ウィンドウには、本体と同じ映像が出ています。");
                }

                // ---- no overlay in it: the info panel and a notice are painted in colours the video does not have, and are seen on the
                //      main window (screen read) but must be absent from the stream window ----
                Color mInfo = Color.FromArgb(0, 255, 0), mOsd = Color.FromArgb(255, 0, 255);
                long n0 = CountColor(s1, mInfo) + CountColor(s1, mOsd);
                InfoMode wasMode = CurrentInfoMode;
                _info.TestBack = mInfo; _osd.TestBack = mOsd;
                try
                {
                    if (wasMode != InfoMode.Full) SetInfoMode(InfoMode.Full); else { UpdateInfo(); _info.Invalidate(); }
                    _info.BringToFront(); _osd.BringToFront(); _testCaption?.BringToFront();
                    ShowOsd(L.T("テスト表示"), OsdLongMilliseconds);
                    await Wait(500);
                    Rectangle area = OverlayScreenRect(_info, new Rectangle(2, Math.Max(0, _info.Height / 2 - 4), 6, 8));
                    bool seen = false, owned = TestProbes.OwnedByWindow(Handle, area);
                    if (owned) seen = TestProbes.SampleMatch(area, mInfo).Item1 >= 0.9;
                    using Bitmap? s2 = await Task.Run(() => _video.StreamSnapshot());
                    if (s2 == null) { item.Num("重ねた表示の写り込み", "読み戻せませんでした"); v = Worst(v, Verdict.Warn); }
                    else
                    {
                        long n1 = CountColor(s2, mInfo) + CountColor(s2, mOsd);
                        long leak = Math.Max(0, n1 - n0);
                        item.Num("重ねた表示の写り込み", $"{leak} ピクセル（情報表示・通知を出して調べた：本体の画面では{(owned ? (seen ? "見えていました" : "見えませんでした") : "確認できませんでした（他のウィンドウが重なっていた）")}）");
                        if (leak > 200) { v = Worst(v, Verdict.Ng); why.Add("配信用ウィンドウに、情報表示や通知が写り込んでいます。"); }
                        else if (owned && seen) why.Add("情報表示と通知を本体に出しても、配信用ウィンドウには入りませんでした。");
                        else { v = Worst(v, Verdict.Warn); why.Add("本体の画面で情報表示が見えていることを確かめられなかったため、写り込みがないことの裏付けが弱くなっています。"); }
                    }
                }
                finally
                {
                    _info.TestBack = null; _osd.TestBack = null; _osdQueue.Clear(); _osdCurrent = null; _osdTimer.Stop(); _osd.Showing = false;
                    if (CurrentInfoMode != wasMode) SetInfoMode(wasMode); else _info.Invalidate();
                }
            }

            // ---- another size is followed: the window and its swap chain ----
            SetStreamSize("1280x720");
            await Wait(SettleMs);
            using Bitmap? s3 = await Task.Run(() => _video.StreamSnapshot());
            bool size2 = s3 != null && s3.Size == new Size(1280, 720) && _stream != null && _stream.ClientSize == new Size(1280, 720);
            item.Num("1280x720 に変えたとき", size2 ? "窓も映像も 1280x720 になりました" : "大きさが変わりませんでした");
            if (!size2) { v = Worst(v, Verdict.Ng); why.Add("大きさを変えても配信用ウィンドウが追従しませんでした。"); }
            // the fit: a picture of the video's aspect inside a square keeps its aspect and is centred
            Rectangle fit = D3DRenderer.StreamFit(new Size(1920, 1080), new Size(1000, 1000), false);
            bool fitOk = fit.Width == 1000 && Math.Abs(fit.Height - 562.5) <= 1 && fit.X == 0 && Math.Abs(fit.Y - (1000 - fit.Height) / 2) <= 1;
            item.Num("縦横比を保って収める計算", fitOk ? "正しい（1920x1080 を 1000x1000 に → 1000x563 ほどを中央）" : $"おかしい（{fit}）");
            if (!fitOk) { v = Worst(v, Verdict.Ng); why.Add("縦横比を保って収める計算が合っていません。"); }

            // ---- pacing of the main window with it closed again ----
            CloseStreamWindow(forget: false);
            await Wait(SettleMs);
            StreamWindowRun b2 = await StreamPacingWindow(ms, srcFps, panel);

            if (b1.Ok && w.Ok && b2.Ok)
            {
                double baseJ = (b1.Stall + b2.Stall) / 2, noise = Math.Abs(b1.Stall - b2.Stall);
                double dj = w.Stall - baseJ;
                item.Num("本体のカクつき率（1.25 倍より長く出たコマ。開く前 / 開いている間 / 閉じた後）", $"{Pct(b1.Judder)} / {Pct(w.Judder)} / {Pct(b2.Judder)}（60 fps を 144 Hz で見るときは、2 コマ分と 3 コマ分の境目にあたるため、この数字は何もしなくても 10 ポイントほど揺れます）")
                    .Num("本当のカクつき率（1.5 倍以上長く出たコマ）", $"{Pct(b1.Stall)} / {Pct(w.Stall)} / {Pct(b2.Stall)}")
                    .Num("開いている間の増え方（本当のカクつき率）", $"{dj * 100:+0.0;-0.0;0.0} ポイント（同じ条件を 2 回測ったときの差 {noise * 100:0.0} ポイント）")
                    .Num("0.5 秒ごとの表示回数（開く前 / 開いている間 / 閉じた後）", $"{b1.Timeline} / {w.Timeline} / {b2.Timeline}")
                    .Num("本体の表示回数（開く前 / 開いている間 / 閉じた後）", $"{b1.MainPerSec:0.0} / {w.MainPerSec:0.0} / {b2.MainPerSec:0.0} 回/秒（映像の入力 {b1.InPerSec:0.0} / {w.InPerSec:0.0} / {b2.InPerSec:0.0} コマ/秒、捨てたコマ {b1.Dropped} / {w.Dropped} / {b2.Dropped}）");
                if (dj > 0.02 + noise) { v = Worst(v, Verdict.Ng); why.Add("配信用ウィンドウを開くと、本体のコマの出方が 2 ポイント以上悪くなりました。"); }
                else if (dj > 0.02) { v = Worst(v, Verdict.Warn); why.Add("開いている間のカクつきは 2 ポイントを超えましたが、同じ条件の測り直しの差の範囲です。"); }
                else why.Add("配信用ウィンドウを開いても、本体のコマの出方は変わりませんでした（増加 2 ポイント以内）。");
                if (w.Dropped > Math.Max(b1.Dropped, b2.Dropped) + 2) { v = Worst(v, Verdict.Warn); why.Add($"開いている間に、本体が捨てたコマが増えました（{w.Dropped} コマ）。"); }
                double base_ = (b1.MainPerSec + b2.MainPerSec) / 2;
                if (base_ > 0 && w.MainPerSec < base_ * 0.95) { v = Worst(v, Verdict.Warn); why.Add("開いている間、本体の表示回数が 5% 以上減りました。"); }
            }
            else
            {
                item.Num("本体のカクつき率", "コマの出方の記録が足りず比べられませんでした");
                v = Worst(v, Verdict.Warn); why.Add("表示側の記録が足りなかったため、本体のコマの出方を比べられませんでした。");
            }
            item.Verdict = v;
            item.Summary = v == Verdict.Ok ? "映像だけが出て、本体の表示は変わりません" : v == Verdict.Warn ? "動きますが、確かめきれない点があります" : "期待どおりではない点があります";
            item.Explanation = string.Join("\n", why) + "\nOBS などの「ウィンドウキャプチャ」で取り込む用のウィンドウです。2 枚目のスワップチェーンに、本体の表示のあとで映像だけを描きます。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng; item.Summary = "この項目は実行できませんでした"; item.Explanation = "理由: " + ex.Message;
        }
        finally
        {
            if (_stream != null) CloseStreamWindow(forget: false);
            _settings.StreamWindowSize = sizeBefore;
            if (openBefore) { try { OpenStreamWindow(); } catch { /* the next tick tries again */ } }
        }
        sec.Items.Add(item);
    }

    // =============================================================================================
    //  信号なしの検出と省電力
    // =============================================================================================

    private static Bitmap SynthCardMessage(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(b);
        g.Clear(Color.Black);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var f = new Font("Arial", h / 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("No Signal", f, Brushes.White, new RectangleF(0, h * 0.45f, w, h * 0.2f), sf);
        g.FillEllipse(Brushes.White, w * 0.46f, h * 0.22f, w * 0.08f, w * 0.08f);
        return b;
    }

    private static Bitmap SynthGameFrame(int w, int h, int phase)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(b);
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(60, 110, 200), Color.FromArgb(230, 200, 120), 45f)) g.FillRectangle(br, 0, 0, w, h);
        for (int i = 0; i < 12; i++)
        {
            using var br = new SolidBrush(Color.FromArgb(40 + i * 15, 220 - i * 12, 60 + i * 10));
            g.FillRectangle(br, (i * 53 + phase * 9) % w, (i * 31 + phase * 4) % h, w / 6, h / 5);
        }
        return b;
    }

    private static Bitmap SynthBlack(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(b);
        g.Clear(Color.Black);
        return b;
    }

    /// <summary>A very dark scene with one bright blob that moves (a game's loading screen or a night scene).</summary>
    private static Bitmap SynthDarkMoving(int w, int h, int phase)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(b);
        g.Clear(Color.Black);
        g.FillEllipse(Brushes.White, (phase * 11) % (w - 40), h / 3, 40, 40);
        return b;
    }

    private sealed record SigSeq(string Name, Func<int, Bitmap> Frame, double Seconds);

    /// <summary>Replays pictures through a headless renderer (the real thumbnail pass on the GPU) on a simulated clock at 30 fps.
    /// Returns the time of each state change and the power-saving flags at checkpoints.</summary>
    private static (List<(double T, SignalState S)> Changes, List<(double T, bool Saving)> Saving, SignalState Final, string? Error)
        ReplaySignal(List<SigSeq> seqs)
    {
        var changes = new List<(double, SignalState)>();
        var saving = new List<(double, bool)>();
        long freq = Stopwatch.Frequency;
        using var r = new D3DRenderer();
        try
        {
            r.CreateDevice();
            r.CreateShaders();
            if (!r.DupAvailable) return (changes, saving, SignalState.Unknown, "同じ絵の検出に使うシェーダーを作れませんでした");
            long simNow = freq;     // start away from 0 (0 means "not set" in the detector)
            r.ClockOverride = () => simNow;
            double t = 0;
            SignalState last = SignalState.Unknown;
            bool lastSaving = false;
            int n = 0;
            foreach (SigSeq s in seqs)
            {
                int frames = (int)Math.Round(s.Seconds * 30);
                var cache = new Dictionary<int, Bitmap>();
                try
                {
                    for (int i = 0; i < frames; i++)
                    {
                        int key = s.Name.StartsWith("moving") ? i % 24 : 0;
                        if (!cache.TryGetValue(key, out Bitmap? bmp)) cache[key] = bmp = s.Frame(key);
                        simNow = freq + (long)(t * freq);
                        long no = r.ReplaySubmit(bmp, simNow, simNow);
                        r.DupResolveUpTo(no);
                        SignalState st = r.Signal.State;
                        if (st != last) { changes.Add((t, st)); last = st; }
                        bool sv = r.Signal.SavingPower(simNow);
                        if (sv != lastSaving) { saving.Add((t, sv)); lastSaving = sv; }
                        t += 1.0 / 30; n++;
                    }
                }
                finally { foreach (Bitmap b in cache.Values) b.Dispose(); }
            }
            return (changes, saving, last, null);
        }
        catch (Exception ex) { return (changes, saving, SignalState.Unknown, ex.Message); }
    }

    private async Task FeatureSignal(TestSection sec, List<CaptureMode> modes, CaptureMode? mode)
    {
        var item = new TestItem { Name = "信号なしの検出と省電力" };
        try { EnsureEngine(false, mode); }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng; item.Summary = "映像を開始できませんでした"; item.Explanation = ex.Message;
            sec.Items.Add(item);
            return;
        }
        Verdict v = Verdict.Ok;
        var why = new List<string>();
        item.Num("今の信号の状態", $"{_video.SignalState}（省電力: {(_video.PowerSaving ? "働いています" : "働いていません")}）")
            .Num("状態が変わった回数（この起動中）", _video.SignalChanges.ToString());

        // ---- synthetic: the real thumbnail pass, a simulated 30 fps clock ----
        const int W = 480, H = 270;
        const double T1 = 7.5, T2 = 10.0;     // the console is switched on at T1 and off again at T2 (seconds of the simulated clock)
        var seqs = new List<SigSeq>
        {
            new("card", _ => SynthCardMessage(W, H), T1),
            new("moving-game", p => SynthGameFrame(W, H, p), T2 - T1),
            new("card", _ => SynthCardMessage(W, H), 3.0),
        };
        var rA = await Task.Run(() => ReplaySignal(seqs));
        // B: a plain black picture (what the card sends with nothing connected) counts after 3 s; C: a dark scene that moves never does
        var rB = await Task.Run(() => ReplaySignal(new List<SigSeq>
        {
            new("black", _ => SynthBlack(W, H), 5.0),
            new("moving-game", p => SynthGameFrame(W, H, p), 2.5),
        }));
        var rC = await Task.Run(() => ReplaySignal(new List<SigSeq> { new("moving-dark", p => SynthDarkMoving(W, H, p), 6.0) }));
        if (rA.Error != null || rB.Error != null || rC.Error != null)
        {
            item.Verdict = Verdict.Info; item.Summary = "[情報] 検出の確認ができませんでした";
            item.Explanation = "理由: " + (rA.Error ?? rB.Error ?? rC.Error);
            sec.Items.Add(item);
            return;
        }
        var chA = rA.Changes;
        double? tNo = chA.Where(c => c.S == SignalState.NoSignal).Select(c => (double?)c.T).FirstOrDefault();
        double? tSig = chA.Where(c => c.S == SignalState.Signal).Select(c => (double?)c.T).FirstOrDefault();
        double? tNo2 = chA.Where(c => c.S == SignalState.NoSignal).Select(c => (double?)c.T).Skip(1).FirstOrDefault();
        double? sv1 = rA.Saving.Where(s => s.Saving).Select(s => (double?)s.T).FirstOrDefault();
        double? svOff = rA.Saving.Where(s => !s.Saving && s.T >= T1 - 1e-6 && s.T < T1 + 0.5).Select(s => (double?)s.T).FirstOrDefault();
        bool aNo = tNo is >= 1.9 and <= 2.5;
        bool aSave = sv1 is { } sv && tNo is { } tn && sv >= tn + 4.9 && sv <= tn + 5.4;
        bool aSig = tSig is { } ts && ts >= T1 + 1.4 && ts <= T1 + 2.0;
        bool aSaveEnds = svOff != null;
        bool aNo2 = tNo2 is { } t2 && t2 >= T2 + 1.9 && t2 <= T2 + 2.6;
        item.Num("電源オフ（黒地に白い文字を静止）", tNo is { } a ? $"{a:0.0} 秒で「信号なし」と判定（2 秒以上続いたら）" : "判定されませんでした")
            .Num("省電力に入る", sv1 is { } b ? $"{b:0.0} 秒（信号なしから約 5 秒後）" : "入りませんでした")
            .Num("電源オン（動く映像に切り替え）", tSig is { } c ? $"{c - T1:0.0} 秒で「信号あり」（1.5 秒続いたら）／ 省電力は{(aSaveEnds ? "すぐ止まりました" : "止まりませんでした")}" : "「信号あり」になりませんでした")
            .Num("もう一度電源オフ", tNo2 is { } d ? $"{d - T2:0.0} 秒で「信号なし」" : "判定されませんでした");
        if (!aNo) { v = Worst(v, Verdict.Ng); why.Add("カードの「No Signal」画面を、2 秒前後で「信号なし」と判定できませんでした。"); }
        if (!aSave) { v = Worst(v, Verdict.Ng); why.Add("信号なしが約 5 秒続いたあとの省電力に入る時刻が合いません。"); }
        if (!aSig) { v = Worst(v, Verdict.Ng); why.Add("動く映像が来たあと、1.5 秒前後で「信号あり」に変われませんでした。"); }
        if (!aSaveEnds) { v = Worst(v, Verdict.Ng); why.Add("映像が戻っても省電力がすぐに止まりませんでした。"); }
        if (!aNo2) { v = Worst(v, Verdict.Ng); why.Add("電源を切り直したとき、もう一度「信号なし」と判定できませんでした。"); }

        double? tBlack = rB.Changes.Where(c => c.S == SignalState.NoSignal).Select(c => (double?)c.T).FirstOrDefault();
        double? tBlackEnd = rB.Changes.Where(c => c.S == SignalState.Signal).Select(c => (double?)c.T).FirstOrDefault();
        bool bOk = tBlack is >= 2.9 and <= 3.6 && tBlackEnd is { } be && be >= 5.0 + 1.4 && be <= 5.0 + 2.0;
        bool cNever = !rC.Changes.Any(c => c.S == SignalState.NoSignal);
        item.Num("真っ黒な画面（何もつないでいないとき）", tBlack is { } bk ? $"{bk:0.0} 秒で「信号なし」（3 秒続いたら）／ 映像が来て {((tBlackEnd ?? 0) - 5.0):0.0} 秒で「信号あり」" : "「信号なし」にならなかった")
            .Num("暗くて動く場面", cNever ? "「信号なし」とは判定されませんでした（静止していないため）" : "「信号なし」と誤判定されました");
        if (!bOk) { v = Worst(v, Verdict.Ng); why.Add("真っ黒な画面を、3 秒前後で「信号なし」と判定し、映像が来たら「信号あり」に戻すことができませんでした。"); }
        if (!cNever) { v = Worst(v, Verdict.Ng); why.Add("暗くて動く場面を「信号なし」と誤判定しました。"); }
        if (bOk && cNever) why.Add("動いている場面は、暗くても「信号なし」になりません。ただし、ゲームの真っ黒な画面が 3 秒以上続くときや、暗くて文字だけがある静止した画面（読み込み中の表示など）は、カードの画面と区別できず「信号なし」になることがあります。そのため「信号が来たら全画面にする」は既定でオフです。");

        // ---- live: reach the card's own message by asking for a mode the console is not sending, then measure with / without saving ----
        string measured = "試せませんでした";
        var live = new List<string>();
        if (_video is D3DVideoEngine)
        {
            _testPowerSave = true;
            ApplyRenderOptions();
            var cands = modes.Where(m => !m.Equals(mode)).OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Fps).Take(_tQuick ? 2 : 3).ToList();
            bool reached = false;
            foreach (CaptureMode m in cands)
            {
                try { StartMode(m); } catch { continue; }
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 7000 && _video.SignalState != SignalState.NoSignal) await Wait(200);
                live.Add($"{ModeLabel(m)}: {_video.SignalState}");
                if (_video.SignalState != SignalState.NoSignal) continue;
                reached = true;
                ApplyRenderOptions();
                var sw2 = Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 8000 && !_video.PowerSaving) await Wait(200);
                bool saving = _video.PowerSaving;
                _settings.Interpolation = true;
                ApplyRenderOptions();
                await Wait(1500);
                async Task<(double Pres, double Gpu, double Recv)> Meas()
                {
                    long p0 = _video.FramesPresented, r0 = _video.FramesReceived; var sx = Stopwatch.StartNew();
                    var gp = new List<double>();
                    RenderStats? last = _video.Stats;
                    while (sx.ElapsedMilliseconds < 3000)
                    {
                        await Wait(200);
                        RenderStats? s = _video.Stats;
                        if (s != null && !ReferenceEquals(s, last)) { gp.Add(s.GpuMsAvg); last = s; }
                    }
                    double el = sx.Elapsed.TotalSeconds;
                    return ((_video.FramesPresented - p0) / el, gp.Count > 0 ? gp.Average() : 0, (_video.FramesReceived - r0) / el);
                }
                var after = await Meas();
                _testPowerSave = false; ApplyRenderOptions();   // the engine may have been replaced meanwhile: the property is on the current one
                await Wait(2000);
                var before = await Meas();
                _testPowerSave = true; ApplyRenderOptions();
                double busyBefore = before.Pres * before.Gpu / 1000.0, busyAfter = after.Pres * after.Gpu / 1000.0;
                item.Num("信号なしの画面で測った映像", $"{ModeLabel(m)}（カード自身の画面。受け取り {after.Recv:0} コマ/秒）")
                    .Num("省電力なし（今までと同じ）", $"表示 {before.Pres:0.0} 回/秒、1 回あたり GPU {before.Gpu:0.00} ms → GPU の使用率の目安 {busyBefore * 100:0.0}%")
                    .Num("省電力あり", $"表示 {after.Pres:0.0} 回/秒、1 回あたり GPU {after.Gpu:0.00} ms → GPU の使用率の目安 {busyAfter * 100:0.00}%{(saving ? "" : "（省電力に入っていませんでした）")}");
                measured = $"省電力 {after.Pres:0.0} 回/秒・GPU 目安 {busyAfter * 100:0.00}%（なし: {before.Pres:0} 回/秒・{busyBefore * 100:0.0}%）";
                if (!saving) { v = Worst(v, Verdict.Ng); why.Add("信号なしが続いても省電力に入りませんでした。"); }
                else if (after.Pres > 2.6) { v = Worst(v, Verdict.Ng); why.Add($"省電力中の表示が 1 秒に {after.Pres:0.0} 回あり、目標の 2 回を超えています。"); }
                else if (after.Pres < before.Pres) why.Add("信号なしのあいだは、表示を 1 秒に 2 回ほどに減らし、補間・拡大の画質処理・計測を止めます。");
                // back to a picture: the state must follow quickly
                break;
            }
            if (!reached)
            {
                item.Num("カード自身の画面での実測", live.Count > 0 ? "試したモードでは「信号なし」の画面になりませんでした（" + string.Join("、", live) + "）" : "試せるモードがありませんでした");
                why.Add("カードが自分の「No Signal」画面を出すモード（ゲーム機が出していない解像度やコマ数）が見つからなかったため、省電力の実測は行っていません。ゲーム機を 1080p で映しているときに、4K や 1440p のモードを選ぶと出ます。");
                v = Worst(v, Verdict.Warn);
            }
            _testPowerSave = false;
            try { EnsureEngine(false, mode); } catch { /* the next step starts it again */ }
        }
        item.Verdict = v;
        item.Summary = v == Verdict.Ok ? "信号なしを判定でき、そのあいだ描画をほとんど止めます" + (measured != "試せませんでした" ? "（" + measured + "）" : "")
            : v == Verdict.Warn ? "判定は正しく働きます（実測は行えませんでした）" : "期待どおりではない点があります";
        item.Explanation = string.Join("\n", why);
        sec.Items.Add(item);
    }
}
