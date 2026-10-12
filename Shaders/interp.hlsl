// Motion-compensated frame interpolation (see README design notes).
// Entry points, all pixel shaders drawn with the fullscreen triangle of vs.hlsl:
//   pyr1   RGB frame -> luma at 1/2 size            (2x2 box average)
//   pyrd   luma level -> luma level at 1/2 size     (2x2 box average)
//   me     block matching of one pyramid level      (symmetric: A(x - v/2) against B(x + v/2))
//   med    3x3 component-wise median of a vector field (and unit conversion on the last level)
//   synth  in-between frame from A, B and the final field
// One shared constant buffer layout; the meaning of the members is listed per entry point.
cbuffer Cb : register(b0)
{
    float4 P0;   // pyr*: source size (xy) | me: level size (xy), 1/size (zw) | synth: oriented size (xy)
    float4 P1;   // me: field size (xy), parent field size (zw) | med: field size (xy) | synth: field size (xy)
    float4 P2;   // me: lambda (x), coarsest (y), finest (z) | med: output scale (y) | synth: alpha (z)
    float4 P3;
};

Texture2D<float4> T0 : register(t0);
Texture2D<float4> T1 : register(t1);
Texture2D<float4> T2 : register(t2);
SamplerState      lin : register(s0);

// ---------------------------------------------------------------- pyramid

float4 pyr1(float4 pos : SV_Position) : SV_Target
{
    int2 o = int2(pos.xy);
    int2 mx = int2(P0.xy) - 1;
    float s = 0;
    [unroll] for (int dy = 0; dy < 2; dy++)
    [unroll] for (int dx = 0; dx < 2; dx++)
    {
        float3 c = T0.Load(int3(min(o * 2 + int2(dx, dy), mx), 0)).rgb;
        s += dot(c, float3(0.299, 0.587, 0.114));
    }
    return float4(s * 0.25, 0, 0, 1);
}

float4 pyrd(float4 pos : SV_Position) : SV_Target
{
    int2 o = int2(pos.xy);
    int2 mx = int2(P0.xy) - 1;
    float s = 0;
    [unroll] for (int dy = 0; dy < 2; dy++)
    [unroll] for (int dx = 0; dx < 2; dx++)
        s += T0.Load(int3(min(o * 2 + int2(dx, dy), mx), 0)).x;
    return float4(s * 0.25, 0, 0, 1);
}

// ---------------------------------------------------------------- motion estimation

// Mean absolute difference over an 8x8 window centred on the 4x4 block whose centre is c
// (level pixels, continuous coordinates), for the displacement v (level pixels, A -> B).
float BlockCost(float2 c, float2 v)
{
    float2 inv = P0.zw;
    float2 ca = (c - 0.5 * v) * inv;
    float2 cb = (c + 0.5 * v) * inv;
    float s = 0;
    [unroll] for (int j = 0; j < 8; j++)
    [unroll] for (int i = 0; i < 8; i++)
    {
        float2 d = float2(i - 3.5, j - 3.5) * inv;
        float a = T0.SampleLevel(lin, ca + d, 0).x;
        float b = T1.SampleLevel(lin, cb + d, 0).x;
        s += abs(a - b);
    }
    return s * (1.0 / 64.0);
}

// A block whose best match is already this good (mean absolute luma difference) is not searched further.
static const float EarlyOut = 0.006;
static const int Order[10] = { 4, 0, 1, 2, 3, 5, 6, 7, 8, 9 };

float4 me(float4 pos : SV_Position) : SV_Target
{
    int2 f = int2(pos.xy);
    float2 c = float2(f) * 4.0 + 2.0;
    float lambda = P2.x;

    float2 best = float2(0, 0);
    float bestC = 1e9;
    float2 pvec = float2(0, 0);

    if (P2.y > 0.5)
    {
        // coarsest level: exhaustive integer search
        [loop] for (int dy = -6; dy <= 6; dy++)
        [loop] for (int dx = -6; dx <= 6; dx++)
        {
            float2 v = float2(dx, dy);
            float cost = BlockCost(c, v) + lambda * length(v);
            if (cost < bestC) { bestC = cost; best = v; }
        }
    }
    else
    {
        int2 pmax = int2(P1.zw) - 1;
        int2 pf = f >> 1;
        pvec = T2.Load(int3(clamp(pf, int2(0, 0), pmax), 0)).xy * 2.0;

        // the covering parent vector first (ties keep it), then its 8 neighbours, then no motion;
        // a vector that was already tried is skipped
        float2 seen[10];
        int ns = 0;
        [loop] for (int n = 0; n < 10; n++)
        {
            int k = Order[n];
            float2 v = float2(0, 0);
            if (k < 9)
            {
                int2 o = int2(k % 3 - 1, k / 3 - 1);
                v = T2.Load(int3(clamp(pf + o, int2(0, 0), pmax), 0)).xy * 2.0;
            }
            bool dup = false;
            [loop] for (int q = 0; q < ns; q++) if (all(seen[q] == v)) dup = true;
            if (dup) continue;
            seen[ns++] = v;
            float cost = BlockCost(c, v) + lambda * length(v - pvec);
            if (cost < bestC) { bestC = cost; best = v; }
            if (bestC < EarlyOut) break;
        }
    }

    // refine: 8 neighbours at +-1 px, then +-0.5 px, and on the finest level +-0.25 px
    float steps[3] = { 1.0, 0.5, 0.25 };
    [loop] for (int s = 0; s < 3; s++)
    {
        if (bestC < EarlyOut) break;
        if (s == 2 && P2.z < 0.5) break;
        float2 centre = best;
        [loop] for (int k = 0; k < 8; k++)
        {
            int j = k < 4 ? k : k + 1;                       // skip the centre of the 3x3
            float2 v = centre + steps[s] * float2(j % 3 - 1, j / 3 - 1);
            float cost = BlockCost(c, v) + lambda * length(v - pvec);
            if (cost < bestC) { bestC = cost; best = v; }
        }
    }
    return float4(best, bestC, 1);
}

// ---------------------------------------------------------------- median

#define SORT(a, b) { float2 t_ = min(a, b); b = max(a, b); a = t_; }

float4 med(float4 pos : SV_Position) : SV_Target
{
    int2 f = int2(pos.xy);
    int2 mx = int2(P1.xy) - 1;
    float4 c4 = T0.Load(int3(clamp(f, int2(0, 0), mx), 0));
    float2 p0 = T0.Load(int3(clamp(f + int2(-1, -1), int2(0, 0), mx), 0)).xy;
    float2 p1 = T0.Load(int3(clamp(f + int2( 0, -1), int2(0, 0), mx), 0)).xy;
    float2 p2 = T0.Load(int3(clamp(f + int2( 1, -1), int2(0, 0), mx), 0)).xy;
    float2 p3 = T0.Load(int3(clamp(f + int2(-1,  0), int2(0, 0), mx), 0)).xy;
    float2 p4 = c4.xy;
    float2 p5 = T0.Load(int3(clamp(f + int2( 1,  0), int2(0, 0), mx), 0)).xy;
    float2 p6 = T0.Load(int3(clamp(f + int2(-1,  1), int2(0, 0), mx), 0)).xy;
    float2 p7 = T0.Load(int3(clamp(f + int2( 0,  1), int2(0, 0), mx), 0)).xy;
    float2 p8 = T0.Load(int3(clamp(f + int2( 1,  1), int2(0, 0), mx), 0)).xy;
    // median of 9 (optimal sorting network, Devillard / Smith)
    SORT(p1, p2); SORT(p4, p5); SORT(p7, p8);
    SORT(p0, p1); SORT(p3, p4); SORT(p6, p7);
    SORT(p1, p2); SORT(p4, p5); SORT(p7, p8);
    SORT(p0, p3); SORT(p5, p8); SORT(p4, p7);
    SORT(p3, p6); SORT(p1, p4); SORT(p2, p5);
    SORT(p4, p7); SORT(p4, p2); SORT(p6, p4);
    SORT(p4, p2);
    return float4(p4 * P2.y, c4.z, 1);
}

// ---------------------------------------------------------------- synthesis

float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

float4 synth(float4 pos : SV_Position) : SV_Target
{
    float2 size = P0.xy;
    float2 isz = 1.0 / size;
    float alpha = P2.z;

    // the field's texel i covers source pixels 8i .. 8i+7, so its centre sits on 8i+4
    float2 v = T2.SampleLevel(lin, pos.xy / (P1.xy * 8.0), 0).xy;

    float3 ca = T0.SampleLevel(lin, (pos.xy - alpha * v) * isz, 0).rgb;
    float3 cb = T1.SampleLevel(lin, (pos.xy + (1.0 - alpha) * v) * isz, 0).rgb;
    float3 mc = lerp(ca, cb, alpha);

    float d = abs(Luma(ca) - Luma(cb));
    float w = saturate((d - 0.06) / 0.10);

    // where the two motion-compensated samples disagree the vector is not to be trusted:
    // fall back to the nearer frame as it is
    int3 p = int3(int2(pos.xy), 0);
    float3 fb = alpha < 0.5 ? T0.Load(p).rgb : T1.Load(p).rgb;
    return float4(lerp(mc, fb, w), 1.0);
}
