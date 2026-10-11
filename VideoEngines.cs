using System;

namespace YuCap;

/// <summary>
/// The one place that turns the <c>AppSettings.Renderer</c> string into an engine, so the
/// app and the self-test choose exactly the same way.
/// </summary>
internal static class VideoEngines
{
    public static IVideoEngine Create(string? renderer) =>
        string.Equals(renderer, "d3d", StringComparison.OrdinalIgnoreCase)
            ? new D3DVideoEngine()
            : new VideoEngine();
}
