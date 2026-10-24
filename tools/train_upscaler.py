#!/usr/bin/env python3
"""Train your own x2 upscaler with the Anime4K CNN layout (so YuCap's GPU runtime runs it unchanged).

Run with the training venv (see tools/setup_train_env.ps1):   %LOCALAPPDATA%\\YuCapTrain\\venv\\Scripts\\python.exe tools\\train_upscaler.py <command>

  train   --name mine --data <dir> [<dir> ...]  [--minutes 45] [--arch S|M|L] [--loss edge|texture]     fine-tune from the original Anime4K weights, export when done
  eval    --name mine --data <dir> ...                              validation table: bilinear / bicubic / Anime4K original / yours
  export  --name mine [--ckpt best|last]                            write models\\<name>.hlsl (+ .json) again from a checkpoint
  dump    --name mine|orig --image in.png --out out.f32             PyTorch reference output
  equiv   --name mine|M --image in.png --exe YuCap.exe              exported shader on the GPU vs PyTorch (float64): max diff must be < 1e-3

Data: folders of lossless frames (the PNGs YuCap's "学習用の絵を集める" saves, plus anything else that is a clean 1080p-or-larger
picture). Each folder may have an index.jsonl (YuCap writes one: file name, size and the estimated native resolution of the frame).
Frames are split into training / validation by *scene group* (consecutive captures or the same tNN_ prefix stay together) so that
the validation never sees a picture it was trained on.

What is learned: low-resolution -> picture, x2.  The low-resolution input is made from the clean frame by the kind of damage a
console capture has (scaler kernel, 4:2:2 / 4:2:0 chroma, a little noise, 8-bit rounding); the loss is L1 plus a small edge term (--loss edge) or L1 + SSIM + a high-pass texture term (--loss texture); --arch S|M|L picks the network size.
Nothing here needs a pretrained network or a download.
"""
import argparse, glob, hashlib, json, math, os, random, re, sys, time
import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import a4k_torch as T

TRAIN_ROOT = os.environ.get("YUCAP_TRAIN_DIR") or os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "YuCapTrain")
DEV = "cuda" if torch.cuda.is_available() else "cpu"


# ---------------------------------------------------------------------------- data
def list_frames(dirs):
    """-> list of dict(path, group, weight). Group = scene id used for the train/validation split."""
    items = []
    for d in dirs:
        idx = {}
        ip = os.path.join(d, "index.jsonl")
        if os.path.exists(ip):
            for line in open(ip, encoding="utf-8-sig", errors="ignore"):
                try:
                    j = json.loads(line)
                    idx[j["f"]] = j
                except Exception:
                    pass
        for p in sorted(glob.glob(os.path.join(d, "**", "*.png"), recursive=True)):
            name = os.path.basename(p)
            j = idx.get(name)
            try:
                with Image.open(p) as im:
                    w, h = im.size
            except Exception:
                continue
            if w < 960 or h < 540:
                continue                                  # too small to be a clean source; the half-size seq frames etc.
            weight = 1.0
            est = 0
            if j is not None and j.get("estV", 0) and j.get("h", h):
                est = int(j["estV"])
                if est < 0.85 * j["h"]:
                    weight = 0.7                  # a stretched 720p picture: trained on its real-detail version (see load_for_training), a little less often
            if j is not None and "t" in j and re.fullmatch(r"\d{8}_\d{6}_\d{3}", os.path.splitext(name)[0]):
                group = f"{os.path.basename(d)}:{int(j['t'] // 300)}"       # pictures YuCap collected: five-minute chunks of play stay together
            else:
                stem = os.path.splitext(name)[0]
                group = f"{os.path.basename(d)}:{stem.split('_')[0]}"       # t05_f1.png -> t05
            items.append(dict(path=p, group=group, weight=weight, size=(w, h), est=est))
    return items


def split(items, val_every=4):
    groups = sorted({it["group"] for it in items})
    val_groups = {g for i, g in enumerate(groups) if i % val_every == val_every - 2}      # a spread of scenes, never the first
    tr = [it for it in items if it["group"] not in val_groups]
    va = [it for it in items if it["group"] in val_groups]
    return tr, va, sorted(val_groups)


def load_u8(path):
    with Image.open(path) as im:
        im = im.convert("RGB")
        return torch.from_numpy(np.asarray(im).copy()).permute(2, 0, 1).contiguous()          # (3,H,W) uint8


def load_for_training(it, dev, native=True):
    """Training target of one frame (uint8 on the device).  A frame whose measured detail is clearly below its size (a 720p game stretched to
    1080p by the console) is shrunk to its real detail first: the clean 720p picture the game drew is the truth worth learning from, and it is what
    YuCap's 元の細かさに合わせて拡大 hands to the network."""
    u8 = load_u8(it["path"]).to(dev)
    h = u8.shape[1]
    est = it.get("est", 0)
    if native and est and est < 0.85 * h:
        nw = max(2, int(round(u8.shape[2] * est / h / 2)) * 2)
        u8 = F.interpolate(u8.float()[None], size=(est // 2 * 2, nw), mode="bicubic", align_corners=False, antialias=True).clamp(0, 255).round().byte()[0]
    return u8


# ---------------------------------------------------------------------------- degradation (HR float batch -> LR float batch)
def rgb2ycc(x):
    r, g, b = x[:, 0:1], x[:, 1:2], x[:, 2:3]
    y = 0.2126 * r + 0.7152 * g + 0.0722 * b
    return torch.cat([y, (b - y) / 1.8556, (r - y) / 1.5748], 1)


def ycc2rgb(x):
    y, cb, cr = x[:, 0:1], x[:, 1:2], x[:, 2:3]
    r = y + 1.5748 * cr
    b = y + 1.8556 * cb
    g = (y - 0.2126 * r - 0.0722 * b) / 0.7152
    return torch.cat([r, g, b], 1)


def chroma_subsample(x, mode):
    if mode == "none":
        return x
    c = rgb2ycc(x)
    ch = c[:, 1:3]
    if mode == "422":
        small = F.avg_pool2d(ch, (1, 2))
        up = F.interpolate(small, scale_factor=(1, 2), mode="bilinear", align_corners=False)
    else:  # 420
        small = F.avg_pool2d(ch, 2)
        up = F.interpolate(small, scale_factor=2, mode="bilinear", align_corners=False)
    return ycc2rgb(torch.cat([c[:, 0:1], up], 1))


def downscale2(x, kind):
    if kind == "area":
        return F.avg_pool2d(x, 2)
    if kind == "bilinear":
        return F.interpolate(x, scale_factor=0.5, mode="bilinear", align_corners=False, antialias=True)
    if kind == "bicubic":
        return F.interpolate(x, scale_factor=0.5, mode="bicubic", align_corners=False, antialias=True)
    if kind == "nearest":
        return x[:, :, ::2, ::2]
    raise ValueError(kind)


KINDS = ["bicubic", "bilinear", "area", "bicubic", "bilinear"]
CHROMA = ["422", "420", "none", "422"]


def degrade(hr, rng=None, fixed=None):
    """hr (B,3,H,W) 0..1 -> lr (B,3,H/2,W/2).  fixed = (kind, chroma, noise) for the validation set."""
    rng = rng or random
    B = hr.shape[0]
    outs = []
    for i in range(B):
        x = hr[i:i + 1]
        if fixed:
            kind, ch, noise = fixed
        else:
            kind, ch = rng.choice(KINDS), rng.choice(CHROMA)
            noise = rng.choice([0.0, 0.0, 0.003, 0.006, 0.01])
            if rng.random() < 0.3:                     # a soft scaler / a lens-like blur before the decimation
                s = rng.uniform(0.3, 0.8)
                k = torch.arange(-2, 3, device=x.device, dtype=x.dtype)
                g = torch.exp(-(k ** 2) / (2 * s * s)); g = g / g.sum()
                x = F.conv2d(F.pad(x, (2, 2, 0, 0), mode="replicate"), g.view(1, 1, 1, 5).repeat(3, 1, 1, 1), groups=3)
                x = F.conv2d(F.pad(x, (0, 0, 2, 2), mode="replicate"), g.view(1, 1, 5, 1).repeat(3, 1, 1, 1), groups=3)
        lr = downscale2(x, kind)
        lr = chroma_subsample(lr, ch)
        if noise > 0:
            lr = lr + torch.randn_like(lr) * noise
        lr = torch.round(lr.clamp(0, 1) * 255) / 255                  # 8-bit capture
        outs.append(lr)
    return torch.cat(outs, 0)


# ---------------------------------------------------------------------------- metrics
def gauss_win(n=11, s=1.5, device="cpu"):
    k = torch.arange(n, device=device, dtype=torch.float32) - n // 2
    g = torch.exp(-(k ** 2) / (2 * s * s)); g /= g.sum()
    return (g[:, None] * g[None, :])[None, None]


def ssim(a, b):
    """mean SSIM over the colour channels, gaussian 11x11, data range 1 (a, b: (1,3,H,W))."""
    w = gauss_win(device=a.device).repeat(3, 1, 1, 1)
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    mu_a = F.conv2d(a, w, groups=3); mu_b = F.conv2d(b, w, groups=3)
    saa = F.conv2d(a * a, w, groups=3) - mu_a ** 2
    sbb = F.conv2d(b * b, w, groups=3) - mu_b ** 2
    sab = F.conv2d(a * b, w, groups=3) - mu_a * mu_b
    m = ((2 * mu_a * mu_b + c1) * (2 * sab + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (saa + sbb + c2))
    return m.mean().item()


def psnr(a, b):
    mse = F.mse_loss(a, b).item()
    return 99.0 if mse <= 1e-12 else -10 * math.log10(mse)


def edge_strength(x):
    y = (0.2126 * x[:, 0:1] + 0.7152 * x[:, 1:2] + 0.0722 * x[:, 2:3])
    gx = y[:, :, :, 1:] - y[:, :, :, :-1]
    gy = y[:, :, 1:, :] - y[:, :, :-1, :]
    return gx.abs().mean().item() + gy.abs().mean().item()


def _gauss1d(s, device, dtype, n=None):
    n = n or int(2 * math.ceil(3 * s) + 1)
    k = torch.arange(n, device=device, dtype=dtype) - n // 2
    g = torch.exp(-(k ** 2) / (2 * s * s)); return g / g.sum()


def blur(x, s):
    """Separable gaussian blur (replicate padding) of (B,C,H,W)."""
    g = _gauss1d(s, x.device, x.dtype); n = g.numel(); C = x.shape[1]
    x = F.conv2d(F.pad(x, (n // 2, n // 2, 0, 0), mode="replicate"), g.view(1, 1, 1, n).repeat(C, 1, 1, 1), groups=C)
    return F.conv2d(F.pad(x, (0, 0, n // 2, n // 2), mode="replicate"), g.view(1, 1, n, 1).repeat(C, 1, 1, 1), groups=C)


def ssim_loss(a, b):
    """1 - mean SSIM (differentiable; gaussian 11x11 on the three colour channels)."""
    w = gauss_win(device=a.device).repeat(3, 1, 1, 1)
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    mu_a = F.conv2d(a, w, groups=3); mu_b = F.conv2d(b, w, groups=3)
    saa = F.conv2d(a * a, w, groups=3) - mu_a ** 2
    sbb = F.conv2d(b * b, w, groups=3) - mu_b ** 2
    sab = F.conv2d(a * b, w, groups=3) - mu_a * mu_b
    m = ((2 * mu_a * mu_b + c1) * (2 * sab + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (saa + sbb + c2))
    return 1 - m.mean()


def texture_loss(sr, hr):
    """Match the fine texture, not only the edges: L1 on two high-pass bands of the luma (x - blur(x) at sigma 0.8 and 1.6, the grain / hatching / cloth
    scale) plus the local contrast (gaussian-weighted standard deviation of the 0.8-band) so that a flattened area is not rewarded."""
    def y(x): return 0.2126 * x[:, 0:1] + 0.7152 * x[:, 1:2] + 0.0722 * x[:, 2:3]
    ys, yh = y(sr), y(hr)
    t = 0.0
    hs = hh = None
    for i, s in enumerate((0.8, 1.6)):
        a = ys - blur(ys, s); b = yh - blur(yh, s)
        t = t + (a - b).abs().mean()
        if i == 0: hs, hh = a, b
    vs = blur(hs * hs, 1.5).clamp_min(0).add(1e-6).sqrt(); vh = blur(hh * hh, 1.5).clamp_min(0).add(1e-6).sqrt()
    return t + (vs - vh).abs().mean()


def sobel_l1(a, b):
    def g(x):
        y = (0.2126 * x[:, 0:1] + 0.7152 * x[:, 1:2] + 0.0722 * x[:, 2:3])
        return y[:, :, :, 1:] - y[:, :, :, :-1], y[:, :, 1:, :] - y[:, :, :-1, :]
    ax, ay = g(a); bx, by = g(b)
    return (ax - bx).abs().mean() + (ay - by).abs().mean()


# ---------------------------------------------------------------------------- model registry
def models_dir():
    return os.path.join(TRAIN_ROOT, "models")


def run_dir(name):
    return os.path.join(TRAIN_ROOT, "runs", name)


def load_model(name, ckpt="best"):
    if name in ("orig", "original", "M", "S", "L", "origM", "origL", "origS"):
        n = T.A4kNet("M" if name in ("orig", "original") else name[-1])
    else:
        p = os.path.join(run_dir(name), f"ckpt_{ckpt}.pt")
        st = torch.load(p, map_location="cpu")
        n = T.A4kNet(st.get("letter", "M"), init_from_glsl=False)
        n.load_state_dict(st["model"])
    return n.to(DEV).eval()


def save_ckpt(net, name, tag, extra):
    os.makedirs(run_dir(name), exist_ok=True)
    torch.save(dict(model=net.state_dict(), letter=net.letter, **extra), os.path.join(run_dir(name), f"ckpt_{tag}.pt"))


# ---------------------------------------------------------------------------- evaluation
@torch.no_grad()
def evaluate(nets, val_items, fixed=("bicubic", "422", 0.0), max_frames=None, size=None, native=False):
    """nets: dict label -> callable(lr)->sr or None for built-in baselines. Returns {label: dict(psnr, ssim, edge)} (means over frames)."""
    res = {}
    frames = val_items[:max_frames] if max_frames else val_items
    for it in frames:
        hr = (load_for_training(it, DEV, True) if native else load_u8(it["path"]).to(DEV)).float()[None] / 255
        H, W = hr.shape[2] // 2 * 2, hr.shape[3] // 2 * 2
        hr = hr[:, :, :H, :W]
        lr = degrade(hr, fixed=fixed)
        outs = {"bilinear": F.interpolate(lr, scale_factor=2, mode="bilinear", align_corners=False),
                "bicubic": F.interpolate(lr, scale_factor=2, mode="bicubic", align_corners=False)}
        for label, f in nets.items():
            outs[label] = f(lr)
        for label, sr in outs.items():
            sr = torch.round(sr.clamp(0, 1) * 255) / 255
            r = res.setdefault(label, dict(psnr=[], ssim=[], edge=[]))
            r["psnr"].append(psnr(sr, hr)); r["ssim"].append(ssim(sr, hr)); r["edge"].append(edge_strength(sr) / max(edge_strength(hr), 1e-9))
    return {k: {m: float(np.mean(v)) for m, v in d.items()} for k, d in res.items()}, len(frames)


def table(res, n):
    lines = [f"{'method':<18}{'PSNR dB':>9}{'SSIM':>9}{'edge/GT':>9}   ({n} frames)"]
    for k, d in res.items():
        lines.append(f"{k:<18}{d['psnr']:9.3f}{d['ssim']:9.4f}{d['edge']:9.3f}")
    return "\n".join(lines)


# ---------------------------------------------------------------------------- training
def cmd_train(a):
    a.edge_w_eff = a.edge_w if a.edge_w is not None else (0.15 if a.loss == "edge" else 0.0)
    items = list_frames(a.data)
    if not items:
        raise SystemExit("no frames found in " + ", ".join(a.data))
    tr, va, vg = split(items)
    if a.max_train and a.max_train < len(tr):                 # experiment: how much does the amount of data matter?
        random.Random(7).shuffle(tr); tr = tr[:a.max_train]
    print(f"frames: {len(items)}  train {len(tr)} / validation {len(va)}  (validation scenes: {', '.join(vg)})")
    if not va or not tr:
        raise SystemExit("need at least 2 scene groups for a train/validation split")
    rng = random.Random(a.seed); torch.manual_seed(a.seed)
    if DEV == "cuda":
        torch.cuda.set_per_process_memory_fraction(a.mem_fraction)
    net = T.A4kNet(a.arch).to(DEV)
    orig = T.A4kNet(a.arch).to(DEV).eval()
    if a.resume and os.path.exists(os.path.join(run_dir(a.name), "ckpt_last.pt")):
        st = torch.load(os.path.join(run_dir(a.name), "ckpt_last.pt"), map_location=DEV)
        if st.get("letter", "M") != a.arch:
            raise SystemExit(f"the checkpoint is architecture {st.get('letter', 'M')}, not {a.arch}")
        net.load_state_dict(st["model"]); print("resumed from ckpt_last.pt")
    print(f"architecture {a.arch}, loss {a.loss}" + (f" (L1 + {a.ssim_w} SSIM + {a.tex_w} texture + {a.edge_w_eff} edge)" if a.loss == "texture" else f" (L1 + {a.edge_w_eff} edge)"))
    opt = torch.optim.Adam(net.parameters(), lr=a.lr)
    scaler = torch.amp.GradScaler(DEV, enabled=a.amp and DEV == "cuda")
    os.makedirs(run_dir(a.name), exist_ok=True)
    logp = os.path.join(run_dir(a.name), "log.csv")
    with open(logp, "w") as f:
        f.write("iter,minutes,loss,val_psnr,val_ssim,val_edge,orig_psnr,orig_ssim,bicubic_psnr,bicubic_ssim\n")
    quick_val = va[:a.val_frames] if a.val_frames else va

    def runner(m):
        return lambda lr: m(lr)

    base, nval = evaluate({"anime4k-orig": runner(orig), "yours": runner(net.eval())}, quick_val)
    print("before training (validation):\n" + table(base, nval))
    net.train()
    def pick_score(r):
        # which checkpoint is "best": PSNR for the edge loss; for the texture loss PSNR plus a SSIM bonus (0.001 SSIM ~ 0.025 dB), because that loss
        # trades a little PSNR for SSIM and must not be judged by PSNR alone
        return r["psnr"] + (25.0 * r["ssim"] if a.loss == "texture" else 0.0)
    best = -1e9                    # the first validation always saves a checkpoint (a run whose loss never beats the starting PSNR must still export)
    t0 = time.time(); it = 0
    total_iters = a.iters
    deadline = t0 + a.minutes * 60 if a.minutes else None
    weights = np.array([x["weight"] for x in tr], np.float64); weights /= weights.sum()
    hist = []
    stop = False
    while not stop:
        # ---- load a shard of training frames onto the GPU
        k = min(a.shard, len(tr))
        pick = np.random.default_rng(rng.randrange(1 << 30)).choice(len(tr), size=k, replace=False, p=weights if k < len(tr) else None) if k < len(tr) else np.arange(len(tr))
        t1 = time.time()
        shard = [load_for_training(tr[i], DEV, a.native) for i in pick]
        print(f"shard of {len(shard)} frames loaded in {time.time() - t1:.1f}s")
        for _ in range(a.shard_iters):
            # lr schedule: cosine over the planned time / iterations
            if deadline:
                prog = min(1.0, (time.time() - t0) / (a.minutes * 60))
            else:
                prog = min(1.0, it / total_iters)
            lr_now = a.lr_min + 0.5 * (a.lr - a.lr_min) * (1 + math.cos(math.pi * prog))
            for g in opt.param_groups: g["lr"] = lr_now
            # ---- batch of HR patches (random frame, random crop, random flip / transpose), detail-biased
            cand = []
            P = a.patch
            for _b in range(int(a.batch * 1.5)):
                fr = shard[rng.randrange(len(shard))]
                _, H, W = fr.shape
                s = rng.uniform(a.scale_min, 1.0) if (a.scale_min < 1.0 and rng.random() < 0.5) else 1.0   # sharper-per-pixel variants of the same picture
                Pc = min(min(H, W) // 2 * 2, int(math.ceil(P / s / 2)) * 2)
                y = rng.randrange(0, (H - Pc) // 2 + 1) * 2; x = rng.randrange(0, (W - Pc) // 2 + 1) * 2
                c = fr[:, y:y + Pc, x:x + Pc].float() / 255
                if Pc != P:
                    c = F.interpolate(c[None], size=(P, P), mode="bicubic", align_corners=False, antialias=True)[0].clamp(0, 1)
                if a.color_jitter and rng.random() < 0.5:
                    g = rng.uniform(0.85, 1.18); b_ = rng.uniform(-0.03, 0.03); ct = rng.uniform(0.9, 1.1)
                    c = ((c.clamp(1e-4, 1) ** g - 0.5) * ct + 0.5 + b_).clamp(0, 1)
                if rng.random() < 0.5: c = c.flip(2)
                if rng.random() < 0.5: c = c.flip(1)
                if rng.random() < 0.5: c = c.transpose(1, 2)
                cand.append(c)
            cand = torch.stack(cand)
            en = (cand[:, :, :, 1:] - cand[:, :, :, :-1]).abs().mean((1, 2, 3)) + (cand[:, :, 1:, :] - cand[:, :, :-1, :]).abs().mean((1, 2, 3))
            hr = cand[en.argsort(descending=True)[:a.batch]]
            with torch.no_grad():
                lr_in = degrade(hr, rng)
            with torch.autocast(DEV, dtype=torch.float16, enabled=a.amp and DEV == "cuda"):
                sr = net(lr_in)
            sr = sr.float()
            loss = F.l1_loss(sr, hr)
            if a.edge_w_eff > 0:
                loss = loss + a.edge_w_eff * sobel_l1(sr, hr)
            if a.loss == "texture":
                loss = loss + a.ssim_w * ssim_loss(sr, hr) + a.tex_w * texture_loss(sr, hr)
            opt.zero_grad(set_to_none=True)
            scaler.scale(loss).backward()
            scaler.unscale_(opt)
            torch.nn.utils.clip_grad_norm_(net.parameters(), 1.0)
            scaler.step(opt); scaler.update()
            it += 1
            hist.append(loss.item() if it % 10 == 0 else hist[-1] if hist else 0.0)
            if it % a.val_every == 0 or (deadline and time.time() > deadline) or (not deadline and it >= total_iters):
                net.eval()
                res, n = evaluate({"anime4k-orig": runner(orig), "yours": runner(net)}, quick_val)
                net.train()
                y, o, bc = res["yours"], res["anime4k-orig"], res["bicubic"]
                mins = (time.time() - t0) / 60
                with open(logp, "a") as f:
                    f.write(f"{it},{mins:.2f},{np.mean(hist[-a.val_every:]):.5f},{y['psnr']:.4f},{y['ssim']:.5f},{y['edge']:.4f},{o['psnr']:.4f},{o['ssim']:.5f},{bc['psnr']:.4f},{bc['ssim']:.5f}\n")
                print(f"it {it:6d} {mins:5.1f}min lr {lr_now:.2e} loss {np.mean(hist[-a.val_every:]):.5f} | val PSNR yours {y['psnr']:.3f} orig {o['psnr']:.3f} bicubic {bc['psnr']:.3f} | SSIM yours {y['ssim']:.4f} orig {o['ssim']:.4f}", flush=True)
                score = pick_score(y)
                if score > best:
                    best = score
                    save_ckpt(net, a.name, "best", dict(it=it, val=res))
                save_ckpt(net, a.name, "last", dict(it=it, val=res))
            if (deadline and time.time() > deadline) or (not deadline and it >= total_iters):
                stop = True
                break
    print(f"finished: {it} iterations in {(time.time() - t0) / 60:.1f} min")
    cmd_export(argparse.Namespace(name=a.name, ckpt="best", tag=None))
    ref = {"anime4k-orig": runner(orig)}
    if a.arch != "M":
        ref["anime4k-M"] = runner(load_model("M"))              # the default network too: is the bigger one worth it?
    full, n = evaluate({**ref, "yours": runner(load_model(a.name, "best"))}, va)
    print("final validation (all held-out frames, best checkpoint):\n" + table(full, n))
    fulln, _ = evaluate({**ref, "yours": runner(load_model(a.name, "best"))}, va, native=True)
    print("same frames shrunk to their measured real detail (the clean 720p-class picture; frames without an estimate unchanged):\n" + table(fulln, n))
    json.dump(dict(final=full, final_native=fulln, frames=n, val_groups=vg, val_frames=[x["path"] for x in va], train_frames=len(tr), iters=it, minutes=(time.time() - t0) / 60),
              open(os.path.join(run_dir(a.name), "final.json"), "w"), indent=1)
    cmd_export(argparse.Namespace(name=a.name, ckpt="best", tag=None))     # again: the .json now carries the validation result


def cmd_finalize(a):
    """Validation table + final.json + export for a run stopped before it finished (uses the best checkpoint)."""
    items = list_frames(a.data)
    tr, va, vg = split(items)
    st = torch.load(os.path.join(run_dir(a.name), "ckpt_best.pt"), map_location="cpu")
    full, n = evaluate({"anime4k-orig": load_model("orig" + st.get("letter", "M")), "yours": load_model(a.name, "best")}, va)
    print(table(full, n))
    json.dump(dict(final=full, frames=n, val_groups=vg, val_frames=[x["path"] for x in va], train_frames=len(tr), iters=st.get("it"), minutes=None),
              open(os.path.join(run_dir(a.name), "final.json"), "w"), indent=1)
    cmd_export(argparse.Namespace(name=a.name, ckpt="best", tag=None))


def cmd_export(a):
    net = load_model(a.name, a.ckpt).cpu()
    os.makedirs(models_dir(), exist_ok=True)
    out = os.path.join(models_dir(), f"{a.name}.hlsl")
    T.export_hlsl(net, out, a.name)
    # first line marker the app reads: architecture letter
    txt = open(out, encoding="ascii").read()
    with open(out, "w", encoding="ascii", newline="\n") as f:
        f.write(f"// YUCAP-MODEL name={a.name} arch={net.letter}\n" + txt)
    meta = dict(name=a.name, arch=net.letter, created=time.strftime("%Y-%m-%d %H:%M:%S"), checkpoint=a.ckpt)
    fp = os.path.join(run_dir(a.name), "final.json")
    if os.path.exists(fp):
        fj = json.load(open(fp))
        meta["final"] = fj.get("final"); meta["val_frames"] = fj.get("val_frames", []); meta["val_groups"] = fj.get("val_groups", [])
        meta["train_frames"] = fj.get("train_frames"); meta["iters"] = fj.get("iters"); meta["minutes"] = fj.get("minutes")
    json.dump(meta, open(os.path.join(models_dir(), f"{a.name}.json"), "w"), indent=1)
    print("wrote", out)


def cmd_eval(a):
    items = list_frames(a.data)
    tr, va, vg = split(items)
    nets = {"anime4k-orig": load_model("orig")}
    if a.name:
        nets["yours"] = load_model(a.name, a.ckpt)
        if nets["yours"].letter != "M":
            nets["anime4k-" + nets["yours"].letter] = load_model(nets["yours"].letter)
    res, n = evaluate(nets, va if not a.all else items, native=a.native)
    print("validation scenes:", ", ".join(vg)); print(table(res, n))


@torch.no_grad()
def cmd_dump(a):
    net = load_model(a.name, a.ckpt) if a.name else None
    im = load_u8(a.image).to(DEV).float()[None] / 255
    out = net.double()(im.double()) if a.double else net(im)
    out = out[0].permute(1, 2, 0).float().cpu().numpy()
    out.astype(np.float32).tofile(a.out)
    print("wrote", a.out, out.shape)


@torch.no_grad()
def cmd_equiv(a):
    """The exported shader against PyTorch: YuCap.exe --a4k-run runs the .hlsl on the GPU (float32 intermediates), PyTorch runs the checkpoint in float64."""
    import subprocess, tempfile
    hlsl = os.path.join(models_dir(), f"{a.name}.hlsl") if a.name not in ("S", "M", "L") else a.name
    tmp = tempfile.mkdtemp(prefix="yucap_equiv_")
    gpu_f = os.path.join(tmp, "gpu.f32")
    subprocess.run([a.exe, "--a4k-run", hlsl, a.image, gpu_f] + (["--half"] if a.half else []), check=True, timeout=300)
    net = load_model(a.name, a.ckpt).double()
    im = load_u8(a.image).to(DEV).double()[None] / 255
    h, w = im.shape[2] // 2 * 2, im.shape[3] // 2 * 2                       # YuCap's dump crops to even sizes
    ref = net(im[:, :, :h, :w])[0].permute(1, 2, 0).float().cpu().numpy()
    gpu = np.fromfile(gpu_f, np.float32).reshape(ref.shape)
    d = np.abs(gpu - ref)
    print(f"{a.name}: GPU shader vs PyTorch on {os.path.basename(a.image)} ({ref.shape[1]}x{ref.shape[0]}): max diff {d.max():.3e}, mean {d.mean():.3e}  ->  {'PASS' if d.max() < (2e-2 if a.half else 1e-3) else 'FAIL'} (limit {'2e-2, float16 intermediates as in the viewer' if a.half else '1e-3'})")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest="cmd", required=True)
    t = sp.add_parser("train")
    t.add_argument("--name", default="mine"); t.add_argument("--data", nargs="+", required=True)
    t.add_argument("--minutes", type=float, default=0, help="time box; 0 = use --iters")
    t.add_argument("--iters", type=int, default=20000)
    t.add_argument("--batch", type=int, default=32); t.add_argument("--patch", type=int, default=96)
    t.add_argument("--lr", type=float, default=1e-4); t.add_argument("--lr-min", dest="lr_min", type=float, default=1e-5)
    t.add_argument("--arch", choices=["S", "M", "L"], default="M", help="network size (Anime4K CNN x2 S / M / L): L is more accurate but about 1.5x the GPU cost of M at play time")
    t.add_argument("--loss", choices=["edge", "texture"], default="edge",
                   help="edge: L1 + Sobel edge term (sharper lines, flatter texture).  texture: L1 + SSIM + high-pass texture terms (keeps grain / hatching / cloth)")
    t.add_argument("--edge-w", dest="edge_w", type=float, default=None, help="weight of the Sobel edge term (default 0.15 for --loss edge, 0 for texture)")
    t.add_argument("--ssim-w", dest="ssim_w", type=float, default=0.5, help="--loss texture: weight of 1-SSIM")
    t.add_argument("--tex-w", dest="tex_w", type=float, default=1.0, help="--loss texture: weight of the high-pass texture term")
    t.add_argument("--shard", type=int, default=120); t.add_argument("--shard-iters", dest="shard_iters", type=int, default=1500)
    t.add_argument("--val-every", dest="val_every", type=int, default=500); t.add_argument("--val-frames", dest="val_frames", type=int, default=0)
    t.add_argument("--no-amp", dest="amp", action="store_false"); t.add_argument("--resume", action="store_true")
    t.add_argument("--scale-min", dest="scale_min", type=float, default=0.6, help="random zoom-out of the training patches (1 = off): more detail per pixel, like a game rendered at 1080p")
    t.add_argument("--no-color-jitter", dest="color_jitter", action="store_false")
    t.add_argument("--no-native", dest="native", action="store_false", help="do not shrink frames whose measured detail is below their size to that detail")
    t.add_argument("--mem-fraction", dest="mem_fraction", type=float, default=0.55, help="share of the GPU memory training may use (the rest stays free for the game viewer)")
    t.add_argument("--seed", type=int, default=1); t.add_argument("--max-train", dest="max_train", type=int, default=0, help="use only this many training frames (experiments)")
    t.set_defaults(fn=cmd_train)
    fz = sp.add_parser("finalize"); fz.add_argument("--name", required=True); fz.add_argument("--data", nargs="+", required=True); fz.set_defaults(fn=cmd_finalize)
    e = sp.add_parser("export"); e.add_argument("--name", required=True); e.add_argument("--ckpt", default="best"); e.set_defaults(fn=cmd_export)
    v = sp.add_parser("eval"); v.add_argument("--name"); v.add_argument("--ckpt", default="best"); v.add_argument("--data", nargs="+", required=True)
    v.add_argument("--all", action="store_true"); v.add_argument("--native", action="store_true"); v.set_defaults(fn=cmd_eval)
    q = sp.add_parser("equiv"); q.add_argument("--name", required=True); q.add_argument("--ckpt", default="best"); q.add_argument("--image", required=True)
    q.add_argument("--exe", required=True, help="YuCap.exe (any build with --a4k-run)")
    q.add_argument("--half", action="store_true", help="GPU with float16 intermediate textures (what the viewer uses)"); q.set_defaults(fn=cmd_equiv)
    d = sp.add_parser("dump"); d.add_argument("--name"); d.add_argument("--ckpt", default="best"); d.add_argument("--image", required=True)
    d.add_argument("--out", required=True); d.add_argument("--double", action="store_true"); d.set_defaults(fn=cmd_dump)
    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
