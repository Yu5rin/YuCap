// NV12 -> RGB with orientation (rotate clockwise first, then mirror horizontally,
// the same order as VideoEngine.OrientLikePreview). Each output pixel is mapped back
// to a source pixel with integer math, so stills are exact.
// Picture adjustments (brightness / contrast / saturation / gamma) are applied here, after
// YUV->RGB and before any scaling, so the oriented texture - and therefore stills, the pause
// image and the FSR input - carries exactly what is on screen.
// Cropping: dims.zw is the size of the CROPPED, rotated picture; the mapping below runs inside the
// cropped picture and crop.xy then shifts it to its place in the full source frame, so only the
// sub-rectangle is ever sampled. dims.xy stays the full source size (texture coordinates).
cbuffer Convert : register(b0)
{
    int4   dims;    // srcW, srcH, outW, outH
    int4   orient;  // rotation steps (0..3, clockwise x90), mirror (0/1), adjust on (0/1), shadow boost level (0..3)
    float4 coef;    // Cr->R, Cb->G, Cr->G, Cb->B
    float4 range;   // yOffset, yScale, cOffset, cScale
    float4 adj;     // brightness (additive), contrast, 1/gamma, saturation
    int4   crop;    // left, top of the cropped sub-rectangle in the SOURCE (before rotation), chroma mode, chroma variant
    float4 ctune;   // chroma upsampling tuning (see ChromaCfl)
};

Texture2D<float>  texY  : register(t0);
Texture2D<float2> texUV : register(t1);
SamplerState      lin   : register(s0);


// ---------------------------------------------------------------------------------------------
// Luma-guided chroma upsampling (the "reduce colour bleeding" option). NV12 holds one Cb/Cr pair per 2x2 luma pixels; sampling it
// bilinearly smears every coloured edge over two pixels. Chroma sample (i,j) sits at luma x = 2i (ON the even
// column) and luma y = 2j + 0.5 (BETWEEN the rows) - MPEG-2 / BT.709 "left" siting, what consoles' 4:2:0 and a
// YUY2 -> NV12 conversion produce. The luma, which is at full resolution, tells where the edges are:
//   mode 1  bilinear with that siting (no luma),
//   mode 2  joint bilateral: the four chroma samples around the pixel are weighted by how close the luma at their site
//           is to the pixel's own luma,
//   mode 3  chroma from luma: a local linear fit  chroma = a * luma + b  over the 3x3 chroma samples around the pixel
//           (weighted by distance), applied to the pixel's luma detail on top of the bilinear value. Thin coloured lines
//           and text get their saturation back, flat and smooth areas stay bilinear (a -> 0 where luma does not vary).
// ctune: x = how far (in units of the local chroma range) a result may leave the range of the four nearest chroma
// samples, y = 1/sigma^2 of the bilateral luma weight, z = distance weight (exp(-d^2 z)), w = regularisation of the fit.
// ---------------------------------------------------------------------------------------------
static int2 gCDim;

float LumaAt(int2 p)
{
    p = clamp(p, int2(0, 0), dims.xy - 1);
    return texY.Load(int3(p, 0));
}

float2 ChromaTex(int2 c)
{
    c = clamp(c, int2(0, 0), gCDim - 1);
    return texUV.Load(int3(c, 0));
}

// The luma the encoder's chroma filter saw at chroma site c.
float SiteLuma(int2 c)
{
    int2 p = int2(2 * c.x, 2 * c.y);
    float a = LumaAt(p) + LumaAt(p + int2(0, 1));
    if ((crop.w & 1) != 0)
    {
        float b = LumaAt(p + int2(-1, 0)) + LumaAt(p + int2(-1, 1)) + LumaAt(p + int2(1, 0)) + LumaAt(p + int2(1, 1));
        return (2.0 * a + b) * 0.125;
    }
    return a * 0.5;
}

float2 ChromaUp(int2 s, float Yp)
{
    gCDim = (dims.xy + 1) >> 1;
    float2 pc = float2(s.x * 0.5, (s.y - 0.5) * 0.5);
    float2 fl = floor(pc);
    int2 i0 = (int2)fl;
    float2 f = pc - fl;
    float4 wb = float4((1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y);
    float2 c00 = ChromaTex(i0), c10 = ChromaTex(i0 + int2(1, 0)), c01 = ChromaTex(i0 + int2(0, 1)), c11 = ChromaTex(i0 + int2(1, 1));
    float2 bil = c00 * wb.x + c10 * wb.y + c01 * wb.z + c11 * wb.w;
    if (crop.z == 1) return bil;

    if (crop.z == 2)
    {
        float4 L = float4(SiteLuma(i0), SiteLuma(i0 + int2(1, 0)), SiteLuma(i0 + int2(0, 1)), SiteLuma(i0 + int2(1, 1)));
        float4 d = Yp - L;
        float4 w = wb * (0.02 + exp(-d * d * ctune.y));
        return (c00 * w.x + c10 * w.y + c01 * w.z + c11 * w.w) / (w.x + w.y + w.z + w.w);
    }

    // mode 3
    int2 ic = (int2)floor(pc + 0.5);
    // luma tile: 8 columns x 6 rows from (2 ic.x - 3, 2 ic.y - 2), read with 12 gathers (the clamp sampler handles the borders)
    float T[6][8];
    float2 gsz = 1.0 / float2(dims.xy);
    float2 org = (float2(2 * ic.x - 3, 2 * ic.y - 2) + 1.0) * gsz;
    [unroll] for (int gy = 0; gy < 3; gy++)
    [unroll] for (int gx = 0; gx < 4; gx++)
    {
        float4 g4 = texY.GatherRed(lin, org + float2(2 * gx, 2 * gy) * gsz);
        T[2 * gy][2 * gx] = g4.w; T[2 * gy][2 * gx + 1] = g4.z;
        T[2 * gy + 1][2 * gx] = g4.x; T[2 * gy + 1][2 * gx + 1] = g4.y;
    }
    float sw = 0, sL = 0, sLL = 0;
    float2 sC = 0, sLC = 0;
    float Ls[9];
    float2 Cs[9];
    [unroll] for (int k = 0; k < 9; k++)
    {
        int sx = k % 3, sy = k / 3;
        float L;
        if ((crop.w & 1) != 0)
            L = (2.0 * (T[2 * sy][2 * sx + 1] + T[2 * sy + 1][2 * sx + 1]) + T[2 * sy][2 * sx] + T[2 * sy + 1][2 * sx] + T[2 * sy][2 * sx + 2] + T[2 * sy + 1][2 * sx + 2]) * 0.125;
        else
            L = (T[2 * sy][2 * sx + 1] + T[2 * sy + 1][2 * sx + 1]) * 0.5;
        int2 site = ic + int2(sx - 1, sy - 1);
        float2 C = ChromaTex(site);
        float2 dd = float2(site) - pc;
        float w = exp(-dot(dd, dd) * ctune.z);
        Ls[k] = L; Cs[k] = C;
        sw += w; sL += w * L; sLL += w * L * L; sC += w * C; sLC += w * L * C;
    }
    float mL = sL / sw;
    float2 mC = sC / sw;
    float varL = sLL / sw - mL * mL;
    float2 cov = sLC / sw - mL * mC;
    float2 a = cov / (max(varL, 0.0) + ctune.w);
    int2 oi = i0 - ic + 1;     // footprint corner inside the 3x3 (0 or 1 each)
    float Lb = 0; float2 lo = 1e9, hi = -1e9;
    [unroll] for (int q = 0; q < 4; q++)
    {
        int ox = q & 1, oy = q >> 1;
        int idx = (oi.y + oy) * 3 + oi.x + ox;
        float wq = q == 0 ? wb.x : (q == 1 ? wb.y : (q == 2 ? wb.z : wb.w));
        Lb += wq * Ls[idx];
        lo = min(lo, Cs[idx]); hi = max(hi, Cs[idx]);
    }
    float2 r = bil + a * (Yp - Lb);
    float2 rg = hi - lo;
    return clamp(r, lo - rg * ctune.x, hi + rg * ctune.x);
}

float4 main(float4 pos : SV_Position) : SV_Target
{
    int2 o = int2(pos.xy);
    int rw = dims.z;   // rotated image width  (= output width)
    int rh = dims.w;   // rotated image height (= output height)
    // Output = mirror(rotate(source)): undo the mirror first, then the rotation.
    int x = orient.y != 0 ? rw - 1 - o.x : o.x;
    int y = o.y;
    int2 s;
    if (orient.x == 0)      s = int2(x, y);
    else if (orient.x == 1) s = int2(y, rw - 1 - x);
    else if (orient.x == 2) s = int2(rw - 1 - x, rh - 1 - y);
    else                    s = int2(rh - 1 - y, x);

    s += crop.xy;
    float Y = texY.Load(int3(s, 0));
    float2 c;
    if (crop.z == 0)
    {
        // plain bilinear chroma (the hardware sampler), the way it always was
        float2 uv = float2((s.x + 0.5) / dims.x, (s.y + 0.5) / dims.y);
        c = texUV.SampleLevel(lin, uv, 0);
    }
    else c = ChromaUp(s, Y);

    float yy = (Y - range.x) * range.y;
    float cb = (c.x - range.z) * range.w;
    float cr = (c.y - range.z) * range.w;
    float3 rgb = float3(yy + coef.x * cr,
                        yy - coef.y * cb - coef.z * cr,
                        yy + coef.w * cb);
    rgb = saturate(rgb);
    // Skipped entirely when neutral (orient.z == 0) so the default path stays an exact passthrough.
    if (orient.z != 0)
    {
        rgb = pow(rgb, adj.z);                                  // gamma
        rgb = (rgb - 0.5) * adj.y + 0.5;                        // contrast around mid grey
        rgb = rgb + adj.x;                                      // brightness
        float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));  // BT.709 luma
        rgb = lerp(float3(luma, luma, luma), rgb, adj.w);       // saturation
        rgb = saturate(rgb);
        // Shadow boost: lift the luma of dark tones by k*y*(1-smoothstep(0.15,0.5,y)) and add the same
        // amount to R, G and B (Cb/Cr stay as they are). 0 -> 0 and everything from y=0.5 up is untouched.
        if (orient.w > 0)
        {
            float k = orient.w == 1 ? 0.2 : (orient.w == 2 ? 0.4 : 0.65);
            float ly = dot(rgb, float3(0.2126, 0.7152, 0.0722));
            float sh = smoothstep(0.15, 0.5, ly);
            rgb = saturate(rgb + k * ly * (1.0 - sh));
        }
    }
    return float4(rgb, 1.0);
}
