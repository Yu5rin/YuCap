// Plain scaling of the oriented picture into the destination rectangle.
// dst = (originX, originY, width, height) in render-target pixels; the origin may be
// negative and the rectangle larger than the target (zoom) — rasterization clips.
cbuffer Scale : register(b0)
{
    float4 dst;
};

Texture2D<float4> tex  : register(t0);
SamplerState      samp : register(s0);

float4 main(float4 pos : SV_Position) : SV_Target
{
    float2 uv = (pos.xy - dst.xy) / dst.zw;
    return float4(tex.SampleLevel(samp, uv, 0).rgb, 1.0);
}
