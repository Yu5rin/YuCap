using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>A release newer than the running build.</summary>
internal sealed record UpdateInfo(
    Version Version,
    string TagName,
    string DownloadUrl,
    string AssetName,
    long Size,
    string? Sha256,
    string PageUrl);

/// <summary>Outcome of an update check. Info != null means a newer release
/// exists; Error != null means the check could not be completed; both null
/// means the running build is current. When <see cref="NewerVersion"/> is set (Error is then also set, with a readable
/// sentence) a newer release exists but it carries no usable exe, so the user must get it from <see cref="PageUrl"/>.</summary>
internal sealed record UpdateCheckResult(UpdateInfo? Info, string? Error, Version? NewerVersion = null, string? PageUrl = null);

/// <summary>What <see cref="Updater.Apply"/> did. A failed swap throws instead (after rolling back).</summary>
internal enum ApplyResult
{
    /// <summary>The new exe is in place and was started (relaunch requested): the caller exits.</summary>
    Relaunched,
    /// <summary>The new exe is in place; no relaunch was requested.</summary>
    AppliedNoRelaunch,
    /// <summary>The new exe is in place but starting it failed: the update IS installed (it runs at the next launch), so the
    /// caller must not report a rollback. <see cref="Updater.LastRelaunchError"/> has the reason.</summary>
    AppliedRelaunchFailed,
}

/// <summary>
/// Checks GitHub Releases for a newer build, downloads it, and replaces the
/// running executable.
///
/// Replacing a running exe is only possible because Windows allows a locked
/// file to be RENAMED even though it cannot be overwritten: the old exe is
/// moved aside, the new one takes its place, and the new process is started
/// while the old one exits. Any failure rolls the rename back, so a half-applied
/// update cannot leave the app unusable.
/// </summary>
internal static class Updater
{
    /// <summary>Marker suffix for the displaced executable, deleted next launch.</summary>
    public const string OldSuffix = ".old";

    /// <summary>Argument the freshly-started build receives so it can wait for
    /// the previous process to exit before taking the single-instance mutex.</summary>
    public const string PostUpdateArg = "--post-update";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // A manual check blocks the user's menu action; a couple of retries at
        // 30s each would read as a hang rather than "checking".
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // GitHub rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("YuCap-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// Hosts allowed for both the release-check endpoint and the asset download
    /// URL. The release response carries the SHA256 we verify the download
    /// against, so if an arbitrary host could serve that response, it could
    /// supply a malicious exe and a matching hash in the same breath — the
    /// verification would prove nothing. Restricting both to GitHub's own
    /// hosts keeps the hash check meaningful.
    /// </summary>
    private static bool IsTrustedHost(Uri uri)
    {
        string host = uri.Host;
        return host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Version of the running build, ignoring any build metadata.</summary>
    public static Version CurrentVersion
    {
        get
        {
            string v = Application.ProductVersion;
            int plus = v.IndexOf('+');
            if (plus > 0) v = v[..plus];
            return Version.TryParse(v, out Version? parsed) ? parsed : new Version(0, 0, 0);
        }
    }

    /// <summary>
    /// Ask GitHub for the latest release. A null Info with a null Error means
    /// the running build is current; a null Info with a non-null Error means
    /// the check itself could not be completed (offline, rate-limited, refused
    /// endpoint) — the two must not be conflated, or an offline user gets told
    /// "no updates" when the truth is "couldn't tell".
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(string apiUrl, CancellationToken ct = default)
    {
        // The endpoint is user-editable in settings.json; refuse anything that
        // would send the check (and the download link it returns) in the clear,
        // or to a host other than GitHub's own (see IsTrustedHost).
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Info($"update: refusing non-HTTPS endpoint '{apiUrl}'");
            return new UpdateCheckResult(null, L.T("更新の確認先が不正です。"));
        }
        if (!IsTrustedHost(uri))
        {
            Log.Info($"update: refusing untrusted endpoint host '{uri.Host}'");
            return new UpdateCheckResult(null, L.T("更新の確認先が不正です。"));
        }

        try
        {
            string json = await Http.GetStringAsync(uri, ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            if (!TryParseTag(tag, out Version? parsedTag) || parsedTag == null)
                return new UpdateCheckResult(null, L.T("最新リリースの情報を解釈できませんでした。"));
            Version latest = parsedTag;

            // Numeric comparison — a string compare would rank 1.0.10 below 1.0.9.
            if (latest <= CurrentVersion)
            {
                Log.Info($"update: up to date (current {CurrentVersion}, latest {latest})");
                return new UpdateCheckResult(null, null);
            }

            string page = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";

            // Pick the .exe asset.
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                Log.Info("update: release has no assets array");
                return NewerWithoutFile(latest, page);
            }
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                string url = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url)) continue;
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info("update: asset URL is not HTTPS — ignoring");
                    continue;
                }
                // Same trust requirement as the check endpoint: the SHA256 we
                // verify against comes from this same response, so an asset
                // hosted anywhere else would make that verification meaningless.
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? assetUri) || !IsTrustedHost(assetUri))
                {
                    Log.Info($"update: asset URL host not trusted — ignoring ({url})");
                    continue;
                }
                long size = a.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0;

                // GitHub exposes "sha256:<hex>" on newer releases; otherwise fall
                // back to a "sha256: <hex>" line in the release notes.
                string? sha = null;
                if (a.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String)
                {
                    string? raw = d.GetString();
                    if (raw != null && raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = raw[7..].Trim();
                }
                sha ??= FindSha256InNotes(body);

                Log.Info($"update: {CurrentVersion} → {latest} ({name}, {size} bytes, sha256={(sha == null ? "none" : "yes")})");
                return new UpdateCheckResult(new UpdateInfo(latest, tag, url, name, size, sha, page), null);
            }
            Log.Info("update: no usable .exe asset found on latest release");
            return NewerWithoutFile(latest, page);
        }
        catch (Exception ex)
        {
            Log.Info($"update check failed: {ex.GetType().Name} {ex.Message}");
            return new UpdateCheckResult(null, Errors.Describe(ex));
        }
    }

    /// <summary>A newer release exists but has no exe we can install (not published yet, or the files are not trusted):
    /// say so instead of "up to date".</summary>
    private static UpdateCheckResult NewerWithoutFile(Version latest, string page) =>
        new(null, L.F("新しいバージョン {0} がありますが、自動で更新できるファイルが見つかりません。リリースページから入手してください。", latest),
            latest, string.IsNullOrEmpty(page) ? null : page);

    private static bool TryParseTag(string tag, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string s = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out version);
    }

    private static string? FindSha256InNotes(string body)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(body, @"\b([0-9a-fA-F]{64})\b"))
        {
            return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Folder the downloaded build is staged in before being applied.
    /// Under LocalAppData rather than %TEMP%: cleanup tools can empty %TEMP%
    /// between the download and the exit that applies it.</summary>
    private static string StagingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuCap", "update");

    private static string StagedPath(string version) => Path.Combine(StagingDir, $"YuCap-{version}.exe");

    /// <summary>Download in the background and verify the SHA256, leaving the
    /// file staged. Only ever used for unattended installs, so a published
    /// hash is mandatory — installing without verification is not acceptable.
    /// Throws on failure and discards the partial file.</summary>
    public static Task<string> StageAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (info.Sha256 == null)
            throw new InvalidOperationException("staging requires a published SHA256");
        return DownloadCoreAsync(info, null, ct);
    }

    /// <summary>True when the staged file for <paramref name="version"/> exists
    /// and still matches <paramref name="sha256"/>. It sat on disk between the
    /// download and now, so this is re-checked before every swap. A mismatch or
    /// missing file discards the staging.</summary>
    public static bool VerifyStaged(string version, string sha256, out string path)
    {
        path = StagedPath(version);
        try
        {
            if (!File.Exists(path))
            {
                Log.Info("update: staged file missing");
                DiscardStaging();
                return false;
            }
            string actual = ComputeSha256(path);
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"update: staged file SHA256 mismatch (expected {sha256}, got {actual}) — discarding");
                DiscardStaging();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("update: staged verify failed — " + ex.Message);
            DiscardStaging();
            return false;
        }
    }

    /// <summary>Download the asset and verify it (when a hash is published).
    /// Throws on failure.</summary>
    public static Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress,
        CancellationToken ct = default)
    {
        // The downloaded exe replaces the running program: without a published hash nothing proves it is the release.
        if (info.Sha256 == null)
        {
            Log.Info("update: refusing to install a download without a published SHA256");
            throw new UserFacingException(L.T("このリリースには検証用のハッシュ（SHA256）がないため、アプリ内では更新できません。リリースページから手動で更新してください。"));
        }
        return DownloadCoreAsync(info, progress, ct);
    }

    private static async Task<string> DownloadCoreAsync(UpdateInfo info, IProgress<int>? progress,
        CancellationToken ct)
    {
        string dir = StagingDir;
        Directory.CreateDirectory(dir);
        string path = StagedPath(info.Version.ToString());

        try
        {
            using HttpResponseMessage resp = await Http
                .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;

            await using Stream net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }
        catch
        {
            // Cancelled or failed: don't leave a partial download behind.
            DiscardStaging();
            throw;
        }

        string actual = ComputeSha256(path);
        if (info.Sha256 == null || !actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            DiscardStaging();
            Log.Info($"update: SHA256 mismatch (expected {info.Sha256}, got {actual})");
            throw new UserFacingException(L.T("ダウンロードしたファイルの検証に失敗しました。"));
        }
        Log.Info("update: SHA256 verified");
        return path;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    /// <summary>Path of the running executable, or null if it cannot be determined.</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>
    /// True when the install directory is writable. Under Program Files it is
    /// not, and the update has to be done by hand.
    /// </summary>
    public static bool CanWriteToInstallDir()
    {
        try
        {
            string? dir = Path.GetDirectoryName(ExePath);
            if (dir == null) return false;
            string probe = Path.Combine(dir, $".yucap-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>True once an exe swap succeeded in this process, so the exit
    /// path does not try to apply again.</summary>
    public static bool AppliedThisSession { get; private set; }

    /// <summary>Why the last <see cref="Apply"/> could not start the new build (<see cref="ApplyResult.AppliedRelaunchFailed"/>).</summary>
    public static string? LastRelaunchError { get; private set; }

    /// <summary>Run <paramref name="action"/>, trying again a few times: antivirus and the search indexer often hold a freshly
    /// written exe for a moment.</summary>
    private static void Retry(string what, Action action, int tries = 5, int delayMs = 300)
    {
        for (int i = 1; ; i++)
        {
            try { action(); return; }
            catch (Exception ex) when (i < tries && ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"update: {what} failed ({ex.Message.Split('\n')[0]}) - retry {i}/{tries - 1}");
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>
    /// Swap in the downloaded build. With <paramref name="relaunch"/> the new
    /// build is started and the caller is expected to exit; without it the
    /// swap is done silently (the exit-time apply) and nothing is started.
    ///
    /// Order, so that a failure at any point leaves a working install: (1) copy the new exe next to the current one as
    /// YuCap.exe.new and verify its SHA256 (<paramref name="expectedSha256"/>, else the source file's own); (2) rename current
    /// to .old; (3) rename .new to current - two renames, each atomic on one volume. If (3) fails, .old is moved back over
    /// the current name. A failed swap throws (after rolling back); a swap that worked but whose relaunch failed is
    /// reported as <see cref="ApplyResult.AppliedRelaunchFailed"/>, not as an exception.
    /// </summary>
    public static ApplyResult Apply(string downloadedExe, bool relaunch = true, string? expectedSha256 = null)
    {
        string? cur = ExePath;
        if (cur == null) throw new UserFacingException(L.T("実行ファイルの場所を特定できません。"));
        string old = cur + OldSuffix, fresh = cur + ".new";

        // (1) stage next to the target and verify the copy
        try
        {
            Retry("copy", () => File.Copy(downloadedExe, fresh, overwrite: true));
            string want = expectedSha256 ?? ComputeSha256(downloadedExe);
            string got = ComputeSha256(fresh);
            if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"update: copy verification failed (expected {want}, got {got})");
                throw new UserFacingException(L.T("ダウンロードしたファイルの検証に失敗しました。"));
            }
        }
        catch
        {
            try { File.Delete(fresh); } catch { /* ignore */ }
            throw;
        }

        // A leftover from a previous update would block the rename.
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch (Exception ex) { Log.Info("update: cannot remove previous .old — " + ex.Message); }
        }

        // (2) + (3)
        Log.Info("update: renaming current exe aside");
        try { Retry("rename aside", () => File.Move(cur, old, overwrite: true)); }
        catch { try { File.Delete(fresh); } catch { /* ignore */ } throw; }   // nothing changed yet
        try
        {
            Retry("rename into place", () => File.Move(fresh, cur));
            Log.Info("update: new exe in place");
        }
        catch (Exception ex)
        {
            Log.Info("update: swap failed, rolling back — " + ex.Message);
            try { Retry("rollback", () => File.Move(old, cur, overwrite: true)); }
            catch (Exception rb) { Log.Info("update: ROLLBACK FAILED — " + rb.Message); }
            try { File.Delete(fresh); } catch { /* ignore */ }
            throw;
        }

        AppliedThisSession = true;
        LastRelaunchError = null;
        if (!relaunch)
        {
            Log.Info("update: applied on exit, no relaunch");
            return ApplyResult.AppliedNoRelaunch;
        }

        // Hand the new process our id so it can wait for us to exit before
        // taking the single-instance mutex.
        try
        {
            Process.Start(new ProcessStartInfo(cur, $"{PostUpdateArg} {Environment.ProcessId}")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(cur)!,
            });
        }
        catch (Exception ex)
        {
            // The swap is done: the next launch runs the new build. Do not claim a rollback that did not happen.
            LastRelaunchError = ex.Message;
            Log.Info("update: installed, but starting the new build failed — " + ex.Message);
            return ApplyResult.AppliedRelaunchFailed;
        }
        Log.Info("update: relaunched, exiting");
        return ApplyResult.Relaunched;
    }

    /// <summary>Delete the staged download. The applying process cannot do this
    /// itself — it exits immediately — so Program clears it at the next startup
    /// once the staged version is no longer newer than the running one.</summary>
    public static void DiscardStaging()
    {
        try
        {
            if (Directory.Exists(StagingDir))
            {
                Directory.Delete(StagingDir, recursive: true);
                Log.Info("update: removed staged download");
            }
        }
        catch (Exception ex) { Log.Info("update: could not remove staging — " + ex.Message); }
    }

    /// <summary>Seconds this build must have kept running before the displaced exe of the previous version is deleted: until then
    /// it is the way back if the new build turns out not to start.</summary>
    private const int KeepOldSeconds = 60;
    private static System.Threading.Timer? _oldTimer;

    /// <summary>Remove leftovers of an update: a half-copied .new now, and the displaced .old once this build has run for
    /// <see cref="KeepOldSeconds"/> (so a new build that dies at start-up does not take its predecessor with it). Staging
    /// is deliberately left alone: it may hold an update waiting to be applied, and Program decides whether to keep it.</summary>
    public static void CleanupOld()
    {
        try
        {
            string? cur = ExePath;
            if (cur == null) return;
            string fresh = cur + ".new";
            if (File.Exists(fresh)) { try { File.Delete(fresh); } catch { /* still locked */ } }
            string old = cur + OldSuffix;
            if (!File.Exists(old)) return;
            Log.Info($"update: keeping {Path.GetFileName(old)} until this build has run for {KeepOldSeconds}s");
            _oldTimer?.Dispose();
            _oldTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (File.Exists(old)) { File.Delete(old); Log.Info("update: removed " + Path.GetFileName(old)); }
                }
                catch { /* still locked; next launch will get it */ }
            }, null, KeepOldSeconds * 1000, Timeout.Infinite);
        }
        catch { /* next launch will get it */ }
    }

    /// <summary>Wait for the superseded process to exit so the mutex is free.</summary>
    public static void WaitForPreviousExit(int pid, int timeoutMs = 10000)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            if (!p.WaitForExit(timeoutMs))
                Log.Info($"update: previous process {pid} still running after {timeoutMs}ms");
        }
        catch (ArgumentException) { /* already gone — the normal case */ }
        catch (Exception ex) { Log.Info("update: wait failed — " + ex.Message); }
    }
}
