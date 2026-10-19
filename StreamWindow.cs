using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>The sizes offered for the 配信用ウィンドウ and how a setting value is turned into pixels.</summary>
internal static class StreamSizes
{
    public const string Video = "video";
    public static readonly string[] Keys = { "1920x1080", "1280x720", "2560x1440", "3840x2160", Video };

    public static string Normalize(string? s)
    {
        s = (s ?? string.Empty).Trim().ToLowerInvariant();
        return Array.IndexOf(Keys, s) >= 0 ? s : Keys[0];
    }

    /// <summary>The client size for <paramref name="key"/>; "映像と同じ" takes <paramref name="video"/> (the picture as displayed), 1920x1080 while unknown.</summary>
    public static Size Resolve(string key, Size video)
    {
        key = Normalize(key);
        if (key == Video)
        {
            if (video.Width < 16 || video.Height < 16) return new Size(1920, 1080);
            return new Size(Math.Min(video.Width, 7680) & ~1, Math.Min(video.Height, 4320) & ~1);
        }
        string[] p = key.Split('x');
        return new Size(int.Parse(p[0]), int.Parse(p[1]));
    }
}

/// <summary>
/// 配信用ウィンドウ: a plain top-level window whose client area the Direct3D renderer fills with the video alone (no OSD, info panel,
/// menu or cursor) so that OBS and the like can capture it with Window Capture. It never takes the focus, has a fixed client size and
/// a black background. Everything drawn into it comes from the render thread's second swap chain; this class only owns the window.
/// </summary>
internal sealed class StreamForm : Form
{
    /// <summary>The window title OBS lists ("[YuCap.exe]: YuCap 配信用"). Deliberately not translated: a scene that captures it by title
    /// must keep working when the language is switched.</summary>
    public const string WindowTitle = "YuCap 配信用";

    private const int WsExNoActivate = 0x08000000;
    private const int WmSetCursor = 0x0020;
    private const int HtClient = 1;
    private const int WmGetMinMaxInfo = 0x0024;
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Pt Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);

    private Size _want;

    public StreamForm(Size client, Point? where)
    {
        _want = client;
        Text = WindowTitle;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.Black;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = client;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }
        if (where is { } p) Location = p;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate;      // clicking it (or being shown) does not take the focus from the game window / the main window
            return cp;
        }
    }

    // No pointer over the picture (a capture that includes the cursor would otherwise show it).
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmGetMinMaxInfo)
        {
            // Windows caps a window at the size of the primary monitor unless told otherwise: a 4K stream window on a 1080p
            // monitor (OBS does not care how big the monitor is) would come out smaller than asked.
            base.WndProc(ref m);
            MinMaxInfo mm = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
            mm.MaxTrack = new Pt { X = 20000, Y = 20000 };
            mm.MaxSize = mm.MaxTrack;
            Marshal.StructureToPtr(mm, m.LParam, false);
            return;
        }
        if (m.Msg == WmSetCursor && (m.LParam.ToInt64() & 0xFFFF) == HtClient)
        {
            SetCursor(IntPtr.Zero);
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

    // The swap chain paints the whole client area; WinForms must not paint over it (that would flicker).
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>Change the client size (the swap chain is made again by the renderer).</summary>
    public void SetClient(Size client)
    {
        _want = client;
        if (ClientSize != client) ClientSize = client;
    }

    // The size was worked out before the window existed (with the DPI of the primary monitor); on a monitor with another scale the
    // frame is thicker and the client area comes out smaller than asked. Ask again once the window is on its monitor.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (ClientSize != _want) ClientSize = _want;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (ClientSize != _want) ClientSize = _want;
    }
}
