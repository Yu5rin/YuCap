using System;
using System.IO;
using System.Text.Json;

namespace YuCap;

/// <summary>
/// Persisted settings stored under %APPDATA%\YuCap\settings.json. Remembers the
/// capture mode, view options, volume, and window placement across launches.
/// </summary>
public sealed class AppSettings
{
    // Capture mode (null members => auto).
    public string? ModeCompression { get; set; }
    public int? ModeWidth { get; set; }
    public int? ModeHeight { get; set; }
    public int? ModeFps { get; set; }

    // View / window options.
    public int DisplayMode { get; set; }          // VideoDisplayMode
    public bool LockAspect { get; set; } = true;
    public bool Borderless { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool MenuVisible { get; set; } = true;
    public bool StatusVisible { get; set; } = true;
    public int Volume { get; set; } = 100;
    public int AudioBufferMs { get; set; } = 120;

    // Snapshot output.
    public string? SnapshotDir { get; set; }      // null => Pictures\CaptureViewer
    public string SnapshotFormat { get; set; } = "png"; // "png" | "jpg"

    // Video orientation (for camera-style sources).
    public int Rotation { get; set; }             // 0 / 90 / 180 / 270
    public bool Mirror { get; set; }

    // Session state restored across launches.
    public bool Fullscreen { get; set; }
    public bool Pip { get; set; }

    // Picture-in-picture.
    public int PipOpacity { get; set; } = 80;      // idle opacity, 0..100 (%)
    public int PipOpacityHover { get; set; } = 100; // opacity while the mouse is over the window
    public bool PipClickThrough { get; set; }
    public int PipSizePct { get; set; } = 25;      // window size as % of the source resolution
    public int PipCorner { get; set; }             // 0=右下 1=左下 2=右上 3=左上

    // Update check. The endpoint lives in the settings file rather than in code
    // so it can be repointed without a rebuild — and so the user can see where
    // the app talks to.
    public bool UpdateCheckOnStartup { get; set; } = true;
    public string UpdateApiUrl { get; set; } = "https://api.github.com/repos/Yu5rin/YuCap/releases/latest";
    /// <summary>Recorded for diagnostics only — the startup check is not throttled.</summary>
    public string? LastUpdateCheckUtc { get; set; }
    /// <summary>A version the user declined; not offered again until a newer one appears.</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>Version that produced the "what's new" notice, so it shows once.</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>Hotkey combos already reported as conflicting, to warn only once.</summary>
    public string? HotkeyConflictNotified { get; set; }

    // Behavior.
    public bool CursorAutoHide { get; set; } = true;
    public int CursorHideSeconds { get; set; } = 3;
    public bool GlobalHotkeys { get; set; } = true;
    // Hotkey combos stored as System.Windows.Forms.Keys values
    // (0x20000 = Ctrl, 0x40000 = Alt, 0x10000 = Shift, low bits = key). 0 = disabled.
    public int HotkeySnapshot { get; set; } = 0x20000 | 0x40000 | 0x53; // Ctrl+Alt+S
    public int HotkeyMute { get; set; } = 0x20000 | 0x40000 | 0x4D;     // Ctrl+Alt+M
    public int HotkeyPip { get; set; } = 0x20000 | 0x40000 | 0x50;      // Ctrl+Alt+P
    public string DeviceKeyword { get; set; } = "JVA14";
    public string Language { get; set; } = "ja";  // "ja" | "en"

    // Window placement.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowW { get; set; }
    public int? WindowH { get; set; }
    public bool WindowMaximized { get; set; }

    public CaptureMode? ToMode()
    {
        if (ModeCompression is { } c && ModeWidth is int w && ModeHeight is int h && ModeFps is int f)
            return new CaptureMode(c, w, h, f);
        return null;
    }

    public void SetMode(CaptureMode? mode)
    {
        ModeCompression = mode?.Compression;
        ModeWidth = mode?.Width;
        ModeHeight = mode?.Height;
        ModeFps = mode?.Fps;
    }
}

public static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YuCap", "settings.json");

    /// <summary>
    /// True when the last <see cref="Load"/> found a settings.json that exists but
    /// could not be read/parsed (as opposed to a first run with no file at all).
    /// Reflects only the most recent call.
    /// </summary>
    public static bool LastLoadFailed { get; private set; }

    public static AppSettings Load()
    {
        string path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                // First run — not a failure.
                LastLoadFailed = false;
                return new AppSettings();
            }
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            LastLoadFailed = false;
            return loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // The file exists but is unreadable/corrupt (e.g. truncated by a power
            // loss mid-write). Keep the evidence instead of silently discarding it,
            // so the user has something to hand us if they notice lost settings.
            LastLoadFailed = true;
            try
            {
                string bad = Path.Combine(Path.GetDirectoryName(path)!, "settings.bad.json");
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(path, bad);
                Log.Info($"settings: load failed ({ex.Message}); moved unreadable file to {bad}");
            }
            catch (Exception moveEx)
            {
                Log.Info($"settings: load failed ({ex.Message}); could not preserve bad file — {moveEx.Message}");
            }
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        string path = FilePath;
        string tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));

            // Swap the temp file in atomically so a crash/power loss mid-write
            // can never leave settings.json half-written. File.Replace (and plain
            // File.Move onto a nonexistent target) are atomic renames on NTFS.
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Log.Info("settings: save failed — " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
