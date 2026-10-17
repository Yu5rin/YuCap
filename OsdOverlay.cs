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
public sealed class OsdOverlay : OverlayControl
{
    // 12pt bold normally; dropped to 9pt when the caller passes a small
    // maxWidth (a small PiP window), where the larger size would force
    // excessive wrapping or simply not fit.
    // Sizes are in pixels (16px = 12pt, 12px = 9pt at 96 DPI) times a scale that follows the
    // monitor's DPI and the size of the video area (see TextScale).
    private Font _fontNormal = TextScale.MakeFont("Segoe UI", 16f, 1f, FontStyle.Bold);
    private Font _fontSmall = TextScale.MakeFont("Segoe UI", 12f, 1f, FontStyle.Bold);
    private const int SmallMaxWidthThreshold = 360;

    private Font _font;
    private bool _clickable;
    private float _scale = 1f;
    private int _lastMaxWidth;

    /// <summary>Test mode: draw an opaque solid background of this colour, so a screen sample can tell
    /// whether the overlay really is on top of the video.</summary>
    internal Color? TestBack { get => _testBack; set { _testBack = value; Changed(); } }
    private Color? _testBack;

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

    internal bool Clickable => _clickable;
    internal float TextFactor => _scale;
    /// <summary>The large font in use (for the test mode's size measurement).</summary>
    internal Font TextFont => _fontNormal;
    /// <summary>Gap to the edge of the video area, in pixels at the current scale.</summary>
    internal int EdgeGap => TextScale.Px(12, _scale);

    /// <summary>Follow the monitor DPI / video size: new fonts, padding and corner radius; re-fit if showing.</summary>
    internal void ApplyScale(float scale)
    {
        if (Math.Abs(scale - _scale) < 0.001f) return;
        bool small = ReferenceEquals(_font, _fontSmall);
        _scale = scale;
        Font n = _fontNormal, s = _fontSmall;
        _fontNormal = TextScale.MakeFont("Segoe UI", 16f, scale, FontStyle.Bold);
        _fontSmall = TextScale.MakeFont("Segoe UI", 12f, scale, FontStyle.Bold);
        _font = small ? _fontSmall : _fontNormal;
        n.Dispose(); s.Dispose();
        if (Showing && !string.IsNullOrEmpty(Text)) ShowText(Text, _lastMaxWidth, _clickable);
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
        _lastMaxWidth = maxWidth;
        _font = maxWidth < SmallMaxWidthThreshold * _scale ? _fontSmall : _fontNormal;

        int padX = TextScale.Px(14, _scale), padY = TextScale.Px(8, _scale);
        int innerMaxWidth = Math.Max(20, maxWidth - padX * 2);
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        Size textSz = TextRenderer.MeasureText(text, _font, new Size(innerMaxWidth, int.MaxValue), flags);
        Size = new Size(Math.Min(innerMaxWidth, textSz.Width) + padX * 2, textSz.Height + padY * 2);

        using var path = Rounded(new Rectangle(0, 0, Width, Height), TextScale.Px(10, _scale));
        Region?.Dispose();
        Region = new Region(path);

        Showing = true;
        if (!Virtual) BringToFront();
        Changed();
    }

    internal override void PaintOverlay(Graphics g, bool offscreen)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var back = new SolidBrush(TestBack ?? Color.FromArgb(215, 20, 20, 20));
        if (offscreen)
        {
            using var path = Rounded(new Rectangle(0, 0, Width, Height), TextScale.Px(10, _scale));
            g.FillPath(back, path);
        }
        else g.FillRectangle(back, ClientRectangle);

        int padX = TextScale.Px(14, _scale), padY = TextScale.Px(8, _scale);
        var textRect = new Rectangle(padX, padY, Math.Max(1, Width - padX * 2), Math.Max(1, Height - padY * 2));
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, Text, _font, textRect, Color.White, flags);

        if (_clickable)
        {
            // Thin accent underline under the last line, so a clickable bubble
            // reads as a link rather than just another status flip.
            Size textSz = TextRenderer.MeasureText(g, Text, _font, textRect.Size, flags);
            int lineY = padY + textSz.Height;
            using var pen = new Pen(Ui.Colors.Accent, 1.5f * _scale);
            g.DrawLine(pen, padX, lineY, padX + Math.Min(textSz.Width, textRect.Width), lineY);
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
