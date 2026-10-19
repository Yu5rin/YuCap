using System;
using System.Numerics;

namespace YuCap;

/// <summary>
/// Settings of the convert pass's chroma upsampling (「色のにじみを抑える」, Shaders/convert.hlsl ChromaUp). Static like the other
/// quality knobs: shared by the live pipeline and the test code.
/// </summary>
internal sealed partial class D3DRenderer
{
    /// <summary>Luma-guided chroma upsampling on (the user setting). Off = the plain bilinear sampler.</summary>
    public static volatile bool ChromaGuided = true;
    /// <summary>The algorithm used when <see cref="ChromaGuided"/> is on: 2 joint bilateral, 3 chroma-from-luma fit (see convert.hlsl).</summary>
    public static int ChromaModeOn = 3;
    /// <summary>Test hook: force a mode (0 bilinear hardware, 1 bilinear with MPEG-2 siting, 2, 3); -1 = follow the setting.</summary>
    public static int ChromaModeOverride = -1;
    /// <summary>Bit 0: the luma at a chroma site is low-passed horizontally ([1 2 1]) as the encoder's chroma filter would.</summary>
    public static int ChromaVariant = 1;
    /// <summary>x = overshoot allowance (fraction of the local chroma range), y = 1/sigma^2 of the bilateral luma weight,
    /// z = distance weight, w = regularisation of the luma-chroma fit.</summary>
    public static Vector4 ChromaTune = new(2.0f, 150f, 2.0f, 0.0003f);

    internal static int ChromaModeNow => ChromaModeOverride >= 0 ? ChromaModeOverride : (ChromaGuided ? ChromaModeOn : 0);
}
