using System;
using System.Collections.Generic;
using System.Drawing;

namespace YuCap;

/// <summary>
/// PLACEHOLDER so the rest of the app can be wired to the Direct3D renderer while it is
/// being written. The real implementation replaces this file entirely.
/// </summary>
public sealed class D3DVideoEngine : IVideoEngine
{
    public D3DVideoEngine() { }

    public void Attach(IntPtr hwnd) { }
    public void Start(VideoDeviceInfo info, CaptureMode? mode = null) =>
        throw new NotImplementedException("D3DVideoEngine is a placeholder");
    public void Stop() { }
    public bool IsRunning => false;

    public string? CurrentDeviceName => null;
    public Size CurrentResolution => Size.Empty;
    public CaptureMode? CurrentMode => null;
    public int NegotiatedFps => 0;
    public Size DisplayResolution => Size.Empty;
    public List<CaptureMode> GetModes() => new();

    public int SetVideoRect(Rectangle dst, bool clearBorder = true, int settleMs = 0) => 0;
    public bool IsRectUpdateStuck(int thresholdMs) => false;

    public int Rotation => 0;
    public bool Mirror => false;
    public bool SetRotation(int degrees) => true;
    public bool SetMirror(bool mirrored) => true;

    public Bitmap? PhotoSnapshot(int timeoutMs = 1500) => null;
    public Bitmap? Snapshot(bool cropToVideo = true) => null;
    public string? LastSnapshotError => null;

    public bool SupportsScalingQuality => true;
    public ScalingQuality Quality { get; set; } = ScalingQuality.Fsr;
    public float Sharpness { get; set; } = 0.5f;
    public bool PixelPerfect { get; set; }
    public RenderStats? Stats => null;
    public Bitmap? RenderOffscreen(Size output, ScalingQuality quality) => null;

    public void Dispose() { }
}
