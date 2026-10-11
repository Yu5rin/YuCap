using System;
using System.Drawing;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// Persistent multi-line information panel (testing aid, Ctrl+Shift+I). A child control over
/// the video area like <see cref="OsdOverlay"/>; it only draws text and resizes to fit it.
/// </summary>
public sealed class InfoOverlay : Control
{
    private readonly Font _font = new("Consolas", 9f, FontStyle.Regular);
    private static readonly Color Fore = Color.FromArgb(220, 220, 220);

    public InfoOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
        TabStop = false;
    }

    /// <summary>Replace the text (no-op when unchanged) and resize to fit.</summary>
    public void SetText(string text)
    {
        if (Text == text) return;
        Text = text;
        Size sz = TextRenderer.MeasureText(text, _font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        Size = new Size(sz.Width + 20, sz.Height + 14);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var back = new SolidBrush(Color.FromArgb(200, 12, 12, 12));
        e.Graphics.FillRectangle(back, ClientRectangle);
        TextRenderer.DrawText(e.Graphics, Text, _font, new Point(10, 7), Fore, TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
