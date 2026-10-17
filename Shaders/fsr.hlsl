// AMD FidelityFX FSR 1: EASU (upscale) and RCAS (sharpen), 32-bit path.
// The vendored headers (ffx_a.h, ffx_fsr1.h) are prepended by D3DShaders before compiling,
// preceded by the A_GPU / A_HLSL / FSR_EASU_F / FSR_RCAS_F defines.
cbuffer Fsr : register(b0)
{
    uint4 con0;
    uint4 con1;
    uint4 con2;
    uint4 con3;
    int4  org;     // x,y: destination origin in target pixels (EASU; RCAS: where its input picture starts, 0 = same as the target)
    int4  clip;    // left, top, right, bottom: area RCAS may read (clamped, so no black bleeds in)
};

Texture2D<float4> tex  : register(t0);
SamplerState      samp : register(s0);

AF4 FsrEasuRF(AF2 p) { return tex.GatherRed(samp, p); }
AF4 FsrEasuGF(AF2 p) { return tex.GatherGreen(samp, p); }
AF4 FsrEasuBF(AF2 p) { return tex.GatherBlue(samp, p); }

AF4 FsrRcasLoadF(ASU2 p)
{
    p = clamp(p, clip.xy, clip.zw - 1);
    return tex.Load(int3(p - org.xy, 0));   // org is 0 unless RCAS reads a picture that starts at the destination origin (Anime4K)
}
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}

float4 easu(float4 pos : SV_Position) : SV_Target
{
    AF3 c;
    FsrEasuF(c, AU2(int2(pos.xy) - org.xy), con0, con1, con2, con3);
    return float4(c, 1.0);
}

float4 rcas(float4 pos : SV_Position) : SV_Target
{
    AF1 r, g, b;
    FsrRcasF(r, g, b, AU2(pos.xy), con0);
    return float4(r, g, b, 1.0);
}
