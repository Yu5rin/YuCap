using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// Small floating on-screen-display shown over the video (volume, snapshot
/// notices, mode changes). It is a sibling window above the EVR host, with a
/// rounded region so the corners reveal the video behind it.
/// </summary>
public sealed class OsdOverlay : Control
{
    private readonly Font _font = new("Segoe UI", 12f, FontStyle.Bold);

    public OsdOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
        TabStop = false;
    }

    /// <summary>Set the message, resize to fit and make visible (caller positions it).</summary>
    public void ShowText(string text)
    {
        Text = text;
        Size sz = TextRenderer.MeasureText(text, _font);
        Size = new Size(sz.Width + 28, sz.Height + 16);

        using var path = Rounded(new Rectangle(0, 0, Width, Height), 10);
        Region?.Dispose();
        Region = new Region(path);

        Visible = true;
        BringToFront();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var back = new SolidBrush(Color.FromArgb(215, 20, 20, 20));
        g.FillRectangle(back, ClientRectangle);
        TextRenderer.DrawText(g, Text, _font, new Point(14, 8), Color.White);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
