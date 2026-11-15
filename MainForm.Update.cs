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
            Log.Info("first run: menu on right-click, hotkey list on F1");   // no OSD: only manual actions show one
            return;
        }

        Log.Info($"update: now running {current}");
        // Long duration: this is the one confirmation that the restart the
        // user just experienced was an update, not a crash — it must survive
        // long enough to actually be read.
        // Kept on purpose (the user asked for it): the one automatic OSD that is not an error.
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
        // Turning updates off must also drop anything already staged: it would
        // otherwise be applied on exit against the user's wish.
        if (!_settings.UpdateCheckOnStartup)
        {
            ClearStagedUpdate();
            _pendingUpdate = null;
            UpdateChecks();
        }
        ShowOsd(_settings.UpdateCheckOnStartup
            ? L.T("自動更新: オン")
            : L.T("自動更新: オフ"));
    }

    /// <summary>True once a verified update is staged for <see cref="_pendingUpdate"/>.
    /// The help menu item reads this to offer "restart to update".</summary>
    private bool _updateStaged;

    /// <summary>Discard the staged download and forget it in settings.</summary>
    private void ClearStagedUpdate()
    {
        Updater.DiscardStaging();
        _settings.StagedUpdateVersion = null;
        _settings.StagedUpdateSha256 = null;
        _updateStaged = false;
        SaveSettings();
    }

    /// <summary>The staged file for <paramref name="info"/>, re-verified against
    /// the recorded SHA256 (it sat on disk since the download), or null.</summary>
    private string? GetVerifiedStagedFile(UpdateInfo info)
    {
        string ver = info.Version.ToString();
        if (_settings.StagedUpdateVersion != ver || _settings.StagedUpdateSha256 == null) return null;
        return Updater.VerifyStaged(ver, _settings.StagedUpdateSha256, out string path) ? path : null;
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
    /// from the check at every start that the user can switch off.
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
                if (manual && result.NewerVersion != null)
                {
                    // Not a network failure: a newer release exists but has no file that can be installed from here.
                    if (Ui.Message(this, L.F("{0}\n\nリリースページを開きますか？", result.Error),
                            MsgKind.Warning, MessageBoxButtons.YesNo) == DialogResult.Yes
                        && result.PageUrl != null)
                        OpenUrl(result.PageUrl);
                }
                else if (manual)
                {
                    Ui.Message(this,
                        L.F("更新の確認に失敗しました。\nネットワーク接続を確認してください。\n\n{0}", result.Error),
                        MsgKind.Warning);
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
                    Ui.Message(this,
                        L.F("現在のバージョンは {0} です。\n更新はありません。", Updater.CurrentVersion),
                        MsgKind.Info);
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
                // the user just asked for this. Already staged: offer the restart.
                if (GetVerifiedStagedFile(info) != null) PromptRestartNow(info);
                else PromptForUpdate(info);
                return;
            }

            // Fully automatic path. Safety rules:
            //  - only stage unattended when the release publishes a SHA256 AND the
            //    install dir is writable; installing without hash verification is
            //    not acceptable;
            //  - the staged file is re-verified right before every swap;
            //  - nothing is applied when the setting is off or the version skipped.
            // Otherwise fall through to the old clickable-OSD behaviour.
            if (info.Sha256 != null && Updater.CanWriteToInstallDir())
            {
                string ver = info.Version.ToString();
                if (GetVerifiedStagedFile(info) == null)
                {
                    // A different (older) staged build is superseded.
                    if (_settings.StagedUpdateVersion != ver) ClearStagedUpdate();
                    try { await Updater.StageAsync(info); }
                    catch (Exception ex)
                    {
                        // Silent: the next launch retries.
                        Log.Info("update: background download failed — " + ex.Message);
                        return;
                    }
                    if (IsDisposed) return;
                    if (!_settings.UpdateCheckOnStartup)
                    {
                        // Switched off while downloading.
                        Updater.DiscardStaging();
                        return;
                    }
                }
                _settings.StagedUpdateVersion = ver;
                _settings.StagedUpdateSha256 = info.Sha256;
                SaveSettings();
                _pendingUpdate = info;
                _updateStaged = true;
                UpdateChecks();
                ShowOsd(L.F("v{0} の準備ができました。YuCap を閉じると更新されます（クリックで今すぐ更新）", ver),
                    8000, () => PromptRestartNow(info));
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
            ClearStagedUpdate();   // also saves settings
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
            if (Ui.Message(this,
                    L.T("インストール先に書き込めないため、自動更新できません。\nリリースページを開きますか？"),
                    MsgKind.Warning, MessageBoxButtons.YesNo) == DialogResult.Yes)
                OpenUrl(info.PageUrl);
            return;
        }

        // Already downloaded and verified in the background: don't fetch again.
        string? staged = GetVerifiedStagedFile(info);
        if (staged != null) ApplyAndClose(staged, info.Sha256);
        else DownloadAndApply(info);
    }

    /// <summary>
    /// "Restart now?" for an update that is already staged. Re-verifies first;
    /// on mismatch the staging is dropped and the normal prompt is used.
    /// "No" leaves it staged — it is applied when YuCap is closed.
    /// </summary>
    private void PromptRestartNow(UpdateInfo info)
    {
        string? staged = Updater.CanWriteToInstallDir() ? GetVerifiedStagedFile(info) : null;
        if (staged == null)
        {
            ClearStagedUpdate();
            UpdateChecks();
            PromptForUpdate(info);
            return;
        }
        if (Ui.Message(this,
                L.F("今すぐ YuCap を再起動して v{0} に更新しますか？\n\n「いいえ」を選んだ場合は、YuCap を閉じたときに更新されます。",
                    info.Version),
                MsgKind.Question, MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        ApplyAndClose(staged, info.Sha256);
    }

    private enum UpdatePrompt { Now, Later, Skip }

    /// <summary>
    /// Offer the update with three answers rather than yes/no: "later" keeps
    /// being asked, "skip" silences this particular version for good. Without
    /// the third option a declined update would nag on every launch.
    /// </summary>
    private UpdatePrompt AskAboutUpdate(UpdateInfo info)
    {
        using var dlg = Ui.NewDialog(L.T("更新があります"), 420);

        var stack = Ui.Stack();
        stack.Add(Ui.Heading(L.F("新しいバージョン {0} があります（現在 {1}）。", info.Version, Updater.CurrentVersion)));
        stack.Add(Ui.Text(L.T("更新すると YuCap は自動的に再起動します。")), before: Ui.Gap + 4);
        stack.Add(Ui.Hint(L.F("ダウンロードサイズ: 約 {0} MB", (info.Size / 1024.0 / 1024.0).ToString("0.0"))), before: 4);
        stack.Add(Ui.Link(L.T("リリースノートを見る"), () => OpenUrl(info.PageUrl)), before: Ui.Gap + 4);

        // 今すぐ更新 is the primary action, 後で next to it (Esc); この版をスキップ apart at the left.
        var now = Ui.Btn(L.T("今すぐ更新"));
        var later = Ui.Btn(L.T("後で"));
        var skip = Ui.Btn(L.T("この版をスキップ"));

        var result = UpdatePrompt.Later;
        now.Click += (_, _) => { result = UpdatePrompt.Now; dlg.Close(); };
        later.Click += (_, _) => { result = UpdatePrompt.Later; dlg.Close(); };
        skip.Click += (_, _) => { result = UpdatePrompt.Skip; dlg.Close(); };

        Ui.Frame(dlg, stack, new Button[] { now, later }, left: new Button[] { skip });
        dlg.AcceptButton = now;
        dlg.CancelButton = later;
        dlg.InitialFocus = now;
        dlg.ShowDialog(this);
        return result;
    }

    /// <summary>Synchronous by design: the progress dialog's own modal loop
    /// drives the download, so there is nothing here to await.</summary>
    private void DownloadAndApply(UpdateInfo info)
    {
        using var dlg = Ui.NewDialog(L.T("更新をダウンロード中"), 400);
        dlg.ControlBox = false;   // only the Cancel button may close this while a download is in flight

        var stack = Ui.Stack();
        stack.Add(Ui.Text(L.F("{0} をダウンロードしています...", info.AssetName)));
        var bar = stack.Add(new UiProgress { Maximum = 100 }, before: Ui.Gap + 4);
        var cancelBtn = Ui.Btn(L.T("キャンセル"));
        var cts = new CancellationTokenSource();
        cancelBtn.Click += (_, _) => { cts.Cancel(); dlg.Close(); };
        Ui.Frame(dlg, stack, new Button[] { cancelBtn });
        cancelBtn.Primary = false;
        dlg.AcceptButton = null;
        dlg.CancelButton = cancelBtn;

        string? file = null;
        Exception? failure = null;
        var progress = new Progress<int>(p => { if (!dlg.IsDisposed) bar.Value = Math.Clamp(p, 0, 100); });

        bool finished = false;
        dlg.Shown += async (_, _) =>
        {
            try { file = await Updater.DownloadAsync(info, progress, cts.Token); }
            catch (OperationCanceledException) { /* user cancelled */ }
            catch (Exception ex) { failure = ex; }
            finally { finished = true; if (!dlg.IsDisposed) dlg.Close(); }
        };
        // Alt+F4 closes the dialog even without a control box: treat any close before the end as a cancel, so the download
        // does not carry on (and finish into the staging folder) behind a dialog that is already gone.
        dlg.FormClosing += (_, _) => { if (!finished) cts.Cancel(); };
        dlg.ShowDialog(this);
        // cts is not disposed here: a cancelled download may still be unwinding and observing its token.
        if (!finished) file = null;

        if (failure != null)
        {
            Ui.Message(this, L.F("更新のダウンロードに失敗しました。\n\n{0}", Errors.Describe(failure)),
                MsgKind.Error);
            return;
        }
        if (file == null) return;   // cancelled

        ApplyAndClose(file, info.Sha256);
    }

    /// <summary>Swap in <paramref name="file"/>, relaunch, and close this window.</summary>
    private void ApplyAndClose(string file, string? sha256 = null)
    {
        // Ask about a running training BEFORE anything is swapped: once the new build has been started, "keep running" can
        // only leave a half-updated session (the old build running from the renamed .old file, the new one giving up).
        if (!ConfirmCloseDuringTraining()) return;
        _trainCloseConfirmed = true;
        try
        {
            // Settings first (the new process reads them), but the video and audio keep running until the swap has
            // really worked: the new process waits for this one to exit before it opens any device, so there is
            // nothing to release early - and a failed swap must leave the user with a working picture.
            SaveSettings();

            ApplyResult result = Updater.Apply(file, relaunch: true, expectedSha256: sha256);   // a failed swap rolls back and throws
            if (result == ApplyResult.AppliedRelaunchFailed)
            {
                // The new build IS installed (it runs at the next launch) but could not be started from here: no rollback
                // happened, so do not claim one. This window keeps running with its devices.
                Ui.Message(this,
                    L.F("更新ファイルの入れ替えは終わりましたが、新しい版を起動できませんでした。YuCap をいったん閉じて、もう一度起動してください。\n\n{0}",
                        Updater.LastRelaunchError ?? "?"),
                    MsgKind.Warning);
                return;
            }
            _skipSaveOnClose = true;  // settings already written above
            Close();                  // OnFormClosing releases the devices
            if (!IsDisposed)
            {
                // The close was cancelled after all (it cannot be by the training question any more, but a test run or a
                // future close guard can): this window keeps running, so it must keep saving its settings.
                Log.Info("update: the close after the swap was cancelled - this window keeps running");
                _skipSaveOnClose = false;
            }
        }
        catch (Exception ex)
        {
            // The swap failed and was rolled back: the running build is untouched and its video and audio were never stopped.
            Log.Info("update apply failed: " + ex.Message);
            Ui.Message(this,
                L.F("更新の適用に失敗しました。元の状態に戻しました。\n\n{0}", Errors.Describe(ex)),
                MsgKind.Error);
        }
        finally
        {
            // Still open (failed swap, relaunch failure, cancelled close): a later close must ask about training again.
            if (!IsDisposed) _trainCloseConfirmed = false;
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
