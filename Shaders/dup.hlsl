// Same-picture detection (see README design notes): one compute dispatch per captured frame.
//   thumb   Y plane of the frame -> 120x68 luma thumbnail (each pixel the mean of up to 8x8 bilinear taps, i.e. of the whole
//           16x16 block of a 1080p source), and the difference to the previous frame's thumbnail reduced to four numbers:
//             Stat[0] sum of |d| * 64        (levels 0..255, so the mean is Stat[0] / 64 / 8160)
//             Stat[1] max of |d| * 64
//             Stat[2] number of thumbnail pixels with |d| > T1
//             Stat[3] number of thumbnail pixels with |d| > T2
// The result is read back asynchronously (staging copy, Map with DO_NOT_WAIT) a frame or so later.
cbuffer Cb : register(b0)
{
    uint4  P0;   // x,y: source width, height | z: 1 when Prev holds the previous frame's thumbnail
    float4 P1;   // x: T1, y: T2 (levels)
};

#define TW 120
#define TH 68

Texture2D<float>  Y    : register(t0);
Buffer<float>     Prev : register(t1);
RWBuffer<float>   Cur  : register(u0);
RWBuffer<uint>    Stat : register(u1);
SamplerState      lin  : register(s0);

groupshared float gSum[64];
groupshared float gMax[64];
groupshared uint  gC1[64];
groupshared uint  gC2[64];

[numthreads(8, 8, 1)]
void thumb(uint3 id : SV_DispatchThreadID, uint gi : SV_GroupIndex)
{
    float d = 0;
    uint c1 = 0, c2 = 0;
    if (id.x < TW && id.y < TH)
    {
        float x0 = (float)(id.x * P0.x / TW), x1 = (float)((id.x + 1) * P0.x / TW);
        float y0 = (float)(id.y * P0.y / TH), y1 = (float)((id.y + 1) * P0.y / TH);
        float bw = max(x1 - x0, 1.0), bh = max(y1 - y0, 1.0);
        uint nx = (uint)clamp(round(bw * 0.5), 1.0, 8.0), ny = (uint)clamp(round(bh * 0.5), 1.0, 8.0);
        float2 inv = 1.0 / float2(P0.x, P0.y);
        float s = 0;
        [loop] for (uint j = 0; j < ny; j++)
        [loop] for (uint i = 0; i < nx; i++)
        {
            float2 p = float2(x0 + (i + 0.5) * bw / nx, y0 + (j + 0.5) * bh / ny);
            s += Y.SampleLevel(lin, p * inv, 0);
        }
        float v = s / (float)(nx * ny) * 255.0;
        uint idx = id.y * TW + id.x;
        Cur[idx] = v;
        if (P0.z != 0)
        {
            d = abs(v - Prev[idx]);
            c1 = d > P1.x ? 1 : 0;
            c2 = d > P1.y ? 1 : 0;
        }
    }
    gSum[gi] = d; gMax[gi] = d; gC1[gi] = c1; gC2[gi] = c2;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint k = 32; k > 0; k >>= 1)
    {
        if (gi < k)
        {
            gSum[gi] += gSum[gi + k];
            gMax[gi] = max(gMax[gi], gMax[gi + k]);
            gC1[gi] += gC1[gi + k];
            gC2[gi] += gC2[gi + k];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (gi == 0)
    {
        uint ignore;
        InterlockedAdd(Stat[0], (uint)(gSum[0] * 64.0), ignore);
        InterlockedMax(Stat[1], (uint)(gMax[0] * 64.0), ignore);
        InterlockedAdd(Stat[2], gC1[0], ignore);
        InterlockedAdd(Stat[3], gC2[0], ignore);
    }
}
