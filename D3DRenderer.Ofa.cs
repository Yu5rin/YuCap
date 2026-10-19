using System;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

/// <summary>Switches of the interpolation pipeline that are tuned on real footage. The environment variable YUCAP_INTERP
/// (and the offline evaluation) set them as a comma separated list, e.g. <c>ofa,g=4,p=5,syn=2</c>; without it the defaults apply.</summary>
internal readonly record struct InterpOptions(bool Ofa, int Grid, int Perf, int Syn, bool Median, float WPhoto, float WCons, bool Hud, float HudTau, int HudN)
{
    public static InterpOptions Default => new(true, 4, 10, 2, true, 4f, 0.05f, true, 0.02f, 4);

    public static InterpOptions Parse(string? spec)
    {
        InterpOptions o = Default;
        if (string.IsNullOrWhiteSpace(spec)) return o;
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2);
            string k = kv[0].ToLowerInvariant(), v = kv.Length > 1 ? kv[1] : "";
            float f = 0;
            bool hasF = kv.Length > 1 && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
            switch (k)
            {
                case "block": o = o with { Ofa = false }; break;
                case "ofa": o = o with { Ofa = true }; break;
                case "g": if (hasF) o = o with { Grid = (int)f }; break;
                case "p": if (hasF) o = o with { Perf = (int)f }; break;
                case "syn": if (hasF) o = o with { Syn = (int)f }; break;
                case "med": o = o with { Median = !hasF || f != 0 }; break;
                case "nomed": o = o with { Median = false }; break;
                case "wp": if (hasF) o = o with { WPhoto = f }; break;
                case "wc": if (hasF) o = o with { WCons = f }; break;
                case "hud": o = o with { Hud = !hasF || f != 0 }; break;
                case "htau": if (hasF) o = o with { HudTau = f }; break;
                case "hn": if (hasF) o = o with { HudN = Math.Max(1, (int)f) }; break;
            }
        }
        return o;
    }
}

// Motion from the hardware optical-flow engine of NVIDIA GPUs (NvOf.cs) instead of the block matcher: forward and backward flow of the
// pair, converted to float fields, and a synthesis that uses both (left-right check). The block matcher's coarsest level still runs,
// only for the scene-cut statistics. Any failure falls back to the block matcher for that pair.
internal sealed partial class D3DRenderer
{
    internal static InterpOptions InterpOpt = InterpOptions.Parse(Environment.GetEnvironmentVariable("YUCAP_INTERP"));
    /// <summary>Source pixels per vector of the field the last synthetic debug run used (the block size, or the OFA grid).</summary>
    internal static int DebugFieldPx;

    private ID3D11PixelShader? _psOfConv, _psSynthOf, _psOfDown;
    private int _ofaScale = 1;                       // the engine sees the picture shrunk by this integer factor
    private NvOfSession? _ofa;
    private Size _ofaFor;
    private InterpOptions _ofaOpt;
    private string _ofaBrokenKey = "";
    private int _ofaFailRun;
    private ID3D11Texture2D? _ofF, _ofB;
    private ID3D11ShaderResourceView? _ofFSrv, _ofBSrv, _ofFRaw, _ofBRaw;
    private ID3D11RenderTargetView? _ofFRtv, _ofBRtv;
    private bool _fieldIsOfa;

    /// <summary>True while the hardware optical flow supplies the motion (otherwise the block matcher does).</summary>
    public bool MotionIsHardware => _fieldIsOfa;

    /// <summary>What finds the motion, for the info panel (a key of the UI string table; the last pair decides).</summary>
    internal static volatile string MotionLabel = "ブロックマッチング（GPU）";

    private void CreateOfaShaders(D3DShaders.Bytecode b)
    {
        if (!b.OfaOk) return;
        try
        {
            _psOfConv = Device.CreatePixelShader(b.OfConv);
            _psSynthOf = Device.CreatePixelShader(b.SynthOf);
            _psOfDown = Device.CreatePixelShader(b.OfDown);
        }
        catch (Exception ex)
        {
            Log.Info("D3D: optical-flow shaders could not be created: " + ex.Message.Split('\n')[0]);
            _psOfConv?.Dispose(); _psSynthOf?.Dispose(); _psOfDown?.Dispose(); _psOfConv = _psSynthOf = _psOfDown = null;
        }
    }

    private void DisposeOfa()
    {
        _ofFSrv?.Dispose(); _ofFRtv?.Dispose(); _ofF?.Dispose(); _ofFRaw?.Dispose();
        _ofBSrv?.Dispose(); _ofBRtv?.Dispose(); _ofB?.Dispose(); _ofBRaw?.Dispose();
        _ofFSrv = _ofBSrv = _ofFRaw = _ofBRaw = null; _ofFRtv = _ofBRtv = null; _ofF = _ofB = null;
        _ofa?.Dispose(); _ofa = null;
        _fieldIsOfa = false;
    }

    /// <summary>Run the hardware optical flow for the pair and convert it to the float fields. False: use the block matcher.</summary>
    private bool TryOfaMotion(OriFrame a, OriFrame b)
    {
        InterpOptions opt = InterpOpt;
        if (!opt.Ofa || _psOfConv == null || _psSynthOf == null) return false;
        int scale = Math.Max(a.Size.Width, a.Size.Height) > 1920 ? 2 : 1;       // 4K and 1440p: the engine's time grows with the area and stalls the queue
        Size inSize = new((a.Size.Width + scale - 1) / scale, (a.Size.Height + scale - 1) / scale);
        string key = $"{a.Size.Width}x{a.Size.Height}/{opt.Grid}/{opt.Perf}";
        if (key == _ofaBrokenKey) return false;
        if (_ofa == null || _ofaFor != a.Size || _ofaOpt.Grid != opt.Grid || _ofaOpt.Perf != opt.Perf)
        {
            DisposeOfa();
            _ofa = NvOfSession.TryCreate(Device, Ctx, inSize.Width, inSize.Height, opt.Grid, true, opt.Perf);
            if (_ofa == null)
            {
                _ofaBrokenKey = key;
                Log.Info($"D3D: NVIDIA optical flow unavailable at {a.Size.Width}x{a.Size.Height} - block matching is used: {NvOfSession.LastFailure}");
                return false;
            }
            _ofaFor = a.Size; _ofaOpt = opt; _ofaScale = scale;
            var os = _ofa.OutSize;
            _ofF = MakeTarget(os.W, os.H, Format.R32G32_Float, out _ofFSrv!, out _ofFRtv!);
            _ofB = MakeTarget(os.W, os.H, Format.R32G32_Float, out _ofBSrv!, out _ofBRtv!);
            _ofFRaw = Device.CreateShaderResourceView(_ofa.FwdTex);
            _ofBRaw = Device.CreateShaderResourceView(_ofa.BwdTex!);
            Log.Info($"D3D: NVIDIA optical flow in use ({a.Size.Width}x{a.Size.Height}, engine input {inSize.Width}x{inSize.Height}, grid {opt.Grid}, perf {opt.Perf})");
        }
        if (!OfaRegister(a, inSize) || !OfaRegister(b, inSize)) return OfaFailed(key, "register");
        if (!_ofa.Execute(a.OfaReg, b.OfaReg)) return OfaFailed(key, "execute");
        _ofaFailRun = 0;

        var os2 = _ofa.OutSize;
        for (int d = 0; d < 2; d++)
        {
            WriteCb(_cbInterp!, new InterpCb
            {
                P1 = new Vector4(os2.W, os2.H, 0, 0),
                P2 = new Vector4(opt.Median ? 1f : 0f, _ofaScale, 0, 0),
            });
            SetupTriangle(d == 0 ? _ofFRtv! : _ofBRtv!, new Rectangle(0, 0, os2.W, os2.H));
            Ctx.PSSetShader(_psOfConv);
            Ctx.PSSetConstantBuffer(0, _cbInterp);
            Ctx.PSSetShaderResource(7, d == 0 ? _ofFRaw! : _ofBRaw!);
            Ctx.Draw(3, 0);
            Ctx.PSSetShaderResource(7, null!);
        }
        return true;
    }

    /// <summary>Make the engine's input of the frame ready: the frame itself, or its shrunk copy (made once per picture).</summary>
    private bool OfaRegister(OriFrame f, Size inSize)
    {
        if (_ofaScale > 1)
        {
            if (f.OfaTex == null || f.OfaOwner != _ofa)
            {
                f.OfaOwner?.Unregister(f.OfaReg);
                f.OfaReg = IntPtr.Zero; f.OfaOwner = null; f.OfaSeq = 0;
                f.OfaRtv?.Dispose(); f.OfaSrv?.Dispose(); f.OfaTex?.Dispose();
                f.OfaTex = MakeTarget(inSize.Width, inSize.Height, Format.B8G8R8A8_UNorm, out f.OfaSrv!, out f.OfaRtv!);
            }
            if (f.Seq == 0 || f.OfaSeq != f.Seq)
            {
                WriteCb(_cbInterp!, new InterpCb { P0 = new Vector4(f.Size.Width, f.Size.Height, _ofaScale, 0) });
                SetupTriangle(f.OfaRtv!, new Rectangle(0, 0, inSize.Width, inSize.Height));
                Ctx.PSSetShader(_psOfDown);
                Ctx.PSSetConstantBuffer(0, _cbInterp);
                Ctx.PSSetShaderResource(0, f.Srv);
                Ctx.Draw(3, 0);
                Ctx.PSSetShaderResource(0, null!);
                f.OfaSeq = f.Seq;
            }
        }
        if (f.OfaOwner == _ofa && f.OfaReg != IntPtr.Zero) return true;
        f.OfaOwner?.Unregister(f.OfaReg);
        f.OfaReg = _ofa!.Register(_ofaScale > 1 ? f.OfaTex! : f.Tex);
        f.OfaOwner = _ofa;
        return f.OfaReg != IntPtr.Zero;
    }

    private bool OfaFailed(string key, string what)
    {
        if (++_ofaFailRun >= 3)
        {
            _ofaBrokenKey = key;
            Log.Info($"D3D: NVIDIA optical flow stopped ({what}: {NvOfSession.LastFailure}) - block matching is used");
            DisposeOfa();
        }
        return false;
    }

    private void SynthesizeOf(OriFrame a, OriFrame b, float alpha)
    {
        Size o = _orientedSize;
        var os = _ofa!.OutSize;
        InterpOptions opt = InterpOpt;
        WriteCb(_cbInterp!, new InterpCb
        {
            P0 = new Vector4(o.Width, o.Height, 0, 0),
            P1 = new Vector4(os.W, os.H, 1f / (os.W * _ofa.Grid * _ofaScale), 1f / (os.H * _ofa.Grid * _ofaScale)),
            P2 = new Vector4(0, 0, alpha, 0),
            P3 = new Vector4(opt.Syn, opt.WPhoto, opt.WCons, 0),
        });
        _orientedVer++;
        SetupTriangle(_orientedRtv!, new Rectangle(0, 0, o.Width, o.Height));
        Ctx.PSSetShader(_psSynthOf);
        Ctx.PSSetConstantBuffer(0, _cbInterp);
        Ctx.PSSetSampler(0, _linear);
        Ctx.PSSetShaderResource(0, a.Srv);
        Ctx.PSSetShaderResource(1, b.Srv);
        Ctx.PSSetShaderResource(4, _cutSrv!);
        Ctx.PSSetShaderResource(5, _ofFSrv!);
        Ctx.PSSetShaderResource(6, _ofBSrv!);
        Ctx.PSSetShaderResource(8, HudSrv());
        Ctx.Draw(3, 0);
        Ctx.PSSetShaderResource(0, null!); Ctx.PSSetShaderResource(1, null!); Ctx.PSSetShaderResource(4, null!);
        Ctx.PSSetShaderResource(5, null!); Ctx.PSSetShaderResource(6, null!); Ctx.PSSetShaderResource(8, null!);
    }

    /// <summary>Debug: wall-clock milliseconds the optical-flow engine needs per pair (execute, then wait for the result).</summary>
    private double OfaBenchMs(OriFrame a, OriFrame b, int n)
    {
        n = Math.Min(Math.Max(n, 4), 40);
        using ID3D11Texture2D st = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R16G16_SInt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            _ofa!.Execute(a.OfaReg, b.OfaReg);
            Ctx.CopySubresourceRegion(st, 0, 0, 0, 0, _ofa.FwdTex, 0, new Vortice.Mathematics.Box(0, 0, 0, 1, 1, 1));
            Ctx.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            Ctx.Unmap(st, 0);
        }
        return sw.Elapsed.TotalMilliseconds / n;
    }

    /// <summary>Debug: the forward flow field of the last pair (x, y per vector, source pixels) and its size.</summary>
    private float[] ReadFieldOfa(out Size size)
    {
        size = new Size(_ofa!.OutSize.W, _ofa.OutSize.H);
        using ID3D11Texture2D staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1, Format = Format.R32G32_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Ctx.CopyResource(staging, _ofF!);
        MappedSubresource map = Ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var xy = new float[size.Width * size.Height * 2];
        try
        {
            for (int y = 0; y < size.Height; y++) Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, xy, y * size.Width * 2, size.Width * 2);
        }
        finally { Ctx.Unmap(staging, 0); }
        return xy;
    }
}
