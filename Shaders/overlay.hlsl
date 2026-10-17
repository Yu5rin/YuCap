// Overlay (notice / info panel) blended onto the back buffer. The image is premultiplied BGRA and is
// drawn 1:1 with its top-left at dst.xy; the blend state does the "over" (One, InvSrcAlpha).
cbuffer Overlay : register(b0)
{
    float4 dst;
};

Texture2D<float4> tex : register(t0);

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(floor(pos.xy - dst.xy));
    return tex.Load(int3(p, 0));
}
