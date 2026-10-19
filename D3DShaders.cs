using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace YuCap;

/// <summary>
/// Shader bytecode for the Direct3D renderer. The HLSL sources are embedded resources
/// (Shaders/*.hlsl plus the two vendored FSR 1 headers) and are compiled at run time with
/// the D3D compiler — once per process, whatever the number of engines or restarts.
/// </summary>
internal static class D3DShaders
{
    internal sealed class Bytecode
    {
        public byte[] Vs = Array.Empty<byte>();
        public byte[] Convert = Array.Empty<byte>();
        public byte[] Scale = Array.Empty<byte>();
        public byte[] Overlay = Array.Empty<byte>();
        public byte[] Easu = Array.Empty<byte>();
        public byte[] Rcas = Array.Empty<byte>();
        // Frame interpolation (Shaders/interp.hlsl). Compiled separately: a failure here only
        // disables interpolation (InterpOk stays false) and never breaks the renderer.
        public bool PrescaleOk;
        public byte[] Prescale = Array.Empty<byte>();
        public byte[] PrescaleIbp = Array.Empty<byte>();
        public bool InterpOk;
        public byte[] PyrLuma = Array.Empty<byte>();
        public byte[] PyrDown = Array.Empty<byte>();
        public byte[] Me = Array.Empty<byte>(), MeCost = Array.Empty<byte>(), CutStat = Array.Empty<byte>();
        public byte[] Med = Array.Empty<byte>();
        public byte[] Synth = Array.Empty<byte>();
        // Same-picture detection (Shaders/dup.hlsl, a compute shader). A failure only disables the game frame rate reading.
        public bool DupOk;
        public byte[] Dup = Array.Empty<byte>();
    }

    private static readonly object Gate = new();
    private static Bytecode? _cached;

    public static Bytecode Get()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
            var sw = Stopwatch.StartNew();

            string fsrPrefix =
                "#define A_GPU 1\n#define A_HLSL 1\n#define FSR_EASU_F 1\n#define FSR_RCAS_F 1\n" +
                Resource("ffx_a.h") + "\n" + Resource("ffx_fsr1.h") + "\n";
            string fsr = fsrPrefix + Resource("fsr.hlsl");

            var b = new Bytecode
            {
                Vs = Compile(Resource("vs.hlsl"), "main", "vs.hlsl", "vs_5_0"),
                Convert = Compile(Resource("convert.hlsl"), "main", "convert.hlsl", "ps_5_0"),
                Scale = Compile(Resource("scale.hlsl"), "main", "scale.hlsl", "ps_5_0"),
                Overlay = Compile(Resource("overlay.hlsl"), "main", "overlay.hlsl", "ps_5_0"),
                Easu = Compile(fsr, "easu", "fsr.hlsl", "ps_5_0"),
                Rcas = Compile(fsr, "rcas", "fsr.hlsl", "ps_5_0"),
            };
            try
            {
                b.Prescale = Compile(Resource("prescale.hlsl"), "main", "prescale.hlsl", "ps_5_0");
                b.PrescaleIbp = Compile(Resource("prescale.hlsl"), "ibp", "prescale.hlsl", "ps_5_0");
                b.PrescaleOk = true;
            }
            catch (Exception ex)
            {
                Log.Info("D3D: prescale shader unavailable - 'match the original detail' is disabled: " + ex.Message.Split((char)10)[0]);
            }
            try
            {
                string interp = Resource("interp.hlsl");
                b.PyrLuma = Compile(interp, "pyr1", "interp.hlsl", "ps_5_0");
                b.PyrDown = Compile(interp, "pyrd", "interp.hlsl", "ps_5_0");
                b.Me = Compile(interp, "me", "interp.hlsl", "ps_5_0");
                b.MeCost = Compile(interp, "mecost", "interp.hlsl", "ps_5_0");
                b.CutStat = Compile(interp, "cutstat", "interp.hlsl", "ps_5_0");
                b.Med = Compile(interp, "med", "interp.hlsl", "ps_5_0");
                b.Synth = Compile(interp, "synth", "interp.hlsl", "ps_5_0");
                b.InterpOk = true;
            }
            catch (Exception ex)
            {
                Log.Info("D3D: interpolation shaders unavailable - frame interpolation is disabled: " + ex.Message.Split((char)10)[0]);
            }
            try
            {
                b.Dup = Compile(Resource("dup.hlsl"), "thumb", "dup.hlsl", "cs_5_0");
                b.DupOk = true;
            }
            catch (Exception ex)
            {
                Log.Info("D3D: same-picture shader unavailable - the game frame rate is not measured: " + ex.Message.Split((char)10)[0]);
            }
            Log.Info($"D3D: shaders compiled in {sw.ElapsedMilliseconds}ms");
            _cached = b;
            return b;
        }
    }

    private static readonly Dictionary<char, byte[][]> A4kCode = new();

    /// <summary>Bytecode of every pass of the Anime4K network <paramref name="variant"/> (S, M or L), in the order of
    /// <c>A4kModels.Passes*</c>. Compiled on first use (about 0.3 s) and kept for the life of the process; throws if the compile fails.</summary>
    public static byte[][] GetA4k(char variant)
    {
        lock (Gate)
        {
            if (A4kCode.TryGetValue(variant, out byte[][]? have)) return have;
            var sw = Stopwatch.StartNew();
            string file = $"a4k_{char.ToLowerInvariant(variant)}.hlsl";
            string src = Resource(file);
            A4kPassInfo[] passes = variant switch { 'S' => A4kModels.PassesS, 'L' => A4kModels.PassesL, _ => A4kModels.PassesM };
            var code = new byte[passes.Length][];
            for (int i = 0; i < passes.Length; i++) code[i] = Compile(src, passes[i].Entry, file, "ps_5_0");
            Log.Info($"D3D: Anime4K {variant} shaders compiled in {sw.ElapsedMilliseconds}ms");
            A4kCode[variant] = code;
            return code;
        }
    }

    private static string Resource(string name)
    {
        // development aid: YUCAP_SHADER_DIR=<Shaders folder> compiles the .hlsl files from disk (no rebuild between edits)
        string? dir = Environment.GetEnvironmentVariable("YUCAP_SHADER_DIR");
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name))) return File.ReadAllText(Path.Combine(dir, name), Encoding.UTF8);
        Assembly asm = typeof(D3DShaders).Assembly;
        using Stream? s = asm.GetManifestResourceStream("Shaders." + name)
            ?? throw new InvalidOperationException("missing embedded shader resource " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static byte[] Compile(string source, string entry, string name, string profile)
    {
        Result hr = Compiler.Compile(source, entry, name, profile, out Blob? code, out Blob? errors);
        try
        {
            if (hr.Failure || code == null)
            {
                string msg = errors != null ? Marshal_PtrToString(errors) : hr.ToString();
                Log.Info($"D3D: shader compile failed ({name}:{entry}): {msg}");
                throw new InvalidOperationException($"shader compile failed ({name}:{entry}): {msg}");
            }
            return code!.AsBytes();
        }
        finally
        {
            code?.Dispose();
            errors?.Dispose();
        }
    }

    private static string Marshal_PtrToString(Blob b) =>
        System.Runtime.InteropServices.Marshal.PtrToStringAnsi(b.BufferPointer) ?? "";
}

/// <summary>
/// CPU-side constant setup for FSR 1, ported line by line from FsrEasuCon / FsrRcasCon in
/// third_party/FidelityFX-FSR1/ffx_fsr1.h (the header's A_CPU path: plain float math and
/// float-to-uint bit casts).
/// </summary>
internal static class Fsr1
{
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
    private static float Rcp(float a) => 1.0f / a;   // ARcpF1

    public static void EasuCon(
        uint[] con0, uint[] con1, uint[] con2, uint[] con3,
        float inputViewportInPixelsX, float inputViewportInPixelsY,
        float inputSizeInPixelsX, float inputSizeInPixelsY,
        float outputSizeInPixelsX, float outputSizeInPixelsY)
    {
        // Output integer position to a pixel position in viewport.
        con0[0] = Bits(inputViewportInPixelsX * Rcp(outputSizeInPixelsX));
        con0[1] = Bits(inputViewportInPixelsY * Rcp(outputSizeInPixelsY));
        con0[2] = Bits(0.5f * inputViewportInPixelsX * Rcp(outputSizeInPixelsX) - 0.5f);
        con0[3] = Bits(0.5f * inputViewportInPixelsY * Rcp(outputSizeInPixelsY) - 0.5f);
        // Viewport pixel position to normalized image space (upper-left of the 'F' tap).
        con1[0] = Bits(Rcp(inputSizeInPixelsX));
        con1[1] = Bits(Rcp(inputSizeInPixelsY));
        // Centers of gather4, first offset from upper-left of 'F'.
        con1[2] = Bits(1.0f * Rcp(inputSizeInPixelsX));
        con1[3] = Bits(-1.0f * Rcp(inputSizeInPixelsY));
        // These are from (0) instead of 'F'.
        con2[0] = Bits(-1.0f * Rcp(inputSizeInPixelsX));
        con2[1] = Bits(2.0f * Rcp(inputSizeInPixelsY));
        con2[2] = Bits(1.0f * Rcp(inputSizeInPixelsX));
        con2[3] = Bits(2.0f * Rcp(inputSizeInPixelsY));
        con3[0] = Bits(0.0f * Rcp(inputSizeInPixelsX));
        con3[1] = Bits(4.0f * Rcp(inputSizeInPixelsY));
        con3[2] = con3[3] = 0;
    }

    /// <param name="sharpness">Stops of sharpness reduction: 0 = maximum, N = N halvings.</param>
    public static void RcasCon(uint[] con, float sharpness)
    {
        // Transform from stops to linear value.
        sharpness = MathF.Pow(2.0f, -sharpness);   // AExp2F1
        con[0] = Bits(sharpness);
        // AU1_AH2_AF2(hSharp): two packed halves.
        uint h = BitConverter.HalfToUInt16Bits((Half)sharpness);
        con[1] = h | (h << 16);
        con[2] = 0;
        con[3] = 0;
    }
}
