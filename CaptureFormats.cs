using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.MediaFoundation;

namespace YuCap;

/// <summary>
/// Enumeration and selection of a capture device's native formats through a source
/// reader. The selection rules are the same as VideoEngine.ConfigureFormat (which keeps
/// its own copy for the Capture Engine path): exact match, else same resolution at the
/// highest frame rate, else the largest resolution at the highest frame rate.
/// An empty Compression means "any".
/// </summary>
internal static class CaptureFormats
{
    internal readonly record struct RawType(int Index, string Comp, int W, int H, int Fps);

    public static List<RawType> Enumerate(IMFSourceReader reader, int stream)
    {
        var list = new List<RawType>();
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = reader.GetNativeMediaType(stream, i); }
            catch { break; }   // MF_E_NO_MORE_TYPES ends the list
            using (type)
            {
                if (TryDescribe(type, out string comp, out int w, out int h, out int fps))
                    list.Add(new RawType(i, comp, w, h, fps));
            }
        }
        return list;
    }

    /// <summary>The selectable modes, in the same shape VideoEngine.GetModes returns.</summary>
    public static List<CaptureMode> ToModes(IEnumerable<RawType> types) =>
        types
            .GroupBy(r => (r.Comp, r.W, r.H))
            .Select(g => new CaptureMode(g.Key.Comp, g.Key.W, g.Key.H, g.Max(r => r.Fps)))
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Fps)
            .ToList();

    /// <summary>Choose the native type to use. <paramref name="current"/> is the device's
    /// own default (used when no mode was asked for). Returns null if there are no types.</summary>
    public static RawType? Pick(List<RawType> types, CaptureMode? desired, RawType? current)
    {
        if (types.Count == 0) return null;

        if (desired == null)
        {
            if (current is { } c)
            {
                int same = types.FindIndex(r => r.Comp == c.Comp && r.W == c.W && r.H == c.H && r.Fps == c.Fps);
                if (same >= 0) return types[same];
            }
            return Largest(types);
        }

        // RawType is a struct: use FindIndex so "not found" is unambiguous.
        bool CompOk(RawType r) => string.IsNullOrEmpty(desired.Compression) || r.Comp == desired.Compression;
        int exact = types.FindIndex(r =>
            CompOk(r) && r.W == desired.Width && r.H == desired.Height &&
            (desired.Fps <= 0 || r.Fps == desired.Fps));
        if (exact >= 0) return types[exact];

        int sameRes = types.FindIndex(r => CompOk(r) && r.W == desired.Width && r.H == desired.Height);
        if (sameRes >= 0)
        {
            return types.Where(r => CompOk(r) && r.W == desired.Width && r.H == desired.Height)
                .OrderByDescending(r => r.Fps).First();
        }
        return Largest(types);
    }

    private static RawType Largest(List<RawType> types) =>
        types.OrderByDescending(r => (long)r.W * r.H).ThenByDescending(r => r.Fps).First();

    public static bool TryDescribe(IMFMediaType type, out string comp, out int w, out int h, out int fps)
    {
        comp = string.Empty; w = h = fps = 0;
        try
        {
            Guid subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
            comp = Fourcc(subtype);
            ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            w = (int)(size >> 32);
            h = (int)(size & 0xFFFFFFFF);
            ulong rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            uint num = (uint)(rate >> 32), den = (uint)(rate & 0xFFFFFFFF);
            fps = den > 0 ? (int)Math.Round((double)num / den) : 0;
            return w > 0 && h > 0;
        }
        catch { return false; }
    }

    public static string Fourcc(Guid subtype)
    {
        byte[] b = subtype.ToByteArray();
        uint c = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));

        // Uncompressed RGB subtypes use small numeric Data1 values, not FOURCCs.
        switch (c)
        {
            case 20: return "RGB24";
            case 21: return "ARGB32";
            case 22: return "RGB32";
            case 23: return "RGB565";
            case 24: return "RGB555";
        }

        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char ch = (char)((c >> (8 * i)) & 0xFF);
            chars[i] = char.IsControl(ch) ? ' ' : ch;
        }
        string s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? $"0x{c:X}" : s;
    }
}
