using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YuCap;

// One dark look for every window: the main window chrome (menu bar, its dropdowns, the context menu, the status bar)
// drawn by the renderer below, and every dialog and tool window built from the kit in UiControls.cs (themed
// controls, content-sized layout, the message box). Ui.Colors is the only palette.
internal static partial class Ui
{
    public const int Pad = 16;           // outer margin of every window, space between groups
    public const int Gap = 8;            // space between related controls
    public const int ButtonWidth = 88;   // minimum button size (96-dpi pixels); buttons grow to fit their caption
    public const int ButtonHeight = 28;

    /// <summary>A blank dialog: dark, sized to its content once laid out with <see cref="Frame"/>.
    /// <paramref name="width96"/> is the client width to aim for (96-dpi pixels; wider when the content needs it);
    /// <paramref name="fillHeight96"/> the client height for a dialog whose body fills it (a list).</summary>
    public static UiDialog NewDialog(string title, int width96 = 400, int fillHeight96 = 0)
    {
        return new UiDialog
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ContentWidth96 = width96,
            ContentHeight96 = fillHeight96,
        };
    }

    /// <summary>Test mode (ダイアログの表示崩れ): when set, every <see cref="UiDialog"/> that would be
    /// shown modally is handed to this instead and reported as cancelled, so each dialog's layout can be
    /// inspected without a person having to open it.</summary>
    public static Func<Form, DialogResult>? DialogAudit;

    public static class Colors
    {
        public static readonly Color Back = ColorTranslator.FromHtml("#1E1E1E");        // window / strip background
        public static readonly Color Surface = ColorTranslator.FromHtml("#252526");     // dropdowns, panels, tool tips
        public static readonly Color Field = ColorTranslator.FromHtml("#2B2B2B");       // text boxes, lists
        public static readonly Color FieldAlt = ColorTranslator.FromHtml("#303031");    // every other row of a table
        public static readonly Color Hover = ColorTranslator.FromHtml("#3A3D41");       // hovered/selected item
        public static readonly Color Border = ColorTranslator.FromHtml("#3F3F46");      // borders, separators
        public static readonly Color ButtonFace = ColorTranslator.FromHtml("#2D2D30");  // push buttons
        public static readonly Color ButtonEdge = ColorTranslator.FromHtml("#54545C");  // push button / field edge
        public static readonly Color ButtonDown = ColorTranslator.FromHtml("#45494E");
        public static readonly Color Track = ColorTranslator.FromHtml("#4A4A50");       // slider channel, progress track
        public static readonly Color Text = ColorTranslator.FromHtml("#E6E6E6");        // ~14:1 on Back
        public static readonly Color SubText = ColorTranslator.FromHtml("#A6A6A6");     // hints, shortcut text (~7:1)
        public static readonly Color Disabled = ColorTranslator.FromHtml("#949494");    // disabled captions (~5.5:1, still readable)
        public static readonly Color KeyText = ColorTranslator.FromHtml("#C8C8C8");     // key names in 操作一覧
        public static readonly Color Accent = ColorTranslator.FromHtml("#4DA3FF");      // check marks, links, focus
        public static readonly Color AccentHot = ColorTranslator.FromHtml("#6CB4FF");
        public static readonly Color AccentFill = ColorTranslator.FromHtml("#0E639C");  // primary button (white text ~6:1)
        public static readonly Color AccentDown = ColorTranslator.FromHtml("#0B4F7D");
        public static readonly Color Danger = ColorTranslator.FromHtml("#F1707B");      // validation messages on dark
        /// <summary>Background of the panels drawn over the video (OSD, info panel): 82% black.</summary>
        public static readonly Color OverlayBack = Color.FromArgb(209, 16, 16, 16);
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
            if (e.Item.AccessibleRole == AccessibleRole.RadioButton)
            {
                // A choose-one item (MainForm.Radio): a dot, so it reads differently from an on/off tick.
                float d = Math.Min(r.Width, r.Height) * 0.38f;
                using var dot = new SolidBrush(Colors.Accent);
                g.FillEllipse(dot, r.Left + (r.Width - d) / 2f, r.Top + (r.Height - d) / 2f, d, d);
                g.SmoothingMode = oldMode;
                return;
            }
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

        protected override void OnRenderOverflowButtonBackground(ToolStripItemRenderEventArgs e)
        {
            // The menu bar's overflow button (a narrow window): a chevron in Text on the dark strip, Hover when pointed at.
            Rectangle b = new Rectangle(Point.Empty, e.Item.Size);
            using (var back = new SolidBrush(e.Item.Selected || e.Item.Pressed ? Colors.Hover : Colors.Back))
                e.Graphics.FillRectangle(back, b);
            SmoothingMode old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Max(3f, b.Height / 7f), cx = b.Width / 2f, cy = b.Height / 2f;
            using var pen = new Pen(Colors.Text, Math.Max(1.2f, s / 2.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            for (int i = 0; i < 2; i++)
            {
                float x = cx - s + i * s;
                e.Graphics.DrawLines(pen, new[] { new PointF(x, cy - s), new PointF(x + s * 0.8f, cy), new PointF(x, cy + s) });
            }
            e.Graphics.SmoothingMode = old;
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

        protected override void OnRenderToolStripStatusLabelBackground(ToolStripItemRenderEventArgs e)
        {
            // (The professional renderer drew BorderSides here as light 3D lines.) No 3D label borders on the dark strip (BorderSides stays None): a status label gets one 1 px Border line
            // at its left edge when a visible label comes before it, so separators sit only BETWEEN segments - never
            // after the last one.
            if (e.Item is not ToolStripStatusLabel label || label.Spring || e.ToolStrip == null) return;
            bool afterAnother = false;
            foreach (ToolStripItem other in e.ToolStrip.Items)
            {
                if (ReferenceEquals(other, label)) break;
                if (other.Available && other is ToolStripStatusLabel { Spring: false } o && o.Text?.Length > 0) afterAnother = true;
            }
            if (!afterAnother) return;
            int inset = Math.Max(3, e.Item.Height / 5);
            using var pen = new Pen(Colors.Border);
            e.Graphics.DrawLine(pen, 0, inset, 0, e.Item.Height - inset);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // The status bar gets one Border line along its top edge (the light 3D edge of the default renderer is gone);
            // the other strips keep the professional renderer's own border.
            if (e.ToolStrip is StatusStrip)
            {
                using var pen = new Pen(Colors.Border);
                e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
                return;
            }
            base.OnRenderToolStripBorder(e);
        }
    }
}

/// <summary>The dialog shell made by <see cref="Ui.NewDialog"/>: a <see cref="UiWindow"/> (dark, content-sized,
/// centred on the window that opened it) plus the audit hook. Callers hold it as a <c>var</c>, so their
/// <c>dlg.ShowDialog(this)</c> binds to the method below (<c>Form.ShowDialog</c> itself is not virtual).</summary>
internal sealed class UiDialog : UiWindow
{
    public new DialogResult ShowDialog(IWin32Window? owner)
    {
        Func<Form, DialogResult>? audit = Ui.DialogAudit;
        if (audit != null) return audit(this);
        PrePlace(owner);
        return owner != null ? base.ShowDialog(owner) : base.ShowDialog();
    }

    // While being audited the dialog is shown invisibly (see MainForm.TestMode.cs); it must not take focus.
    protected override bool ShowWithoutActivation => Ui.DialogAudit != null;
}
