using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YuCap;

// The video area is always black, and the system-light menu bar and status
// strip used to frame it in bright white/gray bands — jarring next to a black
// video canvas and out of place for what is essentially a video viewer. This
// file gives the MAIN window chrome (menu bar, its dropdowns, the context
// menu, the status bar) a dark palette that matches, plus a small shared
// dialog-layout kit so every settings/about dialog is built the same way.
// Dialogs themselves stay system-light (they're not next to the video canvas
// and system controls read better with system theming).
internal static class Ui
{
    public const int Pad = 16;           // outer margin of every dialog
    public const int Gap = 8;            // space between related controls
    public const int ButtonWidth = 88;
    public const int ButtonHeight = 28;

    /// <summary>
    /// A blank dialog shell, sized in client pixels. AutoScaleDimensions is
    /// pinned to (7,15) — the metric WinForms measures for Segoe UI 9pt, the
    /// default font — because every dialog below is laid out with pixel
    /// coordinates drawn AT that metric. Setting it explicitly (rather than
    /// leaving WinForms to infer it from the design-time font) is what makes
    /// AutoScaleMode.Font actually rescale those pixel positions correctly
    /// when the user runs at 125-200% DPI instead of leaving them too small.
    /// </summary>
    public static Form NewDialog(string title, int clientWidth, int clientHeight)
    {
        return new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            KeyPreview = false,
            ClientSize = new Size(clientWidth, clientHeight),
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };
    }

    public static Label Label(string text, int x, int y)
    {
        return new Label { Text = text, AutoSize = true, Location = new Point(x, y) };
    }

    /// <summary>Secondary/explanatory text under a control. Wraps at maxWidth
    /// instead of running off the dialog, and is dimmed relative to the
    /// primary text.</summary>
    public static Label Hint(string text, int x, int y, int maxWidth)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            Location = new Point(x, y),
            // SystemColors.GrayText, not Color.Gray: GrayText tracks the
            // active theme and stays readable, where a fixed gray measures
            // only ~2.9:1 contrast against the dialog background — below the
            // usual 4.5:1 legibility floor for body text.
            ForeColor = SystemColors.GrayText,
        };
    }

    public static Button Button(string text, int x, int y, int width = ButtonWidth)
    {
        return new Button { Text = text, Location = new Point(x, y), Size = new Size(width, ButtonHeight) };
    }

    /// <summary>
    /// Adds OK + キャンセル right-aligned at the bottom of the dialog, using
    /// its current ClientSize. Wires up AcceptButton/CancelButton and each
    /// button's DialogResult so Enter/Escape work without extra code, and
    /// returns the OK button so the caller can attach validation to Click.
    /// </summary>
    public static Button AddOkCancel(Form dlg, string? okText = null)
    {
        int w = dlg.ClientSize.Width;
        int h = dlg.ClientSize.Height;

        var cancel = new Button
        {
            Text = L.T("キャンセル"),
            Location = new Point(w - Pad - ButtonWidth, h - Pad - ButtonHeight),
            Size = new Size(ButtonWidth, ButtonHeight),
            DialogResult = DialogResult.Cancel,
        };
        var ok = new Button
        {
            Text = okText ?? "OK",
            Location = new Point(cancel.Location.X - Gap - ButtonWidth, h - Pad - ButtonHeight),
            Size = new Size(ButtonWidth, ButtonHeight),
            DialogResult = DialogResult.OK,
        };

        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        return ok;
    }

    public static class Colors
    {
        public static readonly Color Back = ColorTranslator.FromHtml("#1E1E1E");     // strip background
        public static readonly Color Surface = ColorTranslator.FromHtml("#252526");  // dropdown background
        public static readonly Color Hover = ColorTranslator.FromHtml("#3A3D41");    // hovered/selected item
        public static readonly Color Border = ColorTranslator.FromHtml("#3F3F46");   // borders, separators
        public static readonly Color Text = ColorTranslator.FromHtml("#E6E6E6");
        public static readonly Color SubText = ColorTranslator.FromHtml("#9D9D9D");  // disabled text, shortcut text
        public static readonly Color Accent = ColorTranslator.FromHtml("#4DA3FF");   // check marks
    }

    private static ToolStripRenderer? _darkRenderer;

    /// <summary>Lazily-created singleton: all menu/status/context strips on
    /// the main window share one renderer instance.</summary>
    public static ToolStripRenderer DarkRenderer => _darkRenderer ??= new DarkToolStripRenderer();

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public DarkColorTable()
        {
            // Without this, ProfessionalColorTable blends its palette with the
            // OS accent/theme colors instead of using the values below as-is.
            UseSystemColors = false;
        }

        public override Color MenuStripGradientBegin => Colors.Back;
        public override Color MenuStripGradientEnd => Colors.Back;
        public override Color ToolStripGradientBegin => Colors.Back;
        public override Color ToolStripGradientMiddle => Colors.Back;
        public override Color ToolStripGradientEnd => Colors.Back;
        public override Color StatusStripGradientBegin => Colors.Back;
        public override Color StatusStripGradientEnd => Colors.Back;

        public override Color ToolStripDropDownBackground => Colors.Surface;
        public override Color ImageMarginGradientBegin => Colors.Surface;
        public override Color ImageMarginGradientMiddle => Colors.Surface;
        public override Color ImageMarginGradientEnd => Colors.Surface;

        public override Color MenuItemSelected => Colors.Hover;
        public override Color MenuItemSelectedGradientBegin => Colors.Hover;
        public override Color MenuItemSelectedGradientEnd => Colors.Hover;
        public override Color MenuItemPressedGradientBegin => Colors.Hover;
        public override Color MenuItemPressedGradientMiddle => Colors.Hover;
        public override Color MenuItemPressedGradientEnd => Colors.Hover;
        public override Color ButtonSelectedHighlight => Colors.Hover;
        public override Color CheckBackground => Colors.Hover;
        public override Color CheckSelectedBackground => Colors.Hover;
        public override Color CheckPressedBackground => Colors.Hover;

        public override Color MenuItemBorder => Colors.Border;
        public override Color MenuBorder => Colors.Border;
        public override Color ToolStripBorder => Colors.Border;
        public override Color SeparatorDark => Colors.Border;

        public override Color SeparatorLight => Colors.Back;
    }

    private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Covers both the label and the shortcut-key text of a
            // ToolStripMenuItem — WinForms routes both through this same
            // event, so dimming disabled items here is enough; no separate
            // hook is needed for the shortcut column.
            e.TextColor = e.Item.Enabled ? Colors.Text : Colors.SubText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Colors.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Replace the default checkmark glyph (drawn for the light
            // system theme) with one in the accent color, centered in the
            // reserved image rectangle.
            Rectangle r = e.ImageRectangle;
            Graphics g = e.Graphics;
            SmoothingMode oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Colors.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x0 = r.Left + r.Width * 0.20f;
            float y0 = r.Top + r.Height * 0.52f;
            float x1 = r.Left + r.Width * 0.42f;
            float y1 = r.Top + r.Height * 0.75f;
            float x2 = r.Left + r.Width * 0.80f;
            float y2 = r.Top + r.Height * 0.28f;
            g.DrawLines(pen, new[] { new PointF(x0, y0), new PointF(x1, y1), new PointF(x2, y2) });
            g.SmoothingMode = oldMode;
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle b = new Rectangle(Point.Empty, e.Item.Size);
            using var pen = new Pen(Colors.Border);
            if (e.Vertical)
            {
                int x = b.Width / 2;
                e.Graphics.DrawLine(pen, x, 0, x, b.Height);
            }
            else
            {
                int y = b.Height / 2;
                e.Graphics.DrawLine(pen, 0, y, b.Width, y);
            }
        }

        protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e)
        {
            // Three small dots in the bottom-right corner, in Border, instead
            // of the light-themed default grip.
            Rectangle b = e.ToolStrip.ClientRectangle;
            using var brush = new SolidBrush(Colors.Border);
            const int dot = 2, gap = 3;
            for (int row = 0; row < 3; row++)
                for (int col = 0; col <= row; col++)
                {
                    int x = b.Right - 4 - col * gap;
                    int y = b.Bottom - 4 - row * gap;
                    e.Graphics.FillEllipse(brush, x - dot / 2, y - dot / 2, dot, dot);
                }
        }

        protected override void OnRenderLabelBackground(ToolStripItemRenderEventArgs e)
        {
            base.OnRenderLabelBackground(e);
            // ProfessionalRenderer draws a ToolStripStatusLabel's BorderSides
            // via the base label background using the (light-themed) 3D
            // border colors baked into the base renderer, which stay
            // invisible-to-wrong on a dark strip. Draw our own 1px Border
            // line for whichever sides the label actually requested; the
            // status bar in this app only ever sets Right, so that is the
            // one guaranteed to render, but the others are handled too in
            // case a future label sets them.
            if (e.Item is not ToolStripStatusLabel label || label.BorderSides == ToolStripStatusLabelBorderSides.None)
                return;

            Rectangle b = new Rectangle(Point.Empty, e.Item.Size);
            using var pen = new Pen(Colors.Border);
            if (label.BorderSides.HasFlag(ToolStripStatusLabelBorderSides.Right))
                e.Graphics.DrawLine(pen, b.Right - 1, 0, b.Right - 1, b.Bottom);
            if (label.BorderSides.HasFlag(ToolStripStatusLabelBorderSides.Left))
                e.Graphics.DrawLine(pen, 0, 0, 0, b.Bottom);
            if (label.BorderSides.HasFlag(ToolStripStatusLabelBorderSides.Top))
                e.Graphics.DrawLine(pen, 0, 0, b.Right, 0);
            if (label.BorderSides.HasFlag(ToolStripStatusLabelBorderSides.Bottom))
                e.Graphics.DrawLine(pen, 0, b.Bottom - 1, b.Right, b.Bottom - 1);
        }
    }
}
