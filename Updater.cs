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
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("YuCap-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
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
    /// Ask GitHub for the latest release. Returns null when the check fails or
    /// nothing newer exists — a failed check must never interrupt the user.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(string apiUrl, CancellationToken ct = default)
    {
        try
        {
            string json = await Http.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            if (!TryParseTag(tag, out Version? parsedTag) || parsedTag == null) return null;
            Version latest = parsedTag;

            // Numeric comparison — a string compare would rank 1.0.10 below 1.0.9.
            if (latest <= CurrentVersion)
            {
                Log.Info($"update: up to date (current {CurrentVersion}, latest {latest})");
                return null;
            }

            string page = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";

            // Pick the .exe asset.
            if (!root.TryGetProperty("assets", out JsonElement assets)) return null;
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                string url = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url)) continue;
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
                return new UpdateInfo(latest, tag, url, name, size, sha, page);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Info("update check failed: " + ex.Message);
            return null;   // offline, rate-limited, malformed — all just "couldn't check"
        }
    }

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

    /// <summary>Folder the downloaded build is staged in before being applied.</summary>
    private static string StagingDir => Path.Combine(Path.GetTempPath(), "YuCapUpdate");

    /// <summary>Download the asset to a temp file and verify it. Throws on failure.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress,
        CancellationToken ct = default)
    {
        string dir = StagingDir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, info.AssetName);

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

        if (info.Sha256 != null)
        {
            string actual = ComputeSha256(path);
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DiscardStaging();
                Log.Info($"update: SHA256 mismatch (expected {info.Sha256}, got {actual})");
                throw new InvalidDataException("ダウンロードしたファイルの検証に失敗しました。");
            }
            Log.Info("update: SHA256 verified");
        }
        else
        {
            Log.Info("update: no SHA256 published — skipping hash verification");
        }
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

    /// <summary>
    /// Swap in the downloaded build and restart. Does not return on success —
    /// the caller's process is replaced by the new one.
    /// </summary>
    public static void Apply(string downloadedExe)
    {
        string? cur = ExePath;
        if (cur == null) throw new InvalidOperationException("実行ファイルの場所を特定できません。");
        string old = cur + OldSuffix;

        // A leftover from a previous update would block the rename.
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch (Exception ex) { Log.Info("update: cannot remove previous .old — " + ex.Message); }
        }

        Log.Info("update: renaming current exe aside");
        File.Move(cur, old);            // permitted even though the file is running
        try
        {
            File.Copy(downloadedExe, cur);
            Log.Info("update: new exe in place");
        }
        catch (Exception ex)
        {
            Log.Info("update: copy failed, rolling back — " + ex.Message);
            try { if (File.Exists(cur)) File.Delete(cur); } catch { /* ignore */ }
            File.Move(old, cur);        // restore the working build
            throw;
        }

        // Hand the new process our id so it can wait for us to exit before
        // taking the single-instance mutex.
        Process.Start(new ProcessStartInfo(cur, $"{PostUpdateArg} {Environment.ProcessId}")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(cur)!,
        });
        Log.Info("update: relaunched, exiting");
    }

    /// <summary>Delete the staged download. The applying process cannot do this
    /// itself — it exits immediately after launching the new build — so the
    /// staged copy is cleared at the next startup instead.</summary>
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

    /// <summary>Remove what a previous update left behind: the displaced
    /// executable, and the downloaded copy staged in temp.</summary>
    public static void CleanupOld()
    {
        try
        {
            string? cur = ExePath;
            if (cur == null) return;
            string old = cur + OldSuffix;
            if (File.Exists(old))
            {
                File.Delete(old);
                Log.Info("update: removed " + Path.GetFileName(old));
            }
        }
        catch { /* still locked; next launch will get it */ }

        DiscardStaging();
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
