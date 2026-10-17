# Anime4K (CNN upscaler)

The weights and network layout of the Direct3D renderer's 「くっきり（アニメ・イラスト向け）」 scaling quality.

- Source: https://github.com/bloc97/Anime4K
- Version: tag `v4.0.1`, commit `4029bf701ecaa15f163cdc49cffe5501c1acf410` (2022). The files below are byte-identical to the
  same files at the current `master` (7684e95, 2024-04-26); only the GAN shaders changed since.
- Files (unmodified): `LICENSE`, and from `glsl/Upscale/`: `Anime4K_Upscale_CNN_x2_S.glsl`, `_M.glsl`, `_L.glsl`
  (the mpv user-shader passes, labelled "Anime4K-v3.2" inside).
- License: MIT, Copyright (c) 2019 bloc97 (each shader file repeats it, 2019-2021). Keep the notice with any copy.

## What was ported

`tools/a4k_gen.py` reads the GLSL and writes `Shaders/a4k_s.hlsl`, `a4k_m.hlsl`, `a4k_l.hlsl` (ASCII, weights copied
as literals) and `A4kModels.g.cs` (which pass reads and writes which texture). The passes:

1. convolution layers (3x3, 4 output channels per pass; inputs are the previous layers with CReLU = max(x,0) and max(-x,0));
2. depth-to-space of the last layer (4 values per pixel become a 2x2 block) added onto the bilinear-enlarged source.
   For S and M (the last layer is 1x1) it is done in the same pass as the last convolution.

Unchanged from the original: all weights, the CReLU/clamp-to-edge arithmetic, the residual added equally to R, G and B
(S, M) or per channel (L). Different from mpv: textures are float16 (float32 in the test), there is no `//!WHEN` gate
(YuCap decides in `D3DRenderer.A4kPlan`), and the result is not clamped before the sharpening that follows.
Not ported: `Restore`, `Upscale_CNN_x2_VL/UL`, the GAN shaders, the denoise/deblur variants.

`--a4k-check` runs a CPU reference that interprets the original `.glsl` files (not the generated HLSL) and compares it with the GPU.
