// SDR -> HDR10 (PQ, BT.2020) for the HDR swap chain.
//   main    : the composed SDR picture, inverse tone mapped (highlights above the knee expanded towards the peak), then PQ encoded.
//   overlay : premultiplied SDR overlay (OSD / info panel) drawn onto the PQ picture at paper white, no expansion.
// hp = (paper white nits, peak nits, knee (linear, 0..1), unused)
cbuffer Dst : register(b0)
{
    float4 dst;
};
cbuffer Hdr : register(b1)
{
    float4 hp;
};

Texture2D<float4> tex : register(t0);

float3 S2L(float3 c)
{
    float3 lo = c / 12.92;
    float3 hi = pow(max((c + 0.055) / 1.055, 0.0), 2.4);
    return float3(c.r <= 0.04045 ? lo.r : hi.r, c.g <= 0.04045 ? lo.g : hi.g, c.b <= 0.04045 ? lo.b : hi.b);
}

float3 Pq(float3 nits)
{
    float3 y = saturate(nits / 10000.0);
    float3 p = pow(y, 0.1593017578125);
    return pow((0.8359375 + 18.8515625 * p) / (1.0 + 18.6875 * p), 78.84375);
}

float3 To2020(float3 c)
{
    return float3(dot(c, float3(0.627404, 0.329283, 0.043313)),
                  dot(c, float3(0.069097, 0.919540, 0.011362)),
                  dot(c, float3(0.016391, 0.088013, 0.895595)));
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    float3 lin = S2L(saturate(tex.Load(int3(int2(pos.xy), 0)).rgb));
    float m = max(max(lin.r, lin.g), lin.b);
    float k = hp.z;
    float R = hp.y / hp.x;
    if (m > k)
    {
        float t = (m - k) / max(1.0 - k, 1e-4);
        float f = m + (R - 1.0) * t * t;
        lin *= f / m;
    }
    return float4(Pq(To2020(lin * hp.x)), 1.0);
}

float4 overlay(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(floor(pos.xy - dst.xy));
    float4 c = tex.Load(int3(p, 0));
    float a = c.a;
    float3 rgb = a > 0.0 ? saturate(c.rgb / a) : float3(0, 0, 0);
    float3 q = Pq(To2020(S2L(rgb) * hp.x));
    return float4(q * a, a);
}

// Windows HDR is on for the monitor but the user's HDR display is off: the picture goes into a 10-bit back buffer (a composition swap chain
// of B8G8R8A8 is held to the primary monitor's refresh rate by DWM on such monitors). Unchanged sRGB codes, so DWM maps them like any SDR window.
float4 copy(float4 pos : SV_Position) : SV_Target
{
    return float4(tex.Load(int3(int2(pos.xy), 0)).rgb, 1.0);
}
