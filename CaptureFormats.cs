using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.MediaFoundation;

namespace YuCap;

/// <summary>
/// Enumeration and selection of a capture device's native formats through a source
/// reader. The selection rules are the same as VideoEngine.ConfigureFormat (which keeps
/// shares it for the Capture Engine path): exact match, else same resolution at the
/// highest frame rate, else the largest resolution at the highest frame rate; with no mode
/// the automatic choice (PickAuto). An empty Compression means "any".
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

    /// <summary>Compressed (decoded by the reader / the system, not a raw pixel format) subtypes.</summary>
    public static bool IsCompressed(string comp) => comp is "MJPG" or "MJPEG" or "H264" or "HEVC" or "H265";

    private static bool SameShape(RawType a, RawType b) => a.W == b.W && a.H == b.H && a.Fps == b.Fps;

    /// <summary>True when <paramref name="r"/> is an NV12 type that has an MJPG type of the same size and frame rate.</summary>
    private static bool HasMjpgTwin(IReadOnlyList<RawType> types, RawType r)
    {
        if (r.Comp != "NV12") return false;
        foreach (RawType m in types) if (m.Comp == "MJPG" && SameShape(m, r)) return true;
        return false;
    }

    /// <summary>A cheap capture stick sends only MJPG; the NV12 types it lists are the Windows camera frame server
    /// decoding that MJPG. Such a device lists an NV12 twin for every MJPG type and the other way round. A real NV12
    /// card that also offers MJPG does not (its sets differ), so it is never treated as a twin device.</summary>
    public static bool IsTwinDevice(IReadOnlyList<RawType> types)
    {
        bool anyNv12 = false;
        foreach (RawType r in types)
        {
            if (r.Comp == "NV12")
            {
                anyNv12 = true;
                if (!HasMjpgTwin(types, r)) return false;
            }
            else if (r.Comp == "MJPG" && !types.Any(n => n.Comp == "NV12" && SameShape(n, r))) return false;
        }
        return anyNv12;
    }

    /// <summary>The selectable modes, in the same shape VideoEngine.GetModes returns. On a twin device the NV12 entries
    /// that duplicate an MJPG entry are left out (one entry per size and rate).</summary>
    public static List<CaptureMode> ToModes(IEnumerable<RawType> types)
    {
        List<RawType> all = types.ToList();
        bool twin = IsTwinDevice(all);
        return all
            .Where(r => !(twin && HasMjpgTwin(all, r)))
            .GroupBy(r => (r.Comp, r.W, r.H))
            .Select(g => new CaptureMode(g.Key.Comp, g.Key.W, g.Key.H, g.Max(r => r.Fps)))
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Fps)
            .ToList();
    }

    /// <summary>Below this a type is not a sensible automatic choice (a device's leftover YUY2 1080p at 5 fps).</summary>
    public const int MinAutoFps = 24;

    /// <summary>On a twin device an NV12 type becomes its MJPG type (the reader then decodes the real stream itself);
    /// anything else is returned unchanged.</summary>
    public static RawType MapTwin(List<RawType> types, RawType r)
    {
        if (!HasMjpgTwin(types, r) || !IsTwinDevice(types)) return r;
        int i = types.FindIndex(m => m.Comp == "MJPG" && SameShape(m, r));
        return i >= 0 ? types[i] : r;
    }

    /// <summary>Automatic choice. The device's own current type is kept when it runs at <see cref="MinAutoFps"/> or
    /// more (a card's 1080p120 stays). Otherwise the largest size, then the highest rate, among the types at
    /// <see cref="MinAutoFps"/> or more; on a tie an uncompressed type that is not an MJPG twin, then MJPG, then the twin.
    /// With <paramref name="mapTwin"/> an NV12 twin is turned into its MJPG type.</summary>
    public static RawType? PickAuto(List<RawType> types, RawType? current, bool mapTwin)
    {
        if (types.Count == 0) return null;
        bool twinDev = IsTwinDevice(types);
        int curIdx = current is { } c
            ? types.FindIndex(r => r.Comp == c.Comp && r.W == c.W && r.H == c.H && r.Fps == c.Fps) : -1;

        RawType chosen;
        if (curIdx >= 0 && types[curIdx].Fps >= MinAutoFps) chosen = types[curIdx];
        else
        {
            var ok = types.Where(r => r.Fps >= MinAutoFps).ToList();
            if (ok.Count == 0) chosen = curIdx >= 0 ? types[curIdx] : Largest(types);
            else
                chosen = ok.OrderByDescending(r => (long)r.W * r.H).ThenByDescending(r => r.Fps)
                    .ThenBy(r => twinDev && r.Comp == "NV12" ? 2 : IsCompressed(r.Comp) ? 1 : 0).First();
        }
        return mapTwin ? MapTwin(types, chosen) : chosen;
    }

    /// <summary>Choose the native type to use. <paramref name="current"/> is the device's
    /// own default (used by the automatic choice when no mode was asked for). Returns null if there are no types.
    /// With <paramref name="mapTwin"/> (the Direct3D path) an NV12 twin of an MJPG type is opened as the MJPG type.</summary>
    public static RawType? Pick(List<RawType> types, CaptureMode? desired, RawType? current, bool mapTwin = false)
    {
        if (types.Count == 0) return null;
        if (desired == null) return PickAuto(types, current, mapTwin);

        // RawType is a struct: use FindIndex so "not found" is unambiguous.
        bool CompOk(RawType r) => string.IsNullOrEmpty(desired.Compression) || r.Comp == desired.Compression;
        int exact = types.FindIndex(r =>
            CompOk(r) && r.W == desired.Width && r.H == desired.Height &&
            (desired.Fps <= 0 || r.Fps == desired.Fps));
        RawType pick;
        if (exact >= 0) pick = types[exact];
        else
        {
            int sameRes = types.FindIndex(r => CompOk(r) && r.W == desired.Width && r.H == desired.Height);
            pick = sameRes >= 0
                ? types.Where(r => CompOk(r) && r.W == desired.Width && r.H == desired.Height)
                    .OrderByDescending(r => r.Fps).First()
                : Largest(types);
        }
        return mapTwin ? MapTwin(types, pick) : pick;
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

    // ---- colour attributes (for --list-formats / --hdr-probe) -------------

    private static string Attr(IMFMediaType t, Guid key, Func<int, string> name)
    {
        try { return name((int)t.GetUInt32(key)); }
        catch { return "-"; }
    }

    private static string PrimariesName(int v) => v switch
    {
        0 => "unknown", 2 => "BT709", 3 => "BT470_2_SysM", 4 => "BT470_2_SysBG", 5 => "SMPTE170M",
        6 => "SMPTE240M", 7 => "EBU3213", 8 => "SMPTE_C", 9 => "BT2020", 10 => "XYZ", 11 => "DCI_P3", 12 => "ACES",
        _ => "?" + v,
    };

    private static string TransferName(int v) => v switch
    {
        0 => "unknown", 1 => "10(linear)", 2 => "18", 3 => "20", 4 => "22", 5 => "709", 6 => "240M",
        7 => "sRGB", 8 => "28", 9 => "Log_100", 10 => "Log_316", 11 => "709_sym", 12 => "2020_const",
        13 => "2020", 14 => "26", 15 => "2084(PQ)", 16 => "HLG", 17 => "10_rel",
        _ => "?" + v,
    };

    private static string MatrixName(int v) => v switch
    {
        0 => "unknown", 1 => "709", 2 => "601", 3 => "240M", 4 => "BT2020_10", 5 => "BT2020_12",
        _ => "?" + v,
    };

    private static string RangeName(int v) => v switch
    {
        0 => "unknown", 1 => "full(0-255)", 2 => "limited(16-235)", 3 => "48-208", 4 => "64-127",
        _ => "?" + v,
    };

    /// <summary>"primaries=BT2020 transfer=2084(PQ) matrix=BT2020_10 range=limited(16-235)"; "-" for an
    /// attribute the type does not carry.</summary>
    public static string DescribeColor(IMFMediaType t) =>
        $"primaries={Attr(t, MediaTypeAttributeKeys.VideoPrimaries, PrimariesName)} " +
        $"transfer={Attr(t, MediaTypeAttributeKeys.TransferFunction, TransferName)} " +
        $"matrix={Attr(t, MediaTypeAttributeKeys.YuvMatrix, MatrixName)} " +
        $"range={Attr(t, MediaTypeAttributeKeys.VideoNominalRange, RangeName)}";

    /// <summary>"P010 3840x2160@30 primaries=... transfer=... matrix=... range=...".</summary>
    public static string DescribeType(IMFMediaType t) =>
        (TryDescribe(t, out string comp, out int w, out int h, out int fps)
            ? $"{comp} {w}x{h}@{fps}" : "(unreadable type)") + " " + DescribeColor(t);

    /// <summary>Open <paramref name="dev"/> with a plain source reader (no Direct3D) and describe every
    /// native media type, colour attributes included. The device must not be in use elsewhere.</summary>
    public static List<string> ProbeDevice(VideoDeviceInfo dev)
    {
        D3DVideoEngine.EnsureStartup();
        using IMFAttributes attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        attrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, dev.Id);
        IMFMediaSource source = MediaFactory.MFCreateDeviceSource(attrs);
        try
        {
            using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, null);
            const int firstVideo = unchecked((int)0xFFFFFFFC);
            reader.SetStreamSelection(unchecked((int)0xFFFFFFFE), false);
            reader.SetStreamSelection(firstVideo, true);
            return DescribeAllNative(reader, firstVideo);
        }
        finally
        {
            try { source.Shutdown(); } catch { }
            source.Dispose();
        }
    }

    /// <summary>One line per native media type of the reader's video stream: its index and
    /// <see cref="DescribeType"/>.</summary>
    public static List<string> DescribeAllNative(IMFSourceReader reader, int stream)
    {
        var lines = new List<string>();
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = reader.GetNativeMediaType(stream, i); }
            catch { break; }
            using (type) lines.Add($"{i,3}: {DescribeType(type)}");
        }
        return lines;
    }
}
