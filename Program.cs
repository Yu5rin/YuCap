using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>Options parsed from the command line, applied over saved settings.</summary>
internal sealed class StartupOptions
{
    public bool Fullscreen;
    public bool Borderless;
    public bool Topmost;
    public bool Muted;
    public int? Volume;
    public CaptureMode? Mode; // Compression "" = any
    public bool TestMode;       // --test-mode: run the automatic test (MainForm.TestMode.cs), then exit
    public bool TestQuick;      // --quick
    public string? TestDisplay; // --display <N | device name | part of the monitor's name>
}

internal static partial class Program
{
    public static readonly StartupOptions Options = new();

    /// <summary>
    /// Settings loaded exactly once, right after Log.Init(). Everywhere else
    /// must read this instead of calling SettingsStore.Load() again: a second
    /// Load() call saw no file after the first one had already moved a
    /// corrupt settings.json aside (the file is gone by then), so it reported
    /// "first run" and SettingsStore.LastLoadFailed silently went back to
    /// false — the corrupt-settings notice the first Load() set up never showed.
    /// </summary>
    internal static AppSettings Settings { get; private set; } = new();

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Log.Init();
        if (args.Length > 0) Log.Info("args: " + string.Join(" ", args));

        // The diagnostic and error paths below (--help, --check-update, the
        // "already running" notice, the crash dialog) can show a MessageBox
        // before MainForm ever runs, so the UI language must be loaded now —
        // otherwise an English-configured user sees Japanese in those dialogs.
        Settings = SettingsStore.Load();

        // An earlier test mode that never finished (crash, power loss, killed process) left the user's
        // real settings in test-in-progress.json: put them back before anything reads the settings.
        AppSettings? recovered = TestModeGuard.TryRecover();
        if (recovered != null)
        {
            recovered.Normalize();
            Settings = recovered;
            SettingsStore.Save(recovered);
            TestModeGuard.Recovered = true;
            Log.Info("test mode: restored the settings of an interrupted test");
        }
        L.English = Settings.Language == "en";

        // A staged update that is already applied (version <= running), or that
        // the user no longer wants (automatic updates switched off), is dropped.
        if (Settings.StagedUpdateVersion != null
            && (!TryGetStagedVersion(out Version? stagedAtStart) || stagedAtStart <= Updater.CurrentVersion
                || !Settings.UpdateCheckOnStartup))
        {
            ClearStaged();
        }

        // Crash log: keep evidence in %APPDATA%\YuCap\error.log even after the
        // dialog is dismissed.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception);

        // Diagnostic mode: dump the capture formats Media Foundation reports.
        if (args.Length > 0 && args[0] == "--list-formats")
        {
            string outPath = args.Length > 1
                ? args[1]
                : Path.Combine(Path.GetTempPath(), "yucap_formats.txt");
            try { File.WriteAllLines(outPath, ListFormats()); }
            catch (Exception ex) { File.WriteAllText(outPath, ex.ToString()); }
            return;
        }

        // Update check as a one-shot, for verifying the endpoint and parsing
        // without needing an actual newer release to exist.
        if (args.Length > 0 && args[0] == "--check-update")
        {
            AppSettings s = Settings;
            UpdateCheckResult result = Updater.CheckAsync(s.UpdateApiUrl).GetAwaiter().GetResult();
            // Three distinct outcomes, not two: a failed check must not read as
            // "you're up to date" — that would hide a broken endpoint from the
            // one command meant to diagnose it.
            string msg = result.Error != null
                ? L.F("現在: {0}\n更新の確認に失敗しました。\n\n{1}\n\nエンドポイント:\n{2}",
                    Updater.CurrentVersion, result.Error, s.UpdateApiUrl)
                : result.Info == null
                    ? L.F("現在: {0}\n更新はありません。\n\nエンドポイント:\n{1}",
                        Updater.CurrentVersion, s.UpdateApiUrl)
                    : L.F("現在: {0}\n最新: {1} ({2})\n\n{3}  {4} bytes\nSHA256: {5}\n{6}",
                        Updater.CurrentVersion, result.Info.Version, result.Info.TagName,
                        result.Info.AssetName, result.Info.Size.ToString("N0"),
                        result.Info.Sha256 ?? L.T("なし"), result.Info.DownloadUrl);
            MessageBox.Show(msg, "YuCap - " + L.T("更新確認"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Self-test: build the capture graph and report status.
        if (args.Length > 0 && args[0] == "--selftest")
        {
            // --selftest [出力先] [--renderer mf|d3d]   (mf = the default Media Foundation path)
            string? outArg = null, rendererArg = "mf";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--renderer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    rendererArg = args[++i].Trim().ToLowerInvariant() == "d3d" ? "d3d" : "mf";
                else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    outArg = args[i];
            }
            RunSelfTest(outArg ?? Path.Combine(Path.GetTempPath(), "yucap_selftest.txt"), rendererArg);
            return;
        }

        // Restart stress (Direct3D only): ResetHandle/Attach/Start N times, check the picture returns each time.
        if (args.Length > 0 && args[0] == "--restart-stress")
        {
            int n = 40;
            bool stressFull = false, stressFast = false;
            string stressOut = Path.Combine(Path.GetTempPath(), "yucap_restart_stress.txt");
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--fullscreen", StringComparison.OrdinalIgnoreCase)) stressFull = true;
                else if (args[i].Equals("--fast", StringComparison.OrdinalIgnoreCase)) stressFast = true;
                else if (int.TryParse(args[i], out int v) && v > 0) n = v;
                else if (!args[i].StartsWith("--", StringComparison.Ordinal)) stressOut = args[i];
            }
            RunRestartStress(stressOut, n, stressFull, stressFast);
            return;
        }

        // GPU benchmark / HDR probe (Direct3D only; see Program.Probes.cs).
        if (args.Length > 0 && args[0] == "--gpu-bench")
        {
            RunGpuBench(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_gpu_bench.txt"));
            return;
        }
        // Selection logic of the live interpolation check on synthetic streams (no device, no window).
        if (args.Length > 0 && args[0] == "--live-triple-check")
        {
            string ltOut = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_live_triple_check.txt");
            File.WriteAllText(ltOut, InterpCheck.SelfCheckLiveFrames());
            return;
        }
        // Calibration of the picture-detail estimator on synthetic enlarged pictures (no device, no window).
        if (args.Length > 0 && args[0] == "--res-estimate-check")
        {
            string reOut = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_res_estimate_check.txt");
            File.WriteAllText(reOut, ResEstimate.SelfCheck());
            return;
        }
        if (args.Length > 0 && args[0] == "--interp-synth")
        {
            string synOut = Path.Combine(Path.GetTempPath(), "yucap_interp_synth.txt"), synIn = "", synStills = "";
            double synScale = 1;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--in", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) synIn = args[++i];
                else if (args[i].Equals("--stills", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) synStills = args[++i];
                else if (args[i].Equals("--scale", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out synScale);
                else if (!args[i].StartsWith("--", StringComparison.Ordinal)) synOut = args[i];
            }
            RunInterpSynth(synOut, synIn, synStills.Length > 0 ? synStills : null, synScale > 0 ? synScale : 1);
            return;
        }
        if (args.Length > 0 && args[0] == "--interp-probe")
        {
            string interpOut = Path.Combine(Path.GetTempPath(), "yucap_interp_probe.txt");
            string? interpStills = null;
            CaptureMode? interpMode = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) interpMode = ParseMode(args[++i]);
                else if (args[i].Equals("--stills", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) interpStills = args[++i];
                else if (!args[i].StartsWith("--", StringComparison.Ordinal)) interpOut = args[i];
            }
            RunInterpProbe(interpOut, interpMode, interpStills);
            return;
        }
        if (args.Length > 0 && args[0] == "--vblank-probe")
        {
            RunVBlankProbe(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_vblank_probe.txt"));
            return;
        }
        if (args.Length > 0 && args[0] == "--hdr-probe")
        {
            RunHdrProbe(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_hdr_probe.txt"));
            return;
        }

        // Render test (Direct3D only): dump source frames and 4K FSR/standard renders
        // so the upscaler can be judged on a monitor smaller than 4K.
        if (args.Length > 0 && args[0] == "--render-test")
        {
            RunRenderTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_render_test"));
            return;
        }

        // Just replaced a running build: wait for it to exit before contending
        // for the single-instance mutex, then clear away the displaced exe.
        int postUpdate = Array.IndexOf(args, Updater.PostUpdateArg);
        if (postUpdate >= 0 && postUpdate + 1 < args.Length
            && int.TryParse(args[postUpdate + 1], out int oldPid))
        {
            Log.Info($"update: started after update, waiting for pid {oldPid}");
            Updater.WaitForPreviousExit(oldPid);
        }
        Updater.CleanupOld();

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            MessageBox.Show(
                L.T("YuCap コマンドラインオプション:\n\n" +
                "  --fullscreen        全画面で起動\n" +
                "  --borderless        ウィンドウ枠なしで起動\n" +
                "  --topmost           常に前面で起動\n" +
                "  --muted             ミュートで起動\n" +
                "  --volume <0-500>    音量を指定\n" +
                "  --mode <指定>       映像モード指定\n" +
                "                      例: 1080p120 / 1440p60 / 1920x1080@120\n" +
                "  --list-formats [出力先]   対応フォーマットを書き出して終了\n" +
                "  --selftest [出力先]       自己診断を実行して終了\n" +
                "  --gpu-bench [出力先]      4K 拡大の GPU 負荷を計測して終了\n" +
                "  --hdr-probe [出力先]      HDR 入力の調査結果を書き出して終了\n" +
                "  --vblank-probe [出力先]   各モニターの垂直同期と表示レートを調べて終了\n" +
                "  --restart-stress [出力先] [回数] [--fullscreen] [--fast]   映像の再起動を繰り返し、毎回映像が戻るか調べて終了\n" +
                "  --interp-probe [出力先] [--mode <指定>] [--stills <フォルダ>]   フレーム補間を実機の映像で調べて終了\n" +
                "  --interp-synth [出力先] --in <画像> [--scale <倍率>] [--stills <フォルダ>]   既知の動きでフレーム補間を調べて終了\n" +
                "  --test-mode [--display <番号>] [--quick]   自動試験を実行してレポートを作り、終了\n" +
                "  --check-update      更新の有無を確認して終了"),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ParseOptions(args);

        // The capture device is exclusive — a second instance could only fail to
        // open it (or steal it), so allow one instance at a time. Rather than
        // just refusing, bring the existing window forward: launching an app and
        // getting only an error box reads as "it didn't start".
        using var single = new System.Threading.Mutex(true, @"Local\YuCap.SingleInstance", out bool first);
        if (!first)
        {
            Log.Info("second instance — activating the existing window");
            if (Options.TestMode)
            {
                Log.Info("test mode: YuCap is already running - the test was not started");
                return;
            }
            if (!SingleInstance.ActivateExisting(args))
            {
                MessageBox.Show(L.T("YuCap は既に起動しています。"), "YuCap",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        // Crash-recovery path: an update was staged but never applied on exit
        // (crash, forced kill, power loss). Apply it first and relaunch via the
        // --post-update handshake. Skipped for a --post-update launch so a
        // failing update cannot loop. The hash is re-verified inside
        // TryGetVerifiedStaged because the file sat on disk since the download.
        if (postUpdate < 0 && !Options.TestMode && Settings.UpdateCheckOnStartup
            && TryGetVerifiedStaged(out string stagedPath) && Updater.CanWriteToInstallDir())
        {
            try
            {
                Log.Info("update: applying staged update left over from the last run");
                Updater.Apply(stagedPath, relaunch: true);
                return;   // the mutex is released on return; the new build waits for our pid
            }
            catch (Exception ex)
            {
                Log.Info("update: startup apply failed — " + ex.Message);
                ClearStaged();
            }
        }

        // Started here, not earlier: the diagnostic modes above never send the
        // heartbeat, which would look like a wedged UI thread.
        Watchdog.Start();

        // Keep the app alive even if a device throws unexpectedly during runtime.
        Application.ThreadException += (s, e) =>
        {
            LogCrash(e.Exception);
            // Describe(), not e.Exception.Message: the raw message can be a bare
            // HRESULT string (e.g. "Exception from HRESULT: 0xC00D3E85"), which
            // means nothing to a user. Full detail still goes to error.log below.
            MessageBox.Show(
                L.F("予期しないエラーが発生しました。\n\n{0}\n\n詳細は error.log に記録しました。", Errors.Describe(e.Exception)),
                "YuCap - " + L.T("エラー"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        Application.Run(new MainForm());

        // Normal exit: swap the staged build in place, no relaunch. The fields
        // are not cleared here — the next launch sees version <= current and
        // cleans up. Never applied when the setting is off or the version was
        // skipped (TryGetVerifiedStaged checks the latter).
        if (!Updater.AppliedThisSession && !Options.TestMode && Settings.UpdateCheckOnStartup
            && TryGetVerifiedStaged(out string exitStaged) && Updater.CanWriteToInstallDir())
        {
            try { Updater.Apply(exitStaged, relaunch: false); }
            catch (Exception ex) { Log.Info("update: apply on exit failed — " + ex.Message); }
        }
        Log.Info("==== exit ====");
    }

    private static bool TryGetStagedVersion(out Version? v)
    {
        v = null;
        return Settings.StagedUpdateVersion != null
            && Version.TryParse(Settings.StagedUpdateVersion, out v);
    }

    /// <summary>True when a staged version newer than the running build exists,
    /// is not the skipped version, and its file still matches the recorded SHA256.</summary>
    private static bool TryGetVerifiedStaged(out string path)
    {
        path = "";
        if (!TryGetStagedVersion(out Version? v) || v == null || v <= Updater.CurrentVersion) return false;
        if (Settings.StagedUpdateSha256 == null) return false;
        if (Settings.SkippedUpdateVersion == v.ToString()) return false;
        return Updater.VerifyStaged(v.ToString(), Settings.StagedUpdateSha256, out path);
    }

    private static void ClearStaged()
    {
        Updater.DiscardStaging();
        Settings.StagedUpdateVersion = null;
        Settings.StagedUpdateSha256 = null;
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) { Log.Info("could not save settings after clearing staged update: " + ex.Message); }
    }

    private static void ParseOptions(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--fullscreen": Options.Fullscreen = true; break;
                case "--borderless": Options.Borderless = true; break;
                case "--topmost": Options.Topmost = true; break;
                case "--muted": Options.Muted = true; break;
                case "--test-mode": Options.TestMode = true; break;
                case "--quick": Options.TestQuick = true; break;
                case "--display":
                    if (i + 1 < args.Length) Options.TestDisplay = args[++i];
                    break;
                case "--volume":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int v))
                        Options.Volume = Math.Clamp(v, 0, AudioEngine.MaxVolumePercent);
                    break;
                case "--mode":
                    if (i + 1 < args.Length) Options.Mode = ParseMode(args[++i]);
                    break;
            }
        }
    }

    /// <summary>Parse "1080p120", "1440p60", "4k30" or "1920x1080@120".</summary>
    internal static CaptureMode? ParseMode(string s)
    {
        s = s.Trim().ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+)x(\d+)@(\d+)$");
        if (m.Success)
            return new CaptureMode("", int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));

        m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+)(p|k)(\d+)$");
        if (!m.Success) return null;
        int n = int.Parse(m.Groups[1].Value), fps = int.Parse(m.Groups[3].Value);
        (int w, int h) = (n, m.Groups[2].Value) switch
        {
            (480, "p") => (640, 480),
            (720, "p") => (1280, 720),
            (1080, "p") => (1920, 1080),
            (1440, "p") => (2560, 1440),
            (2160, "p") => (3840, 2160),
            (2, "k") => (2560, 1440),
            (4, "k") => (3840, 2160),
            _ => (0, 0),
        };
        return w > 0 ? new CaptureMode("", w, h, fps) : null;
    }

    /// <summary>Enumerate every capture device and the modes Media Foundation
    /// offers for it. Replaces the old DirectShow-based probe, which existed
    /// only to diagnose the NV12 problem that led to using MF in the first place.
    /// </summary>
    private static List<string> ListFormats()
    {
        var lines = new List<string> { $"YuCap {Application.ProductVersion} — capture formats (Media Foundation)", string.Empty };
        var devices = VideoEngine.EnumerateDevices();
        if (devices.Count == 0)
        {
            lines.Add("(no video capture devices found)");
            return lines;
        }

        foreach (var dev in devices)
        {
            lines.Add($"=== {dev.Name} ===");
            lines.Add($"    {dev.Id}");
            using (var probe = new VideoEngine())
            {
                try
                {
                    // Needs a window to start a preview, so use a hidden one.
                    using var host = new Form { ShowInTaskbar = false, Opacity = 0 };
                    host.CreateControl();
                    probe.Attach(host.Handle);
                    probe.Start(dev);
                    foreach (CaptureMode m in probe.GetModes())
                        lines.Add($"    {m.Width,5}x{m.Height,-5} {m.Compression,-8} {m.Fps,3}fps");
                    lines.Add($"    current: {probe.CurrentMode}");
                }
                catch (Exception ex)
                {
                    lines.Add("    ERROR: " + ex.Message);
                }
            }

            // Every native media type with its colour attributes (the capture engine's list above
            // hides them): what the device claims about primaries / transfer / matrix / range.
            lines.Add("  native media types (source reader):");
            try
            {
                foreach (string l in CaptureFormats.ProbeDevice(dev)) lines.Add("    " + l);
            }
            catch (Exception ex)
            {
                lines.Add("    ERROR: " + ex.Message);
            }
            lines.Add(string.Empty);
        }
        return lines;
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        Log.Info($"CRASH {ex.GetType().Name}: {ex.Message}");
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuCap");
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "error.log");

            // Unlike yucap.log (which rotates at 2MB), error.log had no cap and
            // grew without bound on machines that crash repeatedly. Rotate it the
            // same way: one backup, kept inside this try so a rotation failure
            // still can't take down the crash logger itself.
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1_000_000)
            {
                string oldPath = Path.Combine(dir, "error.old.log");
                try { File.Delete(oldPath); } catch { /* ignore */ }
                File.Move(logPath, oldPath);
            }

            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{Application.ProductVersion}\n{ex}\n\n");
        }
        catch { /* never throw from the crash logger */ }
    }

    private static void RunSelfTest(string outPath, string renderer)
    {
        var log = new List<string>();
        log.Add($"renderer: {renderer}");
        var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            Size = new Size(900, 560),
            ShowInTaskbar = false,
            Text = "YuCap self-test",
        };
        // Replicate the real app layout: menu (top) + status (bottom) + fill canvas.
        var menu = new MenuStrip();
        menu.Items.Add(new ToolStripMenuItem("ファイル"));
        var status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel("status"));
        // Test the pre-rework config the user confirmed working: Dock.Fill.
        var canvas = new VideoBox { Dock = DockStyle.Fill, BackColor = Color.Black };
        form.Controls.Add(canvas);
        form.Controls.Add(menu);
        form.Controls.Add(status);
        canvas.SendToBack();

        var engine = VideoEngines.Create(renderer);

        form.Load += (_, _) =>
        {
            try
            {
                engine.Attach(canvas.Handle);
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), "JVA14");
                if (dev == null) { log.Add("デバイスなし"); Finish(); return; }

                engine.Start(dev, null);
                Application.DoEvents();
                log.Add($"[Dock.Fill] res={engine.CurrentResolution}");

                var t = new System.Windows.Forms.Timer { Interval = 2500 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    log.Add($"windowed: {VerifyVideo(engine)}");

                    // The photo sink should hand back the source resolution,
                    // not the (much smaller) window size the screen copy gives.
                    using (Bitmap? photo = engine.PhotoSnapshot())
                    {
                        log.Add(photo == null
                            ? "PHOTO SINK: unavailable (falls back to screen copy)"
                            : $"PHOTO SINK: {photo.Width}x{photo.Height}" +
                              $" (source is {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height})" +
                              $" center={photo.GetPixel(photo.Width / 2, photo.Height / 2)}");
                    }

                    // Reproduce the operation that used to deadlock: go
                    // borderless-fullscreen and push a full-screen video rect.
                    // The UI thread must stay responsive throughout.
                    Rectangle scr = Screen.FromControl(form).Bounds;
                    log.Add($"--- fullscreen resize test → {scr.Width}x{scr.Height} ---");
                    menu.Visible = false;
                    status.Visible = false;
                    form.FormBorderStyle = FormBorderStyle.None;
                    form.Bounds = scr;
                    form.TopMost = true;
                    Application.DoEvents();

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    engine.SetVideoRect(new Rectangle(0, 0, scr.Width, scr.Height));
                    log.Add($"SetVideoRect returned in {sw.ElapsedMilliseconds}ms (must be ~0 — it only queues)");

                    // Pump for a few seconds and measure the worst UI stall: a
                    // deadlock would show up as one long gap.
                    long worstGap = 0, lastTick = sw.ElapsedMilliseconds;
                    var pump = new System.Windows.Forms.Timer { Interval = 50 };
                    pump.Tick += (_, _) =>
                    {
                        long now = sw.ElapsedMilliseconds;
                        worstGap = Math.Max(worstGap, now - lastTick);
                        lastTick = now;
                        if (now < 6000) return;
                        pump.Stop();
                        log.Add($"worst UI stall during resize: {worstGap}ms (deadlock would be thousands)");
                        log.Add($"FULLSCREEN: {VerifyVideo(engine)}");
                        if (renderer == "d3d" && engine.Stats is { } st)
                            log.Add($"D3D stats: in={st.InputFps:0}fps shown={st.PresentedFps:0}fps dropped={st.DroppedFrames}");
                        if (renderer == "d3d")
                        {
                            // The figures above overlap VerifyVideo's snapshot readbacks, which
                            // stall the render thread. Sample a quiet window (no snapshots) by
                            // differencing the 1s-updated stats' cumulative drop counter and
                            // averaging the per-second rates.
                            var quiet = System.Diagnostics.Stopwatch.StartNew();
                            long drop0 = engine.Stats?.DroppedFrames ?? 0;
                            double sumIn = 0, sumShown = 0; int n = 0;
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(1100);   // let a stats tick pass the snapshot period
                            long t0 = quiet.ElapsedMilliseconds;
                            while (quiet.ElapsedMilliseconds - t0 < 3000)
                            {
                                System.Threading.Thread.Sleep(1000);
                                if (engine.Stats is { } q) { sumIn += q.InputFps; sumShown += q.PresentedFps; n++; }
                            }
                            long drop1 = engine.Stats?.DroppedFrames ?? 0;
                            if (n > 0)
                                log.Add($"D3D stats (quiet 3s): in={sumIn / n:0}fps shown={sumShown / n:0}fps dropped={drop1 - drop0}");

                            // Picture adjustments live in the convert pass, so a still taken after
                            // the change must already carry it.
                            log.Add("--- adjustments / video delay ---");
                            double lumaA = MeanLuma(engine.PhotoSnapshot());
                            engine.Adjustments = new VideoAdjustments(0.3f, 1f, 1f, 1f);
                            System.Threading.Thread.Sleep(300);
                            double lumaB = MeanLuma(engine.PhotoSnapshot());
                            log.Add($"adjust brightness +0.3: mean {lumaA:0} -> {lumaB:0} ({(lumaB > lumaA ? "ok" : "NO CHANGE")})");
                            engine.Adjustments = VideoAdjustments.Neutral;
                            System.Threading.Thread.Sleep(300);
                            double lumaC = MeanLuma(engine.PhotoSnapshot());
                            log.Add($"adjust reset: {(Math.Abs(lumaC - lumaA) <= 2 ? "ok" : "MISMATCH")} (mean {lumaC:0}, was {lumaA:0})");

                            engine.VideoDelayMs = 200;
                            System.Threading.Thread.Sleep(1500);
                            double actual = engine.VideoDelayActualMs;
                            log.Add($"video delay 200ms: actual\u2248{actual:0}ms ({(actual >= 180 && actual <= 230 + engine.VideoPacingMarginMs ? "ok" : "CHECK")})");
                            engine.VideoDelayMs = 0;
                            System.Threading.Thread.Sleep(500);
                            log.Add($"video delay off: actual={engine.VideoDelayActualMs:0}ms");

                            // ---- crop, display-size save, low latency (Direct3D only) ----
                            log.Add("--- crop / display-size save / low latency ---");
                            try
                            {
                                Size srcRes = engine.CurrentResolution;
                                engine.Crop = new System.Windows.Forms.Padding(100);
                                System.Threading.Thread.Sleep(500);
                                Size disp = engine.DisplayResolution;
                                bool cropOk = disp == new Size(srcRes.Width - 200, srcRes.Height - 200);
                                log.Add($"crop 100px: display={disp.Width}x{disp.Height} ({(cropOk ? "ok" : "CHECK")})"
                                    + (srcRes == new Size(1920, 1080) ? string.Empty : $" source={srcRes.Width}x{srcRes.Height} (expected source-200)"));
                                using (Bitmap? cph = engine.PhotoSnapshot())
                                    log.Add(cph == null
                                        ? "  crop photo: unavailable"
                                        : $"  crop photo: {cph.Width}x{cph.Height} ({(cph.Size == disp ? "size matches display" : "MISMATCH")})");
                                engine.Crop = System.Windows.Forms.Padding.Empty;
                                System.Threading.Thread.Sleep(500);
                                log.Add($"  crop cleared: display={engine.DisplayResolution.Width}x{engine.DisplayResolution.Height} (source {srcRes.Width}x{srcRes.Height})");

                                // Snapshot(cropToVideo) is what 表示どおりに保存 saves: the composed frame
                                // at the size of the destination rectangle (clipped to the window).
                                Rectangle destNow = Rectangle.Intersect(new Rectangle(0, 0, scr.Width, scr.Height),
                                    new Rectangle(Point.Empty, canvas.ClientSize));
                                using (Bitmap? shown = engine.Snapshot(cropToVideo: true))
                                    log.Add(shown == null
                                        ? "display-size save: Snapshot unavailable"
                                        : $"display-size save: Snapshot(cropToVideo)={shown.Width}x{shown.Height}, dest rect={destNow.Width}x{destNow.Height} ({(shown.Size == destNow.Size ? "ok" : "CHECK")})");

                                // The swap chain kind is chosen at start, so low latency is a restart on a
                                // fresh window (what MainForm does when the menu item is toggled).
                                void RestartOnFreshWindow()
                                {
                                    canvas.ResetHandle();
                                    engine.Attach(canvas.Handle);
                                    engine.Start(dev, null);
                                    engine.SetVideoRect(new Rectangle(0, 0, scr.Width, scr.Height));
                                }
                                engine.LowLatency = true;
                                RestartOnFreshWindow();
                                PumpFor(2500);
                                double llShown = engine.Stats?.PresentedFps ?? 0;
                                log.Add($"low latency: on, present mode={engine.PresentMode}, shown={llShown:0}fps");
                                log.Add($"  low latency render: {VerifyVideo(engine)}");
                                engine.LowLatency = false;
                                RestartOnFreshWindow();
                                PumpFor(2500);
                                log.Add($"low latency: off, present mode={engine.PresentMode}, shown={engine.Stats?.PresentedFps ?? 0:0}fps");
                                log.Add($"  after low latency off: {VerifyVideo(engine)}");
                            }
                            catch (Exception cex) { log.Add("crop / low latency test FAILED: " + cex.Message); }
                        }

                        // Rotation goes through the preview sink's vtable, which
                        // is easy to break silently when the interop shifts.
                        log.Add("--- rotation / mirror ---");
                        foreach (int deg in new[] { 90, 180, 0 })
                        {
                            bool okRot = engine.SetRotation(deg);
                            log.Add($"  rotate {deg}° → {(okRot ? "ok" : "unsupported")}"
                                  + $"  reported={engine.Rotation}°"
                                  + $"  display={engine.DisplayResolution.Width}x{engine.DisplayResolution.Height}");

                            // The photo sink is made to rotate its still to match
                            // the preview (VideoEngine.PhotoSnapshot), but the
                            // DIRECTION of the preview sink's own rotation is
                            // unverified — a sign error there would still show a
                            // preview that "looks rotated" while every saved photo
                            // comes out turned the opposite way from what the user
                            // saw on screen. Only 90° is checked: at 180° the two
                            // candidate orientations are identical, so it can't
                            // distinguish a correct rotation from a reversed one.
                            if (deg == 90 && okRot)
                            {
                                System.Threading.Thread.Sleep(700); // let the preview re-render at the new rotation
                                using Bitmap? photo90 = engine.PhotoSnapshot();
                                using Bitmap? shot90 = engine.Snapshot();
                                if (photo90 != null && shot90 != null)
                                {
                                    double[,] shotThumb = Thumbnail8x8(shot90);
                                    double[,] photoThumb = Thumbnail8x8(photo90);
                                    using Bitmap photo90Alt = Rotated180Copy(photo90);
                                    double[,] photoThumbAlt = Thumbnail8x8(photo90Alt);
                                    double mad = MeanAbsDiff(shotThumb, photoThumb);
                                    double madAlt = MeanAbsDiff(shotThumb, photoThumbAlt);
                                    log.Add($"  photo orientation @90°: {(mad < madAlt ? "matches preview" : "MISMATCH (direction reversed?)")}  mad={mad:F1} alt={madAlt:F1}");
                                }
                                else
                                {
                                    log.Add("  photo orientation @90°: skipped (photo or screen snapshot unavailable)");
                                }
                            }
                        }

                        bool okMirror = engine.SetMirror(true);
                        log.Add($"  mirror on → {(okMirror ? "ok" : "unsupported")}");
                        if (okMirror)
                        {
                            System.Threading.Thread.Sleep(700); // let the preview re-render mirrored
                            using Bitmap? photoM = engine.PhotoSnapshot();
                            using Bitmap? shotM = engine.Snapshot();
                            if (photoM != null && shotM != null)
                            {
                                double[,] shotThumb = Thumbnail8x8(shotM);
                                double[,] photoThumb = Thumbnail8x8(photoM);
                                using Bitmap photoMAlt = FlippedCopy(photoM);
                                double[,] photoThumbAlt = Thumbnail8x8(photoMAlt);
                                double mad = MeanAbsDiff(shotThumb, photoThumb);
                                double madAlt = MeanAbsDiff(shotThumb, photoThumbAlt);
                                log.Add($"  photo mirror: {(mad < madAlt ? "matches preview" : "MISMATCH (direction reversed?)")}  mad={mad:F1} alt={madAlt:F1}");
                            }
                            else
                            {
                                log.Add("  photo mirror: skipped (photo or screen snapshot unavailable)");
                            }
                        }
                        engine.SetMirror(false);

                        // Version ordering drives the updater; a string compare
                        // would rank 1.0.10 below 1.0.9 and strand users there.
                        log.Add("--- version ordering ---");
                        log.Add($"  current={Updater.CurrentVersion}"
                              + $"  1.0.9<1.0.10={new Version(1, 0, 9) < new Version(1, 0, 10)}"
                              + $"  1.0.2<1.1.0={new Version(1, 0, 2) < new Version(1, 1, 0)}");

                        // Exercise the real wedge-recovery action, including the
                        // window recreation that DirectComposition requires:
                        // reusing the old HWND fails with
                        // DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED.
                        log.Add("--- preview restart on a fresh window (recovery path) ---");
                        var rsw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            canvas.ResetHandle();
                            engine.Attach(canvas.Handle);
                            engine.Start(dev, null);
                            engine.SetVideoRect(new Rectangle(0, 0, scr.Width, scr.Height));
                            log.Add($"restart completed in {rsw.ElapsedMilliseconds}ms");
                        }
                        catch (Exception rex) { log.Add("restart FAILED: " + rex.Message); }

                        var after = new System.Windows.Forms.Timer { Interval = 2000 };
                        after.Tick += (_, _) =>
                        {
                            after.Stop();
                            string afterVerdict = VerifyVideo(engine);
                            log.Add($"AFTER RESTART: {afterVerdict}");
                            if (afterVerdict.StartsWith("NOT RENDERING"))
                            {
                                // Tell "the renderer shows nothing" from "the source itself is blank".
                                log.Add($"  received={engine.FramesReceived} presented={engine.FramesPresented}"
                                    + $" margin={engine.VideoPacingMarginMs:0.0}ms"
                                    + $" sourceMeanLuma={MeanLuma(engine.PhotoSnapshot()):0.0}");
                            }
                            Finish();
                        };
                        after.Start();
                    };
                    pump.Start();
                };
                t.Start();
            }
            catch (Exception ex)
            {
                log.Add("EXCEPTION: " + ex.Message);
                Finish();
            }

            void Finish()
            {
                try { engine.Dispose(); } catch { }
                File.WriteAllLines(outPath, log);
                form.Close();
            }
        };

        Application.Run(form);
    }

    /// <summary>Mean luminance (0..255) of a still, sampled on a grid; disposes the bitmap. NaN if null.</summary>
    internal static double MeanLuma(Bitmap? bmp)
    {
        if (bmp == null) return double.NaN;
        using (bmp)
        {
            double sum = 0; int n = 0;
            int stepX = Math.Max(1, bmp.Width / 64), stepY = Math.Max(1, bmp.Height / 64);
            for (int y = 0; y < bmp.Height; y += stepY)
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    Color c = bmp.GetPixel(x, y);
                    sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                    n++;
                }
            return n == 0 ? double.NaN : sum / n;
        }
    }

    internal static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(15);
        }
    }

    /// <summary>
    /// <c>--render-test &lt;dir&gt;</c>: start the Direct3D engine in a real (but unobtrusive)
    /// window, then for each of 1080p / 1440p write the source frame and the standard
    /// and FSR renders at 3840x2160, plus render-test.txt with timings and stats.
    /// </summary>
    private static void RunRenderTest(string dir)
    {
        var log = new List<string>();
        var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            Size = new Size(960, 540),
            ShowInTaskbar = false,
            Text = "YuCap render-test",
        };
        var canvas = new VideoBox { Dock = DockStyle.Fill, BackColor = Color.Black };
        form.Controls.Add(canvas);
        IVideoEngine engine = VideoEngines.Create("d3d");

        form.Load += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                engine.Attach(canvas.Handle);
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), "JVA14");
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Start(dev, null);
                log.Add($"device: {dev.Name}");
                PumpFor(2000);

                var target = new Size(3840, 2160);
                foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
                {
                    log.Add($"=== source {w}x{h} ===");
                    if (!engine.GetModes().Any(m => m.Width == w && m.Height == h))
                    {
                        log.Add("  skipped: the device offers no such mode");
                        continue;
                    }
                    try
                    {
                        engine.Start(dev, new CaptureMode("", w, h, 0));
                        PumpFor(2000);
                        log.Add($"  running: {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height} {engine.NegotiatedFps}fps");

                        using (Bitmap? src = engine.PhotoSnapshot())
                        {
                            if (src == null) log.Add("  source: PhotoSnapshot failed");
                            else
                            {
                                src.Save(Path.Combine(dir, $"source_{w}x{h}.png"), System.Drawing.Imaging.ImageFormat.Png);
                                log.Add($"  source_{w}x{h}.png  {src.Width}x{src.Height}");
                            }
                        }
                        foreach (var (name, q) in new[] { ("standard", ScalingQuality.Standard), ("fsr", ScalingQuality.Fsr) })
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            using Bitmap? img = engine.RenderOffscreen(target, q);
                            long ms = sw.ElapsedMilliseconds;
                            if (img == null) { log.Add($"  {name}: RenderOffscreen returned null ({ms}ms)"); continue; }
                            string file = $"{name}_{w}x{h}_to_3840x2160.png";
                            img.Save(Path.Combine(dir, file), System.Drawing.Imaging.ImageFormat.Png);
                            log.Add($"  {file}  {img.Width}x{img.Height}  render {ms}ms");
                        }
                        if (engine.Stats is { } st)
                            log.Add($"  D3D stats: in={st.InputFps:0}fps shown={st.PresentedFps:0}fps dropped={st.DroppedFrames}");
                    }
                    catch (Exception mex) { log.Add("  FAILED: " + mex.Message); }
                }
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { engine.Dispose(); } catch { }
                try { File.WriteAllLines(Path.Combine(dir, "render-test.txt"), log); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }

    /// <summary>
    /// Decide whether real video is on screen. A single centre pixel cannot tell
    /// live video from a blank (all-white or all-black) surface, which is exactly
    /// how the "fullscreen shows nothing" bugs presented. This grabs two frames a
    /// moment apart and reports both the spatial spread and whether the picture
    /// changed — a still, uniform surface means nothing is being rendered.
    /// </summary>
    internal static string VerifyVideo(IVideoEngine engine)
    {
        try
        {
            using Bitmap? a = engine.Snapshot();
            if (a == null) return "NO VIDEO (snapshot failed)";
            System.Threading.Thread.Sleep(400);
            using Bitmap? b = engine.Snapshot();
            if (b == null) return "NO VIDEO (second snapshot failed)";

            int minL = 255, maxL = 0, changed = 0, samples = 0;
            for (int gy = 1; gy <= 4; gy++)
                for (int gx = 1; gx <= 4; gx++)
                {
                    int x = a.Width * gx / 5, y = a.Height * gy / 5;
                    Color ca = a.GetPixel(x, y);
                    int la = (ca.R + ca.G + ca.B) / 3;
                    minL = Math.Min(minL, la);
                    maxL = Math.Max(maxL, la);
                    samples++;
                    if (x < b.Width && y < b.Height)
                    {
                        Color cb = b.GetPixel(x, y);
                        if (Math.Abs((cb.R + cb.G + cb.B) / 3 - la) > 6) changed++;
                    }
                }

            string verdict = (maxL - minL) < 8 && changed == 0
                ? "NOT RENDERING (uniform and static)"
                : "video OK";
            return $"{verdict}  size={a.Width}x{a.Height} spread={maxL - minL} movingPoints={changed}/{samples}";
        }
        catch (Exception ex) { return "verify error: " + ex.Message; }
    }

    /// <summary>8x8 grayscale luminance thumbnail, used to compare two images
    /// cheaply for the rotation/mirror direction check in RunSelfTest.</summary>
    internal static double[,] Thumbnail8x8(Bitmap src)
    {
        using var thumb = new Bitmap(8, 8);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, 8, 8);
        }
        var lum = new double[8, 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                Color c = thumb.GetPixel(x, y);
                lum[x, y] = (c.R + c.G + c.B) / 3.0;
            }
        return lum;
    }

    internal static double MeanAbsDiff(double[,] a, double[,] b)
    {
        double sum = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                sum += Math.Abs(a[x, y] - b[x, y]);
        return sum / 64.0;
    }

    internal static Bitmap Rotated180Copy(Bitmap src)
    {
        var copy = (Bitmap)src.Clone();
        copy.RotateFlip(RotateFlipType.Rotate180FlipNone);
        return copy;
    }

    internal static Bitmap FlippedCopy(Bitmap src)
    {
        var copy = (Bitmap)src.Clone();
        copy.RotateFlip(RotateFlipType.RotateNoneFlipX);
        return copy;
    }

    /// <summary>Sample the true displayed centre pixel via the compositor (screen copy).</summary>
    private static string ScreenCenter(Control canvas)
    {
        try
        {
            Rectangle scr = canvas.RectangleToScreen(canvas.ClientRectangle);
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(scr.X + scr.Width / 2, scr.Y + scr.Height / 2, 0, 0, new Size(1, 1));
            Color c = bmp.GetPixel(0, 0);
            return $"({c.R},{c.G},{c.B})";
        }
        catch (Exception ex) { return "err:" + ex.Message; }
    }

    /// <summary>Bounding box of non-black pixels — reveals where the video is drawn.</summary>
    private static string NonBlackBounds(Bitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < bmp.Height; y += 2)
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    int o = y * stride + x * 4;
                    if (buf[o] > 24 || buf[o + 1] > 24 || buf[o + 2] > 24)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
        }
        finally { bmp.UnlockBits(data); }
        return maxX < 0 ? "(all black)" : $"[{minX},{minY} - {maxX},{maxY}] size {maxX - minX}x{maxY - minY}";
    }
}
