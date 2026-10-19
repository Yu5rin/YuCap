using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>One connected display, as the "全画面にするモニター" menu and the fullscreen code see it.</summary>
internal sealed record DisplayInfo(string DeviceName, string FriendlyName, Rectangle Bounds, int RefreshHz, bool Primary)
{
    /// <summary>The string stored in <c>AppSettings.FullscreenDisplay</c>:
    /// <c>\\.\DISPLAY2|Hisense|3840x2160</c>. The device name alone is not stable (Windows renumbers
    /// displays when cables move), so the monitor's own name and size travel with it.</summary>
    public string Key => $"{DeviceName}|{Clean(FriendlyName)}|{Bounds.Width}x{Bounds.Height}";

    private static string Clean(string s) => s.Replace('|', ' ').Trim();

    /// <summary>"Hisense（3840x2160・144Hz）" and, for the primary display, " ・メイン" after it.</summary>
    public string MenuLabel()
    {
        string hz = RefreshHz > 0 ? $"{L.Sep}{RefreshHz}Hz" : string.Empty;
        string label = $"{FriendlyName}{L.Par($"{Bounds.Width}x{Bounds.Height}{hz}")}";
        return Primary ? label + (L.English ? " - " : " ・") + L.T("メイン") : label;
    }
}

/// <summary>Display enumeration (friendly monitor names, refresh rate) and the lookup of the
/// stored fullscreen target.</summary>
internal static class Displays
{
    public static List<DisplayInfo> List()
    {
        Dictionary<string, string> names = FriendlyNames();
        var list = new List<DisplayInfo>();
        foreach (Screen s in Screen.AllScreens)
        {
            string friendly = names.TryGetValue(s.DeviceName, out string? n) && !string.IsNullOrWhiteSpace(n)
                ? n : s.DeviceName.TrimStart('\\', '.');
            list.Add(new DisplayInfo(s.DeviceName, friendly, s.Bounds, RefreshRate(s.DeviceName), s.Primary));
        }
        return list;
    }

    /// <summary>The screen for a stored key, or null when it is empty ("the window's own display")
    /// or that monitor is not connected. Order: the whole key, then monitor name + size (the display
    /// was renumbered), then - only for a key that carries no usable name - the device name.
    /// A device name that now belongs to a different monitor is deliberately not accepted: going
    /// fullscreen on the wrong screen is worse than the "not found" fallback.</summary>
    public static Screen? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string[] p = key.Split('|');
        string dev = p[0];
        string name = p.Length > 1 ? p[1] : string.Empty;
        string size = p.Length > 2 ? p[2] : string.Empty;

        List<DisplayInfo> all = List();
        DisplayInfo? hit = all.FirstOrDefault(d => d.Key == key);
        if (hit == null && name.Length > 0)
        {
            // The monitor is recognised by its own name: a changed resolution (or a renumbered device) does not
            // make it another monitor. The size only tells identical names apart (two of the same TV model),
            // then the device name, then the first of them.
            List<DisplayInfo> named = all.Where(d => d.FriendlyName.Replace('|', ' ').Trim() == name).ToList();
            hit = named.Count == 1 ? named[0]
                : named.FirstOrDefault(d => $"{d.Bounds.Width}x{d.Bounds.Height}" == size && d.DeviceName == dev)
                  ?? named.FirstOrDefault(d => $"{d.Bounds.Width}x{d.Bounds.Height}" == size)
                  ?? named.FirstOrDefault(d => d.DeviceName == dev)
                  ?? named.FirstOrDefault();
        }
        // A key without a usable name falls back to the device name alone.
        hit ??= name.Length == 0 ? all.FirstOrDefault(d => d.DeviceName == dev) : null;
        if (hit == null) return null;
        return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == hit.DeviceName);
    }

    // ---- refresh rate ----------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode dm);

    [StructLayout(LayoutKind.Sequential)]
    private struct MRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public MRect rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    /// <summary>Current refresh rate (Hz, as Windows reports it - 59.94 Hz shows as 59 or 60) of the
    /// monitor <paramref name="hwnd"/> is mostly on; 0 when it cannot be told. Cheap enough to call
    /// about once a second from any thread.</summary>
    public static int RefreshHzOfWindow(IntPtr hwnd)
    {
        try
        {
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon == IntPtr.Zero) return 0;
            var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            return GetMonitorInfoW(mon, ref mi) ? RefreshRate(mi.szDevice) : 0;
        }
        catch { return 0; }
    }

    /// <summary>GDI device name (\.\DISPLAYn) of the monitor <paramref name="hwnd"/> is mostly on; empty when it cannot be told.</summary>
    public static string DeviceNameOfWindow(IntPtr hwnd)
    {
        try
        {
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon == IntPtr.Zero) return string.Empty;
            var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            return GetMonitorInfoW(mon, ref mi) ? mi.szDevice : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static int RefreshRate(string deviceName)
    {
        try
        {
            var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(deviceName, -1, ref dm)) return (int)dm.dmDisplayFrequency;
        }
        catch { }
        return 0;
    }

    // ---- friendly names (QueryDisplayConfig) -------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology;
        public uint Rotation; public uint Scaling; public uint RefreshNum, RefreshDen;
        public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo Source; public PathTargetInfo Target; public uint Flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths,
        ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(IntPtr header);

    private const uint QdcOnlyActivePaths = 2;
    private const int SourceNameType = 1, TargetNameType = 2;

    /// <summary>GDI device name (\\.\DISPLAYn) -> the monitor's own name, e.g. "HISENSE". Empty when
    /// the query fails; callers then fall back to the device name.</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0) return map;
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;

            IntPtr buf = Marshal.AllocHGlobal(512);
            try
            {
                for (int i = 0; i < np; i++)
                {
                    PathInfo p = paths[i];
                    // header: type, size, adapter LUID (low, high), id = 20 bytes
                    Marshal.WriteInt32(buf, 0, SourceNameType);
                    Marshal.WriteInt32(buf, 4, 84);
                    Marshal.WriteInt32(buf, 8, unchecked((int)p.Source.AdapterId.Low));
                    Marshal.WriteInt32(buf, 12, p.Source.AdapterId.High);
                    Marshal.WriteInt32(buf, 16, unchecked((int)p.Source.Id));
                    if (DisplayConfigGetDeviceInfo(buf) != 0) continue;
                    string gdi = Marshal.PtrToStringUni(buf + 20, 32).Split('\0')[0];

                    Marshal.WriteInt32(buf, 0, TargetNameType);
                    Marshal.WriteInt32(buf, 4, 420);
                    Marshal.WriteInt32(buf, 8, unchecked((int)p.Target.AdapterId.Low));
                    Marshal.WriteInt32(buf, 12, p.Target.AdapterId.High);
                    Marshal.WriteInt32(buf, 16, unchecked((int)p.Target.Id));
                    if (DisplayConfigGetDeviceInfo(buf) != 0) continue;
                    // header 20 + flags 4 + outputTechnology 4 + manufacturer 2 + product 2 + connector 4 = 36
                    string friendly = Marshal.PtrToStringUni(buf + 36, 64).Split('\0')[0].Trim();
                    if (gdi.Length > 0) map[gdi] = friendly;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.Info("Displays: friendly-name query failed: " + ex.Message); }
        return map;
    }

    /// <summary>Windows' "SDR content brightness" of the monitor <paramref name="deviceName"/> (\.\DISPLAYn) in nits
    /// (DISPLAYCONFIG_SDR_WHITE_LEVEL / 1000 x 80); 0 when it cannot be read (the monitor is not in HDR mode, or the query fails).</summary>
    public static float SdrWhiteNits(string deviceName)
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0) return 0;
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return 0;
            IntPtr buf = Marshal.AllocHGlobal(512);
            try
            {
                for (int i = 0; i < np; i++)
                {
                    PathInfo p = paths[i];
                    Marshal.WriteInt32(buf, 0, SourceNameType);
                    Marshal.WriteInt32(buf, 4, 84);
                    Marshal.WriteInt32(buf, 8, unchecked((int)p.Source.AdapterId.Low));
                    Marshal.WriteInt32(buf, 12, p.Source.AdapterId.High);
                    Marshal.WriteInt32(buf, 16, unchecked((int)p.Source.Id));
                    if (DisplayConfigGetDeviceInfo(buf) != 0) continue;
                    string gdi = Marshal.PtrToStringUni(buf + 20, 32).Split(' ')[0];
                    if (!string.Equals(gdi, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                    Marshal.WriteInt32(buf, 0, 11 /* DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL */);
                    Marshal.WriteInt32(buf, 4, 24);
                    Marshal.WriteInt32(buf, 8, unchecked((int)p.Target.AdapterId.Low));
                    Marshal.WriteInt32(buf, 12, p.Target.AdapterId.High);
                    Marshal.WriteInt32(buf, 16, unchecked((int)p.Target.Id));
                    Marshal.WriteInt32(buf, 20, 0);
                    if (DisplayConfigGetDeviceInfo(buf) != 0) return 0;
                    int v = Marshal.ReadInt32(buf, 20);
                    return v > 0 ? v / 1000f * 80f : 0;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return 0;
    }
}
