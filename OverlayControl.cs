using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// One piece of text/graphics drawn over the video (notice, info panel, test caption). It always keeps the
/// layout, state and painting code of an ordinary child control, but it can run in two modes:
/// <list type="bullet">
/// <item>window mode (Media Foundation renderer, pause image): the control is shown as a child window;</item>
/// <item>virtual mode (Direct3D renderer): the control is never shown. Its painting is rendered into a
/// premultiplied BGRA image whenever it changes and handed to the engine, which blends it into the swap chain's
/// back buffer. A window over the swap chain stops direct flip and, with mixed refresh rates, makes DWM compose a
/// secondary monitor at the primary's rate; pixels inside the swap chain do not.</item>
/// </list>
/// Everything that used to read or write <c>Visible</c> goes through <see cref="Showing"/>.
/// </summary>
public abstract class OverlayControl : Control
{
    private bool _virtual, _shown;

    /// <summary>Raised (virtual mode only) when what the overlay looks like or where it is has changed.</summary>
    internal event Action? ContentChanged;

    /// <summary>Virtual mode: drawn into the video by the engine instead of being a window.</summary>
    internal bool Virtual
    {
        get => _virtual;
        set
        {
            if (_virtual == value) return;
            bool was = Showing;
            _virtual = value;
            if (value) { _shown = was; Visible = false; }
            else { Visible = _shown; _shown = false; if (Visible) { BringToFront(); Invalidate(); } }
            ContentChanged?.Invoke();
        }
    }

    /// <summary>Whether the overlay is on (a window shown, or a virtual layer drawn).</summary>
    internal bool Showing
    {
        get => _virtual ? _shown : Visible;
        set
        {
            if (_virtual)
            {
                if (_shown == value) return;
                _shown = value;
                ContentChanged?.Invoke();
            }
            else Visible = value;
        }
    }

    /// <summary>Redraw: invalidates the window, or tells the owner to render the layer again.</summary>
    protected void Changed()
    {
        Invalidate();
        if (_virtual) ContentChanged?.Invoke();
    }

    /// <summary>Paint the whole control into (0,0,Width,Height). <paramref name="offscreen"/> is true when the target is
    /// the image for the engine (no window region clips the corners, so rounded shapes must be drawn).</summary>
    internal abstract void PaintOverlay(Graphics g, bool offscreen);

    protected override void OnPaint(PaintEventArgs e) => PaintOverlay(e.Graphics, false);

    /// <summary>Render the current look into a premultiplied BGRA image. The painting code draws with GDI text on an
    /// opaque target, so it is painted twice - over black and over white - and the alpha and colour of every pixel
    /// are recovered from the two results (exact for any alpha-blended drawing, text included).
    /// Null when the overlay has no size.</summary>
    internal (byte[] Pixels, int W, int H)? RenderImage()
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0 || (long)w * h > 16_000_000) return null;
        byte[] onBlack = PaintOnto(Color.Black, w, h), onWhite = PaintOnto(Color.White, w, h);
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            int loss = ((onWhite[i] - onBlack[i]) + (onWhite[i + 1] - onBlack[i + 1]) + (onWhite[i + 2] - onBlack[i + 2])) / 3;
            int a = Math.Clamp(255 - loss, 0, 255);
            px[i] = (byte)Math.Min((int)onBlack[i], a);
            px[i + 1] = (byte)Math.Min((int)onBlack[i + 1], a);
            px[i + 2] = (byte)Math.Min((int)onBlack[i + 2], a);
            px[i + 3] = (byte)a;
        }
        return (px, w, h);
    }

    private byte[] PaintOnto(Color back, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(back);
            PaintOverlay(g, true);
        }
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, buf, y * w * 4, w * 4);
            return buf;
        }
        finally { bmp.UnlockBits(bd); }
    }
}
