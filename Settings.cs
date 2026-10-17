using System;
using System.Collections.Generic;
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
    public int AudioBufferMs { get; set; } = AudioEngine.DefaultBufferMs;
    /// <summary>0 = written before the audio path was rebuilt (v1.9); the old defaults of the buffer (120 / 200 ms) then mean "never changed" and move to the new default once.</summary>
    public int AudioSettingsVersion { get; set; }

    // Snapshot output.
    public string? SnapshotDir { get; set; }      // null => Pictures\CaptureViewer
    public string SnapshotFormat { get; set; } = "png"; // "png" | "jpg"

    // Video orientation (for camera-style sources).
    public int Rotation { get; set; }             // 0 / 90 / 180 / 270
    public bool Mirror { get; set; }

    // Renderer. "mf" is the proven Media Foundation preview and stays the default;
    // "d3d" is our own Direct3D 11 swap chain, which is what makes FSR upscaling
    // possible. It is opt-in (オプション → 描画方式) until verified on a 4K monitor.
    public string Renderer { get; set; } = "mf";            // "mf" | "d3d"
    public string ScalingQuality { get; set; } = "fsr";     // "standard" | "fsr" (d3d only)
    public int FsrSharpness { get; set; } = 50;              // 0..100 (d3d + fsr only)
    public string NativeResScale { get; set; } = "off";      // "off" | "auto" | "480".."1440" (d3d only): shrink to the picture's real detail before scaling

    // Picture adjustments (Direct3D renderer only), in slider units; the defaults are neutral.
    public int AdjBrightness { get; set; }                   // -100..100 (0 = unchanged)
    public int AdjContrast { get; set; } = 100;              // 0..200 (100 = unchanged)
    public int AdjSaturation { get; set; } = 100;            // 0..200 (100 = unchanged)
    public int AdjGamma { get; set; } = 100;                 // 50..200, % of gamma 1.0 (100 = unchanged)

    // A/V sync offset in ms, one signed value: positive delays the audio, negative makes it
    // earlier (done by delaying the video, which only the Direct3D renderer can do).
    public int AvOffsetMs { get; set; }                      // -300..300 in steps of 10

    // Black-border crop (Direct3D renderer only), in SOURCE pixels before rotation; 0 = none.
    public int CropLeft { get; set; }
    public int CropTop { get; set; }
    public int CropRight { get; set; }
    public int CropBottom { get; set; }

    // Low-latency presentation (Direct3D only, experimental): present at once, tear if allowed.
    public bool LowLatency { get; set; }

    // Frame interpolation (Direct3D only): make in-between frames when the display refreshes clearly
    // faster than the video arrives (30 -> 60, 60 -> 144 ...). Off by default; costs one frame of latency.
    public bool Interpolation { get; set; }

    // Which display fullscreen uses: "" = the one the window is on; otherwise "\.\DISPLAYn|name|WxH"
    // (see Displays.cs). Not part of the video profiles: it describes the room, not the source.
    public string FullscreenDisplay { get; set; } = "";

    // Snapshots at the size shown on screen (FSR included) instead of the source size
    // (Direct3D only). Off by default: the saved file stays the exact source picture.
    public bool SaveDisplaySize { get; set; }

    // Per-source presets (プロファイル): up to MaxProfiles, applied from the menu or Ctrl+1..9.
    // ActiveProfile is the name last applied / saved; whether it still matches the live values
    // is worked out on demand, so editing a value never rewrites the profile behind its back.
    public const int MaxProfiles = 9;
    public List<VideoProfile> Profiles { get; set; } = new();
    public string? ActiveProfile { get; set; }

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
    public const string DefaultUpdateApiUrl = "https://api.github.com/repos/Yu5rin/YuCap/releases/latest";
    public string UpdateApiUrl { get; set; } = DefaultUpdateApiUrl;
    /// <summary>Recorded for diagnostics only — the startup check is not throttled.</summary>
    public string? LastUpdateCheckUtc { get; set; }
    /// <summary>A version the user declined; not offered again until a newer one appears.</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>Version and SHA256 of an update downloaded and verified in the
    /// background. Persisted so it survives the restart: it is applied when the
    /// app exits or, failing that (crash, forced kill, power loss), at the next launch.</summary>
    public string? StagedUpdateVersion { get; set; }
    public string? StagedUpdateSha256 { get; set; }
    /// <summary>Version that produced the "what's new" notice, so it shows once.</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>Hotkey combos already reported as conflicting, to warn only once.</summary>
    public string? HotkeyConflictNotified { get; set; }

    // Behavior.
    public bool CursorAutoHide { get; set; } = true;
    public int CursorHideSeconds { get; set; } = 3;
    public bool GlobalHotkeys { get; set; } = true;
    // The Windows recording level we overwrote when capture started, remembered
    // across restarts so "入力レベルを元に戻す" still works after relaunching —
    // and tied to the device id so it is never written back to a different
    // device than the one it was read from.
    public string? CaptureLevelDeviceId { get; set; }
    public int CaptureLevelBefore { get; set; } = -1; // -1 = nothing to restore
    // Hotkey combos stored as System.Windows.Forms.Keys values
    // (0x20000 = Ctrl, 0x40000 = Alt, 0x10000 = Shift, low bits = key). 0 = disabled.
    public int HotkeySnapshot { get; set; } = 0x20000 | 0x40000 | 0x53; // Ctrl+Alt+S
    public int HotkeyMute { get; set; } = 0x20000 | 0x40000 | 0x4D;     // Ctrl+Alt+M
    public int HotkeyPip { get; set; } = 0x20000 | 0x40000 | 0x50;      // Ctrl+Alt+P
    public int HotkeyVolumeUp { get; set; } = 0x20000 | 0x40000 | 0x26;   // Ctrl+Alt+Up
    public int HotkeyVolumeDown { get; set; } = 0x20000 | 0x40000 | 0x28; // Ctrl+Alt+Down
    /// <summary>Endpoint id of the audio output (speakers) to play the capture audio on; null = follow the Windows default.</summary>
    public string? AudioOutputDeviceId { get; set; }
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

    /// <summary>
    /// Clamp ranges and replace nulls a hand-edited settings.json may hold.
    /// System.Text.Json happily assigns null to a non-nullable string property
    /// (it just skips validation), which used to crash every snapshot the
    /// moment SnapshotFormat or DeviceKeyword was null instead of missing.
    /// Called once by SettingsStore.Load on every successfully parsed file.
    /// </summary>
    public void Normalize()
    {
        DisplayMode = Math.Clamp(DisplayMode, 0, 3);
        Volume = Math.Clamp(Volume, 0, 500);
        if (AudioSettingsVersion < 1)
        {
            // The old buffer was the whole delay, so 120 (the old default) and 200 (a common bump) were never a deliberate
            // choice for the new meaning (a safety margin). Any other value is kept.
            if (AudioBufferMs is AudioEngine.LegacyDefaultMs or AudioEngine.LegacyAltMs) AudioBufferMs = AudioEngine.DefaultBufferMs;
            AudioSettingsVersion = 1;
        }
        AudioBufferMs = Math.Clamp(AudioBufferMs, AudioEngine.MinBufferMs, AudioEngine.MaxBufferMs);
        if (Rotation is not (0 or 90 or 180 or 270)) Rotation = 0;
        PipOpacity = Math.Clamp(PipOpacity, 0, 100);
        PipOpacityHover = Math.Clamp(PipOpacityHover, 10, 100);
        PipSizePct = Math.Clamp(PipSizePct, 5, 100);
        PipCorner = Math.Clamp(PipCorner, 0, 3);
        CursorHideSeconds = Math.Clamp(CursorHideSeconds, 1, 60);

        SnapshotFormat = (SnapshotFormat ?? "png").Trim().ToLowerInvariant();
        if (SnapshotFormat is not ("png" or "jpg")) SnapshotFormat = "png";

        DeviceKeyword = string.IsNullOrWhiteSpace(DeviceKeyword) ? "JVA14" : DeviceKeyword;
        Language = Language is "ja" or "en" ? Language : "ja";
        UpdateApiUrl = string.IsNullOrWhiteSpace(UpdateApiUrl) ? DefaultUpdateApiUrl : UpdateApiUrl;

        CaptureLevelBefore = Math.Clamp(CaptureLevelBefore, -1, 100);
        if (string.IsNullOrWhiteSpace(AudioOutputDeviceId)) AudioOutputDeviceId = null;
        // Settings files from before the volume hotkeys have no such entries: the property initialisers
        // already gave them their defaults. Only a combo without a key (modifiers alone) is meaningless.
        if ((HotkeyVolumeUp & 0xFFFF) == 0) HotkeyVolumeUp = 0;
        if ((HotkeyVolumeDown & 0xFFFF) == 0) HotkeyVolumeDown = 0;

        // Anything unrecognised (typo, hand edit, a value from a newer build) falls
        // back to the safe choice: the Media Foundation path / the FSR default.
        FullscreenDisplay = (FullscreenDisplay ?? "").Trim();
        Renderer = NormalizeRenderer(Renderer);
        ScalingQuality = NormalizeQuality(ScalingQuality);
        FsrSharpness = Math.Clamp(FsrSharpness, 0, 100);
        NativeResScale = NormalizeNativeRes(NativeResScale);

        // Out-of-range picture adjustments (hand edit, newer build) are clamped, never reset:
        // a clamped value is still close to what was meant, and every range ends at a safe
        // picture. Contrast 0 or saturation 0 are valid (flat grey / monochrome) on purpose.
        AdjBrightness = Math.Clamp(AdjBrightness, -100, 100);
        AdjContrast = Math.Clamp(AdjContrast, 0, 200);
        AdjSaturation = Math.Clamp(AdjSaturation, 0, 200);
        AdjGamma = Math.Clamp(AdjGamma, 50, 200);   // also keeps 1/gamma finite in the shader

        // The slider moves in 10 ms steps; snap a hand-edited value to that grid so the dialog
        // and the engines always agree on what "0" and "+30" mean.
        AvOffsetMs = SnapAvOffset(AvOffsetMs);

        CropLeft = Math.Clamp(CropLeft, 0, MaxCrop);
        CropTop = Math.Clamp(CropTop, 0, MaxCrop);
        CropRight = Math.Clamp(CropRight, 0, MaxCrop);
        CropBottom = Math.Clamp(CropBottom, 0, MaxCrop);

        // Profiles are normalised like the top-level values. A nameless one cannot be shown in
        // the menu, a duplicate name cannot be told apart from the first, and more than nine
        // have no Ctrl+digit: all are dropped rather than repaired.
        var kept = new List<VideoProfile>();
        foreach (VideoProfile? p in Profiles ?? new List<VideoProfile>())
        {
            if (p == null) continue;
            p.Normalize();
            if (p.Name.Length == 0) continue;
            if (kept.Exists(k => string.Equals(k.Name, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (kept.Count >= MaxProfiles) break;
            kept.Add(p);
        }
        Profiles = kept;
        ActiveProfile = Profiles.Find(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public const int MaxCrop = 2000;

    // Shared by AppSettings.Normalize and VideoProfile.Normalize so the two can never disagree.
    internal static string NormalizeRenderer(string? v)
    {
        v = (v ?? "mf").Trim().ToLowerInvariant();
        return v is "mf" or "d3d" ? v : "mf";
    }

    public static readonly string[] NativeResValues = { "off", "auto", "480", "576", "720", "900", "1080", "1440" };

    internal static string NormalizeNativeRes(string? v)
    {
        string t = (v ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(NativeResValues, t) >= 0 ? t : "off";
    }

    internal static string NormalizeQuality(string? v)
    {
        v = (v ?? "fsr").Trim().ToLowerInvariant();
        return v is "standard" or "fsr" ? v : "fsr";
    }

    internal static int SnapAvOffset(int ms) => (int)Math.Round(Math.Clamp(ms, -300, 300) / 10.0) * 10;
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
            loaded ??= new AppSettings();
            loaded.Normalize();
            return loaded;
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


/// <summary>
/// One per-source preset (プロファイル). Holds exactly the values that differ between sources:
/// capture mode, renderer and picture settings. Window placement, PiP, hotkeys, language and
/// the other app-wide options are deliberately not part of it.
/// </summary>
public sealed class VideoProfile
{
    public const int MaxNameLength = 32;

    public string Name { get; set; } = string.Empty;

    // Capture mode (null members => auto).
    public string? ModeCompression { get; set; }
    public int? ModeWidth { get; set; }
    public int? ModeHeight { get; set; }
    public int? ModeFps { get; set; }

    public string Renderer { get; set; } = "mf";
    public string ScalingQuality { get; set; } = "fsr";
    public int FsrSharpness { get; set; } = 50;
    public string NativeResScale { get; set; } = "off";
    public int DisplayMode { get; set; }
    public int AdjBrightness { get; set; }
    public int AdjContrast { get; set; } = 100;
    public int AdjSaturation { get; set; } = 100;
    public int AdjGamma { get; set; } = 100;
    public int AvOffsetMs { get; set; }
    public int CropLeft { get; set; }
    public int CropTop { get; set; }
    public int CropRight { get; set; }
    public int CropBottom { get; set; }
    public bool LowLatency { get; set; }
    public bool Interpolation { get; set; }
    public bool SaveDisplaySize { get; set; }

    public CaptureMode? ToMode() =>
        ModeCompression is { } c && ModeWidth is int w && ModeHeight is int h && ModeFps is int f
            ? new CaptureMode(c, w, h, f) : null;

    public void SetMode(CaptureMode? mode)
    {
        ModeCompression = mode?.Compression;
        ModeWidth = mode?.Width;
        ModeHeight = mode?.Height;
        ModeFps = mode?.Fps;
    }

    /// <summary>Same ranges as the top-level settings (see <see cref="AppSettings.Normalize"/>).</summary>
    public void Normalize()
    {
        string name = (Name ?? string.Empty).Trim();
        Name = name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
        // A half-written mode (hand edit) is treated as auto rather than guessed at.
        if (ToMode() == null) SetMode(null);

        DisplayMode = Math.Clamp(DisplayMode, 0, 3);
        Renderer = AppSettings.NormalizeRenderer(Renderer);
        ScalingQuality = AppSettings.NormalizeQuality(ScalingQuality);
        FsrSharpness = Math.Clamp(FsrSharpness, 0, 100);
        NativeResScale = AppSettings.NormalizeNativeRes(NativeResScale);
        AdjBrightness = Math.Clamp(AdjBrightness, -100, 100);
        AdjContrast = Math.Clamp(AdjContrast, 0, 200);
        AdjSaturation = Math.Clamp(AdjSaturation, 0, 200);
        AdjGamma = Math.Clamp(AdjGamma, 50, 200);
        AvOffsetMs = AppSettings.SnapAvOffset(AvOffsetMs);
        CropLeft = Math.Clamp(CropLeft, 0, AppSettings.MaxCrop);
        CropTop = Math.Clamp(CropTop, 0, AppSettings.MaxCrop);
        CropRight = Math.Clamp(CropRight, 0, AppSettings.MaxCrop);
        CropBottom = Math.Clamp(CropBottom, 0, AppSettings.MaxCrop);
    }

    /// <summary>True when every value (the name excluded) is the same.</summary>
    public bool SameValues(VideoProfile o) =>
        ToMode() == o.ToMode() && Renderer == o.Renderer && ScalingQuality == o.ScalingQuality
        && FsrSharpness == o.FsrSharpness && NativeResScale == o.NativeResScale && DisplayMode == o.DisplayMode
        && AdjBrightness == o.AdjBrightness && AdjContrast == o.AdjContrast
        && AdjSaturation == o.AdjSaturation && AdjGamma == o.AdjGamma && AvOffsetMs == o.AvOffsetMs
        && CropLeft == o.CropLeft && CropTop == o.CropTop && CropRight == o.CropRight && CropBottom == o.CropBottom
        && LowLatency == o.LowLatency && Interpolation == o.Interpolation && SaveDisplaySize == o.SaveDisplaySize;

    /// <summary>Copy of the values under a new name.</summary>
    public VideoProfile Clone(string name)
    {
        var c = (VideoProfile)MemberwiseClone();
        c.Name = name;
        return c;
    }
}
