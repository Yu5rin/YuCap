using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

/// <summary>
/// Host for the freeze-frame still. A plain PictureBox (the previous
/// implementation) stretches its Image to fill its whole Bounds, so when the
/// user was zoomed in the still overflowed past the canvas and painted over
/// the menu/status bars. FreezeView instead draws <see cref="Image"/> into
/// <see cref="ImageRect"/> — a sub-rectangle of its OWN client area — and
/// anything outside that rectangle is naturally clipped by GDI+ rather than
/// drawn, since ImageRect is expected to be sized/positioned within Bounds
/// (MainForm keeps FreezeView.Bounds equal to the canvas area at all times).
/// </summary>
public sealed class FreezeView : Control
{
    private Image? _image;
    private Rectangle _imageRect;

    /// <summary>The still to draw. NOT owned by this control — the caller
    /// (MainForm._freezeImage) is responsible for disposing it.</summary>
    public Image? Image
    {
        get => _image;
        set { _image = value; Invalidate(); }
    }

    /// <summary>Where to draw <see cref="Image"/>, in THIS control's own
    /// client coordinates.</summary>
    public Rectangle ImageRect
    {
        get => _imageRect;
        set { if (_imageRect != value) { _imageRect = value; Invalidate(); } }
    }

    public FreezeView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Color.Black);
        if (_image == null || _imageRect.Width <= 0 || _imageRect.Height <= 0) return;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        // GDI+ clips drawing to the control's own client rectangle regardless
        // of how much of ImageRect falls outside it — this is what stops a
        // zoomed-in still from overflowing onto the surrounding chrome.
        g.DrawImage(_image, _imageRect);
    }
}
