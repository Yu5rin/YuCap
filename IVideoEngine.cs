using System;
using System.Collections.Generic;
using System.Drawing;

namespace YuCap;

/// <summary>How the picture is resampled when the window is larger than the source.</summary>
public enum ScalingQuality
{
    /// <summary>GPU bilinear — what the Media Foundation preview has always done.</summary>
    Standard,
    /// <summary>AMD FidelityFX FSR 1: edge-adaptive upscale (EASU) + sharpening (RCAS).</summary>
    Fsr,
}

/// <summary>
/// Picture adjustments applied to the decoded frame (Direct3D only). Brightness is added
/// (-1..1), Contrast and Saturation multiply (0..2), Gamma is the display gamma (0.5..2.0,
/// above 1 = brighter midtones). <see cref="Neutral"/> is an exact passthrough.
/// </summary>
public readonly record struct VideoAdjustments(float Brightness, float Contrast, float Saturation, float Gamma)
{
    public static readonly VideoAdjustments Neutral = new(0f, 1f, 1f, 1f);
    public bool IsNeutral => this == Neutral;

    /// <summary>Clamp every member into its documented range.</summary>
    public VideoAdjustments Clamped() => new(
        Math.Clamp(Brightness, -1f, 1f), Math.Clamp(Contrast, 0f, 2f),
        Math.Clamp(Saturation, 0f, 2f), Math.Clamp(Gamma, 0.5f, 2f));
}

/// <summary>Frame-rate figures measured by a renderer that presents frames itself.</summary>
public sealed record RenderStats(double InputFps, double PresentedFps, long DroppedFrames);

/// <summary>
/// What MainForm needs from a video engine. Two implementations exist side by side:
/// <see cref="VideoEngine"/> (Media Foundation Capture Engine preview — the proven,
/// default path) and <see cref="D3DVideoEngine"/> (frames pulled from a source reader
/// and presented by our own Direct3D 11 swap chain, which is what makes FSR upscaling
/// possible). The setting <c>AppSettings.Renderer</c> chooses between them, so the new
/// path can be tried — and abandoned in one click — without risking the old one.
///
/// Device enumeration stays static on <see cref="VideoEngine"/>; both engines open
/// devices by the same symbolic link.
/// </summary>
public interface IVideoEngine : IDisposable
{
    // ---- lifecycle -------------------------------------------------------
    /// <summary>The window the picture is presented into (the canvas VideoBox).</summary>
    void Attach(IntPtr hwnd);
    /// <summary>Open the device and start showing it. Throws (UserFacingException
    /// for our own messages) on failure; the engine is left stopped.</summary>
    void Start(VideoDeviceInfo info, CaptureMode? mode = null);
    void Stop();
    bool IsRunning { get; }

    // ---- what is being shown ---------------------------------------------
    string? CurrentDeviceName { get; }
    Size CurrentResolution { get; }
    CaptureMode? CurrentMode { get; }
    int NegotiatedFps { get; }
    /// <summary>Resolution as displayed: width/height swap under 90°/270° rotation.</summary>
    Size DisplayResolution { get; }
    List<CaptureMode> GetModes();

    // ---- placement --------------------------------------------------------
    /// <summary>Destination rectangle in canvas client pixels (may exceed the client
    /// when zoomed — the excess is cropped). Must never block the calling (UI) thread.</summary>
    int SetVideoRect(Rectangle dst, bool clearBorder = true, int settleMs = 0);
    /// <summary>True when a placement update has been in flight longer than the
    /// threshold (MF's UpdateVideo can wedge). Engines that cannot wedge return false.</summary>
    bool IsRectUpdateStuck(int thresholdMs);

    // ---- orientation ------------------------------------------------------
    int Rotation { get; }
    bool Mirror { get; }
    bool SetRotation(int degrees);
    bool SetMirror(bool mirrored);

    // ---- stills -----------------------------------------------------------
    /// <summary>Current frame at source resolution, already rotated/mirrored like the
    /// display. Null if unavailable.</summary>
    Bitmap? PhotoSnapshot(int timeoutMs = 1500);
    /// <summary>What is displayed in the canvas: cropped to the video rectangle, or the
    /// whole canvas including letterbox when <paramref name="cropToVideo"/> is false.
    /// Null on failure, with <see cref="LastSnapshotError"/> saying why.</summary>
    Bitmap? Snapshot(bool cropToVideo = true);
    string? LastSnapshotError { get; }

    // ---- optional capabilities (the Media Foundation path supports none) ----
    bool SupportsScalingQuality => false;
    ScalingQuality Quality { get => ScalingQuality.Standard; set { } }
    /// <summary>FSR sharpening amount, 0 (off) .. 1 (strongest).</summary>
    float Sharpness { get => 0.5f; set { } }
    /// <summary>Nearest-neighbour sampling, for integer scaling: every source pixel
    /// becomes an exact block, which is the point of that display mode.</summary>
    bool PixelPerfect { get => false; set { } }
    RenderStats? Stats => null;
    /// <summary>Brightness / contrast / saturation / gamma, applied before scaling so stills
    /// and pause images carry it too.</summary>
    bool SupportsAdjustments => false;
    VideoAdjustments Adjustments { get => VideoAdjustments.Neutral; set { } }
    /// <summary>Hold each frame back by this many milliseconds before showing it, so the picture
    /// lines up with audio that cannot be made any earlier (A/V sync, "audio earlier").</summary>
    bool SupportsVideoDelay => false;
    int VideoDelayMs { get => 0; set { } }
    /// <summary>Measured delay of the frames actually shown while <see cref="VideoDelayMs"/> is
    /// set (ms); 0 when no delay is active or nothing was shown yet.</summary>
    double VideoDelayActualMs => 0;
    /// <summary>Render the current frame at <paramref name="output"/> size off-screen and
    /// read it back — lets the scaler be checked at 4K on a smaller monitor.</summary>
    Bitmap? RenderOffscreen(Size output, ScalingQuality quality) => null;
}
