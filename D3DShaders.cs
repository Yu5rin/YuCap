using System;
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
        public byte[] Easu = Array.Empty<byte>();
        public byte[] Rcas = Array.Empty<byte>();
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
                Easu = Compile(fsr, "easu", "fsr.hlsl", "ps_5_0"),
                Rcas = Compile(fsr, "rcas", "fsr.hlsl", "ps_5_0"),
            };
            Log.Info($"D3D: shaders compiled in {sw.ElapsedMilliseconds}ms");
            _cached = b;
            return b;
        }
    }

    private static string Resource(string name)
    {
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
