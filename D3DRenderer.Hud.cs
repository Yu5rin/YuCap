using System;
using System.Drawing;
using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

// HUD protection: a per-pixel "static confidence" over the last pairs of unique pictures (hudupd in interp.hlsl). Where a pixel has not
// changed for several pairs in a row while the rest of the picture moves, the synthesis shows the plain frame instead of a warp.
internal sealed partial class D3DRenderer
{
    private ID3D11PixelShader? _psHud;
    private readonly ID3D11Texture2D?[] _hudTex = new ID3D11Texture2D?[2];
    private readonly ID3D11ShaderResourceView?[] _hudSrv = new ID3D11ShaderResourceView?[2];
    private readonly ID3D11RenderTargetView?[] _hudRtv = new ID3D11RenderTargetView?[2];
    private ID3D11Texture2D? _hudZero;
    private ID3D11ShaderResourceView? _hudZeroSrv;
    private int _hudCur;
    private Size _hudSize;
    private long _hudPrevB;
    private bool _hudActive;

    private void CreateHudShader(D3DShaders.Bytecode b)
    {
        if (!b.HudOk) return;
        try { _psHud = Device.CreatePixelShader(b.Hud); }
        catch (Exception ex) { Log.Info("D3D: HUD shader could not be created: " + ex.Message.Split('\n')[0]); _psHud = null; }
    }

    private void FreeHud()
    {
        for (int i = 0; i < 2; i++) { _hudRtv[i]?.Dispose(); _hudSrv[i]?.Dispose(); _hudTex[i]?.Dispose(); _hudRtv[i] = null; _hudSrv[i] = null; _hudTex[i] = null; }
        _hudZeroSrv?.Dispose(); _hudZero?.Dispose(); _hudZeroSrv = null; _hudZero = null;
        _hudSize = Size.Empty; _hudPrevB = 0; _hudActive = false;
    }

    /// <summary>The confidence texture the synthesis reads (a 1x1 zero when the protection is off).</summary>
    private ID3D11ShaderResourceView HudSrv()
    {
        if (_hudZeroSrv == null)
        {
            _hudZero = MakeTarget(1, 1, Format.R8_UNorm, out _hudZeroSrv!, out ID3D11RenderTargetView rtv);
            Ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            rtv.Dispose();
        }
        return _hudActive && _hudSrv[_hudCur] != null ? _hudSrv[_hudCur]! : _hudZeroSrv!;
    }

    /// <summary>Once per new pair: fold the pair into the static confidence. <paramref name="aSeq"/> / <paramref name="bSeq"/> identify the
    /// pictures; a pair that does not start where the last one ended restarts the history.</summary>
    private void HudUpdate(OriFrame a, OriFrame b, long aSeq, long bSeq)
    {
        if (!InterpOpt.Hud || _psHud == null) { _hudActive = false; return; }
        if (_hudSize != a.Size)
        {
            FreeHud();
            _hudSize = a.Size;
            for (int i = 0; i < 2; i++) _hudTex[i] = MakeTarget(a.Size.Width, a.Size.Height, Format.R8_UNorm, out _hudSrv[i]!, out _hudRtv[i]!);
        }
        bool consecutive = _hudPrevB != 0 && _hudPrevB == aSeq && _hudActive;
        int next = 1 - _hudCur;
        WriteCb(_cbInterp!, new InterpCb { P2 = new Vector4(InterpOpt.HudTau, 1f / InterpOpt.HudN, consecutive ? 1f : 0f, 0) });
        SetupTriangle(_hudRtv[next]!, new Rectangle(0, 0, a.Size.Width, a.Size.Height));
        Ctx.PSSetShader(_psHud);
        Ctx.PSSetConstantBuffer(0, _cbInterp);
        Ctx.PSSetShaderResource(0, a.Srv);
        Ctx.PSSetShaderResource(1, b.Srv);
        Ctx.PSSetShaderResource(2, _hudSrv[_hudCur]!);
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!); Ctx.PSSetShaderResource(1, null!); Ctx.PSSetShaderResource(2, null!);
        _hudCur = next; _hudPrevB = bSeq; _hudActive = true;
    }

    /// <summary>Debug hook (HUD checks): feed <paramref name="pics"/> as consecutive unique pictures (their motion estimated and
    /// folded into the HUD history); the last pair (the last two pictures) is synthesised at <paramref name="alpha"/>.</summary>
    internal bool InterpDebugChain(System.Collections.Generic.IList<Bitmap> pics, double alpha, out Bitmap? mid)
    {
        mid = null;
        if (!InterpAvailable || pics.Count < 2) return false;
        lock (FrameLock)
        {
            Size o = pics[0].Size;
            OriFrame? fa = null, fb = null;
            EnsureOriFrame(ref fa, o); EnsureOriFrame(ref fb, o);
            EnsureOriented(o);
            _hudPrevB = 0; _hudActive = false;
            _lastAlpha = -1; _curA = _curB = _meA = _meB = 0;
            for (int i = 0; i + 1 < pics.Count; i++)
            {
                Upload(fa!, pics[i]); Upload(fb!, pics[i + 1]);
                BuildPyramid(fa!); BuildPyramid(fb!);
                EstimateMotion(fa!, fb!);
                HudUpdate(fa!, fb!, i + 1, i + 2);
            }
            Synthesize(fa!, fb!, (float)alpha);
            mid = ReadBack(_oriented!, new Rectangle(Point.Empty, o));
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            fa!.Dispose(); fb!.Dispose();
            _hudPrevB = 0; _hudActive = false;
            _lastAlpha = -1;
            return true;
        }
    }
}
