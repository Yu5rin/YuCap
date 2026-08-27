using System;
using System.Runtime.InteropServices;
using System.Text;

namespace YuCap;

/// <summary>
/// Lets a second launch hand its work to the instance already running instead
/// of failing. The capture device is exclusive, so only one instance can own
/// it — but the user who double-clicked the icon wants the window, not an
/// error box, and any switches they passed (`--fullscreen`, `--mode …`) should
/// still take effect.
///
/// The handoff uses WM_COPYDATA, which needs no shared file, pipe or port: the
/// command line is copied straight into the target process by the window
/// manager, and only a window that opts in ever sees it.
/// </summary>
internal static class SingleInstance
{
    /// <summary>Identifies our payload so no unrelated WM_COPYDATA is mistaken for it.</summary>
    public const int CopyDataId = 0x59754361;   // 'YuCa'

    public const int WmCopyData = 0x004A;

    /// <summary>Marks the main window so the second instance can find it.</summary>
    public const string WindowMarker = "YuCap.MainWindow.6E1B4C";

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetPropW(IntPtr hWnd, string name);
    [DllImport("user32.dll")] private static extern bool SetPropW(IntPtr hWnd, string name, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr RemovePropW(IntPtr hWnd, string name);

    private const int SwRestore = 9;

    /// <summary>Tag the window so <see cref="ActivateExisting"/> can find it.</summary>
    public static void MarkWindow(IntPtr hWnd) => SetPropW(hWnd, WindowMarker, new IntPtr(1));

    public static void UnmarkWindow(IntPtr hWnd) => RemovePropW(hWnd, WindowMarker);

    /// <summary>
    /// Find the running instance, restore and focus it, and forward the command
    /// line. Returns false if no marked window was found (it may be starting up
    /// or shutting down), in which case the caller should say so.
    /// </summary>
    public static bool ActivateExisting(string[] args)
    {
        IntPtr target = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (GetPropW(h, WindowMarker) == IntPtr.Zero) return true;
            target = h;
            return false;   // found it
        }, IntPtr.Zero);

        if (target == IntPtr.Zero) return false;

        if (IsIconic(target)) ShowWindow(target, SwRestore);
        SetForegroundWindow(target);

        // Nothing to forward for a plain launch.
        if (args.Length == 0) return true;

        string payload = string.Join('\n', args);
        IntPtr buffer = Marshal.StringToHGlobalUni(payload);
        try
        {
            var data = new CopyDataStruct
            {
                dwData = new IntPtr(CopyDataId),
                cbData = (payload.Length + 1) * 2,   // bytes, including the terminator
                lpData = buffer,
            };
            SendMessage(target, WmCopyData, IntPtr.Zero, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return true;
    }

    /// <summary>Read a forwarded command line out of a WM_COPYDATA message.</summary>
    public static string[]? ReadForwardedArgs(IntPtr lParam)
    {
        try
        {
            var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if (data.dwData.ToInt64() != CopyDataId || data.lpData == IntPtr.Zero) return null;
            string? s = Marshal.PtrToStringUni(data.lpData);
            return string.IsNullOrEmpty(s) ? null : s.Split('\n');
        }
        catch { return null; }
    }
}
