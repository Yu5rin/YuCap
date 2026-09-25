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
    // 12pt bold normally; dropped to 9pt when the caller passes a small
    // maxWidth (a small PiP window), where the larger size would force
    // excessive wrapping or simply not fit.
    private readonly Font _fontNormal = new("Segoe UI", 12f, FontStyle.Bold);
    private readonly Font _fontSmall = new("Segoe UI", 9f, FontStyle.Bold);
    private const int SmallMaxWidthThreshold = 360;

    private Font _font;
    private bool _clickable;

    public OsdOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
        TabStop = false;
        _font = _fontNormal;
        Cursor = Cursors.Default;
    }

    /// <summary>Set the message, wrap it to <paramref name="maxWidth"/>, resize
    /// to fit and make visible (caller positions it). The hand cursor and the
    /// underline hint only appear when <paramref name="clickable"/> is true —
    /// most bubbles are just status text, not a link.</summary>
    public void ShowText(string text, int maxWidth, bool clickable)
    {
        Text = text;
        _clickable = clickable;
        Cursor = clickable ? Cursors.Hand : Cursors.Default;
        _font = maxWidth < SmallMaxWidthThreshold ? _fontSmall : _fontNormal;

        int innerMaxWidth = Math.Max(20, maxWidth - 28);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        Size textSz = TextRenderer.MeasureText(text, _font, new Size(innerMaxWidth, int.MaxValue), flags);
        Size = new Size(Math.Min(innerMaxWidth, textSz.Width) + 28, textSz.Height + 16);

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

        var textRect = new Rectangle(14, 8, Math.Max(1, Width - 28), Math.Max(1, Height - 16));
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Text, _font, textRect, Color.White, flags);

        if (_clickable)
        {
            // Thin accent underline under the last line, so a clickable bubble
            // reads as a link rather than just another status flip.
            Size textSz = TextRenderer.MeasureText(g, Text, _font, textRect.Size, flags);
            int lineY = 8 + textSz.Height;
            using var pen = new Pen(Ui.Colors.Accent, 1.5f);
            g.DrawLine(pen, 14, lineY, 14 + Math.Min(textSz.Width, textRect.Width), lineY);
        }
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
        if (disposing)
        {
            _fontNormal.Dispose();
            _fontSmall.Dispose();
        }
        base.Dispose(disposing);
    }
}
