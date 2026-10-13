using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

// =============================================================================================
//  テストモード（自動試験）
//
//  Drives the real app (real capture card, real D3D engine, the real window on the chosen monitor)
//  through a fixed list of steps, measures each one and writes a report (TestReport.cs).
//
//  SAFETY. Everything the run changes is in AppSettings and a few window fields, so:
//    - AppSettings is deep-copied first and written to test-in-progress.json (next to settings.json);
//    - nothing is saved to settings.json while the run is active (SaveSettings returns early);
//    - the finally block restores the copy, rebuilds the engine from it and puts the window back;
//    - if the process dies anyway, the next start finds the marker and restores from it (Program.Main).
//  The UI thread is never blocked while waiting: every pause is `await Task.Delay`, so Esc is read
//  at once. Only the engine start itself is synchronous, exactly as in normal use.
//
//  The report's wording is Japanese only (it is a diagnostic document, see TestReport.cs).
// =============================================================================================

/// <summary>The marker file that makes an interrupted test mode recoverable, and the report location.</summary>
internal static class TestModeGuard
{
    /// <summary>True when this start restored settings left behind by an interrupted test.</summary>
    public static bool Recovered;

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuCap", "test-in-progress.json");

    public static string ReportRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "YuCap", "テストレポート");

    private sealed class Marker
    {
        public int Pid { get; set; }
        public string? StartedAt { get; set; }
        public AppSettings? Settings { get; set; }
    }

    public static void WriteMarker(AppSettings saved)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(
                new Marker { Pid = Environment.ProcessId, StartedAt = DateTime.Now.ToString("o"), Settings = saved },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Info("test mode: could not write the marker - " + ex.Message); }
    }

    public static void ClearMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); }
        catch (Exception ex) { Log.Info("test mode: could not delete the marker - " + ex.Message); }
    }

    /// <summary>The settings saved by a test that never finished, or null. A marker whose process is still
    /// alive belongs to a test running right now in another YuCap and is left alone. The marker is deleted
    /// once read.</summary>
    public static AppSettings? TryRecover()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            Marker? m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
            if (m?.Settings != null && m.Pid != Environment.ProcessId && m.Pid > 0)
            {
                try
                {
                    using Process p = Process.GetProcessById(m.Pid);
                    if (!p.HasExited && p.ProcessName.Contains("YuCap", StringComparison.OrdinalIgnoreCase)) return null;
                }
                catch (ArgumentException) { /* no such process: the test died */ }
            }
            File.Delete(MarkerPath);
            return m?.Settings;
        }
        catch (Exception ex)
        {
            Log.Info("test mode: marker unreadable - " + ex.Message);
            try { File.Delete(MarkerPath); } catch { }
            return null;
        }
    }
}

/// <summary>What to test and where.</summary>
internal sealed class TestOptions
{
    public DisplayInfo Target = null!;
    public bool Quick;
}

/// <summary>The caption bar shown over the video while the test runs (a child control like InfoOverlay).</summary>
internal sealed class TestCaption : Control
{
    private readonly Font _font = new("Segoe UI", 12f, FontStyle.Bold);
    private readonly Font _small = new("Segoe UI", 10f, FontStyle.Regular);
    private string _hint = string.Empty;

    public TestCaption()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
        TabStop = false;
        Height = 40;
    }

    public void Set(string text, string hint)
    {
        Text = text;
        _hint = hint;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var back = new SolidBrush(Color.FromArgb(225, 16, 16, 16));
        e.Graphics.FillRectangle(back, ClientRectangle);
        using var accent = new SolidBrush(Ui.Colors.Accent);
        e.Graphics.FillRectangle(accent, 0, 0, 5, Height);
        Size hs = TextRenderer.MeasureText(_hint, _small, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, _hint, _small, new Point(Width - hs.Width - 14, (Height - hs.Height) / 2),
            Color.FromArgb(200, 200, 200), TextFormatFlags.NoPadding);
        var textRect = new Rectangle(16, 0, Math.Max(10, Width - hs.Width - 48), Height);
        TextRenderer.DrawText(e.Graphics, Text, _font, textRect, Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _small.Dispose(); }
        base.Dispose(disposing);
    }
}

public sealed partial class MainForm
{
    // ---- state ----
    private bool _testRunning;
    private CancellationTokenSource? _testCts;
    private TestCaption? _testCaption;
    private TestOptions _topt = null!;
    private TestReport _rep = null!;
    private AppSettings? _testSaved;
    private int _testStep, _testTotal;
    private bool _testCtxBlocked;

    private bool _tQuick => _topt.Quick;
    private int SettleMs => _tQuick ? 1000 : 1500;
    private int CaptureMeasureMs => _tQuick ? 3000 : 5000;
    private int RenderMeasureMs => _tQuick ? 2000 : 5000;

    private Task Wait(int ms) => Task.Delay(ms, _testCts!.Token);

    // =============================================================================================
    //  Start dialog
    // =============================================================================================

    /// <summary>The monitor the dialog proposes: the stored fullscreen display, else the biggest
    /// non-primary one (the TV the test is meant for), else the primary.</summary>
    private DisplayInfo DefaultTestDisplay(List<DisplayInfo> all)
    {
        Screen? stored = Displays.Find(_settings.FullscreenDisplay);
        if (stored != null)
        {
            DisplayInfo? d = all.FirstOrDefault(x => x.DeviceName == stored.DeviceName);
            if (d != null) return d;
        }
        DisplayInfo? other = all.Where(x => !x.Primary).OrderByDescending(x => (long)x.Bounds.Width * x.Bounds.Height).FirstOrDefault();
        return other ?? all.FirstOrDefault(x => x.Primary) ?? all[0];
    }

    /// <summary>The value of <c>--display</c>: an integer N means "\\.\DISPLAYN" (as Windows numbers them, or
    /// failing that the Nth monitor in the list, counting from 1); otherwise the device name or part of the
    /// monitor's name.</summary>
    private static DisplayInfo? ParseTestDisplay(string? arg, List<DisplayInfo> all)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;
        arg = arg.Trim();
        if (int.TryParse(arg, out int n))
        {
            return all.FirstOrDefault(d => string.Equals(d.DeviceName, @"\\.\DISPLAY" + n, StringComparison.OrdinalIgnoreCase))
                   ?? (n >= 1 && n <= all.Count ? all[n - 1] : null);
        }
        return all.FirstOrDefault(d => string.Equals(d.DeviceName, arg, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(d => d.FriendlyName.Contains(arg, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowTestModeDialog()
    {
        if (_testRunning) return;
        List<DisplayInfo> all = Displays.List();
        if (all.Count == 0) return;

        using var dlg = Ui.NewDialog(L.T("テストモード（自動試験）"), 480, 300);
        var stack = Ui.Stack();
        var lbl = Ui.Label(L.T("試験するモニター:"), 0, 0);
        lbl.Margin = new Padding(0, 0, 0, Ui.Gap / 2);
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, Ui.Gap * 2),
        };
        foreach (DisplayInfo d in all) combo.Items.Add(d.MenuLabel());
        combo.SelectedIndex = Math.Max(0, all.IndexOf(DefaultTestDisplay(all)));

        int modes = _video.IsRunning ? Math.Max(8, _video.GetModes().Count) : 20;
        int minutes = (int)Math.Ceiling((90 + modes * 8 + 10 * 11 + 70 + 60) / 60.0);
        var est = Ui.Label(L.F("所要時間の目安: 約 {0} 分", minutes), 0, 0);
        est.Margin = new Padding(0, 0, 0, Ui.Gap);
        var note = Ui.FlowHint(L.T("試験中は画面が何度も切り替わります。Esc でいつでも中止できます。終了すると設定は元に戻ります。"));
        note.Margin = new Padding(0, 0, 0, Ui.Gap);
        var where = Ui.FlowHint(L.T("レポートは「ピクチャ」フォルダの YuCap\\テストレポート に保存されます。"));
        stack.Controls.Add(lbl);
        stack.Controls.Add(combo);
        stack.Controls.Add(est);
        stack.Controls.Add(note);
        stack.Controls.Add(where);
        Ui.FrameOkCancel(dlg, stack, false, L.T("開始"));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var opt = new TestOptions { Target = all[Math.Clamp(combo.SelectedIndex, 0, all.Count - 1)] };
        BeginInvoke(new Action(async () => await RunTestModeAsync(opt, openReport: true)));
    }

    /// <summary>Called once the window is up when the app was started with <c>--test-mode</c>.</summary>
    private void StartUnattendedTest()
    {
        List<DisplayInfo> all = Displays.List();
        if (all.Count == 0) { Log.Info("test mode: no display found"); Close(); return; }
        DisplayInfo? chosen = ParseTestDisplay(Program.Options.TestDisplay, all);
        if (chosen == null)
        {
            if (!string.IsNullOrWhiteSpace(Program.Options.TestDisplay))
                Log.Info($"test mode: display '{Program.Options.TestDisplay}' not found - using the default");
            chosen = DefaultTestDisplay(all);
        }
        var opt = new TestOptions { Target = chosen, Quick = Program.Options.TestQuick };
        BeginInvoke(new Action(async () => await RunTestModeAsync(opt, openReport: false)));
    }

    // =============================================================================================
    //  Run / restore
    // =============================================================================================

    private sealed record UiBefore(bool Fullscreen, bool Pip, double Zoom, int PanX, int PanY, bool InfoVisible);

    private async Task RunTestModeAsync(TestOptions o, bool openReport)
    {
        if (_testRunning) return;
        _testRunning = true;
        _testCts = new CancellationTokenSource();
        _topt = o;
        _testStep = 0;
        _testTotal = 0;

        _rep = new TestReport(Path.Combine(TestModeGuard.ReportRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        string json = JsonSerializer.Serialize(_settings);
        _testSaved = JsonSerializer.Deserialize<AppSettings>(json)!;
        TestModeGuard.WriteMarker(JsonSerializer.Deserialize<AppSettings>(json)!);
        var before = new UiBefore(_isFullscreen, _isPip, _zoom, _panX, _panY, _info.Visible);
        Log.Info($"test mode: start target={o.Target.DeviceName} quick={o.Quick} report={_rep.Folder}");

        try
        {
            await TestBody(before);
            _rep.State = "完了";
        }
        catch (OperationCanceledException)
        {
            _rep.State = "中止";
            Log.Info("test mode: aborted by the user");
        }
        catch (Exception ex)
        {
            _rep.State = "エラー";
            _rep.Failure = ex.Message;
            Log.Info("test mode: failed - " + ex);
        }
        finally
        {
            try { RestoreAfterTest(before); }
            catch (Exception ex) { Log.Info("test mode: restore failed - " + ex); }
            _rep.Save();
            try
            {
                Directory.CreateDirectory(TestModeGuard.ReportRoot);
                File.WriteAllText(Path.Combine(TestModeGuard.ReportRoot, "latest.txt"), _rep.Folder + Environment.NewLine,
                    new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Info("test mode: latest.txt - " + ex.Message); }
            TestModeGuard.ClearMarker();
            _testRunning = false;
            _testCts?.Dispose();
            _testCts = null;
            Log.Info($"test mode: finished [{_rep.State}] {_rep.Folder}");
        }

        if (openReport)
        {
            OpenInBrowser(_rep.HtmlPath);
        }
        if (Program.Options.TestMode) Close();
    }

    private static void OpenInBrowser(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info("test mode: could not open the report - " + ex.Message); }
    }

    private static void CopySettings(AppSettings from, AppSettings to)
    {
        foreach (var p in typeof(AppSettings).GetProperties())
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                p.SetValue(to, p.GetValue(from));
    }

    private void RestoreAfterTest(UiBefore before)
    {
        // test widgets away
        if (_testCaption != null) { _testCaption.Visible = false; Controls.Remove(_testCaption); _testCaption.Dispose(); _testCaption = null; }
        _osd.TestBack = null; _info.TestBack = null;
        _osdQueue.Clear(); _osdCurrent = null; _osdTimer.Stop(); _osd.Visible = false;
        Ui.DialogAudit = null;
        if (_frozen) ToggleFreeze();
        if (_info.Visible != before.InfoVisible) ToggleInfo();

        // settings back, then everything that was derived from them
        if (_testSaved != null) CopySettings(_testSaved, _settings);
        L.English = _settings.Language == "en";
        _displayMode = (VideoDisplayMode)Math.Clamp(_settings.DisplayMode, 0, 3);
        _zoom = before.Zoom; _panX = before.PanX; _panY = before.PanY;

        // a fresh engine from the saved renderer / low-latency / mode choice
        Exception? failure = ReplaceEngine(_settings.Renderer);
        if (failure != null)
        {
            _videoError = Errors.Describe(failure);
            Log.Info("test mode: could not restart the saved renderer - " + failure.Message);
        }
        else _videoError = null;
        ApplyRenderOptions();

        // window back
        if (!before.Fullscreen && _isFullscreen) ExitFullscreen(userAction: false);
        else if (before.Fullscreen && _isFullscreen) PlaceFullscreen();
        if (before.Pip && !_isPip) EnterPip();
        TopMost = _isFullscreen || _isPip || _alwaysOnTop;

        _testRunning = false;   // from here on settings may be saved again
        LayoutCanvas(ModeChangeSettleMs);
        UpdateChecks();
        UpdateStatus();
        _canvas.Invalidate();
        Activate();
    }

    // =============================================================================================
    //  The run
    // =============================================================================================

    private async Task TestBody(UiBefore before)
    {
        TestOptions o = _topt;
        TestReport rep = _rep;
        rep.Header = $"YuCap {VersionText()} / 試験するモニター: {o.Target.MenuLabel()}" + (o.Quick ? " / 簡易（--quick）" : "");
        rep.Save();

        PrepareTestUi(before);

        // ---- step 0: environment (also brings the Direct3D engine up) ----
        SetCaption(0, L.T("環境の確認"), preparing: true);
        TestSection env = rep.AddSection("1. 環境");
        await EnvironmentSection(env);
        rep.Save();

        bool videoOk = _video.IsRunning && _settings.Renderer == "d3d";
        List<CaptureMode> modes = videoOk ? _video.GetModes() : new List<CaptureMode>();

        // ---- build the step list ----
        var steps = new List<(string Desc, TestSection Sec, Func<Task> Run)>();
        TestSection secCap = rep.AddSection("2. 取り込み",
            "キャプチャカードから映像が指定のコマ数どおり届くかを、すべての映像モードで調べます。");
        TestSection secRender = rep.AddSection("3. 表示のなめらかさ・負荷",
            "モニターに全画面で出したときの、コマ数・GPU の余裕・コマの出方（フレームペーシング）・可変リフレッシュレート（VRR）の効きを調べます。続けて、フレーム補間の絵の正しさと、映像の上に何かを重ねたときの更新の低下も調べます。");
        TestSection secNotice = rep.AddSection("4. 通知の表示",
            "全画面のとき、通知・一時停止の画像・情報表示が映像の上に見えているかを、画面の色で確かめます。");
        TestSection secFsr = rep.AddSection("5. FSR の効き",
            "標準の拡大と FSR（高画質）の拡大を、このモニターの大きさで比べます。");
        TestSection secHdr = rep.AddSection("6. HDR");
        TestSection secFunc = rep.AddSection("7. 機能",
            "切り取り・色の調整・映像の遅延・表示どおりの保存・回転と反転が正しく働くかを調べます。");
        TestSection secAudio = rep.AddSection("8. 音声");
        TestSection secDlg = rep.AddSection("9. ダイアログの表示崩れ",
            "設定のダイアログを日本語と英語で作り、部品が重なったり枠からはみ出したりしていないかを調べます。");
        TestSection secManual = rep.AddSection("10. 目で確かめる項目", "自動では測れない項目だけを載せています（好みの問題や、基準になる映像が必要なもの）。それぞれに測れない理由を書きました。");

        if (videoOk)
        {
            // 2. capture: every native mode (quick: the fastest of each resolution)
            IEnumerable<CaptureMode> capModes = modes;
            if (o.Quick)
                capModes = modes.GroupBy(m => (m.Width, m.Height)).Select(g => g.OrderByDescending(m => m.Fps).First());
            foreach (CaptureMode m in capModes.ToList())
            {
                CaptureMode mm = m;
                steps.Add((L.F("取り込み: {0}", ModeLabel(mm)), secCap, () => CaptureStep(secCap, mm)));
            }

            // 3. smoothness / load
            var cases = BuildRenderCases(modes, o.Quick, o.Target.RefreshHz);
            var vrrNotes = new List<(bool Ll, PacingQuality Q)>();
            _judderCases.Clear(); _interpOkFps.Clear();
            foreach (RenderCase c in cases)
            {
                RenderCase cc = c;
                steps.Add((L.F("表示の計測: {0}", cc.Label), secRender, () => RenderStep(secRender, cc, vrrNotes)));
            }
            steps.Add((L.F("表示の計測: {0}", L.T("まとめ")), secRender, () => { VrrSummary(secRender, vrrNotes); return Task.CompletedTask; }));
            steps.Add((L.F("表示の計測: {0}", L.T("フレーム補間の画質（既知の動き）")), secRender, () => InterpSynthStep(secRender, modes)));
            steps.Add((L.F("表示の計測: {0}", L.T("フレーム補間の画質（実際の映像）")), secRender, () => InterpLiveStep(secRender, modes)));
            steps.Add((L.F("表示の計測: {0}", L.T("重ねた表示による更新の低下")), secRender, () => OverlayCapStep(secRender, modes)));

            // 4. notices, with low latency off and on
            CaptureMode? m1080 = PickMode(modes, 1920, 1080, maxFps: 60) ?? PickMode(modes, 1920, 1080, 0);
            foreach (bool ll in new[] { false, true })
            {
                bool l2 = ll;
                string tag = l2 ? L.T("低遅延オン") : L.T("低遅延オフ");
                steps.Add((L.F("通知の表示: {0}", tag + " / " + L.T("通知")), secNotice, () => NoticeStep(secNotice, l2, m1080, 0)));
                steps.Add((L.F("通知の表示: {0}", tag + " / " + L.T("一時停止の画像")), secNotice, () => NoticeStep(secNotice, l2, m1080, 1)));
                steps.Add((L.F("通知の表示: {0}", tag + " / " + L.T("情報表示")), secNotice, () => NoticeStep(secNotice, l2, m1080, 2)));
            }

            // 5. FSR
            foreach ((string label, int h) in new[] { ("1080p", 1080), ("1440p", 1440) })
            {
                string lb = label; int hh = h;
                steps.Add((L.F("FSR の見え方: {0}", lb), secFsr, () => FsrStep(secFsr, lb, PickMode(modes, hh * 16 / 9, hh, 0))));
            }

            // 6. HDR
            steps.Add((L.T("HDR の確認"), secHdr, () => HdrStep(secHdr, modes)));

            // 7. features
            CaptureMode? mFeat = PickMode(modes, 1920, 1080, maxFps: 60) ?? modes.FirstOrDefault();
            steps.Add((L.F("機能の確認: {0}", L.T("切り取り")), secFunc, () => FeatureCrop(secFunc, mFeat)));
            steps.Add((L.F("機能の確認: {0}", L.T("色の調整")), secFunc, () => FeatureAdjust(secFunc)));
            steps.Add((L.F("機能の確認: {0}", L.T("映像の遅延")), secFunc, () => FeatureDelay(secFunc)));
            steps.Add((L.F("機能の確認: {0}", L.T("表示どおりの保存")), secFunc, () => FeatureSnapshot(secFunc)));
            steps.Add((L.F("機能の確認: {0}", L.T("回転と反転")), secFunc, () => FeatureOrientation(secFunc)));
        }
        else
        {
            secCap.Items.Add(new TestItem
            {
                Name = "取り込み・表示の試験", Verdict = Verdict.Ng,
                Summary = "Direct3D の映像を開始できなかったため、映像に関する試験は行えませんでした。",
                Explanation = "キャプチャカード（JVA14 など）が USB に接続されているか、他のアプリが使っていないかを確認してから、もう一度試験してください。",
            });
        }

        steps.Add((L.T("音声の確認"), secAudio, () => AudioStep(secAudio)));
        steps.Add((L.T("遅延の確認"), secAudio, () => LatencyStep(secAudio, videoOk)));
        steps.Add((L.T("ダイアログの確認"), secDlg, () => DialogStep(secDlg)));

        _testTotal = steps.Count;
        foreach (var (desc, stepSec, run) in steps)
        {
            _testCts!.Token.ThrowIfCancellationRequested();
            SetCaption(++_testStep, desc);
            try
            {
                if (videoOk && !_video.IsRunning && desc != L.T("ダイアログの確認") && desc != L.T("音声の確認") && desc != L.T("遅延の確認"))
                {
                    Log.Info("test mode: the video stopped - restarting before the next step");
                    Exception? ex = ReplaceEngine("d3d", null);
                    if (ex != null) throw new InvalidOperationException("映像が止まり、再開できませんでした: " + Errors.Describe(ex));
                }
                await run();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Info($"test mode: step '{desc}' failed - {ex}");
                stepSec.Items.Add(new TestItem
                {
                    Name = desc, Verdict = Verdict.Ng, Summary = "この項目は実行できませんでした。",
                    Explanation = "理由: " + ex.Message,
                });
            }
            rep.Save();
        }

        secManual.Items.Add(new TestItem
        {
            Name = "シャープさの好み", Verdict = Verdict.Info,
            Summary = "再生中に Q を押している間は標準の拡大、離すと FSR になります。",
            Explanation = "【測れない理由】輪郭の強さは5の項目で数字にしていますが、どれくらいのくっきり感を心地よいと感じるかは人によって違い、正解がありません。いろいろなシーンで見比べて、シャープさ（表示メニュー → 拡大の画質）を決めてください。5 の項目の画像も参考になります。",
        });
        secManual.Items.Add(new TestItem
        {
            Name = "補間した映像の見た目の好み", Verdict = Verdict.Info,
            Summary = "補間の正しさは3の項目で数字にしています。ぬるぬるした動きが好みかどうかは、ご自身で。",
            Explanation = "【測れない理由】フレーム補間は動きがなめらかになる代わりに、映画のような「コマの間」の感じが薄れ、映像が少し遅れます。この感じ方の良し悪しは人によって違うため、数字では決められません。好みでなければ、メニューの「フレーム補間」をオフにしてください。",
        });
        secManual.Items.Add(new TestItem
        {
            Name = "音ズレ", Verdict = Verdict.Info,
            Summary = "手をたたく動画などで、映像の動きと音が合っているか見てください。",
            Explanation = "【測れない理由】映像の遅れ（3 の数字）と音声側のたまっている量（8）は測っていますが、「映像のこの瞬間に音が鳴る」という基準を持つ映像がないと、実際の音ズレは測れません。ずれていたら、デバイス → 音ズレ補正 で調整します（音を遅らせる／映像を遅らせる）。",
        });
        secManual.Items.Add(new TestItem
        {
            Name = "文字の読みやすさ・色合いの好み", Verdict = Verdict.Info,
            Summary = "ゲーム画面や文字の多い画面を全画面で見て、読みにくい所や好みに合わない色がないか見てください。",
            Explanation = "【測れない理由】色や明るさの調整が正しく効くかは7の項目で、映像のカクつきやちらつきは3の項目で測っています。ここに残るのは、文字が読みやすいか・色合いが好みに合うかという感じ方の部分で、数字にはできません。気になる点があれば、その設定（プロファイル）を保存しておくと切り替えが楽になります。",
        });
        rep.Save();
    }

    private static string VersionText()
    {
        string v = Application.ProductVersion;
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    private void PrepareTestUi(UiBefore before)
    {
        _testCaption = new TestCaption();
        Controls.Add(_testCaption);
        if (!_testCtxBlocked)
        {
            _testCtxBlocked = true;
            _ctx.Opening += (_, e) => { if (_testRunning) e.Cancel = true; };
        }

        // a neutral baseline, so a step measures one thing; restored from the snapshot at the end
        _settings.AdjBrightness = 0; _settings.AdjContrast = 100; _settings.AdjSaturation = 100; _settings.AdjGamma = 100;
        _settings.AvOffsetMs = 0;
        _settings.CropLeft = _settings.CropTop = _settings.CropRight = _settings.CropBottom = 0;
        _settings.LowLatency = false;
        _settings.Interpolation = false;   // only the 補間 cases of section 3 turn it on
        _settings.ScalingQuality = "fsr";
        _settings.FsrSharpness = 50;
        _settings.Rotation = 0; _settings.Mirror = false;
        _settings.DisplayMode = (int)VideoDisplayMode.AspectFit;
        _displayMode = VideoDisplayMode.AspectFit;
        _zoom = 1.0; _panX = _panY = 0;
        _settings.FullscreenDisplay = _topt.Target.Key;

        if (_frozen) ToggleFreeze();
        if (_info.Visible) ToggleInfo();
        if (_isPip) ExitPip();
        if (_isFullscreen) PlaceFullscreen(); else EnterFullscreen(userAction: false);
        TopMost = true;
        Activate();
        Focus();
        LayoutCanvas();
    }

    private void SetCaption(int step, string desc, bool preparing = false)
    {
        if (_testCaption == null) return;
        _testCaption.Set(preparing ? L.T("テストモード：準備中") + " " + desc : L.F("テストモード {0}/{1}：{2}", step, _testTotal, desc),
            L.T("Esc で中止"));
        _testCaption.Visible = true;
        PositionTestCaption();
        _testCaption.BringToFront();
        _testCaption.Update();
    }

    private void PositionTestCaption()
    {
        if (_testCaption is not { Visible: true }) return;
        int w = Math.Min(Math.Max(200, _canvas.Width - 24), 1200);
        _testCaption.Bounds = new Rectangle(_canvas.Left + (_canvas.Width - w) / 2, _canvas.Bottom - _testCaption.Height - 16, w, _testCaption.Height);
    }

    // =============================================================================================
    //  helpers
    // =============================================================================================

    private static CaptureMode? PickMode(List<CaptureMode> modes, int w, int h, int maxFps)
    {
        IEnumerable<CaptureMode> q = modes.Where(m => m.Height == h && (w <= 0 || m.Width == w));
        if (maxFps > 0) q = q.Where(m => m.Fps <= maxFps);
        return q.OrderByDescending(m => m.Fps).FirstOrDefault();
    }

    private void StartMode(CaptureMode mode)
    {
        VideoDeviceInfo info = _currentVideoInfo ?? throw new InvalidOperationException("映像デバイスがありません");
        RunDeviceStart(info.Name, () => _video.Start(info, mode));
        LayoutCanvas();
    }

    /// <summary>Make sure the Direct3D engine runs with the given low-latency setting (a fresh window when it
    /// changes) and the given mode.</summary>
    private void EnsureEngine(bool lowLatency, CaptureMode? mode)
    {
        bool fresh = !_video.IsRunning || _settings.Renderer != "d3d" || _settings.LowLatency != lowLatency;
        _settings.LowLatency = lowLatency;
        if (fresh)
        {
            Exception? ex = ReplaceEngine("d3d", mode);
            if (ex != null) throw new InvalidOperationException(Errors.Describe(ex));
        }
        else if (mode != null && !mode.Equals(_video.CurrentMode))
        {
            StartMode(mode);
        }
        ApplyRenderOptions();
        LayoutCanvas();
        _testCaption?.BringToFront();
    }

    private async Task<List<RenderStats>> CollectStats(int ms)
    {
        var list = new List<RenderStats>();
        RenderStats? last = _video.Stats;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            await Wait(100);
            RenderStats? s = _video.Stats;
            if (s != null && !ReferenceEquals(s, last)) { list.Add(s); last = s; }
        }
        return list;
    }

    /// <summary>Waits for the first received frame (up to 6 s), then measures the exact received-frame
    /// counter over a Stopwatch window. Returns fps = delta / elapsed plus the per-second deltas.</summary>
    private async Task<(double Fps, List<long> PerSec, double Elapsed)> MeasureInputFps(int ms, bool waitFirst)
    {
        if (waitFirst)
        {
            var w = Stopwatch.StartNew();
            while (_video.FramesReceived == 0 && w.ElapsedMilliseconds < 6000) await Wait(50);
            await Wait(3000);
        }
        var per = new List<long>();
        long f0 = _video.FramesReceived, last = f0;
        var sw = Stopwatch.StartNew();
        int nextSec = 1;
        while (sw.ElapsedMilliseconds < ms)
        {
            await Wait(20);
            if (sw.ElapsedMilliseconds >= nextSec * 1000L && nextSec * 1000L <= ms)
            {
                long now = _video.FramesReceived;
                per.Add(now - last); last = now; nextSec++;
            }
        }
        double el = sw.Elapsed.TotalSeconds;
        long f1 = _video.FramesReceived;
        return (el > 0 ? (f1 - f0) / el : 0, per, el);
    }

    private static Verdict Worst(Verdict a, Verdict b) => (Verdict)Math.Max((int)a, (int)b);

    private static string Pct(double f) => f.ToString("P0");

    // =============================================================================================
    //  1. environment
    // =============================================================================================

    private async Task EnvironmentSection(TestSection sec)
    {
        TestOptions o = _topt;

        sec.Items.Add(new TestItem { Name = "YuCap と Windows", Verdict = Verdict.Info }
            .Num("YuCap", VersionText())
            .Num("Windows", TestProbes.OsDescription())
            .Num(".NET", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
            .Num("言語設定", _testSaved?.Language == "en" ? "English" : "日本語")
            .Num("元の描画方式", _testSaved?.Renderer == "d3d" ? "Direct3D" : "標準（Media Foundation）"));

        GpuInfo gpu = TestProbes.QueryGpu();
        sec.Items.Add(new TestItem { Name = "GPU", Verdict = Verdict.Info }
            .Num("名前", gpu.Name).Num("メーカー", gpu.Vendor)
            .Num("ドライバのバージョン", gpu.DriverVersion).Num("ドライバの日付", gpu.DriverDate)
            .Num("専用メモリ", gpu.DedicatedMb > 0 ? $"{gpu.DedicatedMb} MB" : "不明"));

        List<Program.OutputHdr> hdr = new();
        try { hdr = Program.QueryOutputsHdr(); } catch (Exception ex) { Log.Info("test: hdr query failed - " + ex.Message); }
        foreach (DisplayInfo d in Displays.List())
        {
            Program.OutputHdr? h = hdr.FirstOrDefault(x => string.Equals(x.DeviceName, d.DeviceName, StringComparison.OrdinalIgnoreCase));
            bool target = d.DeviceName == o.Target.DeviceName;
            var it = new TestItem { Name = $"モニター: {d.FriendlyName}{(target ? "（試験するモニター）" : "")}", Verdict = Verdict.Info };
            it.Num("デバイス名", d.DeviceName)
              .Num("解像度", $"{d.Bounds.Width}x{d.Bounds.Height}")
              .Num("リフレッシュレート", d.RefreshHz > 0 ? $"{d.RefreshHz} Hz" : "不明")
              .Num("表示の拡大率", TestProbes.ScalePercent(d.Bounds) is int sc && sc > 0 ? $"{sc}%" : "不明")
              .Num("メイン", d.Primary ? "はい" : "いいえ");
            if (h == null || !h.HasInfo) it.Num("HDR（Windows の設定）", "情報なし");
            else it.Num("HDR（Windows の設定）", h.HdrOn ? $"オン（最大輝度 {h.MaxLuminance:0} nits）" : $"オフ（このモニターの最大輝度 {h.MaxLuminance:0} nits）");
            if (target && d.RefreshHz > 0 && d.RefreshHz < 100)
                it.Explanation = "このモニターのリフレッシュレートが 100Hz 未満のため、120fps の映像は全部は表示できません（一部のコマは表示されずに捨てられます）。高リフレッシュレートのテレビ・モニターで試験するときは、そちらのゲームモード（144Hz など）と Windows の画面設定が効いているか確認してください。";
            if (target && d.RefreshHz >= 100)
                it.Explanation = "高リフレッシュレートで動いています。可変リフレッシュ（VRR）の効きは 3 の項目で確かめます。";
            sec.Items.Add(it);
        }

        bool? tearing = TestProbes.TearingSupported();
        sec.Items.Add(new TestItem
        {
            Name = "ティアリング許可の表示（可変リフレッシュに必要）",
            Verdict = tearing == true ? Verdict.Info : tearing == false ? Verdict.Warn : Verdict.Info,
            Summary = tearing == true ? "対応しています" : tearing == false ? "対応していません" : "調べられませんでした",
            Explanation = tearing == false
                ? "この環境では低遅延モードを使っても、画面の更新を待たない表示（同期なし）になるだけで、可変リフレッシュ（VRR）には乗れません。"
                : "低遅延モードのとき、画面の更新を待たずに表示でき、VRR のモニターではコマに合わせて画面が更新されます。",
        });

        // device + engine
        _currentVideoInfo ??= VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), _settings.DeviceKeyword);
        if (_currentVideoInfo == null)
        {
            sec.Items.Add(new TestItem
            {
                Name = "キャプチャデバイス", Verdict = Verdict.Ng, Summary = "見つかりません",
                Explanation = "キャプチャカードが接続されていないか、別のアプリが使っています。映像に関する試験は行いません。",
            });
            return;
        }

        var sw = Stopwatch.StartNew();
        Exception? fail = ReplaceEngine("d3d", null);
        long ms = sw.ElapsedMilliseconds;
        if (fail != null)
        {
            sec.Items.Add(new TestItem
            {
                Name = "キャプチャデバイスと Direct3D", Verdict = Verdict.Ng, Summary = "Direct3D で映像を開始できませんでした",
                Explanation = "理由: " + Errors.Describe(fail) + "\nGPU のドライバが古いか、別のアプリがキャプチャカードを使っている可能性があります。",
            }.Num("デバイス", _currentVideoInfo.Name));
            return;
        }
        LayoutCanvas();
        await Wait(SettleMs);
        var dev = new TestItem { Name = "キャプチャデバイスと Direct3D", Verdict = Verdict.Ok, Summary = "映像を開始できました" }
            .Num("デバイス", _currentVideoInfo.Name)
            .Num("開始にかかった時間", $"{ms} ms")
            .Num("初期モード", _video.CurrentMode is { } cm ? ModeLabel(cm) : "?")
            .Num("映像モードの数", _video.GetModes().Count.ToString())
            .Num("表示方式", string.IsNullOrEmpty(_video.PresentMode) ? "?" : _video.PresentMode);
        sec.Items.Add(dev);
    }

    // =============================================================================================
    //  2. capture
    // =============================================================================================

    private async Task CaptureStep(TestSection sec, CaptureMode m)
    {
        var item = new TestItem { Name = "取り込み " + ModeLabel(m) };
        // Same reason as in RenderStepCore: a caption over the video makes Windows compose the monitor, which on a
        // mixed-refresh setup holds the picture to the main monitor's rate and makes a 120 fps source drop frames.
        if (_testCaption != null) _testCaption.Visible = false;
        var sw = Stopwatch.StartNew();
        try { StartMode(m); }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng;
            item.Summary = "このモードで映像を開始できませんでした";
            item.Explanation = "理由: " + Errors.Describe(ex);
            sec.Items.Add(item);
            return;
        }
        long startMs = sw.ElapsedMilliseconds;
        // settle is done inside MeasureInputFps (first frame + 3 s); drops are read from the counters
        long drop0 = _video.FramesDropped;
        var (avg, perSec, elapsed) = await MeasureInputFps(CaptureMeasureMs, true);
        long drops = _video.FramesDropped - drop0;
        var st = new List<RenderStats>();
        if (avg > 0) st.Add(new RenderStats(avg, 0, drops));

        double nominal = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : m.Fps;
        double ratio = nominal > 0 ? avg / nominal : 0;
        item.Num("指定のコマ数", $"{nominal:0} fps")
            .Num("届いたコマ数（平均）", $"{avg:0.0} fps（{Pct(ratio)}、{elapsed:0.0} 秒間の実測）")
            .Num("1秒ごとの届いたコマ数", perSec.Count > 0 ? string.Join(" / ", perSec) : "?")
            .Num("捨てたコマ", $"{drops}（描画が追いつかず飛ばしたコマ）")
            .Num("開始にかかった時間", $"{startMs} ms")
            .Num("映像の大きさ", $"{_video.CurrentResolution.Width}x{_video.CurrentResolution.Height}");

        if (st.Count == 0 || avg < 1)
        {
            item.Verdict = Verdict.Ng;
            item.Summary = "映像が届いていません";
            item.Explanation = "このモードでコマが 1 つも届きませんでした。入力機器（ゲーム機など）がこの解像度・コマ数の映像を出していない可能性があります。入力側の出力設定に合う項目だけ、実際には使えます。";
        }
        else if (ratio < 0.90)
        {
            item.Verdict = Verdict.Ng;
            item.Summary = $"コマが足りません（{Pct(ratio)}）";
            item.Explanation = "指定より大幅に少ないコマしか届いていません。USB の転送量が足りていない可能性があります。USB 3.0 以上のポートに直接つなぐ、ハブを使わない、ケーブルを替える、他の USB 機器を外す、を試してください。入力側がこのコマ数で出していない場合も同じ数字になります。";
        }
        else if (ratio < 0.98)
        {
            item.Verdict = Verdict.Warn;
            item.Summary = $"ときどきコマが足りません（{Pct(ratio)}）";
            item.Explanation = "ほぼ届いていますが、わずかに足りません。USB ポートやケーブル、同じ USB に付いている他の機器の負荷を確認してください。";
        }
        else
        {
            item.Verdict = Verdict.Ok;
            item.Summary = $"指定どおり届いています（{avg:0.0} fps）";
            item.Explanation = "映像は指定のコマ数どおりに届いています。";
        }
        double panelHz = _topt.Target.RefreshHz;
        bool displaySlower = panelHz > 0 && panelHz < nominal * 0.98;
        if (displaySlower && drops > 0)
            item.Num("捨てたコマについて", $"モニターが {panelHz:0} Hz のため、{nominal:0} fps の映像は全部は表示できず、捨てるのは正常です");
        if (!displaySlower && drops > 0 && avg > 0 && drops > avg * (CaptureMeasureMs / 1000.0) * 0.01)
        {
            // This step is about frames ARRIVING; a few frames not shown is information only.
            item.Num("捨てたコマについて", "映像は指定どおり届いていますが、画面に出す側で一部を捨てました。モニターの更新の間隔と映像のコマの間隔が近いと（例: 60 fps の映像と 59.94 Hz のモニター）、少数は捨てられることがあります。表示の滑らかさは 3 の項目で調べています。");
        }
        sec.Items.Add(item);
    }

    // =============================================================================================
    //  3. smoothness / load
    // =============================================================================================

    private sealed record RenderCase(string Label, CaptureMode Mode, ScalingQuality Quality, bool LowLatency, bool Interp = false);
    private sealed record PacingQuality(bool Known, bool Working, string Case);

    private static List<RenderCase> BuildRenderCases(List<CaptureMode> modes, bool quick, double refreshHz)
    {
        var list = new List<RenderCase>();
        (string Name, int W, int H, bool Four)[] defs =
        {
            ("1080p120", 1920, 1080, false), ("1440p60", 2560, 1440, false), ("4K30", 3840, 2160, true),
        };
        foreach (bool ll in new[] { false, true })
        {
            foreach (var d in defs)
            {
                CaptureMode? mode = PickMode(modes, d.W, d.H, 0);
                if (mode == null) continue;
                string lab = $"{d.Name}（{mode.Fps}fps）";
                var qs = d.Four ? new[] { ScalingQuality.Standard } : new[] { ScalingQuality.Standard, ScalingQuality.Fsr };
                foreach (ScalingQuality q in qs)
                {
                    if (quick)
                    {
                        // one mode per resolution; low latency and FSR only on the 1080p one
                        bool first = d.W == 1920;
                        if (!first && (ll || q == ScalingQuality.Fsr)) continue;
                        if (first && ll && q == ScalingQuality.Fsr) continue;
                    }
                    list.Add(new RenderCase($"{lab} / {(q == ScalingQuality.Fsr ? "FSR" : "標準")} / 低遅延{(ll ? "オン" : "オフ")}", mode, q, ll));
                }
            }
        }

        // Frame interpolation: every source frame rate the screen refreshes clearly faster than (the
        // setting only works from refresh >= 1.1 x fps). Per rate the best mode (NV12 first, then the
        // largest picture), plus the 1080p one when that is a different mode.
        if (refreshHz > 0)
        {
            foreach (int fps in modes.Select(m => m.Fps).Distinct().Where(f => f > 0 && f * 1.1 <= refreshHz).OrderBy(f => f))
            {
                var atFps = modes.Where(m => m.Fps == fps).ToList();
                CaptureMode best = atFps.OrderByDescending(m => m.Compression == "NV12" ? 1 : 0)
                    .ThenByDescending(m => (long)m.Width * m.Height).First();
                var picks = new List<CaptureMode> { best };
                CaptureMode? hd = atFps.Where(m => m.Height == 1080 && !m.Equals(best))
                    .OrderByDescending(m => m.Compression == "NV12" ? 1 : 0).FirstOrDefault();
                if (hd != null) picks.Add(hd);
                foreach (CaptureMode m in picks)
                    list.Add(new RenderCase($"{ResolutionName(m)}{fps}（{fps}fps） / 標準 / フレーム補間", m, ScalingQuality.Standard, false, true));
            }
        }
        return list;
    }

    private static string ResolutionName(CaptureMode m) => m.Height switch
    {
        2160 => "4K", 1440 => "1440p", 1080 => "1080p", 720 => "720p", _ => $"{m.Height}p",
    };

    // Cases of section 3 that judder only because the source (e.g. 120 fps) does not divide the monitor's refresh
    // (e.g. 144 Hz), and the source rates for which a frame-interpolation case came out OK: VrrSummary joins them, so
    // the hint "interpolation makes it even" appears only where this very run proved it.
    private readonly List<(TestItem Item, double SrcFps)> _judderCases = new();
    private readonly HashSet<int> _interpOkFps = new();

    private async Task RenderStep(TestSection sec, RenderCase c, List<(bool Ll, PacingQuality Q)> vrr)
    {
        try { await RenderStepCore(sec, c, vrr); }
        finally
        {
            // interpolation belongs to its own cases only; the user's value comes back with the settings snapshot
            if (_settings.Interpolation) { _settings.Interpolation = false; try { _video.Interpolation = false; } catch { } }
        }
    }

    private async Task RenderStepCore(TestSection sec, RenderCase c, List<(bool Ll, PacingQuality Q)> vrr)
    {
        var item = new TestItem { Name = c.Label };
        // Anything drawn over the video makes Windows compose the monitor (DWM) instead of flipping the video
        // straight to the screen, and a composed monitor runs at the refresh rate of the primary display: a
        // 144 Hz TV beside a 60 Hz main monitor then shows 60 fps whatever YuCap does. The caption is therefore
        // out of the way while a rate is measured (the next SetCaption shows it again).
        if (_testCaption != null) _testCaption.Visible = false;
        try
        {
            _settings.ScalingQuality = c.Quality == ScalingQuality.Fsr ? "fsr" : "standard";
            _settings.Interpolation = c.Interp;
            EnsureEngine(c.LowLatency, c.Mode);
        }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng; item.Summary = "開始できませんでした"; item.Explanation = "理由: " + ex.Message;
            sec.Items.Add(item);
            return;
        }
        await Wait(SettleMs);

        _video.BeginMeasure();
        List<RenderStats> st;
        RenderMeasurement? meas;
        long drop0 = _video.FramesDropped;
        long pres0 = _video.FramesPresented;
        long recv0 = _video.FramesReceived;
        var recvSw = Stopwatch.StartNew();
        try { st = await CollectStats(RenderMeasureMs); }
        finally { meas = _video.EndMeasure(); }   // also when Esc aborts the wait
        InterpolationStatus? interpSt = c.Interp ? _video.InterpolationState : null;   // still running: asked after the window
        long recvN = _video.FramesReceived - recv0, presN = _video.FramesPresented - pres0;
        long drops = _video.FramesDropped - drop0;
        double recvEl = recvSw.Elapsed.TotalSeconds;
        double recvFps = recvEl > 0 ? recvN / recvEl : 0;

        double srcFps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : c.Mode.Fps;
        double panel = _topt.Target.RefreshHz;
        double inFps = recvFps;
        double shown = recvEl > 0 ? presN / recvEl : 0;
        // With interpolation every refresh gets a new picture, so a frame's time budget is one refresh.
        bool interpOn = c.Interp && interpSt is { Active: true } && panel > 0;
        double budget = 1000.0 / Math.Max(1, interpOn ? panel : srcFps);

        item.Num("映像", $"{ModeLabel(c.Mode)}  → {_topt.Target.Bounds.Width}x{_topt.Target.Bounds.Height} に拡大")
            .Num("拡大の画質", c.Quality == ScalingQuality.Fsr ? "FSR（高画質）" : "標準")
            .Num("表示方式", meas != null && meas.PresentMode.Length > 0 ? meas.PresentMode : "?");

        Verdict v = Verdict.Ok;
        var why = new List<string>();

        if (c.Interp)
        {
            item.Num("フレーム補間", interpOn
                ? $"働いています（映像 {interpSt!.SourceFps:0.0} fps → モニターの更新 {interpSt.RefreshHz:0} Hz に合わせて間のコマを作成）"
                : $"働いていません（{InterpIdleText(interpSt)}）");
            why.Add("フレーム補間は、映像のコマ数よりモニターの更新が十分速いときに、前後のコマの動きから間のコマを作って、更新のたびに新しい絵を出す機能です。補間のために映像が約 1 コマぶん遅れます（遅れは上の「映像の遅れ（YuCap の中）」に出ています）。");
            if (!interpOn) { v = Worst(v, Verdict.Ng); why.Add("フレーム補間が働いていないため、この項目は補間なしの表示になっています。"); }
        }

        // ---- shown frame rate ----
        double expect = panel > 0 ? (interpOn ? panel : Math.Min(srcFps, panel)) : srcFps;
        double shownRatio = expect > 0 ? shown / expect : 0;
        item.Num("映像のコマ数", $"{inFps:0.0} fps（捨てたコマ {drops}）")
            .Num("表示されたコマ数", interpOn
                ? $"{shown:0.0} fps（期待 {expect:0} fps＝モニターの更新と同じ数 → {Pct(shownRatio)}）"
                : $"{shown:0.0} fps（期待 {expect:0} fps＝映像 {srcFps:0} fps とモニター {panel:0} Hz の小さい方 → {Pct(shownRatio)}）");
        if (shownRatio > 1.1) why.Add("モニターのリフレッシュより多くのコマを出しています（同期なしの表示のため、画面の途中で絵が切り替わるティアリングが出ることがあります。可変リフレッシュ対応のモニターでは出ません）。");
        bool srcShort = c.Interp && srcFps > 0 && recvFps < srcFps * 0.9;   // the card itself delivers fewer frames than named
        if (shownRatio < 0.90 && srcShort)
        {
            v = Worst(v, Verdict.Warn);
            why.Add($"キャプチャカードが指定の {srcFps:0} fps で届けられず（実際 {recvFps:0.0} fps）、映像が来ない間は補間する元がないため、表示されたコマ数が期待に届いていません。補間の不具合ではなく、このモードの取り込みの限界です。");
        }
        else if (shownRatio < 0.90) { v = Worst(v, Verdict.Ng); why.Add("表示されたコマ数が期待の 9 割に届いていません。"); }
        else if (shownRatio < 0.98) { v = Worst(v, Verdict.Warn); why.Add("表示されたコマ数がわずかに足りません。"); }

        // ---- GPU ----
        double gpuShare = -1;   // 95th percentile GPU time as a share of the frame budget; -1 = unknown
        bool gpuOverrun = false; double gpuP95 = 0, gpuMax = 0;
        bool judderSeen = false;       // some frames stay on screen longer than the rest (the source rate does not divide the refresh)
        bool displayPerfect = false;   // every frame reached the screen on time (what decides if a GPU overrun matters)
        double[] gpu = meas?.GpuMs ?? Array.Empty<double>();
        if (gpu.Length >= 10)
        {
            double[] sorted = gpu.OrderBy(x => x).ToArray();
            double g = gpu.Average(), p95 = sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)], mx = sorted[^1];
            gpuShare = p95 / budget;
            item.Num("GPU 処理時間", $"平均 {g:0.00} ms / 95% {p95:0.00} ms / 最大 {mx:0.00} ms（1コマの持ち時間 {budget:0.00} ms、{gpu.Length} コマ）");
            if (p95 > budget) { gpuOverrun = true; gpuP95 = p95; gpuMax = mx; }   // judged after the pacing below: the screen decides how bad it is
            else if (p95 > budget * 0.8) { v = Worst(v, Verdict.Warn); why.Add($"GPU の処理が持ち時間の 8 割を超えています（余裕がありません）。他のアプリが GPU を使うと詰まる可能性があります。"); }
            else why.Add($"GPU は持ち時間の {Pct(p95 / budget)} しか使っておらず、余裕があります。");
        }
        else
        {
            double g = st.Count > 0 ? st.Average(s => s.GpuMsAvg) : 0;
            item.Num("GPU 処理時間", g > 0 ? $"平均 {g:0.00} ms（細かい数字は取れませんでした）" : "計測できませんでした");
        }

        // ---- latency inside YuCap (frame received -> shown) ----
        {
            var ls = TestProbes.LatencyStats(meas?.LatencyMs);
            string approx = meas is { LatencyEstimated: true } ? "約 " : "";
            item.Num("映像の遅れ（YuCap の中）", ls != null
                ? $"中央 {approx}{ls.Value.Median:0} ms ／ 95% {approx}{ls.Value.P95:0} ms"
                : "測定できません（" + (meas == null ? "記録を取れませんでした" : "画面に出た時刻を取得できなかったため") + "）");
        }

        // ---- pacing ----
        if (meas != null)
        {
            TestProbes.PacingAnalysis p = TestProbes.AnalyzePacing(meas.Pacing, interpOn ? panel : srcFps, panel);
            item.Num("コマの出方の記録", p.Source)
                .Num("記録の状況", p.StatsNote);
            if (p.Frames > 0)
            {
                if (p.ObservedHz > 0) item.Num("実測のリフレッシュレート", $"{p.ObservedHz:0.0} Hz");
                item.Num("1コマが画面に出ていた時間", $"平均 {p.MeanMs:0.00} ms（ばらつき {p.StdMs:0.00} ms、5〜95% は {p.P5Ms:0.0}〜{p.P95Ms:0.0} ms）  [{p.Frames} コマ]")
                    .Num("カクつき率", $"{Pct(p.JudderShare)}（1コマの本来の長さ {budget:0.0} ms の 1.25 倍より長く出ていたコマ）")
                    .Num("コマ飛び", panel > 0 && panel < srcFps * 0.98
                        ? $"{drops} コマ（モニターが {panel:0} Hz のため、映像の一部は表示されず捨てられます。これは正常です）"
                        : $"{drops} コマ（描画が追いつかず捨てたもの）");
                if (p.UsedSpread) why.Add("表示側の記録に飛びがあったため、一部のコマは平均の時間で補っています。ばらつきの数字は参考程度です。");
                if (p.UsedStats && p.SameRefreshShare > 0.02)
                    item.Num("同じ更新に複数のコマ", Pct(p.SameRefreshShare) + "（画面の途中で絵が切り替わる＝ティアリングの可能性）");
                item.Num("可変リフレッシュ（VRR）", p.Vrr);
                foreach (var b in p.Histogram) item.Bars.Add(b);
                if (p.JudderShare > 0.30) { v = Worst(v, Verdict.Ng); why.Add($"コマの出方が不揃いで、{Pct(p.JudderShare)} のコマが長く出ています（カクつきとして見えます）。"); }
                else if (p.JudderShare > 0.05) { v = Worst(v, Verdict.Warn); why.Add($"{Pct(p.JudderShare)} のコマが本来より長く出ています（軽いカクつき）。"); }
                else why.Add("コマはほぼ一定の間隔で出ています。");
                judderSeen = p.JudderShare > 0.05;
                displayPerfect = shownRatio >= 0.97 && drops == 0 && p.JudderShare <= 0.02;
                vrr.Add((c.LowLatency, new PacingQuality(p.VrrKnown, p.VrrWorking, c.Label)));
            }
            else
            {
                item.Num("可変リフレッシュ（VRR）", p.Vrr);
            }
            if (!p.UsedStats && p.UsedVBlank && p.Frames > 0)
                why.Add("表示側の記録が取れなかったため、垂直同期（画面の更新）ごとの記録で代用しました。1コマが何回の更新ぶん出ていたかは分かりますが、細かい時刻は分かりません。");
            else if (!p.UsedStats && p.Frames > 0)
                why.Add("この表示方式では画面に出た時刻が取れなかったため、描画側の間隔で代用しました。実際に画面でカクつくかは、目で確かめてください。");
        }
        else why.Add("コマの出方の記録を取れませんでした。");

        if (gpuOverrun)
        {
            if (displayPerfect)
            {
                v = Worst(v, Verdict.Warn);
                why.Add($"GPU の処理時間が、ときどき 1 コマの持ち時間（{budget:0.0}ms）を超えました（95% で {gpuP95:0.0} ms、最大 {gpuMax:0.0} ms）。ただし、この試験では映像は期待どおりの数で、カクつきもなく、コマ飛びもなく表示されました。GPU の余裕は少ないものの、いまの使い方では問題として表れていません。" +
                    "他のアプリが GPU を使うと、カクつきが出る可能性があります。" +
                    (c.Interp ? "フレーム補間は動きの検出を映像の 1 コマごとに 1 回行うため、その瞬間だけ処理時間が跳ね上がります。" : ""));
            }
            else
            {
                v = Worst(v, Verdict.Ng);
                why.Add($"GPU の処理が 1 コマの持ち時間（{budget:0.0}ms）を超えています（95% で {gpuP95:0.0} ms）。この設定は GPU の力が足りません。");
            }
        }

        if (drops > 0 || shownRatio < 0.98)
        {
            if (gpuShare >= 0 && gpuShare < 0.5)
                why.Add("コマが一部表示されていませんが、GPU には十分な余裕があります。原因は負荷ではなく、モニターの更新のタイミングと映像のコマのタイミングが近いこと（例: 60 fps の映像と 59.94 Hz のモニター）です。この場合、少数のコマが飛ぶのは想定内です。");
            else if (gpuShare >= 0.8)
                why.Add("GPU の処理時間に余裕がなく、描画が間に合わずコマが飛んでいる可能性があります。");
        }

        item.Verdict = v;
        item.Summary = v switch
        {
            Verdict.Ng => "問題があります",
            Verdict.Warn => "少し注意が必要です",
            _ => "問題ありません",
        };
        item.Explanation = string.Join("\n", why);
        sec.Items.Add(item);
        if (c.Interp)
        {
            if (interpOn && v == Verdict.Ok) _interpOkFps.Add((int)Math.Round(srcFps));
        }
        else if (judderSeen && panel > srcFps * 1.1)
            _judderCases.Add((item, srcFps));
    }

    private static string InterpIdleText(InterpolationStatus? st) => st == null ? "状態を取れませんでした" : st.Idle switch
    {
        InterpolationIdle.RefreshTooLow => $"モニターの更新 {st.RefreshHz:0} Hz が映像 {st.SourceFps:0.0} fps より十分高くありません",
        InterpolationIdle.LowLatency => "低遅延モードでは使えません",
        InterpolationIdle.NoSignal => "映像のコマ数が分かりませんでした",
        InterpolationIdle.Unavailable => "補間用のシェーダーを使えませんでした",
        InterpolationIdle.Disabled => "設定がオフでした",
        _ => "理由不明",
    };

    private void VrrSummary(TestSection sec, List<(bool Ll, PacingQuality Q)> vrr)
    {
        var it = new TestItem { Name = "可変リフレッシュ（VRR）のまとめ", Verdict = Verdict.Info };
        foreach (bool ll in new[] { false, true })
        {
            var rows = vrr.Where(x => x.Ll == ll).ToList();
            if (rows.Count == 0) { it.Num($"低遅延{(ll ? "オン" : "オフ")}", "計測なし"); continue; }
            int known = rows.Count(r => r.Q.Known), works = rows.Count(r => r.Q.Working);
            it.Num($"低遅延{(ll ? "オン" : "オフ")}",
                known == 0 ? "判定できませんでした" : $"{known} 件中 {works} 件で VRR が効いていました");
        }
        bool onWorks = vrr.Any(x => x.Ll && x.Q.Working), offWorks = vrr.Any(x => !x.Ll && x.Q.Working);
        string steps =
            "VRR を使うには、次を順に確かめてください。\n" +
            "1. テレビ: ゲームモードにして、VRR（機種により「VRR」「可変リフレッシュレート」）をオンにします。Hisense のテレビは、設定のゲームモード／VRR の項目です。\n" +
            "2. GPU のドライバ: NVIDIA は、NVIDIA コントロールパネル → G-SYNC の設定 → 「G-SYNC 互換を有効にする」にチェックを入れ、「ウィンドウモードと全画面モードで有効にする」を選びます。AMD は、Radeon 設定 → ディスプレイ → AMD FreeSync をオンにします。\n" +
            "3. Windows: 設定 → システム → ディスプレイ → グラフィックス → 既定のグラフィックス設定 で、「可変リフレッシュレート」と「ウィンドウ化されたゲームの最適化」をオンにします。\n" +
            "4. YuCap: VRR を使うのは低遅延モードのときだけです。オプション → 低遅延モードをオンにしてください（オフのときは Windows が画面を合成するため、VRR は効きません）。\n" +
            "設定を変えたあと、もう一度試験すると、効いているかを確かめられます。";
        it.Explanation = onWorks && !offWorks
            ? "低遅延モードをオンにしたときだけ VRR が効きました。VRR を使いたいときは、オプション → 低遅延モードをオンにしてください。"
            : onWorks
                ? "VRR が効く設定があります。どの設定かは上の各項目を見てください。"
                : "VRR が効いている設定は確認できませんでした。120 fps のように、モニターの更新（例: 144 Hz）の整数倍にならない映像は、固定のリフレッシュレートでは 1 コマが 1 回か 2 回の更新で出るため、約 2 割のコマが長く見えます（軽いカクつき）。これは VRR が効いていないときの普通の動きで、故障ではありません。\n" + steps +
                  "\n表示方式によっては、画面に出た時刻が取れず判定できないこともあります。";

        // joins the judder cases with the interpolation cases of this run
        foreach (var (item, fps) in _judderCases)
        {
            if (!_interpOkFps.Contains((int)Math.Round(fps))) continue;
            item.Explanation += $"\nフレーム補間をオンにすると、同じ {fps:0} fps の映像でも、モニターの更新（{_topt.Target.RefreshHz:0} Hz）のたびに新しい絵が出るようになり、VRR がなくてもコマの出方が揃います（この試験の「{fps:0}fps / 標準 / フレーム補間」の項目で、問題ないことを確認しました）。ただしフレーム補間は低遅延モードでは使えず、映像が約 1 コマぶん遅れます。";
        }
        _judderCases.Clear();
        sec.Items.Add(it);
    }

    // =============================================================================================
    //  4. notices on top of the video
    // =============================================================================================

    private async Task NoticeStep(TestSection sec, bool ll, CaptureMode? mode, int what)
    {
        string tag = ll ? "低遅延オン" : "低遅延オフ";
        string[] names = { "通知（画面左上の表示）", "一時停止の画像", "情報表示" };
        var item = new TestItem { Name = $"{names[what]} / {tag}" };
        try { EnsureEngine(ll, mode); }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng; item.Summary = "映像を開始できませんでした"; item.Explanation = ex.Message;
            sec.Items.Add(item);
            return;
        }
        await Wait(SettleMs);
        item.Num("表示方式", string.IsNullOrEmpty(_video.PresentMode) ? "?" : _video.PresentMode);

        Color marker;
        Rectangle area;
        Action cleanup;
        switch (what)
        {
            case 0:
                marker = Color.FromArgb(255, 0, 255);
                _osd.TestBack = marker;
                ShowOsd(L.T("テスト表示"), OsdLongMilliseconds);
                await Wait(300);
                area = _osd.RectangleToScreen(new Rectangle(3, Math.Max(0, _osd.Height / 2 - 4), 8, 8));
                cleanup = () =>
                {
                    _osd.TestBack = null; _osdQueue.Clear(); _osdCurrent = null; _osdTimer.Stop(); _osd.Visible = false;
                };
                break;
            case 1:
                marker = Color.FromArgb(0, 255, 255);
                ToggleFreeze();   // the real pause path (keeps its own image, audio mute and OSD)
                if (!_frozen)
                {
                    item.Verdict = Verdict.Info; item.Summary = "一時停止にできなかったため確認できませんでした";
                    sec.Items.Add(item);
                    return;
                }
                var probe = new Bitmap(64, 64);
                using (Graphics g = Graphics.FromImage(probe)) g.Clear(marker);
                _freezeView.Image = probe;
                _freezeView.ImageRect = new Rectangle(Point.Empty, _freezeView.ClientSize);
                _freezeView.Update();
                await Wait(300);
                Point c = new(_freezeView.ClientSize.Width / 2 - 6, _freezeView.ClientSize.Height / 2 - 6);
                area = _freezeView.RectangleToScreen(new Rectangle(c, new Size(12, 12)));
                cleanup = () =>
                {
                    _freezeView.Image = _freezeImage;
                    _freezeView.ImageRect = FreezeImageRect();
                    probe.Dispose();
                    ToggleFreeze();
                };
                break;
            default:
                marker = Color.FromArgb(0, 255, 0);
                bool wasVisible = _info.Visible;
                _info.TestBack = marker;
                if (!wasVisible) ToggleInfo(); else _info.Invalidate();
                _info.BringToFront(); _osd.BringToFront(); _testCaption?.BringToFront();
                await Wait(300);
                area = _info.RectangleToScreen(new Rectangle(2, Math.Max(0, _info.Height / 2 - 4), 6, 8));
                cleanup = () => { _info.TestBack = null; if (!wasVisible && _info.Visible) ToggleInfo(); else _info.Invalidate(); };
                break;
        }

        try
        {
            if (!TestProbes.OwnedByWindow(Handle, area))
            {
                item.Verdict = Verdict.Info;
                item.Summary = "確認できませんでした";
                item.Explanation = "調べる場所に YuCap 以外のウィンドウが重なっていたため、画面は読み取っていません（他のアプリの画面は撮りません）。";
            }
            else
            {
                (double match, Color mean) = TestProbes.SampleMatch(area, marker);
                item.Num("調べた場所", $"画面の {area.X},{area.Y}（{area.Width}x{area.Height} ピクセル）")
                    .Num("期待した色", $"R{marker.R} G{marker.G} B{marker.B}")
                    .Num("実際の色（平均）", $"R{mean.R} G{mean.G} B{mean.B}")
                    .Num("一致した割合", Pct(match));
                if (match >= 0.9)
                {
                    item.Verdict = Verdict.Ok;
                    item.Summary = "映像の上に見えています";
                    item.Explanation = "この表示方式でも、重ねて出す表示は映像の上に出ています。";
                }
                else
                {
                    item.Verdict = Verdict.Ng;
                    item.Summary = "映像に隠れています";
                    item.Explanation = ll
                        ? "低遅延モード（ウィンドウ直結の表示）では、通知などが映像の下に隠れています。低遅延モードをオンにしている間は通知が見えません。"
                        : "通知などが映像の下に隠れています。";
                }
            }
        }
        finally { cleanup(); }
        sec.Items.Add(item);
    }

    // =============================================================================================
    //  5. FSR
    // =============================================================================================

    private async Task FsrStep(TestSection sec, string label, CaptureMode? mode)
    {
        var item = new TestItem { Name = $"標準と FSR の比較（{label} → このモニターの大きさ）" };
        if (mode == null)
        {
            item.Summary = "この解像度のモードがありません";
            sec.Items.Add(item);
            return;
        }
        EnsureEngine(false, mode);
        await Wait(SettleMs);
        Size outSize = _topt.Target.Bounds.Size;
        // Debug aid: YUCAP_TEST_OUTPUT=3840x2160 renders the comparison at that size even on a smaller monitor,
        // so this step can be verified without a 4K display. Noted in the report when used.
        string? forced = Environment.GetEnvironmentVariable("YUCAP_TEST_OUTPUT");
        if (forced != null)
        {
            string[] wh = forced.ToLowerInvariant().Split('x');
            if (wh.Length == 2 && int.TryParse(wh[0], out int fw) && int.TryParse(wh[1], out int fh) && fw > 0 && fh > 0)
            {
                outSize = new Size(fw, fh);
                item.Num("注意", $"環境変数 YUCAP_TEST_OUTPUT により、拡大先を {fw}x{fh} にして比べています");
            }
        }
        Size src = _video.DisplayResolution;
        item.Num("映像", ModeLabel(mode)).Num("拡大先", $"{outSize.Width}x{outSize.Height}");
        if (outSize.Width <= src.Width || outSize.Height <= src.Height)
        {
            item.Summary = "拡大にならないため、比べる意味がありません";
            item.Explanation = "このモニターは映像と同じか小さいため、FSR は使われず、標準と同じ表示になります。4K のモニターで試験すると比べられます。";
            sec.Items.Add(item);
            return;
        }

        string safe = label.Replace(" ", "");
        Bitmap? std = await Task.Run(() => _video.RenderOffscreen(outSize, ScalingQuality.Standard));
        Bitmap? fsr = await Task.Run(() => _video.RenderOffscreen(outSize, ScalingQuality.Fsr));
        try
        {
            if (std == null || fsr == null)
            {
                item.Verdict = Verdict.Warn; item.Summary = "拡大した絵を作れませんでした";
                sec.Items.Add(item);
                return;
            }
            var (es, ef) = await Task.Run(() => (TestProbes.MeasureEdges(std), TestProbes.MeasureEdges(fsr)));
            item.Num("輪郭の強さ（標準）", $"{es.MeanGradient:0.000}")
                .Num("輪郭の強さ（FSR）", $"{ef.MeanGradient:0.000}")
                .Num("FSR ÷ 標準", es.MeanGradient > 0 ? $"{ef.MeanGradient / es.MeanGradient:0.00} 倍" : "—");
            Rectangle? tile = TestProbes.BestTile(ef, fsr.Size);
            if (tile is { } t)
            {
                string file = $"fsr_{safe}.png";
                TestProbes.SaveSideBySide(std, fsr, t, "標準", "FSR", Path.Combine(_rep.Folder, file));
                item.Images.Add(file);
            }
            item.Verdict = Verdict.Info;
            item.Summary = "数字は参考です。画像を見比べて、好みで決めてください。";
            item.Explanation = "「輪郭の強さ」は絵の細かい線のくっきり具合を数にしたもので、大きいほどくっきりです。ただし強すぎると不自然に見えることもあるため、良し悪しは数字では決まりません。左が標準、右が FSR です（画像は絵の中で線が一番多い 480x270 の部分を切り出しています）。再生中に Q を押している間は標準の拡大で見られます。";
        }
        finally { std?.Dispose(); fsr?.Dispose(); }
        sec.Items.Add(item);
    }

    // =============================================================================================
    //  6. HDR
    // =============================================================================================

    private async Task HdrStep(TestSection sec, List<CaptureMode> modes)
    {
        var item = new TestItem { Name = "HDR（P010 モード・Windows の HDR）", Verdict = Verdict.Info };
        Program.OutputHdr? h = null;
        try { h = Program.QueryOutputsHdr().FirstOrDefault(x => string.Equals(x.DeviceName, _topt.Target.DeviceName, StringComparison.OrdinalIgnoreCase)); }
        catch { }
        bool tvHdr = h is { HasInfo: true, HdrOn: true };
        item.Num("試験するモニターの Windows HDR", h == null || !h.HasInfo ? "情報なし" : tvHdr ? $"オン（最大輝度 {h.MaxLuminance:0} nits）" : $"オフ（最大輝度 {h.MaxLuminance:0} nits）");

        CaptureMode? p010 = modes.Where(m => m.Compression == "P010")
            .OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Fps).FirstOrDefault();
        if (p010 == null)
        {
            item.Num("P010 モード", "ありません");
            item.Summary = "このカードには 10bit（P010）のモードがありません";
            item.Explanation = "HDR の信号を受けるには 10bit のモードが必要です。カードの設定や接続（HDMI の入力側の出力設定）を確認してください。";
            sec.Items.Add(item);
            return;
        }
        EnsureEngine(false, p010);
        await Wait(SettleMs + 500);
        item.Num("調べたモード", ModeLabel(p010));
        string info = _video.SourceColorInfo ?? "(取得できませんでした)";
        foreach (string line in info.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = line.IndexOf(':');
            if (i > 0) item.Num(line[..i].Trim(), line[(i + 1)..].Trim()); else item.Num("色の情報", line.Trim());
        }
        string? selected = info.Split('\n').FirstOrDefault(l => l.StartsWith("native (selected)"));
        bool signalHdr = selected != null && (selected.Contains("2084") || selected.Contains("HLG"));
        Bitmap? bmp = await Task.Run(() => _video.PhotoSnapshot());
        if (bmp != null)
        {
            using (bmp)
                foreach (string l in Program.DescribeLuminance(bmp).Take(2)) item.Num("明るさの分布", l);
        }
        item.Summary = signalHdr ? "カードは HDR（PQ/HLG）の信号を示しています" : "カードの信号は SDR（通常の明るさ）です";
        item.Explanation = signalHdr
            ? "カードが HDR の信号と申告しています。ただし今の YuCap は HDR を HDR のままでは表示できず、通常の明るさ（SDR）に当てはめて表示するため、色がうすく見えます。"
            : "カードから来ている信号は SDR です。そのため YuCap で HDR を表示することは今のところできません。HDR を試すには、入力側（ゲーム機など）を HDR 出力にして、もう一度この試験をしてください。";
        item.Explanation += "\n" + (tvHdr
            ? "Windows 側では、このモニターの HDR がオンになっています。YuCap の画面は SDR のため、HDR がオンのモニターでは明るさや色の出方が変わって見えることがあります。"
            : "Windows 側では、このモニターの HDR がオフです。");
        sec.Items.Add(item);
    }

    // =============================================================================================
    //  7. features
    // =============================================================================================

    private async Task FeatureCrop(TestSection sec, CaptureMode? mode)
    {
        var item = new TestItem { Name = "黒い縁の切り取り" };
        EnsureEngine(false, mode);
        await Wait(SettleMs);
        Size src = _video.CurrentResolution;
        _settings.CropLeft = _settings.CropTop = _settings.CropRight = _settings.CropBottom = 100;
        ApplyRenderOptions();
        await Wait(500);
        Size disp = _video.DisplayResolution;
        Size expect = new(src.Width - 200, src.Height - 200);
        Bitmap? photo = await Task.Run(() => _video.PhotoSnapshot());
        bool photoOk = photo != null && photo.Size == disp;
        photo?.Dispose();
        _settings.CropLeft = _settings.CropTop = _settings.CropRight = _settings.CropBottom = 0;
        ApplyRenderOptions();
        await Wait(500);
        Size cleared = _video.DisplayResolution;
        bool ok = disp == expect && photoOk && cleared == src;
        item.Num("映像の大きさ", $"{src.Width}x{src.Height}")
            .Num("四辺を 100px 切ったときの表示", $"{disp.Width}x{disp.Height}（期待 {expect.Width}x{expect.Height}）")
            .Num("保存される静止画の大きさ", photoOk ? "表示と同じ" : "表示と食い違い")
            .Num("解除後の表示", $"{cleared.Width}x{cleared.Height}");
        item.Verdict = ok ? Verdict.Ok : Verdict.Ng;
        item.Summary = ok ? "切り取りは正しく働きます" : "切り取りの結果が期待と違います";
        item.Explanation = ok ? "切った分だけ表示が小さくなり、静止画も同じ大きさで、解除すると元に戻りました。" : "切り取りの大きさか解除の動きが期待どおりではありませんでした。";
        sec.Items.Add(item);
    }

    private async Task FeatureAdjust(TestSection sec)
    {
        var item = new TestItem { Name = "映像の調整（明るさ）" };
        Bitmap? a = await Task.Run(() => _video.PhotoSnapshot());
        double lumaA = Program.MeanLuma(a);
        _settings.AdjBrightness = 30;
        ApplyRenderOptions();
        await Wait(400);
        double lumaB = Program.MeanLuma(await Task.Run(() => _video.PhotoSnapshot()));
        _settings.AdjBrightness = 0;
        ApplyRenderOptions();
        await Wait(400);
        double lumaC = Program.MeanLuma(await Task.Run(() => _video.PhotoSnapshot()));
        item.Num("明るさの平均（調整前）", $"{lumaA:0.0}")
            .Num("明るさ +30 のとき", $"{lumaB:0.0}")
            .Num("戻したあと", $"{lumaC:0.0}");
        if (double.IsNaN(lumaA) || double.IsNaN(lumaB) || double.IsNaN(lumaC))
        {
            item.Verdict = Verdict.Warn; item.Summary = "静止画を取れず確認できませんでした";
        }
        else if (lumaA > 235)
        {
            item.Verdict = Verdict.Info; item.Summary = "映像がほぼ真っ白で、明るさの変化を確認できませんでした";
        }
        else if (lumaB > lumaA + 10 && lumaC < lumaB - 10)
        {
            item.Verdict = Verdict.Ok; item.Summary = "明るさの調整は正しく働きます";
            item.Explanation = "明るさを上げると絵が明るくなり、戻すと元に近づきました（映像が動いているため、数字は多少前後します）。";
        }
        else
        {
            item.Verdict = Verdict.Ng; item.Summary = "調整が絵に反映されていません";
            item.Explanation = "明るさを変えても絵の明るさがほとんど変わりませんでした。";
        }
        sec.Items.Add(item);
    }

    private async Task FeatureDelay(TestSection sec)
    {
        var item = new TestItem { Name = "映像の遅延（音ズレ補正で音を早めるときに使う）" };
        _settings.AvOffsetMs = -200;
        ApplyRenderOptions();
        await Wait(1500);
        double actual = _video.VideoDelayActualMs;
        _settings.AvOffsetMs = 0;
        ApplyRenderOptions();
        await Wait(500);
        double after = _video.VideoDelayActualMs;
        double pad = _video.VideoPacingMarginMs;   // the renderer's own jitter margin is part of the measured age
        bool ok = actual >= 180 && actual <= 230 + pad;
        item.Num("指定した遅れ", "200 ms").Num("実測", $"{actual:0} ms").Num("0 に戻したあと", $"{after:0} ms");
        item.Verdict = ok && after < 1 ? Verdict.Ok : Verdict.Warn;
        item.Summary = ok ? "指定どおり遅らせられます" : "実測が指定とずれています";
        item.Explanation = ok
            ? "映像を 200ms 遅らせると、実測も 200ms 前後でした。"
            : "180〜230ms の範囲から外れています。映像のコマ数が少ない、または GPU が忙しい可能性があります。";
        sec.Items.Add(item);
    }

    private async Task FeatureSnapshot(TestSection sec)
    {
        var item = new TestItem { Name = "表示どおりの大きさで保存" };
        await Wait(300);
        Rectangle dest = Rectangle.Intersect(ComputeDest(_canvas.ClientSize), new Rectangle(Point.Empty, _canvas.ClientSize));
        Bitmap? shown = await Task.Run(() => _video.Snapshot(cropToVideo: true));
        if (shown == null)
        {
            item.Verdict = Verdict.Warn; item.Summary = "静止画を取れませんでした";
            item.Explanation = _video.LastSnapshotError ?? string.Empty;
        }
        else
        {
            using (shown)
            {
                bool ok = shown.Size == dest.Size;
                item.Num("保存される大きさ", $"{shown.Width}x{shown.Height}").Num("画面で映像が占める大きさ", $"{dest.Width}x{dest.Height}");
                item.Verdict = ok ? Verdict.Ok : Verdict.Ng;
                item.Summary = ok ? "表示どおりの大きさで保存できます" : "保存される大きさが画面と合いません";
                item.Explanation = ok ? "全画面で見えている映像と同じ大きさの静止画になります。" : "表示と保存の大きさが食い違っています。";
            }
        }
        sec.Items.Add(item);
    }

    private async Task FeatureOrientation(TestSection sec)
    {
        var item = new TestItem { Name = "回転と反転" };
        Size src = _video.CurrentResolution;
        var lines = new List<string>();
        Verdict v = Verdict.Ok;
        try
        {
            _video.SetRotation(90);
            LayoutCanvas();
            await Wait(700);
            Size d90 = _video.DisplayResolution;
            bool sizeOk = d90 == new Size(src.Height, src.Width);
            item.Num("90° 回転したときの表示の大きさ", $"{d90.Width}x{d90.Height}（{(sizeOk ? "縦横が入れ替わっています" : "縦横が入れ替わっていません")}）");
            if (!sizeOk) v = Verdict.Ng;
            string r1 = await OrientationCheck(flip: false);
            item.Num("90° 回転の向き（静止画と画面の比較）", r1);
            if (r1.StartsWith("逆")) v = Worst(v, Verdict.Ng);

            _video.SetRotation(0);
            _video.SetMirror(true);
            LayoutCanvas();
            await Wait(700);
            string r2 = await OrientationCheck(flip: true);
            item.Num("左右反転の向き（静止画と画面の比較）", r2);
            if (r2.StartsWith("逆")) v = Worst(v, Verdict.Ng);
        }
        finally
        {
            _video.SetRotation(0);
            _video.SetMirror(false);
            LayoutCanvas();
        }
        item.Verdict = v;
        item.Summary = v == Verdict.Ok ? "回転と反転は正しく働きます" : "回転か反転の結果が期待と違います";
        item.Explanation = v == Verdict.Ok
            ? "画面に出ている絵と、保存される静止画の向きが一致しました（映像が動いているため、判定は大まかな比較です）。"
            : "画面に出ている絵と、保存される静止画の向きが食い違いました。";
        sec.Items.Add(item);
    }

    /// <summary>Compare the saved-still orientation with what the screen shows, using 8x8 thumbnails: the
    /// still must match the display better than its 180° turn (rotation) or mirror image (flip).</summary>
    private async Task<string> OrientationCheck(bool flip)
    {
        Bitmap? photo = await Task.Run(() => _video.PhotoSnapshot());
        Bitmap? shot = await Task.Run(() => _video.Snapshot());
        try
        {
            if (photo == null || shot == null) return "比べられませんでした（静止画なし）";
            double[,] t1 = Program.Thumbnail8x8(shot), t2 = Program.Thumbnail8x8(photo);
            using Bitmap alt = flip ? Program.FlippedCopy(photo) : Program.Rotated180Copy(photo);
            double mad = Program.MeanAbsDiff(t1, t2), madAlt = Program.MeanAbsDiff(t1, Program.Thumbnail8x8(alt));
            return mad < madAlt ? "一致" : "逆の向きの疑いがあります";
        }
        finally { photo?.Dispose(); shot?.Dispose(); }
    }

    // =============================================================================================
    //  8. audio
    // =============================================================================================

    private async Task AudioStep(TestSection sec)
    {
        var item = new TestItem { Name = "音声" };
        // measured with the user's own A/V offset, not the neutral one used for the picture tests
        _settings.AvOffsetMs = _testSaved?.AvOffsetMs ?? 0;
        ApplyAvSync();
        if (!_audio.IsRunning)
        {
            item.Verdict = Verdict.Warn; item.Summary = "音声が動いていません";
            item.Explanation = "音声デバイスが接続されていないか、再生に失敗しています。キャプチャカードの音声入力と、Windows の既定の出力デバイスを確認してください。";
            sec.Items.Add(item);
            return;
        }
        await Wait(500);
        var samples = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            samples.Add(_audio.BufferedMs);
            await Wait(100);
        }
        double avg = samples.Average();
        item.Num("音声デバイス", _audio.CurrentDeviceName ?? "?")
            .Num("バッファの目標", $"{_audio.BufferMilliseconds} ms")
            .Num("たまっていた音声（3 秒間の平均）", $"{avg:0} ms（{samples.Min():0}〜{samples.Max():0} ms）")
            .Num("音ズレ補正の設定", _settings.AvOffsetMs == 0 ? "なし" : _settings.AvOffsetMs > 0 ? $"音を {_settings.AvOffsetMs} ms 遅らせる" : $"音を {-_settings.AvOffsetMs} ms 早める（映像を遅らせる）");
        item.Verdict = Verdict.Info;
        item.Summary = $"音声の遅れの目安は {avg:0} ms です";
        item.Explanation = "「たまっていた音声」は、音を出すまでに待っている量で、そのまま音の遅れの目安になります。小さすぎて音が途切れるなら音声バッファ設定を上げ、遅れが気になるなら下げてください。映像との差は、次の「遅延（音と映像のずれ）」の項目で数字にしています。実際に耳と目で合っているかは、10 の項目で確かめてください。";
        sec.Items.Add(item);
    }

    /// <summary>Picture and sound delay inside YuCap / Windows (informational; never NG), with the user's own settings.</summary>
    private async Task LatencyStep(TestSection sec, bool videoOk)
    {
        var item = new TestItem { Name = "遅延（音と映像のずれ）", Verdict = Verdict.Info };
        _settings.AvOffsetMs = _testSaved?.AvOffsetMs ?? 0;
        double[] vLat = Array.Empty<double>();
        bool vEst = false;
        string vWhy = "Direct3D の映像が動いていませんでした";
        if (videoOk)
        {
            try
            {
                // The test caption is drawn over the video; with something on top Windows composes the monitor and the
                // screen time of a frame can no longer be read from the display, so the caption stays hidden while
                // measuring (the next SetCaption shows it again). Everything else is the user's own setting.
                if (_testCaption != null) _testCaption.Visible = false;
                _settings.Interpolation = _testSaved?.Interpolation ?? false;
                EnsureEngine(_testSaved?.LowLatency ?? false, _savedMode);
                ApplyAvSync();
                await Wait(SettleMs);
                _video.BeginMeasure();
                var aSamples0 = new List<double>();
                RenderMeasurement? meas = null;
                try
                {
                    var sw0 = Stopwatch.StartNew();
                    while (sw0.ElapsedMilliseconds < 4000)
                    {
                        if (_audio.IsRunning && _audio.LatencyMs > 0) aSamples0.Add(_audio.LatencyMs);
                        await Wait(100);
                    }
                }
                finally { meas = _video.EndMeasure(); }
                vLat = meas?.LatencyMs ?? Array.Empty<double>();
                vEst = meas is { LatencyEstimated: true };
                if (vLat.Length < 5) vWhy = vLat.Length == 0 ? "画面に出た時刻を取得できませんでした" : $"測定できた回数が少なすぎました（{vLat.Length} 回）";
                _latencyAudioSamples = aSamples0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { vWhy = ex.Message; _latencyAudioSamples = new List<double>(); }
        }
        else
        {
            ApplyAvSync();
            _latencyAudioSamples = new List<double>();
            if (_audio.IsRunning)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000)
                {
                    if (_audio.LatencyMs > 0) _latencyAudioSamples.Add(_audio.LatencyMs);
                    await Wait(100);
                }
            }
        }

        var vs = TestProbes.LatencyStats(vLat);
        var asx = TestProbes.LatencyStats(_latencyAudioSamples.ToArray());
        string approx = vEst ? "約 " : "";
        item.Num("映像の遅れ（YuCap の中）", vs != null
            ? $"中央 {approx}{vs.Value.Median:0} ms ／ 95% {approx}{vs.Value.P95:0} ms"
            : $"測定できません（{vWhy}）");
        item.Num("音の遅れ（YuCap の中）", asx != null
            ? $"中央 {asx.Value.Median:0} ms ／ 95% {asx.Value.P95:0} ms（取り込みの区切り＋たまっている音声＋出力デバイスの待ち）"
            : _audio.IsRunning ? "測定できません" : "音声は動いていませんでした（音声なし）");
        string summary;
        if (vs != null && asx != null)
        {
            double d = asx.Value.Median - vs.Value.Median;
            string which = Math.Abs(d) < 10 ? "ほぼ同じです" : d > 0 ? $"音のほうが {d:0} ms 遅れています" : $"映像のほうが {-d:0} ms 遅れています";
            item.Num("映像と音の差", which);
            summary = which;
        }
        else
        {
            item.Num("映像と音の差", "求められません（どちらかを測定できなかったため）");
            summary = vs == null && asx == null ? "映像・音とも測定できませんでした" : vs == null ? "映像の遅れを測定できませんでした" : "音の遅れを測定できませんでした";
        }
        item.Num("音ズレ補正の設定", _settings.AvOffsetMs == 0 ? "なし" : _settings.AvOffsetMs > 0 ? $"音を {_settings.AvOffsetMs} ms 遅らせる" : $"音を {-_settings.AvOffsetMs} ms 早める（映像を遅らせる）");
        if (videoOk && _testSaved is { Renderer: not "d3d" })
            item.Num("描画方式", "この項目は Direct3D で測りました（普段の設定は標準（Media Foundation）です。Media Foundation では映像の遅れを測れないため）");
        item.Summary = summary;
        item.Explanation = "ここで測っているのは、キャプチャカードから YuCap に届いてから、Windows が画面に映す・音を鳴らすまでの時間です（音ズレ補正・映像の遅延・コマの間隔調整・フレーム補間による遅れも含みます）。" +
            "キャプチャカードの中や、テレビ・スピーカーの中で生じる遅れは測れないため含まれていません。実際の映像と音のずれは、この数字に加えてそれらの遅れが乗ります。目安として使い、最終的には音ズレ補正の画面で目と耳で合わせてください。";
        sec.Items.Add(item);
    }

    private List<double> _latencyAudioSamples = new();

    // =============================================================================================
    //  9. dialogs
    // =============================================================================================

    private sealed record DialogRow(int Index, string Title, bool English, List<string> Problems, int Dpi, Size Client);
    private readonly List<DialogRow> _dlgRows = new();
    private int _dlgIndex;

    private static string Describe(Control c)
    {
        string t = c.Text ?? string.Empty;
        t = t.Replace("\r", " ").Replace("\n", " ");
        if (t.Length > 18) t = t[..18] + "…";
        string n = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.GetType().Name + " " + c.Name;
        return t.Length > 0 ? $"{n}「{t}」" : n;
    }

    private static void AuditControls(Control parent, List<string> problems)
    {
        var kids = parent.Controls.Cast<Control>().Where(c => c.Visible).ToList();
        Rectangle client = parent.ClientRectangle;
        foreach (Control c in kids)
        {
            if (c.Left < -1 || c.Top < -1 || c.Right > client.Width + 1 || c.Bottom > client.Height + 1)
                problems.Add($"{Describe(c)} が枠からはみ出しています（{c.Bounds.X},{c.Bounds.Y} {c.Bounds.Width}x{c.Bounds.Height} / 枠 {client.Width}x{client.Height}）");

            // text that does not fit its own control
            try
            {
                if (c is Button b && b.Text.Length > 0)
                {
                    int tw = TextRenderer.MeasureText(b.Text, b.Font).Width;
                    if (tw > b.Width - 8) problems.Add($"{Describe(c)} の文字が切れます（文字 {tw}px / ボタン {b.Width}px）");
                }
                else if ((c is CheckBox || c is RadioButton || (c is Label lb && !lb.AutoSize)) && c.Text.Length > 0)
                {
                    int avail = c.Width - (c is Label ? 4 : 22);
                    Size ts = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(10, avail), int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    if (ts.Height > c.Height + 2) problems.Add($"{Describe(c)} の文字が枠に入りきりません（文字の高さ {ts.Height}px / 枠 {c.Height}px）");
                }
            }
            catch { }

            if (c is Panel or GroupBox or TabControl or TabPage) AuditControls(c, problems);
        }
        for (int i = 0; i < kids.Count; i++)
            for (int j = i + 1; j < kids.Count; j++)
            {
                Control a = kids[i], b2 = kids[j];
                if (a is Panel or GroupBox or TabControl || b2 is Panel or GroupBox or TabControl) continue;
                Rectangle r = Rectangle.Intersect(a.Bounds, b2.Bounds);
                if (r.Width > 2 && r.Height > 2)
                    problems.Add($"{Describe(a)} と {Describe(b2)} が重なっています（{r.Width}x{r.Height}px）");
            }
    }

    /// <summary>Called instead of showing a dialog (see <see cref="Ui.DialogAudit"/>). The dialog is put on the
    /// test monitor, shown fully transparent without taking focus - so that its DPI and font scaling really
    /// apply, which an unshown form does not get - inspected, and hidden again.</summary>
    private DialogResult AuditDialog(Form dlg)
    {
        var problems = new List<string>();
        int dpi = 0;
        try
        {
            Rectangle tb = _topt.Target.Bounds;
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Opacity = 0;
            dlg.Location = new Point(tb.X + Math.Max(0, (tb.Width - dlg.Width) / 2), tb.Y + Math.Max(0, (tb.Height - dlg.Height) / 2));
            dlg.Show();
            dlg.PerformLayout();
            dpi = (int)GetDpiForWindow(dlg.Handle);
            AuditControls(dlg, problems);
            dlg.Hide();
        }
        catch (Exception ex) { problems.Add("検査できませんでした: " + ex.Message); }
        _dlgRows.Add(new DialogRow(_dlgIndex, dlg.Text, L.English, problems, dpi, dlg.ClientSize));
        return DialogResult.Cancel;
    }

    private async Task DialogStep(TestSection sec)
    {
        _dlgRows.Clear();
        bool lang0 = L.English;
        var dialogs = new List<Action>
        {
            ShowBurstDialog, ShowAudioBufferSettings, ShowAvSyncDialog, ShowAdjustmentsDialog, ShowCropDialog,
            ShowSnapshotSettings, ShowAbout, ShowShortcuts, ShowDeviceKeywordSettings, ShowHotkeySettings,
            () => AskProfileName(L.T("新しいプロファイル"), string.Empty, null),
        };
        Ui.DialogAudit = AuditDialog;
        try
        {
            foreach (bool english in new[] { false, true })
            {
                L.English = english;
                for (int i = 0; i < dialogs.Count; i++)
                {
                    _dlgIndex = i;
                    try { dialogs[i](); }
                    catch (Exception ex) { _dlgRows.Add(new DialogRow(i, "(" + (i + 1) + ")", english, new List<string> { "作れませんでした: " + ex.Message }, 0, Size.Empty)); }
                    await Wait(10);
                }
            }
        }
        finally
        {
            Ui.DialogAudit = null;
            L.English = lang0;
        }
        // the dialogs' cancel paths put the live values back; make sure of the ones the test relies on
        ApplyRenderOptions();

        foreach (var g in _dlgRows.GroupBy(r => r.Index).OrderBy(g => g.Key))
        {
            // The same dialog in Japanese and in English: reported together, named by its Japanese title.
            var list = g.ToList();
            var problems = list.SelectMany(r => r.Problems.Select(p => (r.English ? "[英語] " : "[日本語] ") + p)).ToList();
            var it = new TestItem { Name = "ダイアログ: " + (list.FirstOrDefault(r => !r.English)?.Title ?? list[0].Title) };
            it.Num("検査した言語", string.Join("・", list.Select(r => r.English ? "英語" : "日本語")));
            if (list.Any(r => r.Dpi > 0)) it.Num("表示のスケール", $"{list.First(r => r.Dpi > 0).Dpi} dpi（{Math.Round(list.First(r => r.Dpi > 0).Dpi * 100.0 / 96)}%）");
            if (problems.Count == 0)
            {
                it.Verdict = Verdict.Ok; it.Summary = "崩れは見つかりませんでした";
            }
            else
            {
                it.Verdict = Verdict.Ng;
                it.Summary = $"{problems.Count} か所が崩れています";
                it.Explanation = string.Join("\n", problems.Distinct());
            }
            sec.Items.Add(it);
        }

        int dpi = _dlgRows.FirstOrDefault(r => r.Dpi > 0)?.Dpi ?? 0;
        int targetScale = TestProbes.ScalePercent(_topt.Target.Bounds);
        var note = new TestItem { Name = "検査の方法とスケール", Verdict = Verdict.Info };
        note.Num("方法", "ダイアログを試験するモニターの上で、完全に透明・フォーカスなしで表示し、部品の位置と大きさを調べて閉じる")
            .Num("ダイアログのスケール", dpi > 0 ? $"{dpi} dpi（{Math.Round(dpi * 100.0 / 96)}%）" : "取得できませんでした")
            .Num("試験するモニターのスケール", targetScale > 0 ? $"{targetScale}%" : "不明")
            .Num("検査しなかったもの", "更新のお知らせ・ダウンロード中のダイアログ（更新の有無で内容が決まるため）");
        if (dpi > 0 && targetScale > 0 && Math.Abs(dpi * 100.0 / 96 - targetScale) > 3)
            note.Explanation = "ダイアログのスケールが試験するモニターのスケールと違います。表示前のダイアログに別のモニターのスケールが適用されなかった可能性があり、この結果はモニターのスケールを反映していません。";
        else
            note.Explanation = "ダイアログはモニターのスケールで作られています。";
        sec.Items.Add(note);
    }
}
