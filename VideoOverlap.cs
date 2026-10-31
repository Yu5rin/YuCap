using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace YuCap;

/// <summary>Which visible windows sit above the video window and cover part of its picture. DWM keeps a flip-model swap chain on the fast path
/// (the monitor's own refresh rate) only while nothing overlaps it; as soon as something does, the video is composed and held to the
/// primary monitor's rate on a mixed-refresh setup. Used to name the culprit in the test report and to decide when a nudge makes sense.</summary>
internal static class VideoOverlap
{
    internal sealed record Item(IntPtr Hwnd, string Title, string Class, string Process, Rectangle Bounds, bool TopMost, bool Layered);

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RectI v, int size);

    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int L, T, R, B; }

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "EdgeUiInputTopWndClass", "EdgeUiInputWndClass", "tooltips_class32" };

    /// <summary>Windows above <paramref name="video"/> in the z-order that intersect <paramref name="area"/> (screen coordinates).
    /// Own-process windows are included (a menu or dialog of YuCap's own does overlap too).</summary>
    public static List<Item> Find(IntPtr video, Rectangle area, bool includeTaskbar = false)
    {
        var list = new List<Item>();
        if (video == IntPtr.Zero) return list;
        bool above = true;
        try
        {
            EnumWindows((h, _) =>
            {
                if (h == video) { above = false; return false; }
                if (!above || !IsWindowVisible(h) || IsIconic(h)) return true;
                if (DwmGetWindowAttribute(h, 13 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                var cls = new StringBuilder(128); GetClassNameW(h, cls, 128);
                if (Ignored.Contains(cls.ToString()) && !(includeTaskbar && cls.ToString().StartsWith("Shell_"))) return true;
                Rectangle b;
                if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RectI r, Marshal.SizeOf<RectI>()) == 0) b = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
                else return true;
                Rectangle x = Rectangle.Intersect(b, area);
                if (x.Width <= 0 || x.Height <= 0) return true;
                int ex = GetWindowLong(h, -20);
                if ((ex & 0x20) != 0 && (ex & 0x80000) != 0) return true;      // WS_EX_TRANSPARENT + LAYERED: click-through, usually an invisible host
                var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
                GetWindowThreadProcessId(h, out uint pid);
                string proc = "?";
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
                list.Add(new Item(h, t.ToString(), cls.ToString(), proc, x, (ex & 0x8) != 0, (ex & 0x80000) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    public static string Describe(List<Item> items)
    {
        if (items.Count == 0) return "none";
        var sb = new StringBuilder();
        foreach (Item i in items)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"{i.Process}: \"{(i.Title.Length > 40 ? i.Title[..40] : i.Title)}\" [{i.Class}] {i.Bounds.Width}x{i.Bounds.Height}@{i.Bounds.X},{i.Bounds.Y}");
        }
        return sb.ToString();
    }
}
