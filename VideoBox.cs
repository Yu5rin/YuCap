using System;
using System.Drawing;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// Host window that Media Foundation's preview renders into on the GPU. It does
/// not paint anything itself — MF owns the surface — so WinForms never draws its
/// background over the D3D-presented video.
/// When <see cref="EnableEdgeResize"/> is set (borderless mode), hits near an edge
/// that coincides with the form's outer edge are passed through (HTTRANSPARENT) so
/// the parent form can handle border resizing.
/// </summary>
public sealed class VideoBox : Control
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int Border = 6;

    /// <summary>True in borderless mode: expose the form's edges for resizing.</summary>
    public bool EnableEdgeResize { get; set; }

    public VideoBox()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Black;
        TabStop = false;
    }

    /// <summary>
    /// Destroy and recreate the underlying HWND. Required before restarting a
    /// preview whose previous session was abandoned mid-call: Media Foundation
    /// renders through DirectComposition, and the dead session's composition
    /// target stays bound to this window — a new preview on the same HWND fails
    /// with DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED (0x88980800). A fresh
    /// window has no target bound to it. The caller must re-Attach the new
    /// handle to the engine afterwards.
    /// </summary>
    public void ResetHandle() => RecreateHandle();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest && EnableEdgeResize && Parent != null)
        {
            int lp = (int)(long)m.LParam;
            Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            Size pc = Parent.ClientSize;
            bool left = p.X < Border && Left <= 0;
            bool right = p.X >= Width - Border && Right >= pc.Width;
            bool top = p.Y < Border && Top <= 0;
            bool bottom = p.Y >= Height - Border && Bottom >= pc.Height;
            if (left || right || top || bottom)
            {
                m.Result = (IntPtr)HtTransparent; // let the parent form resize
                return;
            }
        }
        base.WndProc(ref m);
    }
}
