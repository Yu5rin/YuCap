// Shrink the oriented picture to the size of its real detail (the "match the original detail" option): the picture
// was enlarged by the console / the card, and FSR does best when it enlarges from the true resolution.
// One pass, separable weights evaluated inline over the whole footprint (the footprint grows with the
// shrink ratio, so every source pixel under a destination pixel contributes - no aliasing).
//   p0 = (srcW, srcH, dstW, dstH), p1.x = filter: 0 box (exact area), 1 tent, 2 Catmull-Rom, 3 Lanczos3
cbuffer Pre : register(b0)
{
    float4 p0;
    float4 p1;
};

Texture2D<float4> tex : register(t0);

static const float PI = 3.14159265358979;

// Weight of a source sample whose centre is t destination pixels from the output pixel's centre.
float Wt(float t, int filter)
{
    t = abs(t);
    if (filter == 1) return max(0.0, 1.0 - t);
    if (filter == 2)
    {
        if (t < 1.0) return 1.5 * t * t * t - 2.5 * t * t + 1.0;
        if (t < 2.0) return -0.5 * t * t * t + 2.5 * t * t - 4.0 * t + 2.0;
        return 0.0;
    }
    if (filter == 3)
    {
        if (t < 1e-4) return 1.0;
        if (t >= 3.0) return 0.0;
        float pt = PI * t;
        return 3.0 * sin(pt) * sin(pt / 3.0) / (pt * pt);
    }
    return 0.0;   // box is handled by area overlap
}

float Support(int filter)
{
    if (filter == 0) return 0.5;
    if (filter == 1) return 1.0;
    if (filter == 2) return 2.0;
    return 3.0;
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    int filter = (int)p1.x;
    float2 ratio = p0.xy / p0.zw;                 // source pixels per destination pixel (>= 1)
    float2 c = pos.xy * ratio;                    // footprint centre in source pixels (pos is at x+0.5)
    float2 r = Support(filter) * ratio;           // footprint radius in source pixels
    int2 lo = (int2)floor(c - r - 0.5);
    int2 hi = (int2)ceil(c + r - 0.5);
    int2 mx = (int2)p0.xy - 1;

    float3 acc = 0;
    float wsum = 0;
    for (int j = lo.y; j <= hi.y; j++)
    {
        float wy;
        if (filter == 0)
            wy = max(0.0, min(c.y + 0.5 * ratio.y, j + 1.0) - max(c.y - 0.5 * ratio.y, j));
        else
            wy = Wt((j + 0.5 - c.y) / ratio.y, filter);
        if (wy == 0.0) continue;
        int jj = clamp(j, 0, mx.y);
        for (int i = lo.x; i <= hi.x; i++)
        {
            float wx;
            if (filter == 0)
                wx = max(0.0, min(c.x + 0.5 * ratio.x, i + 1.0) - max(c.x - 0.5 * ratio.x, i));
            else
                wx = Wt((i + 0.5 - c.x) / ratio.x, filter);
            if (wx == 0.0) continue;
            float w = wx * wy;
            acc += tex.Load(int3(clamp(i, 0, mx.x), jj, 0)).rgb * w;
            wsum += w;
        }
    }
    return float4(saturate(acc / max(wsum, 1e-6)), 1.0);
}

// ---- back-projection ("ibp"): refine the shrunk picture L so that enlarging it with a bilinear filter (what a console / card
// does) gives back the captured picture. One iteration: L += shrink(src - enlarge(L)), the shrink being an area average.
// t0 = captured picture (srcW x srcH, read with Load), t1 = L (dstW x dstH, read bilinearly), s0 = linear clamp sampler.
Texture2D<float4> low : register(t1);
SamplerState      lin : register(s0);

float4 ibp(float4 pos : SV_Position) : SV_Target
{
    float2 ratio = p0.xy / p0.zw;
    float2 c = pos.xy * ratio;
    float2 a = c - 0.5 * ratio, b = c + 0.5 * ratio;
    int2 lo = (int2)floor(a) - 1;
    int2 hi = (int2)ceil(b);          // one pixel wider than the area footprint, for the clamp range below
    int2 mx = (int2)p0.xy - 1;
    float3 acc = 0;
    float wsum = 0;
    float3 smin = 1e9, smax = -1e9;
    for (int j = lo.y; j <= hi.y; j++)
    {
        float wy = max(0.0, min(b.y, j + 1.0) - max(a.y, (float)j));
        int jj = clamp(j, 0, mx.y);
        for (int i = lo.x; i <= hi.x; i++)
        {
            float wx = max(0.0, min(b.x, i + 1.0) - max(a.x, (float)i));
            int2 q = int2(clamp(i, 0, mx.x), jj);
            float3 s = tex.Load(int3(q, 0)).rgb;
            smin = min(smin, s);
            smax = max(smax, s);
            float w = wx * wy;
            if (w > 0.0)
            {
                float3 up = low.SampleLevel(lin, (float2(q) + 0.5) / p0.xy, 0).rgb;
                acc += (s - up) * w;
                wsum += w;
            }
        }
    }
    float3 cur = low.Load(int3((int2)pos.xy, 0)).rgb;
    float3 res = cur + acc / max(wsum, 1e-6);
    // p1.y >= 0: the refined value may not leave the captured picture's local range by more than that share of the range
    if (p1.y >= 0.0)
    {
        float3 m = (smax - smin) * p1.y;
        res = clamp(res, smin - m, smax + m);
    }
    return float4(res, 1.0);
}
