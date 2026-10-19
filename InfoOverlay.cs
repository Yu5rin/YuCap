using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

/// <summary>情報表示 (Ctrl+Shift+I): off / FPS だけ（小さな角のパネル）/ 詳しく（全項目）.</summary>
public enum InfoMode { Off, Fps, Full }

/// <summary>
/// Persistent information panel (Ctrl+Shift+I): the full multi-line list, or a compact FPS counter. A child control over
/// the video area like <see cref="OsdOverlay"/>; it only draws text and resizes to fit it.
/// </summary>
public sealed class InfoOverlay : OverlayControl
{
    // Small like the FPS counters of games (Steam, RTSS): 11px = 8pt at 96 DPI, times a scale that follows the monitor DPI only
    // (not the video size; see TextScale).
    private const float BasePx = 11f;
    private Font _font = TextScale.MakeFont("Consolas", BasePx, 1f, FontStyle.Regular);
    private Font _bigFont = TextScale.MakeFont("Consolas", BigBasePx, 1f, FontStyle.Bold);
    private float _scale = 1f;
    private const float BigBasePx = 17f;   // 12.75pt: capitals about 11px
    private string _big = "", _small = "";
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

    /// <summary>What the panel shows (<see cref="InfoMode.Full"/> unless <see cref="SetFps"/> was used last).</summary>
    internal InfoMode Mode { get; private set; } = InfoMode.Full;
    /// <summary>The big-number font of the FPS mode (for the test mode's size measurement).</summary>
    internal Font BigFont => _bigFont;

    /// <summary>Scale factor currently applied (1 = 8pt at 100% DPI).</summary>
    internal float TextFactor => _scale;
    /// <summary>The font in use (for the test mode's size measurement).</summary>
    internal Font TextFont => _font;
    /// <summary>Gap to the edge of the video area, in pixels at the current scale.</summary>
    internal int EdgeGap => TextScale.Px(8, _scale);

    /// <summary>Follow the monitor DPI / video size: new font, new padding, re-fit. No-op when unchanged.</summary>
    internal void ApplyScale(float scale)
    {
        if (Math.Abs(scale - _scale) < 0.001f) return;
        _scale = scale;
        Font old = _font;
        _font = TextScale.MakeFont("Consolas", BasePx, scale, FontStyle.Regular);
        old.Dispose();
        Font oldBig = _bigFont;
        _bigFont = TextScale.MakeFont("Consolas", BigBasePx, scale, FontStyle.Bold);
        oldBig.Dispose();
        Fit();
    }

    /// <summary>Switch what is drawn. The contents are cleared; the caller fills them right after.</summary>
    internal void SetMode(InfoMode mode)
    {
        if (mode == InfoMode.Off) mode = InfoMode.Full;
        if (Mode == mode) return;
        Mode = mode;
        Text = string.Empty; _big = _small = string.Empty;
    }

    /// <summary>Replace the text (no-op when unchanged) and resize to fit.</summary>
    public void SetText(string text)
    {
        SetMode(InfoMode.Full);
        if (Text == text) return;
        Text = text;
        Fit();
    }

    /// <summary>FPS mode: a big number and one small line under it (no-op when unchanged).</summary>
    internal void SetFps(string big, string small)
    {
        SetMode(InfoMode.Fps);
        if (_big == big && _small == small) return;
        _big = big; _small = small;
        Text = big + "\n" + small;
        Fit();
    }

    private const TextFormatFlags RightFlags = TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.SingleLine;
    private static readonly Size Huge = new(int.MaxValue, int.MaxValue);

    // FPS mode layout (set by Fit): where the two texts are drawn so the dark box hugs the visible glyphs.
    private Rectangle _bigRect, _smallRect;

    /// <summary>Bounds of the visible glyphs of <paramref name="text"/> drawn with <paramref name="f"/> at (0,0) with no padding,
    /// and the width GDI advances for it (the rectangle a right-aligned draw of the same text needs).</summary>
    internal static Rectangle InkBounds(string text, Font f, out int advance)
    {
        Size adv = TextRenderer.MeasureText(text, f, Huge, TextFormatFlags.NoPadding);
        advance = adv.Width;
        int m = 6;
        using var bmp = new Bitmap(adv.Width + m * 2, adv.Height + m * 2);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            TextRenderer.DrawText(g, text, f, new Point(m, m), Color.White, TextFormatFlags.NoPadding);
        }
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).R > 90)
                {
                    if (x < l) l = x; if (x > r) r = x;
                    if (y < t) t = y; if (y > b) b = y;
                }
        if (r < 0) return new Rectangle(0, 0, Math.Max(1, adv.Width), Math.Max(1, adv.Height));
        return new Rectangle(l - m, t - m, r - l + 1, b - t + 1);
    }

    /// <summary>The padding of the FPS box on every side, in pixels: proportional to the big number's font.</summary>
    internal int FpsPad => Math.Max(4, (int)Math.Round(_bigFont.Size * 0.42f));

    /// <summary>Measured margins (left, top, right, bottom) between the box edge and the visible glyphs in FPS mode, for the test.</summary>
    internal (int L, int T, int R, int B) FpsInkMargins()
    {
        if (Mode != InfoMode.Fps || Width <= 0) return default;
        using var bmp = new Bitmap(Width, Height);
        using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.Black); PaintText(g); }
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).R > 90)
                {
                    if (x < l) l = x; if (x > r) r = x;
                    if (y < t) t = y; if (y > b) b = y;
                }
        return r < 0 ? default : (l, t, Width - 1 - r, Height - 1 - b);
    }

    private void Fit()
    {
        if (string.IsNullOrEmpty(Text)) { Changed(); return; }
        if (Mode == InfoMode.Fps)
        {
            // The box is the visible glyphs plus the same padding on all four sides, right edge of the text on the right edge of
            // the box. Consolas digits are all the same width: the big number is measured as that many zeros, so the box keeps its
            // size while the digits change and only follows the number of digits (or a different word, like 静止).
            bool digits = _big.Length > 0 && _big.All(char.IsDigit);
            string refBig = digits ? new string('0', _big.Length) : _big;
            Rectangle bi = InkBounds(refBig, _bigFont, out int bAdv);
            Rectangle si = _small.Length == 0 ? Rectangle.Empty : InkBounds(_small, _font, out _);
            int smallAdv = _small.Length == 0 ? 0 : TextRenderer.MeasureText(_small, _font, Huge, TextFormatFlags.NoPadding).Width;
            int pad = FpsPad;
            int gap = _small.Length == 0 ? 0 : Math.Max(2, (int)Math.Round(_bigFont.Size * 0.22f));
            int contentW = Math.Max(bi.Width, si.Width);
            int w = pad * 2 + contentW, h = pad * 2 + bi.Height + (_small.Length == 0 ? 0 : gap + si.Height);
            // right-aligned draws in rectangles of the text's advance width: the ink of the text ends at x = right
            int right = w - pad - 1;
            int bigH = TextRenderer.MeasureText(refBig, _bigFont, Huge, TextFormatFlags.NoPadding).Height;
            _bigRect = new Rectangle(right - bi.Right + 1, pad - bi.Top, bAdv, bigH);
            if (_small.Length > 0)
            {
                int smallH = TextRenderer.MeasureText(_small, _font, Huge, TextFormatFlags.NoPadding).Height;
                _smallRect = new Rectangle(right - si.Right + 1, pad + bi.Height + gap - si.Top, smallAdv, smallH);
            }
            Size = new Size(w, h);
            SetRoundedRegion();
            Changed();
            return;
        }
        Size sz = TextRenderer.MeasureText(Text, _font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int px = TextScale.Px(7, _scale), py = TextScale.Px(5, _scale);
        Size = new Size(sz.Width + px * 2, sz.Height + py * 2);
        SetRoundedRegion();
        Changed();
    }

    private int CornerRadius => TextScale.Px(6, _scale);

    private void SetRoundedRegion()
    {
        using var path = Rounded(new Rectangle(0, 0, Width, Height), Math.Min(CornerRadius, Math.Min(Width, Height) / 2));
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void PaintText(Graphics g)
    {
        if (Mode == InfoMode.Fps)
        {
            TextRenderer.DrawText(g, _big, _bigFont, _bigRect, Fore, RightFlags);
            if (_small.Length > 0) TextRenderer.DrawText(g, _small, _font, _smallRect, Fore, RightFlags);
            return;
        }
        TextRenderer.DrawText(g, Text, _font,
            new Point(TextScale.Px(7, _scale), TextScale.Px(5, _scale)), Fore, TextFormatFlags.NoPadding);
    }

    internal override void PaintOverlay(Graphics g, bool offscreen)
    {
        using var back = new SolidBrush(TestBack ?? Color.FromArgb(200, 12, 12, 12));
        if (offscreen)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new Rectangle(0, 0, Width, Height), Math.Min(CornerRadius, Math.Min(Width, Height) / 2));
            g.FillPath(back, path);
            g.SmoothingMode = old;
        }
        else g.FillRectangle(back, new Rectangle(0, 0, Width, Height));
        PaintText(g);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _bigFont.Dispose(); }
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
    public const double KInfo = 0;       // info panels follow the monitor DPI only (like game overlays): 4K at 200% -> digits about 1.0% of the screen height
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
