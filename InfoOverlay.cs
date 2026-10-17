using System;
using System.Drawing;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// Persistent multi-line information panel (testing aid, Ctrl+Shift+I). A child control over
/// the video area like <see cref="OsdOverlay"/>; it only draws text and resizes to fit it.
/// </summary>
public sealed class InfoOverlay : OverlayControl
{
    // 12px = 9pt at 96 DPI, times a scale that follows the monitor DPI and the video size (see TextScale).
    private Font _font = TextScale.MakeFont("Consolas", 12f, 1f, FontStyle.Regular);
    private float _scale = 1f;
    private static readonly Color Fore = Color.FromArgb(220, 220, 220);

    /// <summary>Test mode: opaque solid background of this colour (see <see cref="OsdOverlay.TestBack"/>).</summary>
    internal Color? TestBack { get => _testBack; set { _testBack = value; Changed(); } }
    private Color? _testBack;

    public InfoOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
        TabStop = false;
    }

    /// <summary>Scale factor currently applied (1 = the classic 9pt at 100% DPI).</summary>
    internal float TextFactor => _scale;
    /// <summary>The font in use (for the test mode's size measurement).</summary>
    internal Font TextFont => _font;
    /// <summary>Gap to the edge of the video area, in pixels at the current scale.</summary>
    internal int EdgeGap => TextScale.Px(12, _scale);

    /// <summary>Follow the monitor DPI / video size: new font, new padding, re-fit. No-op when unchanged.</summary>
    internal void ApplyScale(float scale)
    {
        if (Math.Abs(scale - _scale) < 0.001f) return;
        _scale = scale;
        Font old = _font;
        _font = TextScale.MakeFont("Consolas", 12f, scale, FontStyle.Regular);
        old.Dispose();
        Fit();
    }

    /// <summary>Replace the text (no-op when unchanged) and resize to fit.</summary>
    public void SetText(string text)
    {
        if (Text == text) return;
        Text = text;
        Fit();
    }

    private void Fit()
    {
        if (string.IsNullOrEmpty(Text)) { Changed(); return; }
        Size sz = TextRenderer.MeasureText(Text, _font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int px = TextScale.Px(10, _scale), py = TextScale.Px(7, _scale);
        Size = new Size(sz.Width + px * 2, sz.Height + py * 2);
        Changed();
    }

    internal override void PaintOverlay(Graphics g, bool offscreen)
    {
        using var back = new SolidBrush(TestBack ?? Color.FromArgb(200, 12, 12, 12));
        g.FillRectangle(back, new Rectangle(0, 0, Width, Height));
        TextRenderer.DrawText(g, Text, _font,
            new Point(TextScale.Px(10, _scale), TextScale.Px(7, _scale)), Fore, TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Size of the text drawn over the video (OSD, info panel, test caption, "no video" message).
/// scale = max(monitor DPI / 96, 1 + (videoHeight/1080 - 1) * k) * user multiplier.
/// At 1080 px of video height or less the second term is 1, so small windows are unchanged
/// (only the DPI follows); a bigger video area (4K fullscreen on a TV) makes the text grow
/// so it stays readable at viewing distance. k differs per kind of text.
/// </summary>
internal static class TextScale
{
    public const double KInfo = 3.2;     // 4K: x4.2 -> Consolas capitals about 1.5% of the screen height
    public const double KOsd = 4.2;      // 4K: x5.2 -> Segoe UI bold capitals about 2.7%
    public const double KCaption = 1.5;  // test-mode caption bar
    public const double KMessage = 2.0;  // "no video" message

    public static float For(int dpi, int videoHeight, double k, int userPct)
    {
        double byDpi = Math.Max(96, dpi) / 96.0;
        double byHeight = 1 + Math.Max(0, videoHeight / 1080.0 - 1) * k;
        double s = Math.Max(byDpi, byHeight) * Math.Clamp(userPct, 50, 300) / 100.0;
        // Steps of 0.05, so dragging a window edge does not rebuild fonts at every pixel.
        return (float)(Math.Round(s * 20) / 20);
    }

    public static int Px(int basePx, float scale) => Math.Max(1, (int)Math.Round(basePx * scale));

    /// <summary>A font whose em size is basePx * scale pixels (independent of the DC's DPI).</summary>
    public static Font MakeFont(string family, float basePx, float scale, FontStyle style) =>
        new(family, basePx * scale, style, GraphicsUnit.Pixel);

    /// <summary>Height in pixels of a capital letter, measured by actually drawing "H" with the font.</summary>
    public static int CapHeightPx(Font f)
    {
        int h = (int)Math.Ceiling(f.Size * 2.5) + 8;
        using var bmp = new Bitmap(h * 2, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            TextRenderer.DrawText(g, "H", f, new Point(4, 4), Color.White, TextFormatFlags.NoPadding);
        }
        int top = -1, bottom = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).R > 128) { if (top < 0) top = y; bottom = y; break; }
        return top < 0 ? 0 : bottom - top + 1;
    }
}
