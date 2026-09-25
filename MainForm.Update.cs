using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>Update checking, download and installation.</summary>
public sealed partial class MainForm
{

    /// <summary>
    /// After an update, confirm what happened: an update that restarts the app
    /// silently is indistinguishable from a crash-and-relaunch. Shown once per
    /// version, with the release notes a click away.
    /// </summary>
    private void AnnounceNewVersion()
    {
        string current = Updater.CurrentVersion.ToString();
        if (_settings.LastRunVersion == current) return;

        bool upgraded = _settings.LastRunVersion != null;   // not a first run
        _settings.LastRunVersion = current;
        // A version we arrived at is obviously no longer one to skip.
        if (_settings.SkippedUpdateVersion == current) _settings.SkippedUpdateVersion = null;
        if (!upgraded)
        {
            // A brand new install has no prior version to compare against —
            // point the user at the menu and hotkey list instead of the
            // "updated to" notice, which would be meaningless here.
            ShowOsd(L.T("右クリックでメニュー、F1 で操作一覧を表示できます"), 6000);
            return;
        }

        Log.Info($"update: now running {current}");
        // Long duration: this is the one confirmation that the restart the
        // user just experienced was an update, not a crash — it must survive
        // long enough to actually be read.
        ShowOsd(L.F("バージョン {0} に更新しました", current), OsdLongMilliseconds);
    }

    /// <summary>
    /// Startup check, on every launch when enabled. Fired and forgotten so
    /// nothing about it can delay the window appearing; the check itself stays
    /// quiet unless there is genuinely something newer.
    /// </summary>
    private void MaybeCheckForUpdatesOnStartup()
    {
        if (!_settings.UpdateCheckOnStartup) return;

        // Let the capture settle before adding network work.
        var delay = new System.Windows.Forms.Timer { Interval = 4000 };
        delay.Tick += async (_, _) =>
        {
            delay.Stop();
            delay.Dispose();
            if (IsDisposed) return;
            try { await CheckForUpdatesAsync(manual: false); }
            catch (Exception ex) { Log.Info("startup update check failed: " + ex.Message); }
        };
        delay.Start();
    }

    private void ToggleUpdateCheck()
    {
        _settings.UpdateCheckOnStartup = !_settings.UpdateCheckOnStartup;
        UpdateChecks();
        ShowOsd(_settings.UpdateCheckOnStartup
            ? L.T("起動時の更新確認: オン")
            : L.T("起動時の更新確認: オフ"));
    }

    /// <summary>True while a check is running, so a second click on the menu
    /// item (or an overlapping startup check) cannot stack a second check —
    /// which would mean two prompts and, worse, two downloads.</summary>
    private bool _updateCheckInFlight;

    /// <summary>A newer release found by the silent startup check and not yet
    /// skipped or acted on. MainForm.UpdateChecks relabels the help menu item
    /// from this while it is set.</summary>
    private UpdateInfo? _pendingUpdate;

    /// <summary>
    /// Look for a newer release and, with the user's consent, install it.
    /// Communication only ever happens here — from an explicit menu action, or
    /// from the once-a-day startup check the user can switch off.
    /// </summary>
    /// <param name="manual">True when the user asked; only then do we report
    /// "no update" or a failed check. The startup check stays silent.</param>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInFlight) return;
        _updateCheckInFlight = true;
        try
        {
            if (!IsDisposed) _miCheckUpdate.Enabled = false;
            // A manual check can take a few seconds against GitHub with
            // nothing else on screen to say it's doing anything — the OSD and
            // wait cursor are the only sign of life until it resolves.
            if (manual)
            {
                ShowOsd(L.T("更新を確認しています..."));
                Cursor = Cursors.WaitCursor;
            }

            UpdateCheckResult result;
            try
            {
                result = await Updater.CheckAsync(_settings.UpdateApiUrl);
            }
            catch (Exception ex)
            {
                // CheckAsync is expected to report failures through Error rather
                // than throw; this only catches something unexpected so it still
                // reaches the same "failed" path instead of crashing.
                Log.Info("update check threw: " + ex.Message);
                result = new UpdateCheckResult(null, Errors.Describe(ex));
            }

            // The window may have closed while we awaited the network — check
            // before any use of `this` (message boxes, settings, dialogs).
            if (IsDisposed) return;

            _settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o");

            if (result.Error != null)
            {
                Log.Info("update check failed: " + result.Error);
                if (manual)
                {
                    MessageBox.Show(this,
                        L.F("更新の確認に失敗しました。\nネットワーク接続を確認してください。\n\n{0}", result.Error),
                        "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            UpdateInfo? info = result.Info;
            if (info == null)
            {
                if (manual)
                {
                    // A manual check that finds nothing means whatever was
                    // pending is no longer news — clear it so the help menu
                    // item goes back to "check for updates".
                    _pendingUpdate = null;
                    UpdateChecks();
                    MessageBox.Show(this,
                        L.F("現在のバージョンは {0} です。\n更新はありません。", Updater.CurrentVersion),
                        "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            // A version the user already declined must not be offered on every
            // launch — the startup check now runs each time.
            if (!manual && _settings.SkippedUpdateVersion == info.Version.ToString())
            {
                Log.Info($"update: {info.Version} was skipped by the user — not prompting");
                return;
            }

            if (manual)
            {
                // An explicit menu click may show the modal prompt directly —
                // the user just asked for this.
                PromptForUpdate(info);
                return;
            }

            // The silent startup check must not pop a modal over whatever the
            // user is currently watching. Park it and offer it through the OSD
            // (and the relabeled help menu item) instead.
            _pendingUpdate = info;
            UpdateChecks();
            ShowOsd(L.F("新しいバージョン {0} があります（クリックで詳細）", info.Version), 8000,
                () => PromptForUpdate(info));
        }
        finally
        {
            _updateCheckInFlight = false;
            if (!IsDisposed)
            {
                _miCheckUpdate.Enabled = true;
                Cursor = Cursors.Default;
            }
        }
    }

    /// <summary>
    /// Ask the user what to do about <paramref name="info"/> and act on the
    /// answer. Shared by the manual "check for updates" menu item and the
    /// clickable OSD the silent startup check raises — both just need to
    /// arrive here with an <see cref="UpdateInfo"/> in hand.
    /// </summary>
    private void PromptForUpdate(UpdateInfo info)
    {
        UpdatePrompt choice = AskAboutUpdate(info);
        if (choice == UpdatePrompt.Skip)
        {
            _settings.SkippedUpdateVersion = info.Version.ToString();
            SaveSettings();
            // The user just resolved this one — nothing left to relabel the
            // help menu item for.
            _pendingUpdate = null;
            UpdateChecks();
            ShowOsd(L.F("{0} をスキップします", info.Version), OsdLongMilliseconds);
            return;
        }
        if (choice != UpdatePrompt.Now) return;

        // Under Program Files the swap cannot work; say so instead of failing
        // halfway through.
        if (!Updater.CanWriteToInstallDir())
        {
            if (MessageBox.Show(this,
                    L.T("インストール先に書き込めないため、自動更新できません。\nリリースページを開きますか？"),
                    "YuCap", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                OpenUrl(info.PageUrl);
            return;
        }

        DownloadAndApply(info);
    }

    private enum UpdatePrompt { Now, Later, Skip }

    /// <summary>
    /// Offer the update with three answers rather than yes/no: "later" keeps
    /// being asked, "skip" silences this particular version for good. Without
    /// the third option a declined update would nag on every launch.
    /// </summary>
    private UpdatePrompt AskAboutUpdate(UpdateInfo info)
    {
        using var dlg = Ui.NewDialog(L.T("更新があります"), 420, 200);

        var head = Ui.Label(
            L.F("新しいバージョン {0} があります（現在 {1}）。", info.Version, Updater.CurrentVersion), 16, 20);
        head.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        var body = Ui.Hint(L.T("更新すると YuCap は自動的に再起動します。"), 16, 48, 420 - Ui.Pad * 2);
        var size = Ui.Hint(
            L.F("ダウンロードサイズ: 約 {0} MB", (info.Size / 1024.0 / 1024.0).ToString("0.0")),
            16, 72, 420 - Ui.Pad * 2);
        var notes = new LinkLabel
        {
            Text = L.T("リリースノートを見る"),
            AutoSize = true,
            Location = new Point(16, 98),
        };
        notes.LinkClicked += (_, _) => OpenUrl(info.PageUrl);

        // Right-aligned as a group, in reading order 今すぐ更新 / 後で / この版をスキップ.
        // Widths are sized to fit each label rather than sharing Ui.ButtonWidth.
        int y = 200 - Ui.Pad - Ui.ButtonHeight;
        var skip = Ui.Button(L.T("この版をスキップ"), 420 - Ui.Pad - 146, y, 146);
        var later = Ui.Button(L.T("後で"), skip.Left - Ui.Gap - 100, y, 100);
        var now = Ui.Button(L.T("今すぐ更新"), later.Left - Ui.Gap - 120, y, 120);

        var result = UpdatePrompt.Later;
        now.Click += (_, _) => { result = UpdatePrompt.Now; dlg.Close(); };
        later.Click += (_, _) => { result = UpdatePrompt.Later; dlg.Close(); };
        skip.Click += (_, _) => { result = UpdatePrompt.Skip; dlg.Close(); };

        dlg.Controls.AddRange(new Control[] { head, body, size, notes, now, later, skip });
        dlg.AcceptButton = now;
        dlg.CancelButton = later;
        dlg.ShowDialog(this);
        head.Font.Dispose();
        return result;
    }

    /// <summary>Synchronous by design: the progress dialog's own modal loop
    /// drives the download, so there is nothing here to await.</summary>
    private void DownloadAndApply(UpdateInfo info)
    {
        using var dlg = Ui.NewDialog(L.T("更新をダウンロード中"), 380, 120);
        dlg.ControlBox = false;   // only the Cancel button may close this while a download is in flight

        var lbl = Ui.Label(L.F("{0} をダウンロードしています...", info.AssetName), 16, 18);
        var bar = new ProgressBar { Location = new Point(16, 46), Size = new Size(348, 22), Maximum = 100 };
        var cancelBtn = Ui.Button(L.T("キャンセル"), 380 - Ui.Pad - 90, 120 - Ui.Pad - Ui.ButtonHeight, 90);
        var cts = new CancellationTokenSource();
        cancelBtn.Click += (_, _) => { cts.Cancel(); dlg.Close(); };
        dlg.Controls.AddRange(new Control[] { lbl, bar, cancelBtn });

        string? file = null;
        Exception? failure = null;
        var progress = new Progress<int>(p => { if (!dlg.IsDisposed) bar.Value = Math.Clamp(p, 0, 100); });

        dlg.Shown += async (_, _) =>
        {
            try { file = await Updater.DownloadAsync(info, progress, cts.Token); }
            catch (OperationCanceledException) { /* user cancelled */ }
            catch (Exception ex) { failure = ex; }
            finally { if (!dlg.IsDisposed) dlg.Close(); }
        };
        dlg.ShowDialog(this);
        cts.Dispose();

        if (failure != null)
        {
            MessageBox.Show(this, L.F("更新のダウンロードに失敗しました。\n\n{0}", Errors.Describe(failure)),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (file == null) return;   // cancelled

        try
        {
            // Save settings and release devices before the swap: the new process
            // starts immediately and would otherwise fight over the capture card.
            SaveSettings();
            try { _video.Dispose(); } catch { /* ignore */ }
            try { _audio.Dispose(); } catch { /* ignore */ }

            Updater.Apply(file);      // rolls back internally if the swap fails
            _skipSaveOnClose = true;  // settings already written above
            Close();
        }
        catch (Exception ex)
        {
            Log.Info("update apply failed: " + ex.Message);
            MessageBox.Show(this,
                L.F("更新の適用に失敗しました。元の状態に戻しました。\n\n{0}", Errors.Describe(ex)),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenUrl(string url)
    {
        // `url` comes straight from the GitHub API response (html_url); refuse
        // anything that isn't a plain web link before handing it to
        // ShellExecute, which would otherwise happily launch any registered
        // scheme handler (file://, a custom protocol, etc.).
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Info("open url refused (not http/https): " + url);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info("open url failed: " + ex.Message); }
    }
}
