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
    int4   orient;  // rotation steps (0..3, clockwise x90), mirror (0/1), adjust on (0/1), 0
    float4 coef;    // Cr->R, Cb->G, Cr->G, Cb->B
    float4 range;   // yOffset, yScale, cOffset, cScale
    float4 adj;     // brightness (additive), contrast, 1/gamma, saturation
    int4   crop;    // left, top of the cropped sub-rectangle in the SOURCE (before rotation), 0, 0
};

Texture2D<float>  texY  : register(t0);
Texture2D<float2> texUV : register(t1);
SamplerState      lin   : register(s0);

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
    float2 uv = float2((s.x + 0.5) / dims.x, (s.y + 0.5) / dims.y);
    float2 c = texUV.SampleLevel(lin, uv, 0);

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
    }
    return float4(rgb, 1.0);
}
