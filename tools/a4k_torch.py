#!/usr/bin/env python3
"""The Anime4K CNN (third_party/Anime4K/Anime4K_Upscale_CNN_x2_{S,M,L}.glsl) as a PyTorch module, and the way back to HLSL.

The network is read from the same GLSL files tools/a4k_gen.py turns into Shaders/a4k_*.hlsl, so the PyTorch model and the GPU
runtime have one common source.  Layout (M):
    MAIN (RGB, 0..1)
    l0   : 3x3 conv  3 -> 4                           (raw RGB, no CReLU)
    l1-6 : 3x3 conv  8 -> 4   on CReLU(previous 4)    CReLU(x) = [max(x,0), max(-x,0)]
    last : 1x1 conv 56 -> 4   on CReLU(all 7 outputs) (S: 3x3 conv on the last layer; L: three 3x3 convs of 16 inputs)
    depth-to-space: the 4 channels are the 4 sub-pixels of a 2x2 block (channel = (y&1)*2 + (x&1)); the single plane
    is added to R, G and B of the bilinear-doubled picture  (L: one plane per colour channel)
Edge pixels repeat (clamp-to-edge) like the mpv hook textures and the HLSL `LD` macro.

    python a4k_torch.py check [M]        load the GLSL weights, compare with an independent term-by-term numpy reference
"""
import copy, os, re, sys
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import a4k_gen as G


def glsl_path(letter):
    return os.path.join(G.SRC, f"Anime4K_Upscale_CNN_x2_{letter}.glsl")


class A4kNet(nn.Module):
    """x: (N,3,H,W) in 0..1 -> (N,3,2H,2W), not clamped."""

    def __init__(self, letter="M", init_from_glsl=True):
        super().__init__()
        self.letter = letter
        self.skeleton = G.parse(glsl_path(letter))            # kept for the export (desc / binds / save / term order)
        self.specs = []                                       # per conv pass: (save, [(input name, 'raw'|'crelu', channels)], k)
        self.convs = nn.ModuleList()
        self.d2s_inputs = []
        for p in self.skeleton:
            if p["kind"] == "d2s":
                self.d2s_inputs = [b for b in p["binds"] if b != "MAIN"]
                continue
            ins = []
            cin = 0
            for b in p["binds"]:
                modes = {t[1] for t in p["terms"] if t[0] == b}
                if modes == {0}:
                    ch = 3 if b == "MAIN" else 4
                    ins.append((b, "raw", ch))
                else:
                    assert modes == {1, -1}, modes
                    ch = 8
                    ins.append((b, "crelu", ch))
                cin += ch
            taps = {(t[2], t[3]) for t in p["terms"]}
            r = max(max(abs(a), abs(b_)) for a, b_ in taps)
            k = 2 * r + 1
            assert len(taps) == k * k
            self.specs.append((p["save"], ins, k))
            self.convs.append(nn.Conv2d(cin, 4, k, padding=0, bias=True))
        if init_from_glsl:
            self.load_glsl()

    # ------------------------------------------------------------------ weights <-> GLSL terms
    @torch.no_grad()
    def load_glsl(self):
        for conv, (save, ins, k), p in zip(self.convs, self.specs, [q for q in self.skeleton if q["kind"] == "conv"]):
            W = np.zeros(tuple(conv.weight.shape), np.float32)
            c = k // 2
            offs, o = {}, 0
            for name, kind, ch in ins:
                offs[name] = o
                o += ch
            for tex, mode, dx, dy, lits in p["terms"]:
                m = np.array([float(x) for x in lits], np.float32).reshape(4, 4).T   # m[i][j] = v[4j+i]: out i, in j
                base = offs[tex]
                kind = next(kk for nn_, kk, _ in ins if nn_ == tex)
                if kind == "raw":
                    assert not m[:, 3].any() or tex != "MAIN"
                    W[:, base:base + (3 if tex == "MAIN" else 4), dy + c, dx + c] = m[:, :3 if tex == "MAIN" else 4]
                else:
                    W[:, base + (0 if mode == 1 else 4):base + (4 if mode == 1 else 8), dy + c, dx + c] = m
            conv.weight.copy_(torch.from_numpy(W))
            conv.bias.copy_(torch.tensor([float(x) for x in (p["bias"] or ["0"] * 4)], dtype=torch.float32))

    def export_passes(self):
        """The skeleton with the current weights written back as GLSL-style terms (same order / modes / taps)."""
        out = copy.deepcopy(self.skeleton)
        convs = [q for q in out if q["kind"] == "conv"]
        for conv, (save, ins, k), p in zip(self.convs, self.specs, convs):
            W = conv.weight.detach().cpu().numpy().astype(np.float32)
            b = conv.bias.detach().cpu().numpy().astype(np.float32)
            c = k // 2
            offs, o = {}, 0
            for name, kind, ch in ins:
                offs[name] = o
                o += ch
            terms = []
            for tex, mode, dx, dy, _ in p["terms"]:
                base = offs[tex]
                kind = next(kk for nn_, kk, _ in ins if nn_ == tex)
                m = np.zeros((4, 4), np.float32)
                if kind == "raw":
                    n = 3 if tex == "MAIN" else 4
                    m[:, :n] = W[:, base:base + n, dy + c, dx + c]
                else:
                    s = base + (0 if mode == 1 else 4)
                    m[:, :] = W[:, s:s + 4, dy + c, dx + c]
                v = m.T.reshape(16)                     # v[4j+i] = m[i][j]
                terms.append((tex, mode, dx, dy, [fmt(x) for x in v]))
            p["terms"] = terms
            p["bias"] = [fmt(x) for x in b]
        return out

    # ------------------------------------------------------------------ forward
    def forward(self, x):
        tex = {"MAIN": x}
        for conv, (save, ins, k) in zip(self.convs, self.specs):
            feats = []
            for name, kind, ch in ins:
                t = tex[name]
                feats.append(torch.cat([F.relu(t), F.relu(-t)], 1) if kind == "crelu" else t)
            y = torch.cat(feats, 1) if len(feats) > 1 else feats[0]
            if k > 1:
                y = F.pad(y, (k // 2,) * 4, mode="replicate")
            tex[save] = F.conv2d(y, conv.weight, conv.bias)
        base = F.interpolate(x, scale_factor=2, mode="bilinear", align_corners=False)
        planes = [F.pixel_shuffle(tex[n], 2) for n in self.d2s_inputs]
        c = planes[0].expand(-1, 3, -1, -1) if len(planes) == 1 else torch.cat(planes, 1)
        return base + c


def fmt(x):
    return str(np.float32(x))


# ---------------------------------------------------------------------- HLSL export (through tools/a4k_gen.py)
def export_hlsl(net, out_path, label):
    """Write the HLSL of <net> (same entry points / wiring as Shaders/a4k_<letter>.hlsl, so A4kModels.Passes<letter> applies)."""
    model, n = G.emit_upscale(net.export_passes(), f"user-trained model {label} (architecture of Anime4K x2 {net.letter})", out_path, user=True)
    return model, n


# ---------------------------------------------------------------------- independent reference (the C# A4kTest.Reference semantics)
def reference_numpy(letter, rgb):
    """Term-by-term interpretation of the *original GLSL text* (not through A4kNet): rgb (H,W,3) float32 -> (2H,2W,3)."""
    passes = G.parse(glsl_path(letter))
    h, w, _ = rgb.shape
    main = np.concatenate([rgb, np.ones((h, w, 1), np.float32)], 2).astype(np.float32)
    tex = {"MAIN": main}
    for p in passes:
        if p["kind"] == "conv":
            out = np.empty((h, w, 4), np.float32)
            out[:] = np.array([float(x) for x in (p["bias"] or ["0"] * 4)], np.float32)
            for t, mode, dx, dy, lits in p["terms"]:
                m = np.array([float(x) for x in lits], np.float32).reshape(4, 4)     # m[j][i] = v[4j+i]
                src = tex[t]
                ys = np.clip(np.arange(h) + dy, 0, h - 1)
                xs = np.clip(np.arange(w) + dx, 0, w - 1)
                v = src[ys][:, xs]
                if mode == 1: v = np.maximum(v, 0)
                elif mode == -1: v = np.maximum(-v, 0)
                out += v @ m
            tex[p["save"]] = out
        else:
            ow, oh = w * 2, h * 2
            res = np.empty((oh, ow, 3), np.float32)
            outs = [b for b in p["binds"] if b != "MAIN"]
            ys = (np.arange(oh) + 0.5) / 2 - 0.5
            xs = (np.arange(ow) + 0.5) / 2 - 0.5
            y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
            fy = (ys - y0)[:, None, None].astype(np.float32); fx = (xs - x0)[None, :, None].astype(np.float32)
            def S(yy, xx): return main[np.clip(yy, 0, h - 1)][:, np.clip(xx, 0, w - 1), :3]
            base = (S(y0, x0) * (1 - fx) + S(y0, x0 + 1) * fx) * (1 - fy) + (S(y0 + 1, x0) * (1 - fx) + S(y0 + 1, x0 + 1) * fx) * fy
            yy = np.arange(oh)[:, None] >> 1; xx = np.arange(ow)[None, :] >> 1
            comp = ((np.arange(oh)[:, None] & 1) * 2 + (np.arange(ow)[None, :] & 1))
            cs = [np.take_along_axis(tex[o][yy, xx], comp[..., None], 2)[..., 0] for o in outs]
            if len(cs) == 1: cs = cs * 3
            res = base + np.stack(cs, 2)
            return res.astype(np.float32)
    raise SystemExit("no depth-to-space pass")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "check"
    letter = sys.argv[2] if len(sys.argv) > 2 else "M"
    if cmd == "check":
        torch.manual_seed(1)
        net = A4kNet(letter).double().eval()
        rng = np.random.default_rng(3)
        for (h, w) in [(23, 31), (40, 64)]:
            img = rng.random((h, w, 3)).astype(np.float32)
            ref = reference_numpy(letter, img)
            with torch.no_grad():
                got = net(torch.from_numpy(img).permute(2, 0, 1)[None].double())[0].permute(1, 2, 0).numpy()
            print(f"{letter} {w}x{h}: torch(f64) vs numpy-f32 reference of the GLSL text: max|diff| = {np.abs(got - ref).max():.3e}")
