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
}

internal static class Program
{
    public static readonly StartupOptions Options = new();

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Log.Init();
        if (args.Length > 0) Log.Info("args: " + string.Join(" ", args));

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
            AppSettings s = SettingsStore.Load();
            UpdateInfo? info = Updater.CheckAsync(s.UpdateApiUrl).GetAwaiter().GetResult();
            string msg = info == null
                ? $"現在: {Updater.CurrentVersion}\n更新はありません（または確認できませんでした）。\n\nエンドポイント:\n{s.UpdateApiUrl}"
                : $"現在: {Updater.CurrentVersion}\n最新: {info.Version} ({info.TagName})\n\n{info.AssetName}  {info.Size:N0} bytes\nSHA256: {info.Sha256 ?? "(なし)"}\n{info.DownloadUrl}";
            MessageBox.Show(msg, "YuCap - 更新確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Self-test: build the capture graph and report status.
        if (args.Length > 0 && args[0] == "--selftest")
        {
            RunSelfTest(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_selftest.txt"));
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
                "YuCap コマンドラインオプション:\n\n" +
                "  --fullscreen        全画面で起動\n" +
                "  --borderless        ウィンドウ枠なしで起動\n" +
                "  --topmost           常に前面で起動\n" +
                "  --muted             ミュートで起動\n" +
                "  --volume <0-200>    音量を指定\n" +
                "  --mode <指定>       映像モード指定\n" +
                "                      例: 1080p120 / 1440p60 / 1920x1080@120\n" +
                "  --list-formats [出力先]   対応フォーマットを書き出して終了\n" +
                "  --selftest [出力先]       自己診断を実行して終了\n" +
                "  --check-update      更新の有無を確認して終了",
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
            if (!SingleInstance.ActivateExisting(args))
            {
                MessageBox.Show("YuCap は既に起動しています。", "YuCap",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        // Started here, not earlier: the diagnostic modes above never send the
        // heartbeat, which would look like a wedged UI thread.
        Watchdog.Start();

        // Keep the app alive even if a device throws unexpectedly during runtime.
        Application.ThreadException += (s, e) =>
        {
            LogCrash(e.Exception);
            MessageBox.Show(e.Exception.Message, "YuCap - エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        Application.Run(new MainForm());
        Log.Info("==== exit ====");
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
                case "--volume":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int v))
                        Options.Volume = Math.Clamp(v, 0, 200);
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
            using var probe = new VideoEngine();
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
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{Application.ProductVersion}\n{ex}\n\n");
        }
        catch { /* never throw from the crash logger */ }
    }

    private static void RunSelfTest(string outPath)
    {
        var log = new List<string>();
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

        var engine = new VideoEngine();

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

                        // Rotation goes through the preview sink's vtable, which
                        // is easy to break silently when the interop shifts.
                        log.Add("--- rotation / mirror ---");
                        foreach (int deg in new[] { 90, 180, 0 })
                        {
                            bool okRot = engine.SetRotation(deg);
                            log.Add($"  rotate {deg}° → {(okRot ? "ok" : "unsupported")}"
                                  + $"  reported={engine.Rotation}°"
                                  + $"  display={engine.DisplayResolution.Width}x{engine.DisplayResolution.Height}");
                        }
                        log.Add($"  mirror on → {(engine.SetMirror(true) ? "ok" : "unsupported")}");
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
                            log.Add($"AFTER RESTART: {VerifyVideo(engine)}");
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

    /// <summary>
    /// Decide whether real video is on screen. A single centre pixel cannot tell
    /// live video from a blank (all-white or all-black) surface, which is exactly
    /// how the "fullscreen shows nothing" bugs presented. This grabs two frames a
    /// moment apart and reports both the spatial spread and whether the picture
    /// changed — a still, uniform surface means nothing is being rendered.
    /// </summary>
    private static string VerifyVideo(VideoEngine engine)
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
