// Motion-compensated frame interpolation (see README design notes).
// Entry points, all pixel shaders drawn with the fullscreen triangle of vs.hlsl:
//   pyr1   RGB frame -> luma at 1/F size            (FxF box average; F = 2, or 4 for sources wider than 2560)
//   pyrd   luma level -> luma level at 1/2 size     (2x2 box average)
//   mecost matching costs of all integer vectors of the coarsest level (parallel)
//   me     block matching of one pyramid level      (symmetric: A(x - v/2) against B(x + v/2))
//   cutstat scene-cut statistics of the coarsest field (one pixel)
//   med    3x3 component-wise median of a vector field (and unit conversion on the last level)
//   synth  in-between frame from A, B and the final field
// One shared constant buffer layout; the meaning of the members is listed per entry point.
cbuffer Cb : register(b0)
{
    float4 P0;   // pyr*: source size (xy), box size F (z, pyr1 only) | me: level size (xy), 1/size (zw) | synth: oriented size (xy)
    float4 P1;   // me: field size (xy), parent field size (zw) | med: field size (xy) | synth: field size (xy)
    float4 P2;   // me: lambda (x), coarsest (y), finest (z) | med: output scale (y: source pixels per finest-level pixel) | synth: alpha (z), source pixels per field texel (w)
    float4 P3;   // mecost, me (coarsest level): search range R in level pixels (x), 2R+1 (y)
};

Texture2D<float4> T0 : register(t0);
Texture2D<float4> T1 : register(t1);
Texture2D<float4> T2 : register(t2);
Texture2D<float>  T3 : register(t3);
Texture2D<float4> T4 : register(t4);
Texture2D<float>  T8 : register(t8);   // HUD static confidence (hudupd), 1x1 zero when off
static const float CutLo = 0.04, CutMid = 0.08, CutHi = 0.15;
SamplerState      lin : register(s0);

// ---------------------------------------------------------------- pyramid

float4 pyr1(float4 pos : SV_Position) : SV_Target
{
    int2 o = int2(pos.xy);
    int2 mx = int2(P0.xy) - 1;
    int n = (int)P0.z;
    float s = 0;
    [loop] for (int dy = 0; dy < n; dy++)
    [loop] for (int dx = 0; dx < n; dx++)
    {
        float3 c = T0.Load(int3(min(o * n + int2(dx, dy), mx), 0)).rgb;
        s += dot(c, float3(0.299, 0.587, 0.114));
    }
    return float4(s / (n * n), 0, 0, 1);
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

// Coarsest level, step 1: the cost of every integer vector -R..R of every block, one pixel per (block, vector):
// block f, vector d is the pixel f*N + d + R (N = 2R+1). Spread over many pixels so the GPU is kept busy.
float4 mecost(float4 pos : SV_Position) : SV_Target
{
    int R = (int)P3.x, N = (int)P3.y;
    int2 p = int2(pos.xy);
    int2 f = p / N;
    int2 d = p - f * N - R;
    return float4(BlockCost(float2(f) * 4.0 + 2.0, float2(d)), 0, 0, 1);
}

// A block whose best match is already this good (mean absolute luma difference) is not searched further.
static const float EarlyOut = 0.001;
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
        // (the matching costs were computed in parallel by mecost into T3, N x N per block)
        int R = (int)P3.x, N = (int)P3.y;
        [loop] for (int dy = -R; dy <= R; dy++)
        [loop] for (int dx = -R; dx <= R; dx++)
        {
            float2 v = float2(dx, dy);
            float cost = T3.Load(int3(f * N + int2(dx + R, dy + R), 0)) + lambda * length(v);
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

// ---------------------------------------------------------------- scene cut

// One pixel: how badly the blocks of the coarsest field matched. x = mean cost, y/z/w = share of blocks whose cost is above
// three levels. Unrelated pictures (a cut) match badly nearly everywhere, a pan or moving objects only in a few blocks.
float4 cutstat(float4 pos : SV_Position) : SV_Target
{
    int2 n = int2(P1.xy);
    float s = 0, a = 0, b = 0, c = 0;
    [loop] for (int y = 0; y < n.y; y++)
    [loop] for (int x = 0; x < n.x; x++)
    {
        float z = T2.Load(int3(x, y, 0)).z;
        s += z;
        a += z > CutLo ? 1 : 0;
        b += z > CutMid ? 1 : 0;
        c += z > CutHi ? 1 : 0;
    }
    float k = 1.0 / (n.x * n.y);
    return float4(s * k, a * k, b * k, c * k);
}

// ---------------------------------------------------------------- synthesis

float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

// weight of "this pixel did not move (HUD, text): show the plain frame" from the static confidence
float HudW(int2 p)
{
    uint w, h; T8.GetDimensions(w, h);
    return saturate((T8.Load(int3(min(p, int2(w, h) - 1), 0)) - 0.4) / 0.4);
}

float4 synth(float4 pos : SV_Position) : SV_Target
{
    float2 size = P0.xy;
    float2 isz = 1.0 / size;
    float alpha = P2.z;

    // the field's texel i covers source pixels B*i .. B*i+B-1 (B = P2.w: 8, or 16 for a wide source)
    float2 v = T2.SampleLevel(lin, pos.xy / (P1.xy * P2.w), 0).xy;

    float3 ca = T0.SampleLevel(lin, (pos.xy - alpha * v) * isz, 0).rgb;
    float3 cb = T1.SampleLevel(lin, (pos.xy + (1.0 - alpha) * v) * isz, 0).rgb;
    float3 mc = lerp(ca, cb, alpha);

    // where the two motion-compensated samples disagree the vector is not to be trusted. The picture then goes
    // smoothly over to the plain blend of the two frames at this pixel (not to the nearer frame: a hard switch
    // between two different pictures scores worse than the blend on real footage).
    float d = abs(Luma(ca) - Luma(cb));
    float w = max(saturate((d - 0.08) / 0.25), HudW(int2(pos.xy)));

    int3 p = int3(int2(pos.xy), 0);
    float3 pa = T0.Load(p).rgb, pb = T1.Load(p).rgb;
    float3 fb = lerp(pa, pb, alpha);
    float3 o = lerp(mc, fb, w);

    // a scene cut (A and B are unrelated pictures: nearly every block matched badly, see cutstat): show the plain
    // blend of the two frames (about one source frame of cross-fade) instead of a warp of two different pictures. On real footage the share of
    // blocks with cost > 0.08 is 0.00-0.04 for pans (up to 0.06 inside a dissolve) and 0.14-0.28 across a cut.
    float cut = saturate((T4.Load(int3(0, 0, 0)).z - 0.07) / 0.05);
    return float4(lerp(o, fb, cut), 1.0);
}

// ---------------------------------------------------------------- hardware optical flow (NVIDIA OFA) path
//   ofconv   R16G16_SINT flow (S10.5) -> float vectors in source pixels (optionally 3x3 vector median)
//   synthof  in-between frame from A, B and the forward (A -> B, anchored at A) and backward (B -> A, anchored at B) flow fields

Texture2D<float4> TF : register(t5);       // forward flow, xy in source pixels
Texture2D<float4> TB : register(t6);       // backward flow
Texture2D<int2>   TI : register(t7);       // raw OFA output

float4 ofconv(float4 pos : SV_Position) : SV_Target
{
    // P1.xy = flow size; P2.x = median on (>0.5); P2.y = source pixels per input pixel (OFA input may be smaller than the picture)
    int2 f = int2(pos.xy);
    int2 mx = int2(P1.xy) - 1;
    float k = P2.y * (1.0 / 32.0);
    float2 p4 = float2(TI.Load(int3(clamp(f, int2(0, 0), mx), 0))) * k;
    if (P2.x < 0.5) return float4(p4, 0, 1);
    float2 p0 = float2(TI.Load(int3(clamp(f + int2(-1, -1), int2(0, 0), mx), 0))) * k;
    float2 p1 = float2(TI.Load(int3(clamp(f + int2( 0, -1), int2(0, 0), mx), 0))) * k;
    float2 p2 = float2(TI.Load(int3(clamp(f + int2( 1, -1), int2(0, 0), mx), 0))) * k;
    float2 p3 = float2(TI.Load(int3(clamp(f + int2(-1,  0), int2(0, 0), mx), 0))) * k;
    float2 p5 = float2(TI.Load(int3(clamp(f + int2( 1,  0), int2(0, 0), mx), 0))) * k;
    float2 p6 = float2(TI.Load(int3(clamp(f + int2(-1,  1), int2(0, 0), mx), 0))) * k;
    float2 p7 = float2(TI.Load(int3(clamp(f + int2( 0,  1), int2(0, 0), mx), 0))) * k;
    float2 p8 = float2(TI.Load(int3(clamp(f + int2( 1,  1), int2(0, 0), mx), 0))) * k;
    SORT(p1, p2); SORT(p4, p5); SORT(p7, p8);
    SORT(p0, p1); SORT(p3, p4); SORT(p6, p7);
    SORT(p1, p2); SORT(p4, p5); SORT(p7, p8);
    SORT(p0, p3); SORT(p5, p8); SORT(p4, p7);
    SORT(p3, p6); SORT(p1, p4); SORT(p2, p5);
    SORT(p4, p7); SORT(p4, p2); SORT(p6, p4);
    SORT(p4, p2);
    return float4(p4, 0, 1);
}

// the picture shrunk by the integer factor P0.z (the optical-flow engine's input at 4K / 1440p)
float4 ofdown(float4 pos : SV_Position) : SV_Target
{
    int2 o = int2(pos.xy);
    int2 mx = int2(P0.xy) - 1;
    int n = (int)P0.z;
    float3 s = 0;
    [loop] for (int dy = 0; dy < n; dy++)
    [loop] for (int dx = 0; dx < n; dx++)
        s += T0.Load(int3(min(o * n + int2(dx, dy), mx), 0)).rgb;
    return float4(s / (n * n), 1);
}

float2 FAt(float2 p) { return TF.SampleLevel(lin, p * P1.zw, 0).xy; }
float2 BAt(float2 p) { return TB.SampleLevel(lin, p * P1.zw, 0).xy; }

float4 synthof(float4 pos : SV_Position) : SV_Target
{
    // P0.xy = picture size | P1.xy = flow size, P1.zw = 1 / (flow size * source pixels per flow texel) | P2.z = alpha | P3.x = mode
    float2 isz = 1.0 / P0.xy;
    float alpha = P2.z;
    int mode = (int)P3.x;
    float2 p = pos.xy;

    float3 ca, cb;
    if (mode <= 1)
    {
        float2 v = FAt(p);
        v = FAt(p - alpha * v);
        ca = T0.SampleLevel(lin, (p - alpha * v) * isz, 0).rgb;
        cb = T1.SampleLevel(lin, (p + (1.0 - alpha) * v) * isz, 0).rgb;
    }
    else
    {
        // two trajectories through p: the forward flow (anchored in A) and the backward flow (anchored in B)
        float2 vf = FAt(p);
        vf = FAt(p - alpha * vf);
        float2 mb = -BAt(p);
        mb = -BAt(p + (1.0 - alpha) * mb);
        float2 xAf = p - alpha * vf, xBf = p + (1.0 - alpha) * vf;
        float2 xAb = p - alpha * mb, xBb = p + (1.0 - alpha) * mb;
        float3 caf = T0.SampleLevel(lin, xAf * isz, 0).rgb, cbf = T1.SampleLevel(lin, xBf * isz, 0).rgb;
        float3 cab = T0.SampleLevel(lin, xAb * isz, 0).rgb, cbb = T1.SampleLevel(lin, xBb * isz, 0).rgb;
        // each trajectory is trusted as far as the opposite flow brings it back (left-right check) and the two pictures agree on it
        float ef = length(BAt(xBf) + vf), eb = length(FAt(xAb) - mb);
        float costF = abs(Luma(caf) - Luma(cbf)) * P3.y + ef * P3.z;
        float costB = abs(Luma(cab) - Luma(cbb)) * P3.y + eb * P3.z;
        if (costF <= costB) { ca = caf; cb = cbf; } else { ca = cab; cb = cbb; }
    }

    float3 mc = lerp(ca, cb, alpha);
    float d = abs(Luma(ca) - Luma(cb));
    float w = max(saturate((d - 0.08) / 0.25), HudW(int2(pos.xy)));
    int3 q = int3(int2(pos.xy), 0);
    float3 fb = lerp(T0.Load(q).rgb, T1.Load(q).rgb, alpha);
    float3 o = lerp(mc, fb, w);
    float cut = saturate((T4.Load(int3(0, 0, 0)).z - 0.07) / 0.05);
    return float4(lerp(o, fb, cut), 1.0);
}

// ---------------------------------------------------------------- HUD protection
// Per pixel "static confidence": grows by P2.y for every consecutive pair of unique pictures in which the pixel did not change
// (P2.x = largest per-channel difference counted as unchanged), drops to 0 as soon as it changes. T2 = the previous confidence.
float4 hudupd(float4 pos : SV_Position) : SV_Target
{
    int3 q = int3(int2(pos.xy), 0);
    float3 d = abs(T0.Load(q).rgb - T1.Load(q).rgb);
    float prev = T2.Load(q).x * P2.z;
    bool stat = max(d.r, max(d.g, d.b)) <= P2.x;
    return float4(stat ? min(1.0, prev + P2.y) : 0.0, 0, 0, 1);
}
