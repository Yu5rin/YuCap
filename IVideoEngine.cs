using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace YuCap;

/// <summary>How the picture is resampled when the window is larger than the source.</summary>
/// <summary>What "元の細かさに合わせて拡大" is doing now. <c>Active</c> is the height the last drawn frame was shrunk to
/// (0 = not shrunk: off, native detail, or not applicable to the current window); <c>Judging</c> is auto mode before the
/// first decision; <c>Target</c> is the height decided or chosen (0 = none); <c>SourceHeight</c> the oriented picture's height.</summary>
public sealed record NativeResStatus(string Mode, int Target, int Active, bool Judging, int SourceHeight);

public enum ScalingQuality
{
    /// <summary>GPU bilinear — what the Media Foundation preview has always done.</summary>
    Standard,
    /// <summary>AMD FidelityFX FSR 1: edge-adaptive upscale (EASU) + sharpening (RCAS).</summary>
    Fsr,
    /// <summary>Anime4K CNN upscaler (x2 per stage), then RCAS / bilinear to the exact size: 「くっきり（アニメ・イラスト向け）」.</summary>
    Anime4K,
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

/// <summary>Why frame interpolation is not running (<see cref="InterpolationStatus.Idle"/>).</summary>
public enum InterpolationIdle
{
    /// <summary>It is running.</summary>
    None,
    /// <summary>The setting is off.</summary>
    Disabled,
    /// <summary>Low-latency presentation is not display-driven, so there are no refreshes to fill.</summary>
    LowLatency,
    /// <summary>The display refresh is not clearly higher than the source frame rate.</summary>
    RefreshTooLow,
    /// <summary>No video yet, so the source frame rate is not known.</summary>
    NoSignal,
    /// <summary>The interpolation shaders could not be built on this system.</summary>
    Unavailable,
}

/// <summary>What frame interpolation is doing right now: running (Active) from SourceFps up to RefreshHz,
/// or idle with the reason.</summary>
public sealed record InterpolationStatus(bool Active, double SourceFps, double RefreshHz, InterpolationIdle Idle);

/// <summary>Frame-rate figures measured by a renderer that presents frames itself.</summary>
public sealed record RenderStats(double InputFps, double PresentedFps, long DroppedFrames,
    double GpuMsAvg = 0, double GpuMsMax = 0);

/// <summary>One overlay image (notice, info panel, test caption) for <see cref="IVideoEngine.SetOverlays"/>:
/// premultiplied BGRA <paramref name="Pixels"/> of W x H, drawn with its top-left at (X, Y) in canvas client pixels.
/// <paramref name="Version"/> changes whenever the pixels do, so the engine uploads only on change.</summary>
public sealed record OverlayLayer(int Id, int X, int Y, int W, int H, byte[] Pixels, long Version);

/// <summary>"Crispness" reading (くっきり度): edge strength of YuCap's current pipeline divided by plain bilinear
/// on the same crop of the current frame, minus 1 (0.28 = 28% crisper). RenderMsMax is the longest time one sample
/// kept the render thread busy.</summary>
public sealed record CrispnessReading(double Ratio, long TickMs, int Samples, double RenderMsLast, double RenderMsMax);

/// <summary>Per-frame GPU milliseconds from <see cref="IVideoEngine.GpuBench"/>.</summary>
public sealed record GpuBenchResult(string Adapter, double[] FrameMs);

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
    /// <summary>The running session stopped by itself (device lost, present failure, stream end/error), not via <see cref="Stop"/>.</summary>
    bool EndedUnexpectedly => false;

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
    /// <summary>Unique pictures per second over about the last second (the game's own frame rate, e.g. 30 for a 30 fps game
    /// captured at 60 fps; Direct3D only). 0 = unknown, or the picture is still (see <see cref="PictureStill"/>).</summary>
    double GameFps => 0;
    /// <summary>A game frame rate was measured and the picture is moving (<see cref="GameFps"/> may be shown).</summary>
    bool GameFpsReliable => false;
    /// <summary>The picture did not change over the last second or so: there is no frame rate to show.</summary>
    bool PictureStill => false;
    /// <summary>Exact cumulative count of frames received from the capture device since Start
    /// (0 when not applicable). Unlike <see cref="Stats"/> it is not a rolling average, so the
    /// caller can take deltas over any precise window.</summary>
    long FramesReceived => 0;
    /// <summary>Exact cumulative count of successful Present calls since Start.</summary>
    long FramesPresented => 0;
    /// <summary>Exact cumulative count of frames replaced before being drawn since Start.</summary>
    long FramesDropped => 0;
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
    /// <summary>Extra hold-back the renderer adds on its own to even out arrival jitter (display-driven
    /// vsync pacing), in ms; part of what <see cref="VideoDelayActualMs"/> measures. 0 when not used.</summary>
    double VideoPacingMarginMs => 0;
    /// <summary>Cut a black border off the picture (Direct3D only). Left/Top/Right/Bottom are in
    /// SOURCE pixels, before rotation. <see cref="DisplayResolution"/> becomes the cropped (then
    /// rotated) size, so layout, FSR and stills all follow the cropped picture; at least 64x64
    /// always remains.</summary>
    bool SupportsCrop => false;
    Padding Crop { get => Padding.Empty; set { } }
    /// <summary>Present each new frame at once instead of waiting for vsync (Direct3D only). The kind
    /// of swap chain is chosen when the engine starts, so a change takes effect at the next Start
    /// (MainForm restarts the engine on a fresh window when the user toggles it).</summary>
    bool SupportsLowLatency => false;
    bool LowLatency { get => false; set { } }
    /// <summary>How frames reach the screen, e.g. "composition vsync", "hwnd tearing", "hwnd sync0".
    /// Empty when not running or not applicable.</summary>
    string PresentMode => string.Empty;
    /// <summary>Motion-compensated frame interpolation (Direct3D only): when the display refreshes clearly
    /// faster than the video arrives, in-between pictures are made so that every refresh shows a new one.
    /// It costs one source frame of extra latency. <see cref="InterpolationState"/> says whether it is
    /// actually running and, if not, why.</summary>
    bool SupportsInterpolation => false;
    bool Interpolation { get => false; set { } }
    InterpolationStatus? InterpolationState => null;
    /// <summary>"元の細かさに合わせて拡大" (Direct3D only): shrink the picture to the resolution its detail really has
    /// (a game drawn at 720p and enlarged to 1080p by the console) before scaling, so FSR enlarges from the true size.
    /// "off", "auto" (judged from the picture) or a fixed height such as "720".</summary>
    bool SupportsNativeRes => false;
    string NativeResScale { get => "off"; set { } }
    NativeResStatus? NativeResState => null;
    /// <summary>Test mode: <see cref="RenderOffscreen(Size, ScalingQuality)"/> with the prescale pass (<paramref name="nativeH"/> lines;
    /// <paramref name="filter"/> -1 = the default filter).</summary>
    Bitmap? RenderOffscreen(Size output, ScalingQuality quality, int nativeH, int filter) => null;
    /// <summary>Test mode: render <paramref name="source"/> (instead of the live frame) through the real GPU pipeline at
    /// <paramref name="output"/>. The live picture is replaced until the next frame arrives.</summary>
    Bitmap? RenderBitmap(Bitmap source, Size output, ScalingQuality quality, int nativeH, int filter) => null;
    /// <summary>Test mode: GPU milliseconds per frame without / with the prescale and of the prescale pass alone.</summary>
    bool BenchPrescale(Bitmap? source, Size output, ScalingQuality quality, int nativeH, int filter, int frames,
        out double[] plain, out double[] withPre, out double[] passOnly)
    { plain = withPre = passOnly = Array.Empty<double>(); return false; }
    /// <summary>Test mode: GPU milliseconds per presented frame at <paramref name="output"/> for <paramref name="quality"/>.
    /// With <paramref name="b"/> the in-between picture of the pair is synthesised each time (frame interpolation on); otherwise
    /// <paramref name="cached"/> says whether a repeated frame (the Anime4K result reused) or a new one is timed.</summary>
    bool BenchQuality(Bitmap a, Bitmap? b, Size output, ScalingQuality quality, int nativeH, int frames, bool cached, out double[] ms)
    { ms = Array.Empty<double>(); return false; }
    /// <summary>Render the current frame at <paramref name="output"/> size off-screen and
    /// read it back — lets the scaler be checked at 4K on a smaller monitor.</summary>
    Bitmap? RenderOffscreen(Size output, ScalingQuality quality) => null;
    /// <summary>Debug hook for <c>--gpu-bench</c>: render the live source <paramref name="frames"/> times
    /// at <paramref name="output"/> size off-screen and report the GPU time of each frame.</summary>
    GpuBenchResult? GpuBench(Size output, ScalingQuality quality, int frames) => null;
    /// <summary>Colour attributes the source reader negotiated (native and output media type), as
    /// text lines, for <c>--hdr-probe</c>. Null when not running or not applicable.</summary>
    string? SourceColorInfo => null;
    /// <summary>Smoothed time from a frame reaching YuCap until the display shows it (ms), including any video delay,
    /// pacing margin and interpolation hold; 0 when unknown (Media Foundation renderer, nothing shown yet).
    /// Does not include the capture card's or the display's own delay.</summary>
    double VideoLatencyMs => 0;
    /// <summary>True when <see cref="VideoLatencyMs"/> is derived from when Present returned rather than from
    /// the display's frame statistics.</summary>
    bool VideoLatencyEstimated => false;
    /// <summary>Start / stop recording frame pacing and per-frame GPU time (test mode, Direct3D only).
    /// <see cref="EndMeasure"/> returns null when nothing was recorded.</summary>
    void BeginMeasure() { }
    RenderMeasurement? EndMeasure() => null;

    /// <summary>Direct3D only: overlays are blended into the swap chain's back buffer (after the copy stills are made
    /// from), so nothing has to be a window over the video. Null/empty removes them.</summary>
    bool SupportsOverlays => false;
    void SetOverlays(IReadOnlyList<OverlayLayer>? layers) { }
    /// <summary>Direct3D only: sample the crispness every <see cref="CrispnessIntervalMs"/> while this is on.</summary>
    bool SupportsCrispness => false;
    bool CrispnessSampling { get => false; set { } }
    int CrispnessIntervalMs { get => 5000; set { } }
    /// <summary>Latest reading, or null when none is valid (output not larger than the source, paused, flat picture...).</summary>
    CrispnessReading? Crispness => null;
}
