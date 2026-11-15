using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

// The look every YuCap window shares: one dark theme (the palette is Ui.Colors), one font family (the system message
// font: Yu Gothic UI on Japanese Windows), and one way of laying out a dialog - content-sized stacks, rows and grids
// on an 8 px grid, measured from the real fonts at the window's own DPI instead of pixel positions drawn at 96 dpi.
// The controls WinForms cannot paint dark (buttons, check boxes, sliders, progress bars) are drawn here.

/// <summary>Layout options of one control inside a Ui layout panel. Sizes are in 96-dpi pixels; the panels scale them.</summary>
internal sealed class LayoutOpt
{
    public int Before = -1;      // space above (stack, grid row) or to the left (row); -1 = the panel's default gap
    public int Indent;           // stack: left indent
    public bool? Stretch;        // stack/grid: as wide as the column; row: takes the remaining width
    public int Width96;          // natural width of a control that has none of its own (spin box, combo box, picture)
    public int Height96;         // fixed height (multi-line boxes, lists, pictures)
    public bool Hidden;          // left out of the layout (see Ui.Show)
    public bool Top;             // row: align to the top instead of centring
    public float FontPt;         // > 0: this control has its own font size (pt at 96 dpi), re-made for every DPI
    public FontStyle FontStyle;
}

/// <summary>A window with the YuCap look: dark title bar and body, fonts made for the monitor's DPI, and (with a
/// <see cref="UiFrame"/>) a client size that follows its content. Dialogs (<see cref="UiDialog"/>) and the training
/// progress window derive from it.</summary>
internal class UiWindow : Form
{
    private UiFrame? _frame;
    private bool _fitPending, _loaded, _placing, _shown;

    /// <summary>Client width to aim for, in 96-dpi pixels (the content may make it wider, the screen narrower).</summary>
    public int ContentWidth96 { get; set; } = 400;
    /// <summary>For a frame whose body fills the window (lists): the client height to aim for when the body has no
    /// height of its own; 0 = the body's preferred height.</summary>
    public int ContentHeight96 { get; set; }
    /// <summary>Control focused when the window opens (null: the first one in tab order).</summary>
    public Control? InitialFocus { get; set; }
    /// <summary>Keep the window where the caller put it instead of centring it on its owner.</summary>
    public bool KeepLocation { get; set; }
    /// <summary>The window that opened this one (the owner passed to ShowDialog, or <see cref="Form.Owner"/>).</summary>
    protected Form? Opener { get; set; }

    public UiWindow()
    {
        AutoScaleMode = AutoScaleMode.None;   // every size is computed from the fonts at the window's DPI (see Fit)
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Colors.Back;
        ForeColor = Ui.Colors.Text;
        Font = Ui.FontAt(0, Ui.BodyPt);
        KeyPreview = false;
    }

    internal UiFrame? Frame => _frame;

    internal void SetFrame(UiFrame frame)
    {
        _frame = frame;
        frame.Dock = DockStyle.Fill;
        Controls.Add(frame);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.DarkTitleBar(Handle);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _loaded = true;
        Ui.ApplyTheme(this);
        ApplyDpiFonts();
        Fit();
        Place();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        Control? focus = InitialFocus;
        if (focus != null && focus.CanFocus) focus.Focus();
        else if (AcceptButton is Control ok && ActiveControl == null && ok.CanFocus) ok.Focus();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!_loaded) return;
        ApplyDpiFonts();
        Fit();
        if (_placing) Place();
    }

    /// <summary>The body font for this monitor, and the controls that have a font role of their own re-made to match.</summary>
    private void ApplyDpiFonts()
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        Font body = Ui.FontAt(dpi, Ui.BodyPt);
        if (!Equals(Font, body)) Font = body;
        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                LayoutOpt? o = Ui.OptOrNull(c);
                if (o != null && o.FontPt > 0)
                {
                    Font f = Ui.FontAt(dpi, o.FontPt, o.FontStyle);
                    if (!Equals(c.Font, f)) c.Font = f;
                }
                if (c is not UpDownBase) Walk(c);
            }
        }
        Walk(this);
    }

    /// <summary>Re-measure soon (coalesced): a text, a font or a shown/hidden part changed.</summary>
    internal void RequestFit()
    {
        if (!_loaded || _fitPending || !IsHandleCreated || IsDisposed) return;
        _fitPending = true;
        BeginInvoke(new Action(() => { _fitPending = false; if (!IsDisposed) Fit(); }));
    }

    /// <summary>Size the client area to the content at this DPI: as wide as asked (or as the widest part that cannot
    /// wrap), as tall as the content at that width - never larger than the monitor's working area.</summary>
    internal void Fit()
    {
        if (_frame == null || IsDisposed || !IsHandleCreated) return;
        Rectangle wa;
        try { wa = Screen.FromHandle(Handle).WorkingArea; } catch { wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720); }
        Size chrome = Size - ClientSize;
        int maxW = Math.Max(200, wa.Width - chrome.Width - Ui.Px(this, 32));
        int maxH = Math.Max(150, wa.Height - chrome.Height - Ui.Px(this, 32));
        int w = Math.Max(Ui.Px(this, ContentWidth96), _frame.MinWidth());
        if (_shown) w = Math.Max(w, ClientSize.Width);   // once open, the width never jumps narrower
        w = Math.Min(w, maxW);
        int h = _frame.Measure(w).Height;
        if (_frame.Fill && ContentHeight96 > 0) h = Math.Max(h, Ui.Px(this, ContentHeight96));
        h = Math.Min(h, maxH);
        SuspendLayout();
        if (ClientSize != new Size(w, h)) ClientSize = new Size(w, h);
        ResumeLayout(false);
        _frame.Relayout();
    }

    /// <summary>Centre on the window that opened this one, inside the working area of that window's monitor.</summary>
    internal void Place()
    {
        if (KeepLocation || Ui.DialogAudit != null) return;   // the audit puts the window on the monitor under test
        Form? o = Opener ?? Owner;
        Rectangle area;
        Rectangle wa;
        if (o != null && !o.IsDisposed && o.IsHandleCreated && o.WindowState != FormWindowState.Minimized)
        {
            area = o.Bounds;
            wa = Screen.FromHandle(o.Handle).WorkingArea;
        }
        else
        {
            wa = (o != null && o.IsHandleCreated ? Screen.FromHandle(o.Handle) : Screen.PrimaryScreen)?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            area = wa;
        }
        int x = area.X + (area.Width - Width) / 2, y = area.Y + (area.Height - Height) / 2;
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - Width));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height));
        _placing = true;
        try { Location = new Point(x, y); }
        finally { _placing = false; }
    }

    /// <summary>Before the handle exists: put the window on the opener's monitor, so it is created at that monitor's DPI.</summary>
    protected void PrePlace(IWin32Window? owner)
    {
        Opener = owner as Form ?? (owner is Control c ? c.FindForm() : null) ?? Ui.MainWindow;
        if (KeepLocation || Opener == null || Opener.IsDisposed || IsHandleCreated) return;
        try
        {
            Rectangle b = Opener.WindowState == FormWindowState.Minimized ? Screen.FromControl(Opener).WorkingArea : Opener.Bounds;
            Location = new Point(b.X + Math.Max(0, (b.Width - Width) / 2), b.Y + Math.Max(0, (b.Height - Height) / 2));
        }
        catch { /* the default position */ }
    }

    public new void Show(IWin32Window? owner)
    {
        PrePlace(owner);
        if (owner != null) base.Show(owner); else base.Show();
    }
}

/// <summary>Base of the layout panels: measures its children at a given width (<see cref="Measure"/>), knows the
/// narrowest it can be (<see cref="MinWidth"/>) and puts the children where the measurement said.</summary>
internal abstract class UiLayout : Panel
{
    protected UiLayout()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        TabStop = false;
    }

    /// <summary>Size at width <paramref name="avail"/> (0 = as wide as the content likes).</summary>
    public abstract Size Measure(int avail);
    public abstract int MinWidth();
    protected abstract void Arrange(Rectangle r);

    public override Size GetPreferredSize(Size proposedSize) =>
        Measure(proposedSize.Width > 1 && proposedSize.Width < 100000 ? proposedSize.Width : 0);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (Width > 0) Arrange(ClientRectangle);
        if (AutoScroll) base.OnLayout(levent);   // the scroll range, from the children where they now are
    }

    /// <summary>Lay out again now, nested panels included (their bounds may not have changed, their content has).</summary>
    public void Relayout()
    {
        if (Width > 0) Arrange(ClientRectangle);
        foreach (Control c in Controls) if (c is UiLayout l) l.Relayout();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        Control c = e.Control!;
        c.TextChanged += ChildChanged;
        c.FontChanged += ChildChanged;
        if (FindForm() is UiWindow w && w.IsHandleCreated) Ui.ApplyTheme(c);
    }

    private void ChildChanged(object? sender, EventArgs e) => Ui.RequestFit(this);

    protected IEnumerable<Control> Shown => Controls.Cast<Control>().Where(Ui.IsShown);

    protected void Place(Control c, int x, int y, int w, int h)
    {
        var r = new Rectangle(x, y, Math.Max(0, w), Math.Max(0, h));
        if (c.Bounds != r) c.Bounds = r;
    }
}

/// <summary>Controls top to bottom, each as wide as the panel (or its natural width), each as tall as it needs at that
/// width - wrapped text included. The gap above a control is <see cref="LayoutOpt.Before"/> (default 8).</summary>
internal sealed class UiStack : UiLayout
{
    public int DefaultGap = Ui.Gap;

    public T Add<T>(T c, int before = -1, int indent = 0) where T : Control
    {
        LayoutOpt o = Ui.Opt(c);
        if (before >= 0) o.Before = before;
        if (indent > 0) o.Indent = indent;
        Controls.Add(c);
        return c;
    }

    private IEnumerable<(Control C, int Before, int Indent)> Items()
    {
        bool first = true;
        foreach (Control c in Shown)
        {
            LayoutOpt o = Ui.Opt(c);
            int before = first ? 0 : Ui.Px(this, o.Before >= 0 ? o.Before : DefaultGap);
            first = false;
            yield return (c, before, Ui.Px(this, o.Indent));
        }
    }

    public override Size Measure(int avail)
    {
        int h = 0, w = 0;
        foreach (var (c, before, indent) in Items())
        {
            Size p = Ui.Pref(c, avail > 0 ? Math.Max(1, avail - indent) : 0);
            h += before + p.Height;
            w = Math.Max(w, indent + p.Width);
        }
        return new Size(avail > 0 ? avail : w, h);
    }

    public override int MinWidth()
    {
        int w = 0;
        foreach (var (c, _, indent) in Items()) w = Math.Max(w, indent + Ui.MinW(c));
        return w;
    }

    protected override void Arrange(Rectangle r)
    {
        int y = r.Y;
        foreach (var (c, before, indent) in Items())
        {
            int avail = Math.Max(1, r.Width - indent);
            Size p = Ui.Pref(c, avail);
            y += before;
            Place(c, r.X + indent, y, Ui.IsStretch(c) ? avail : Math.Min(p.Width, avail), p.Height);
            y += p.Height;
        }
    }
}

/// <summary>Controls left to right, vertically centred, 8 px apart. A control with <see cref="LayoutOpt.Stretch"/>
/// (at most one) takes the width the others leave.</summary>
internal sealed class UiRow : UiLayout
{
    public int DefaultGap = Ui.Gap;

    private List<(Control C, int Gap)> Items()
    {
        var l = new List<(Control, int)>();
        bool first = true;
        foreach (Control c in Shown)
        {
            LayoutOpt o = Ui.Opt(c);
            l.Add((c, first ? 0 : Ui.Px(this, o.Before >= 0 ? o.Before : DefaultGap)));
            first = false;
        }
        return l;
    }

    private static bool Fills(Control c) => Ui.OptOrNull(c)?.Stretch == true;

    private List<(Control C, int Gap, int W, int H)> Solve(int avail)
    {
        var items = Items();
        var res = new List<(Control, int, int, int)>();
        int fixedW = 0;
        foreach (var (c, gap) in items) fixedW += gap + (Fills(c) ? 0 : Ui.Pref(c, 0).Width);
        int fillCount = items.Count(i => Fills(i.C));
        foreach (var (c, gap) in items)
        {
            int w;
            if (Fills(c)) w = avail > 0 ? Math.Max(Ui.MinW(c), (avail - fixedW) / Math.Max(1, fillCount)) : Ui.Pref(c, 0).Width;
            else w = Ui.Pref(c, 0).Width;
            int h = Ui.Pref(c, Math.Max(1, w)).Height;
            res.Add((c, gap, w, h));
        }
        return res;
    }

    public override Size Measure(int avail)
    {
        var s = Solve(avail);
        int w = s.Sum(i => i.Gap + i.W), h = s.Count == 0 ? 0 : s.Max(i => i.H);
        return new Size(avail > 0 ? Math.Max(avail, 0) : w, h);
    }

    public override int MinWidth() => Items().Sum(i => i.Gap + (Fills(i.C) ? Ui.MinW(i.C) : Ui.Pref(i.C, 0).Width));

    protected override void Arrange(Rectangle r)
    {
        var s = Solve(r.Width);
        int h = s.Count == 0 ? 0 : s.Max(i => i.H);
        int x = r.X;
        foreach (var (c, gap, w, ch) in s)
        {
            x += gap;
            bool top = Ui.OptOrNull(c)?.Top == true;
            Place(c, x, r.Y + (top ? 0 : (h - ch) / 2), w, ch);
            x += w;
        }
    }
}

/// <summary>A form grid: a caption column as wide as the widest caption, a field column, and an optional trailing
/// column (units, a 解除 button). Captions are centred on their field. Span rows (hints) start under the fields.</summary>
internal sealed class UiGrid : UiLayout
{
    private sealed class GridRow { public Control? Cap, Field, Trail, Span; public int Before = -1; public bool SpanFromField; }
    private readonly List<GridRow> _rows = new();
    public int RowGap = Ui.Gap;

    public UiGrid AddRow(Control? caption, Control field, Control? trailing = null, int before = -1)
    {
        var r = new GridRow { Cap = caption, Field = field, Trail = trailing, Before = before };
        _rows.Add(r);
        foreach (Control? c in new[] { caption, field, trailing }) if (c != null) Controls.Add(c);
        return this;
    }

    /// <summary>A control across the field and trailing columns (or the whole width), e.g. a hint under a field.</summary>
    public UiGrid AddSpan(Control c, bool underField = true, int before = -1)
    {
        _rows.Add(new GridRow { Span = c, SpanFromField = underField, Before = before });
        Controls.Add(c);
        return this;
    }

    private IEnumerable<GridRow> Rows() => _rows.Where(r => r.Span != null ? Ui.IsShown(r.Span) : r.Field != null && Ui.IsShown(r.Field));

    private (int C0, int C2) Cols()
    {
        int c0 = 0, c2 = 0;
        foreach (GridRow r in Rows())
        {
            if (r.Cap != null) c0 = Math.Max(c0, Ui.Pref(r.Cap, 0).Width);
            if (r.Trail != null && Ui.IsShown(r.Trail)) c2 = Math.Max(c2, Ui.Pref(r.Trail, 0).Width);
        }
        return (c0, c2);
    }

    private IEnumerable<(GridRow R, int Gap, int FieldX, int FieldW, int H, int SpanX)> Solve(int avail)
    {
        var (c0, c2) = Cols();
        int gap = Ui.Px(this, Ui.Gap);
        int fieldX = c0 > 0 ? c0 + gap : 0;
        int trailW = c2 > 0 ? c2 + gap : 0;
        bool first = true;
        foreach (GridRow r in Rows())
        {
            int before = first ? 0 : Ui.Px(this, r.Before >= 0 ? r.Before : RowGap);
            first = false;
            if (r.Span != null)
            {
                int sx = r.SpanFromField ? fieldX : 0;
                int sw = avail > 0 ? avail - sx : 0;
                Size p = Ui.Pref(r.Span, sw);
                yield return (r, before, sx, avail > 0 ? sw : p.Width, p.Height, sx);
                continue;
            }
            int fw = avail > 0 ? Math.Max(1, avail - fieldX - trailW) : 0;
            Size fp = Ui.Pref(r.Field!, fw);
            int w = avail > 0 ? (Ui.IsStretch(r.Field!) ? fw : Math.Min(fp.Width, fw)) : fp.Width;
            int h = fp.Height;
            if (r.Cap != null) h = Math.Max(h, Ui.Pref(r.Cap, 0).Height);
            if (r.Trail != null && Ui.IsShown(r.Trail)) h = Math.Max(h, Ui.Pref(r.Trail, 0).Height);
            yield return (r, before, fieldX, w, h, 0);
        }
    }

    public override Size Measure(int avail)
    {
        var (c0, c2) = Cols();
        int gap = Ui.Px(this, Ui.Gap);
        int h = 0, w = 0;
        foreach (var s in Solve(avail))
        {
            h += s.Gap + s.H;
            int tw = s.R.Trail != null && c2 > 0 ? gap + c2 : 0;
            w = Math.Max(w, s.FieldX + s.FieldW + (s.R.Span == null ? tw : 0));
        }
        return new Size(avail > 0 ? avail : w, h);
    }

    public override int MinWidth()
    {
        var (c0, c2) = Cols();
        int gap = Ui.Px(this, Ui.Gap);
        int fieldX = c0 > 0 ? c0 + gap : 0, trailW = c2 > 0 ? c2 + gap : 0;
        int w = 0;
        foreach (GridRow r in Rows())
        {
            if (r.Span != null) w = Math.Max(w, (r.SpanFromField ? fieldX : 0) + Ui.MinW(r.Span));
            else w = Math.Max(w, fieldX + Ui.MinW(r.Field!) + trailW);
        }
        return w;
    }

    protected override void Arrange(Rectangle rect)
    {
        var (_, c2) = Cols();
        int gap = Ui.Px(this, Ui.Gap);
        int y = rect.Y;
        foreach (var s in Solve(rect.Width))
        {
            y += s.Gap;
            if (s.R.Span != null) { Place(s.R.Span, rect.X + s.SpanX, y, s.FieldW, s.H); y += s.H; continue; }
            if (s.R.Cap != null)
            {
                Size cp = Ui.Pref(s.R.Cap, 0);
                Place(s.R.Cap, rect.X, y + (s.H - cp.Height) / 2, cp.Width, cp.Height);
            }
            Size fp = Ui.Pref(s.R.Field!, s.FieldW);
            Place(s.R.Field!, rect.X + s.FieldX, y + (s.H - fp.Height) / 2, s.FieldW, fp.Height);
            if (s.R.Trail != null && Ui.IsShown(s.R.Trail))
            {
                Size tp = Ui.Pref(s.R.Trail, 0);
                int tx = rect.X + s.FieldX + s.FieldW + gap;
                if (Ui.IsStretch(s.R.Field!)) tx = rect.Right - c2;
                Place(s.R.Trail, tx, y + (s.H - tp.Height) / 2, tp.Width, tp.Height);
            }
            y += s.H;
        }
    }
}

/// <summary>A scrolling viewport for one tall child (a long list): as tall as the child wants; a dark scroll bar
/// appears only when the window is too short to show it all.</summary>
internal sealed class UiScroll : UiLayout
{
    private readonly Control _child;

    public UiScroll(Control child)
    {
        _child = child;
        // Vertical scrolling only: the child is always given the visible width.
        HorizontalScroll.Enabled = false;
        HorizontalScroll.Visible = false;
        HorizontalScroll.Maximum = 0;
        AutoScroll = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        child.Margin = Padding.Empty;   // a margin would count as content past the right edge (a horizontal scroll bar)
        Controls.Add(child);
    }

    /// <summary>Drawn as a field: the input background, a 1 px edge and a small inner margin.</summary>
    public bool Boxed { get => _boxed; set { _boxed = value; if (value) BackColor = Ui.Colors.Field; Invalidate(); } }
    private bool _boxed;
    /// <summary>Never taller than this (96-dpi pixels; 0 = as tall as the child); taller content scrolls.</summary>
    public int MaxHeight96 { get; set; }

    private int Inset => Boxed ? Ui.Px(this, 6) + 1 : 0;
    private int ScrollW => SystemInformation.GetVerticalScrollBarWidthForDpi(Ui.DpiOf(this));

    public override Size Measure(int avail)
    {
        int inset = Inset;
        Size p = Ui.Pref(_child, avail > 0 ? Math.Max(1, avail - 2 * inset) : 0);
        int h = p.Height + 2 * inset;
        if (MaxHeight96 > 0 && h > Ui.Px(this, MaxHeight96))
        {
            h = Ui.Px(this, MaxHeight96);
            p.Width += ScrollW;
        }
        return new Size(avail > 0 ? avail : p.Width + 2 * inset, h);
    }

    public override int MinWidth() => Ui.MinW(_child) + 2 * Inset + ScrollW;

    protected override void Arrange(Rectangle r)
    {
        int inset = Inset;
        int w = Width - 2 * inset;
        int h = Ui.Pref(_child, w).Height;
        if (h + 2 * inset > Height)
        {
            w = Width - 2 * inset - ScrollW;
            h = Ui.Pref(_child, w).Height;
        }
        Point at = AutoScrollPosition;
        var b = new Rectangle(inset + at.X, inset + at.Y, Math.Max(1, w), h);
        if (_child.Bounds != b) _child.Bounds = b;
        if (AutoScrollMinSize.Height != h + 2 * inset) AutoScrollMinSize = new Size(0, h + 2 * inset);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Boxed) return;
        using var p = new Pen(Ui.Colors.ButtonEdge);
        e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
}

/// <summary>The body of a dialog with its button bar: 16 px margins, buttons along the bottom - the left group
/// (既定に戻す, ライセンス...) on the left, the right group (OK, キャンセル) on the right.</summary>
internal sealed class UiFrame : UiLayout
{
    public Control Body { get; }
    public bool Fill { get; }
    private readonly Button[] _left, _right;

    public UiFrame(Control body, bool fill, Button[] left, Button[] right)
    {
        Body = body; Fill = fill; _left = left; _right = right;
        Controls.Add(body);
        foreach (Button b in left) Controls.Add(b);
        foreach (Button b in right) Controls.Add(b);
    }

    private int Pad => Ui.Px(this, Ui.Pad);
    private IEnumerable<Button> Buttons(Button[] g) => g.Where(Ui.IsShown);

    private int BarHeight()
    {
        int h = 0;
        foreach (Button b in Buttons(_left).Concat(Buttons(_right))) h = Math.Max(h, Ui.Pref(b, 0).Height);
        return h;
    }

    private int GroupWidth(IEnumerable<Button> g)
    {
        int w = 0, gap = Ui.Px(this, Ui.Gap);
        foreach (Button b in g) w += (w > 0 ? gap : 0) + Ui.Pref(b, 0).Width;
        return w;
    }

    public override Size Measure(int avail)
    {
        int inner = avail > 0 ? Math.Max(1, avail - 2 * Pad) : 0;
        Size bp = Ui.Pref(Body, inner);
        int bar = BarHeight();
        return new Size(avail > 0 ? avail : bp.Width + 2 * Pad, 2 * Pad + bp.Height + (bar > 0 ? Pad + bar : 0));
    }

    public override int MinWidth()
    {
        int lw = GroupWidth(Buttons(_left)), rw = GroupWidth(Buttons(_right));
        int bar = lw + rw + (lw > 0 && rw > 0 ? Ui.Px(this, 24) : 0);
        return 2 * Pad + Math.Max(Ui.MinW(Body), bar);
    }

    protected override void Arrange(Rectangle r)
    {
        int bar = BarHeight();
        int inner = Math.Max(1, r.Width - 2 * Pad);
        int bodyH = Fill ? Math.Max(1, r.Height - 2 * Pad - (bar > 0 ? Pad + bar : 0)) : Ui.Pref(Body, inner).Height;
        Place(Body, r.X + Pad, r.Y + Pad, inner, bodyH);
        if (bar == 0) return;
        int y = r.Bottom - Pad - bar, gap = Ui.Px(this, Ui.Gap);
        int x = r.X + Pad;
        foreach (Button b in Buttons(_left)) { int w = Ui.Pref(b, 0).Width; Place(b, x, y, w, bar); x += w + gap; }
        x = r.Right - Pad;
        foreach (Button b in Buttons(_right).Reverse()) { int w = Ui.Pref(b, 0).Width; x -= w; Place(b, x, y, w, bar); x -= gap; }
    }
}

/// <summary>Takes the width the other controls of a <see cref="UiRow"/> leave (pushes what follows to the right).</summary>
internal sealed class UiSpring : Control
{
    public UiSpring() { TabStop = false; Ui.Opt(this).Stretch = true; }
    public override Size GetPreferredSize(Size proposedSize) => Size.Empty;
}

/// <summary>A push button drawn in the dark palette: rounded, a visible 1 px edge, the accent fill for the primary
/// (default) button, and a disabled caption that stays readable.</summary>
internal class UiButton : Button
{
    private bool _hot, _down;

    private bool _primary;

    /// <summary>Drawn in the accent colour (the dialog's main action).</summary>
    public bool Primary { get => _primary; set { _primary = value; Invalidate(); } }

    public UiButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        UseVisualStyleBackColor = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Ui.Colors.Text;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        return new Size(Math.Max(Ui.Px(this, Ui.ButtonWidth), t.Width + Ui.Px(this, 32)),
                        Math.Max(Ui.Px(this, Ui.ButtonHeight), t.Height + Ui.Px(this, 12)));
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _down = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Graphics g = pevent.Graphics;
        g.Clear(Ui.BackOf(this));
        bool primary = Primary && Enabled;
        Color fill = !Enabled ? Ui.Colors.ButtonFace
            : primary ? (_down ? Ui.Colors.AccentDown : _hot ? Ui.Colors.AccentHot : Ui.Colors.AccentFill)
            : _down ? Ui.Colors.ButtonDown : _hot ? Ui.Colors.Hover : Ui.Colors.ButtonFace;
        Color edge = primary ? Ui.Colors.AccentHot : Enabled ? Ui.Colors.ButtonEdge : Ui.Colors.Border;
        Color text = !Enabled ? Ui.Colors.Disabled : primary ? Color.White : Ui.Colors.Text;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = Ui.Px(this, 4);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Ui.RoundRect(rect, radius))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            using var p = new Pen(edge);
            g.DrawPath(p, path);
        }
        if (Focused && ShowFocusCues)
        {
            float inset = Ui.Px(this, 2) + 0.5f;
            using GraphicsPath ring = Ui.RoundRect(RectangleF.Inflate(rect, -inset + 0.5f, -inset + 0.5f), Math.Max(1, radius - 1));
            using var p = new Pen(primary ? Color.White : Ui.Colors.Text) { DashStyle = DashStyle.Dot };
            g.DrawPath(p, ring);
        }
        g.SmoothingMode = SmoothingMode.None;
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, fill, flags);
    }
}

/// <summary>A check box drawn in the dark palette whose caption wraps to the width it is given; its preferred size is
/// the size it really paints, so a long caption can never be cut off.</summary>
internal class UiCheck : CheckBox
{
    private bool _hot;

    public UiCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        BackColor = Color.Transparent;
        ForeColor = Ui.Colors.Text;
    }

    private int Box => Ui.Px(this, 16);
    private int TextX => Box + Ui.Px(this, 8);
    // The same flags (and so the same small left padding) as a Label: a hint indented under the caption lines up with it.
    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    private int LineHeight => TextRenderer.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height;

    public override Size GetPreferredSize(Size proposedSize)
    {
        int avail = proposedSize.Width > 1 && proposedSize.Width < 100000 ? proposedSize.Width : 100000;
        int tw = Math.Max(1, avail - TextX - 2);
        Size t = Text.Length == 0 ? Size.Empty : TextRenderer.MeasureText(Text, Font, new Size(tw, int.MaxValue), Flags);
        return new Size(TextX + t.Width + 2, Math.Max(Math.Max(Box, LineHeight), t.Height) + 2);
    }

    protected override void OnMouseEnter(EventArgs eventargs) { _hot = true; Invalidate(); base.OnMouseEnter(eventargs); }
    protected override void OnMouseLeave(EventArgs eventargs) { _hot = false; Invalidate(); base.OnMouseLeave(eventargs); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        Graphics g = pevent.Graphics;
        Color back = Ui.BackOf(this);
        g.Clear(back);
        int box = Box, line = LineHeight;
        int by = 1 + Math.Max(0, (line - box) / 2);
        var r = new RectangleF(1.5f, by + 0.5f, box - 1, box - 1);
        bool on = CheckState != CheckState.Unchecked;
        Color fill = !Enabled ? (on ? Ui.Colors.Disabled : Ui.Colors.Field) : on ? (_hot ? Ui.Colors.AccentHot : Ui.Colors.Accent) : _hot ? Ui.Colors.Hover : Ui.Colors.Field;
        Color edge = !Enabled ? Ui.Colors.Border : on ? fill : _hot ? Ui.Colors.Text : Ui.Colors.SubText;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = Ui.RoundRect(r, Ui.Px(this, 3)))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            using var p = new Pen(edge);
            g.DrawPath(p, path);
        }
        if (on)
        {
            using var pen = new Pen(Ui.Colors.Back, Math.Max(1.5f, box / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (CheckState == CheckState.Indeterminate)
                g.DrawLine(pen, r.Left + r.Width * 0.25f, r.Top + r.Height / 2, r.Right - r.Width * 0.25f, r.Top + r.Height / 2);
            else
                g.DrawLines(pen, new[]
                {
                    new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                    new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                    new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
                });
        }
        g.SmoothingMode = SmoothingMode.None;
        var tr = new Rectangle(TextX, 1, Math.Max(1, Width - TextX - 2), Math.Max(1, Height - 2));
        TextRenderer.DrawText(g, Text, Font, tr, Enabled ? ForeColor : Ui.Colors.Disabled, back, Flags);
        if (Focused && ShowFocusCues && Text.Length > 0)
        {
            Size t = TextRenderer.MeasureText(Text, Font, new Size(tr.Width, int.MaxValue), Flags);
            var fr = new Rectangle(tr.X - 2, tr.Y - 1, Math.Min(tr.Width, t.Width) + 4, Math.Min(Height - 1, t.Height + 1));
            using var p = new Pen(Ui.Colors.SubText) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(p, fr);
        }
    }
}

/// <summary>A slider in the dark palette (replaces TrackBar, whose channel and thumb stay light): arrow keys,
/// Page Up/Down, Home/End, dragging and the wheel; the part between zero (or the minimum) and the thumb is filled.</summary>
internal sealed class UiSlider : Control
{
    private int _min, _max = 100, _value;
    private bool _drag, _hot;

    public event EventHandler? ValueChanged;
    public int SmallChange { get; set; } = 1;
    public int LargeChange { get; set; } = 10;
    /// <summary>Distance between tick marks under the track; 0 = none.</summary>
    public int TickFrequency { get; set; }

    public UiSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = true;
        BackColor = Color.Transparent;
    }

    public int Minimum { get => _min; set { _min = value; if (_max < _min) _max = _min; Value = _value; Invalidate(); } }
    public int Maximum { get => _max; set { _max = value; if (_min > _max) _min = _max; Value = _value; Invalidate(); } }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Thumb => Ui.Px(this, 18);
    private int TrackY => Ui.Px(this, 11);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Ui.Px(this, 200), Ui.Px(this, TickFrequency > 0 ? 30 : 24));

    private float XOf(int v)
    {
        float l = Thumb / 2f, r = Width - Thumb / 2f;
        return _max == _min ? l : l + (r - l) * (v - _min) / (float)(_max - _min);
    }

    private int ValueAt(int x)
    {
        float l = Thumb / 2f, r = Width - Thumb / 2f;
        if (r <= l) return _min;
        return _min + (int)Math.Round((x - l) / (r - l) * (_max - _min));
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value -= SmallChange; break;
            case Keys.Right: case Keys.Up: Value += SmallChange; break;
            case Keys.PageDown: Value -= LargeChange; break;
            case Keys.PageUp: Value += LargeChange; break;
            case Keys.Home: Value = _min; break;
            case Keys.End: Value = _max; break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        _drag = true;
        Capture = true;
        Value = ValueAt(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) Value = ValueAt(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Enabled) Value += Math.Sign(e.Delta) * SmallChange;
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Ui.BackOf(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int th = Ui.Px(this, 4), y = TrackY;
        float l = Thumb / 2f, r = Width - Thumb / 2f;
        using (GraphicsPath track = Ui.RoundRect(new RectangleF(l, y - th / 2f, Math.Max(1, r - l), th), th / 2f))
        using (var b = new SolidBrush(Ui.Colors.Track))
            g.FillPath(b, track);
        int origin = _min <= 0 && _max >= 0 ? 0 : _min;
        float x0 = XOf(origin), x1 = XOf(_value);
        if (Math.Abs(x1 - x0) > 0.5f)
        {
            using GraphicsPath fill = Ui.RoundRect(new RectangleF(Math.Min(x0, x1), y - th / 2f, Math.Abs(x1 - x0), th), th / 2f);
            using var b = new SolidBrush(Enabled ? Ui.Colors.Accent : Ui.Colors.Disabled);
            g.FillPath(b, fill);
        }
        if (TickFrequency > 0 && _max > _min)
        {
            using var p = new Pen(Ui.Colors.SubText);
            int ty = y + Thumb / 2 + Ui.Px(this, 3), tl = Ui.Px(this, 4);
            for (int v = _min; v <= _max; v += TickFrequency)
            {
                float x = (float)Math.Round(XOf(v)) + 0.5f;
                g.DrawLine(p, x, ty, x, ty + (v == origin ? tl + Ui.Px(this, 2) : tl));
            }
        }
        float tr = Thumb / 2f - 0.5f;
        using (var ring = new SolidBrush(Ui.Colors.ButtonFace))
            g.FillEllipse(ring, x1 - tr, y - tr, tr * 2, tr * 2);
        using (var edge = new Pen(Focused && ShowFocusCues ? Ui.Colors.Text : Ui.Colors.ButtonEdge))
            g.DrawEllipse(edge, x1 - tr, y - tr, tr * 2, tr * 2);
        float ir = tr * (_drag ? 0.42f : _hot || Focused ? 0.55f : 0.48f);
        using (var dot = new SolidBrush(Enabled ? Ui.Colors.Accent : Ui.Colors.Disabled))
            g.FillEllipse(dot, x1 - ir, y - ir, ir * 2, ir * 2);
    }
}

/// <summary>A thin progress bar in the dark palette (replaces ProgressBar, which ignores colours under visual
/// styles). <see cref="ProgressBarStyle.Marquee"/> runs a segment back and forth while there is no estimate.</summary>
internal sealed class UiProgress : Control
{
    private int _value, _max = 100;
    private ProgressBarStyle _style = ProgressBarStyle.Continuous;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private int _phase;

    public UiProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        BackColor = Color.Transparent;
        _timer.Tick += (_, _) => { _phase = (_phase + 2) % 200; Invalidate(); };
    }

    public int Maximum { get => _max; set { _max = Math.Max(1, value); Invalidate(); } }
    public int Value { get => _value; set { int v = Math.Clamp(value, 0, _max); if (v != _value) { _value = v; Invalidate(); } } }

    public ProgressBarStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            if (value == ProgressBarStyle.Marquee) _timer.Start(); else _timer.Stop();
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Ui.Px(this, 200), Ui.Px(this, 6));

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Ui.BackOf(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width, Height);
        float rad = Height / 2f;
        using (GraphicsPath t = Ui.RoundRect(r, rad))
        using (var b = new SolidBrush(Ui.Colors.Track))
            g.FillPath(b, t);
        float x, w;
        if (_style == ProgressBarStyle.Marquee)
        {
            w = Width * 0.25f;
            float p = _phase < 100 ? _phase / 100f : (200 - _phase) / 100f;
            x = (Width - w) * p;
        }
        else
        {
            x = 0;
            w = Width * (float)_value / _max;
        }
        if (w >= 1)
        {
            using GraphicsPath f = Ui.RoundRect(new RectangleF(x, 0, Math.Max(Height, w), Height), rad);
            using var b = new SolidBrush(Ui.Colors.Accent);
            g.FillPath(b, f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A spin box whose frame is drawn in the palette (WinForms paints a light themed frame after the Paint event,
/// so it is covered here, after the base painting).</summary>
internal sealed class UiNumber : NumericUpDown
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Ui.FieldEdge(this));
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using var inner = new Pen(Ui.Colors.Field);
        e.Graphics.DrawRectangle(inner, 1, 1, Width - 3, Height - 3);
    }

    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
}

/// <summary>An icon drawn at the size its layout gives it, from the icon frame closest to that size (crisp at any DPI).</summary>
internal sealed class UiIcon : Control
{
    private readonly Func<int, Icon?> _source;
    private Icon? _icon;
    private int _iconSize;

    public UiIcon(Func<int, Icon?> source, int size96)
    {
        _source = source;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        Ui.Fixed(this, size96, size96);
        Ui.Opt(this).Top = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Ui.BackOf(this));
        int size = Math.Min(Width, Height);
        if (size <= 0) return;
        if (_icon == null || _iconSize != size)
        {
            _icon?.Dispose();
            try { _icon = _source(size); } catch { _icon = null; }
            _iconSize = size;
        }
        if (_icon != null) e.Graphics.DrawIcon(_icon, new Rectangle(0, 0, size, size));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A read-only two-column table with group headings (操作一覧): the left column the action, the right one the
/// keys; both wrap, so nothing is truncated whatever the language or the width.</summary>
internal sealed class UiTable : Control
{
    private readonly List<(bool Head, string A, string B)> _rows = new();

    public UiTable()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Colors.Field;
        ForeColor = Ui.Colors.Text;
        TabStop = false;
    }

    /// <summary>Share of the width given to the first column.</summary>
    public float Split { get; set; } = 0.55f;

    public void AddGroup(string title) { _rows.Add((true, title, string.Empty)); Invalidate(); }
    public void AddRow(string a, string b) { _rows.Add((false, a, b)); Invalidate(); }
    public int RowCount => _rows.Count;

    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    private Font HeadFont => Ui.FontAt(Ui.DpiOf(this), Ui.BodyPt, FontStyle.Bold);

    private IEnumerable<(int Index, Rectangle Row, Rectangle A, Rectangle B)> Rows(int width)
    {
        int padX = Ui.Px(this, 12), padY = Ui.Px(this, 6), gap = Ui.Px(this, 16), headTop = Ui.Px(this, 10);
        int colA = (int)((width - 2 * padX - gap) * Split);
        int colB = Math.Max(1, width - 2 * padX - gap - colA);
        int y = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Head)
            {
                int top = i == 0 ? padY : headTop + padY;
                Size t = TextRenderer.MeasureText(row.A, HeadFont, new Size(width - 2 * padX, int.MaxValue), Flags);
                int h = top + t.Height + padY;
                yield return (i, new Rectangle(0, y, width, h), new Rectangle(padX, y + top, width - 2 * padX, t.Height), Rectangle.Empty);
                y += h;
                continue;
            }
            Size ta = TextRenderer.MeasureText(row.A, Font, new Size(Math.Max(1, colA), int.MaxValue), Flags);
            Size tb = TextRenderer.MeasureText(row.B, Font, new Size(colB, int.MaxValue), Flags);
            int rh = Math.Max(ta.Height, tb.Height) + 2 * padY;
            yield return (i, new Rectangle(0, y, width, rh),
                new Rectangle(padX, y + padY, colA, ta.Height), new Rectangle(padX + colA + gap, y + padY, colB, tb.Height));
            y += rh;
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int w = proposedSize.Width > 1 && proposedSize.Width < 100000 ? proposedSize.Width : Ui.Px(this, 520);
        int h = 0;
        foreach (var r in Rows(w)) h = r.Row.Bottom;
        return new Size(w, h + Ui.Px(this, 6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        int band = 0;
        using var line = new Pen(Ui.Colors.Border);
        foreach (var (i, row, a, b) in Rows(Width))
        {
            if (!row.IntersectsWith(e.ClipRectangle)) { if (!_rows[i].Head) band++; else band = 0; continue; }
            if (_rows[i].Head)
            {
                band = 0;
                TextRenderer.DrawText(g, _rows[i].A, HeadFont, a, Ui.Colors.SubText, BackColor, Flags);
                g.DrawLine(line, a.Left, row.Bottom - 1, row.Right - a.Left, row.Bottom - 1);
                continue;
            }
            Color back = band++ % 2 == 1 ? Ui.Colors.FieldAlt : BackColor;
            if (back != BackColor) using (var bb = new SolidBrush(back)) g.FillRectangle(bb, row);
            TextRenderer.DrawText(g, _rows[i].A, Font, a, ForeColor, back, Flags);
            TextRenderer.DrawText(g, _rows[i].B, Font, b, Ui.Colors.KeyText, back, Flags);
        }
    }
}

internal enum MsgKind { Info, Warning, Error, Question }

internal static partial class Ui
{
    // ---- type -------------------------------------------------------------------------------------------------------

    /// <summary>The one font family of the UI: the system message font (Yu Gothic UI on Japanese Windows).</summary>
    public static FontFamily Family { get; } = (SystemFonts.MessageBoxFont ?? Control.DefaultFont).FontFamily;
    public static string FamilyName => Family.Name;
    public const float BodyPt = 9f;
    public const float HeadingPt = 10.5f;
    public const float TitlePt = 13f;

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    private static readonly int SystemDpi = QuerySystemDpi();
    private static int QuerySystemDpi() { try { int d = (int)GetDpiForSystem(); return d > 0 ? d : 96; } catch { return 96; } }

    private static readonly Dictionary<(int, int, FontStyle), Font> _fonts = new();

    /// <summary>A shared font of <paramref name="pt"/> points as seen on a monitor of <paramref name="dpi"/> (WinForms
    /// fonts are sized for the system DPI; a per-monitor window needs them made for its own). Never dispose it.</summary>
    public static Font FontAt(int dpi, float pt, FontStyle style = FontStyle.Regular)
    {
        if (dpi <= 0) dpi = SystemDpi;
        var key = (dpi, (int)Math.Round(pt * 100), style);
        lock (_fonts)
        {
            if (!_fonts.TryGetValue(key, out Font? f))
                _fonts[key] = f = new Font(Family, pt * dpi / SystemDpi, style, GraphicsUnit.Point);
            return f;
        }
    }

    // ---- scaling and layout options ---------------------------------------------------------------------------------

    public static int DpiOf(Control c)
    {
        Control top = c.TopLevelControl ?? c;
        int d = top.DeviceDpi;
        return d > 0 ? d : 96;
    }

    /// <summary><paramref name="v96"/> pixels at 96 dpi, in pixels of the monitor <paramref name="c"/> is on.</summary>
    public static int Px(Control c, int v96) => (int)Math.Round(v96 * DpiOf(c) / 96.0);

    private static readonly ConditionalWeakTable<Control, LayoutOpt> _opts = new();
    public static LayoutOpt Opt(Control c) => _opts.GetValue(c, _ => new LayoutOpt());
    public static LayoutOpt? OptOrNull(Control c) => _opts.TryGetValue(c, out LayoutOpt? o) ? o : null;

    /// <summary>Natural width (and height) of a control that has none of its own, in 96-dpi pixels.</summary>
    public static T Fixed<T>(T c, int width96, int height96 = 0) where T : Control
    {
        LayoutOpt o = Opt(c);
        if (width96 > 0) { o.Width96 = width96; o.Stretch ??= false; }
        if (height96 > 0) o.Height96 = height96;
        return c;
    }

    public static T Stretch<T>(T c, bool on = true) where T : Control { Opt(c).Stretch = on; return c; }
    public static T Before<T>(T c, int before96) where T : Control { Opt(c).Before = before96; return c; }

    /// <summary>Show or hide a control and take it in or out of its dialog's layout (re-measuring the dialog).</summary>
    public static void Show(Control c, bool shown)
    {
        LayoutOpt o = Opt(c);
        bool changed = o.Hidden == shown || c.Visible != shown;
        o.Hidden = !shown;
        c.Visible = shown;
        if (changed) RequestFit(c);
    }

    public static bool IsShown(Control c) => OptOrNull(c) is not { Hidden: true };

    public static bool IsStretch(Control c)
    {
        if (OptOrNull(c)?.Stretch is bool s) return s;
        return c switch
        {
            UiButton => false,
            NumericUpDown => false,
            Label l => !l.AutoSize,
            _ => true,
        };
    }

    /// <summary>Size of <paramref name="c"/> at width <paramref name="avail"/> (0 = its natural width).</summary>
    public static Size Pref(Control c, int avail)
    {
        LayoutOpt? o = OptOrNull(c);
        int fw = o is { Width96: > 0 } ? Px(c, o.Width96) : 0;
        int fh = o is { Height96: > 0 } ? Px(c, o.Height96) : 0;
        bool stretch = IsStretch(c);
        switch (c)
        {
            case UiLayout lp:
                return lp.Measure(avail);
            case UiCheck:
            case Label { AutoSize: false }:
            {
                int at = fw > 0 && !stretch ? (avail > 0 ? Math.Min(fw, avail) : fw) : avail;
                Size p = c.GetPreferredSize(new Size(Math.Max(0, at), 0));
                if (fw > 0 && !stretch) p.Width = at;
                else if (avail > 0) p.Width = stretch ? avail : Math.Min(p.Width, avail);
                return p;
            }
            case Label:
            case UiButton:
                return c.GetPreferredSize(Size.Empty);
            case TextBox tb:
                return new Size(stretch && avail > 0 ? avail : fw > 0 ? fw : Px(c, 160), tb.Multiline ? (fh > 0 ? fh : Px(c, 120)) : tb.PreferredHeight);
            case NumericUpDown nud:
                return new Size(fw > 0 ? fw : Px(c, 96), nud.PreferredHeight);
            case ComboBox cb:
                return new Size(stretch && avail > 0 ? avail : fw > 0 ? fw : Px(c, 160), cb.PreferredHeight);
            case UiSpring:
                return new Size(avail, 0);
        }
        Size pref = c.GetPreferredSize(new Size(avail, 0));
        int w = fw > 0 && !(stretch && avail > 0) ? fw : stretch && avail > 0 ? avail : pref.Width;
        return new Size(w, fh > 0 ? fh : pref.Height);
    }

    /// <summary>The narrowest <paramref name="c"/> can be without clipping (wrapping text: its longest word).</summary>
    public static int MinW(Control c)
    {
        LayoutOpt? o = OptOrNull(c);
        int fw = o is { Width96: > 0 } ? Px(c, o.Width96) : 0;
        switch (c)
        {
            case UiLayout lp: return lp.MinWidth();
            case UiSpring: return 0;
            case UiCheck:
            case Label { AutoSize: false }:
            {
                if (fw > 0 && !IsStretch(c)) return fw;
                int word = 0;
                foreach (string part in c.Text.Split(' ', '\n'))
                    if (part.Length > 0 && part.Length < 40) word = Math.Max(word, TextRenderer.MeasureText(part, c.Font, Size.Empty, TextFormatFlags.NoPadding).Width);
                int extra = c is UiCheck ? Px(c, 26) : 0;
                return Math.Min(Pref(c, 0).Width, Math.Max(word, Px(c, 60)) + extra);
            }
            case Label:
            case UiButton:
                return Pref(c, 0).Width;
            case TextBox:
            case ComboBox:
                return fw > 0 ? fw : Px(c, 80);
            case NumericUpDown:
                return fw > 0 ? fw : Px(c, 96);
            case UiSlider:
                return fw > 0 ? fw : Px(c, 120);
        }
        return fw > 0 ? fw : IsStretch(c) ? Px(c, 60) : Pref(c, 0).Width;
    }

    /// <summary>Ask the window <paramref name="c"/> is in to re-measure itself (coalesced).</summary>
    public static void RequestFit(Control c)
    {
        if (c.FindForm() is UiWindow w) w.RequestFit();
    }

    // ---- building blocks --------------------------------------------------------------------------------------------

    public static UiStack Stack() => new();
    public static UiRow Row(params Control[] controls)
    {
        var r = new UiRow();
        foreach (Control c in controls) r.Controls.Add(c);
        return r;
    }
    public static UiGrid Grid() => new();
    public static UiSpring Spring() => new();

    /// <summary>A one-line caption (a field label): never wraps, sized to its text.</summary>
    public static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false, BackColor = Color.Transparent };

    /// <summary>Body text that wraps to the width it is given.</summary>
    public static Label Text(string text) =>
        new() { Text = text, AutoSize = false, Margin = Padding.Empty, UseMnemonic = false, BackColor = Color.Transparent };

    /// <summary>Dimmed explanatory text that wraps to the width it is given.</summary>
    public static Label Hint(string text)
    {
        Label l = Text(text);
        l.ForeColor = Colors.SubText;
        return l;
    }

    /// <summary>Text in its own size/weight (heading, the About title, a value read-out), re-made for every DPI.</summary>
    public static Label Styled(string text, float pt, FontStyle style, bool wrap = false)
    {
        Label l = wrap ? Text(text) : Caption(text);
        LayoutOpt o = Opt(l);
        o.FontPt = pt;
        o.FontStyle = style;
        l.Font = FontAt(0, pt, style);
        return l;
    }

    public static Label Heading(string text, bool wrap = true) => Styled(text, HeadingPt, FontStyle.Bold, wrap);

    public static LinkLabel Link(string text, Action click)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false, BackColor = Color.Transparent };
        l.LinkClicked += (_, _) => click();
        return l;
    }

    public static UiButton Btn(string text, DialogResult result = DialogResult.None) =>
        new() { Text = text, DialogResult = result };

    public static UiCheck Check(string text, bool on) => new() { Text = text, Checked = on };

    /// <summary>A spin box in the dark palette (its natural width in 96-dpi pixels).</summary>
    public static NumericUpDown Number(decimal min, decimal max, decimal value, int width96 = 96)
    {
        var n = new UiNumber { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), TextAlign = HorizontalAlignment.Right };
        return Fixed(n, width96);
    }

    /// <summary>Edge colour of an input field: the accent while it has the focus.</summary>
    public static Color FieldEdge(Control c) => !c.Enabled ? Colors.Border : c.ContainsFocus ? Colors.Accent : Colors.ButtonEdge;

    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);

    /// <summary>Paints the 1 px edge of a native field (text box: its non-client border; combo box: the edge of its themed
    /// box) in <see cref="FieldEdge"/> after Windows has drawn it, so every input has the same edge as the buttons.</summary>
    private sealed class EdgePainter : NativeWindow
    {
        private const int WmPaint = 0x000F, WmNcPaint = 0x0085;
        private readonly Control _c;
        private readonly bool _client;

        public EdgePainter(Control c, bool client)
        {
            _c = c;
            _client = client;
            if (c.IsHandleCreated) AssignHandle(c.Handle);
            c.HandleCreated += (_, _) => AssignHandle(c.Handle);
            c.HandleDestroyed += (_, _) => ReleaseHandle();
            c.Enter += (_, _) => Redraw();
            c.Leave += (_, _) => Redraw();
            c.EnabledChanged += (_, _) => Redraw();
        }

        private void Redraw()
        {
            if (Handle != IntPtr.Zero) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0400 | 0x0100);   // INVALIDATE | FRAME | UPDATENOW
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == (_client ? WmPaint : WmNcPaint)) PaintEdge();
        }

        private void PaintEdge()
        {
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using Graphics g = Graphics.FromHdc(dc);
                Size s = _c.Size;
                using var p = new Pen(FieldEdge(_c));
                g.DrawRectangle(p, 0, 0, s.Width - 1, s.Height - 1);
            }
            catch { /* the window is going away */ }
            finally { ReleaseDC(Handle, dc); }
        }
    }

    public static ComboBox Combo(int width96 = 0, params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        if (width96 > 0) Fixed(c, width96);
        return c;
    }

    /// <summary>Lay out a dialog: <paramref name="body"/> above a button bar. <paramref name="right"/> are given left
    /// to right (OK, キャンセル); the first one is the accept button and drawn as the primary action.
    /// <paramref name="left"/> sit at the bottom-left (既定に戻す).</summary>
    public static void Frame(UiWindow dlg, Control body, Button[] right, Button[]? left = null, bool fill = false)
    {
        var frame = new UiFrame(body, fill, left ?? Array.Empty<Button>(), right);
        dlg.SetFrame(frame);
        if (right.Length > 0)
        {
            if (dlg.AcceptButton == null) dlg.AcceptButton = right[0];
            if (right[0] is UiButton p) p.Primary = true;
        }
    }

    /// <summary>OK + キャンセル (OK left of キャンセル) with Enter/Esc wired; returns OK for validation.</summary>
    public static UiButton FrameOkCancel(UiWindow dlg, Control body, string? okText = null, Button[]? left = null, bool fill = false)
    {
        UiButton ok = Btn(okText ?? "OK", DialogResult.OK);
        UiButton cancel = Btn(L.T("キャンセル"), DialogResult.Cancel);
        Frame(dlg, body, new Button[] { ok, cancel }, left, fill);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return ok;
    }

    /// <summary>One 閉じる (or OK) button that is both Enter and Esc.</summary>
    public static UiButton FrameClose(UiWindow dlg, Control body, string? text = null, Button[]? left = null, bool fill = false)
    {
        UiButton close = Btn(text ?? L.T("閉じる"), DialogResult.OK);
        Frame(dlg, body, new Button[] { close }, left, fill);
        dlg.AcceptButton = close;
        dlg.CancelButton = close;
        dlg.InitialFocus = close;
        return close;
    }

    // ---- the main window and its app icon -----------------------------------------------------------------------------

    /// <summary>The main window (dialogs without an owner centre on it).</summary>
    public static Form? MainWindow
    {
        get
        {
            try { foreach (Form f in Application.OpenForms) if (f is MainForm && !f.IsDisposed) return f; } catch { /* enumerating while closing */ }
            return null;
        }
    }

    /// <summary>The application icon at <paramref name="size"/> pixels, from the exe's own icon frames (crisp at any DPI).</summary>
    public static Icon? AppIcon(int size)
    {
        try { return Icon.ExtractIcon(Application.ExecutablePath, 0, size); }
        catch { return null; }
    }

    // ---- theme -----------------------------------------------------------------------------------------------------

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int LvmGetHeader = 0x1000 + 31;

    /// <summary>Dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE; 19 on Windows 10 before 20H1).</summary>
    public static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int one = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref one, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref one, sizeof(int));
        }
        catch { /* older Windows: the title bar stays light */ }
    }

    /// <summary>The background a control sits on (for controls that paint their own corners).</summary>
    public static Color BackOf(Control c)
    {
        for (Control? p = c; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Colors.Back;
    }

    private static readonly ConditionalWeakTable<Control, object> _themed = new();

    private static void WindowTheme(Control c, string name)
    {
        void Apply() { try { SetWindowTheme(c.Handle, name, null); } catch { /* no theming */ } }
        if (c.IsHandleCreated) Apply();
        c.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Give <paramref name="root"/> and everything in it the dark look (once per control; controls added later
    /// to a Ui layout panel are themed when they arrive).</summary>
    public static void ApplyTheme(Control root)
    {
        if (!_themed.TryGetValue(root, out _))
        {
            _themed.Add(root, new object());
            ThemeOne(root);
        }
        if (root is UpDownBase) return;   // its edit and buttons are handled with it
        foreach (Control c in root.Controls) ApplyTheme(c);
    }

    private static void ThemeOne(Control c)
    {
        switch (c)
        {
            case Form f:
                if (f.BackColor == SystemColors.Control) f.BackColor = Colors.Back;
                if (f.ForeColor == SystemColors.ControlText) f.ForeColor = Colors.Text;
                if (f.IsHandleCreated) DarkTitleBar(f.Handle);
                f.HandleCreated += (_, _) => DarkTitleBar(f.Handle);
                break;
            case UiButton or UiCheck or UiSlider or UiProgress or UiTable or UiIcon:
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = Colors.ButtonFace;
                b.ForeColor = Colors.Text;
                b.FlatAppearance.BorderColor = Colors.ButtonEdge;
                b.FlatAppearance.MouseOverBackColor = Colors.Hover;
                b.FlatAppearance.MouseDownBackColor = Colors.ButtonDown;
                break;
            case CheckBox or RadioButton:
                c.ForeColor = Colors.Text;
                break;
            case LinkLabel ll:
                ll.LinkColor = Colors.Accent;
                ll.ActiveLinkColor = Colors.AccentHot;
                ll.VisitedLinkColor = Colors.Accent;
                ll.DisabledLinkColor = Colors.Disabled;
                ll.LinkBehavior = LinkBehavior.HoverUnderline;
                break;
            case Label l:
                if (l.ForeColor == SystemColors.GrayText) l.ForeColor = Colors.SubText;
                else if (l.ForeColor == Color.Firebrick) l.ForeColor = Colors.Danger;
                else if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Colors.Text;
                break;
            case TextBox tb:
                tb.BackColor = Colors.Field;
                tb.ForeColor = Colors.Text;
                if (tb.BorderStyle != BorderStyle.None)
                {
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    _ = new EdgePainter(tb, client: false);   // the 1 px edge in the palette (Windows draws it black)
                }
                WindowTheme(tb, "DarkMode_Explorer");          // dark scroll bars on multi-line boxes
                break;
            case NumericUpDown nud:
                nud.BackColor = Colors.Field;
                nud.ForeColor = Colors.Text;
                nud.BorderStyle = BorderStyle.FixedSingle;
                DarkSpin(nud);
                break;
            case ComboBox cb:
                cb.BackColor = Colors.Field;
                cb.ForeColor = Colors.Text;
                cb.FlatStyle = FlatStyle.Standard;
                WindowTheme(cb, "DarkMode_CFD");
                _ = new EdgePainter(cb, client: true);
                if (cb.DropDownStyle == ComboBoxStyle.DropDownList && cb.DrawMode == DrawMode.Normal) DarkComboItems(cb);
                break;
            case ListView lv:
                lv.BackColor = Colors.Field;
                lv.ForeColor = Colors.Text;
                WindowTheme(lv, "DarkMode_Explorer");
                void Header() { try { IntPtr h = SendMessage(lv.Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero); if (h != IntPtr.Zero) SetWindowTheme(h, "DarkMode_ItemsView", null); } catch { } }
                if (lv.IsHandleCreated) Header();
                lv.HandleCreated += (_, _) => Header();
                break;
            case ListBox lb:
                lb.BackColor = Colors.Field;
                lb.ForeColor = Colors.Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                WindowTheme(lb, "DarkMode_Explorer");
                break;
            case ScrollBar:
                WindowTheme(c, "DarkMode_Explorer");
                break;
            case ProgressBar pb:
                WindowTheme(pb, string.Empty);
                pb.ForeColor = Colors.Accent;
                pb.BackColor = Colors.Track;
                break;
            case TrackBar tr:
                tr.BackColor = Colors.Back;
                break;
            case ScrollableControl sc when sc.AutoScroll:
                WindowTheme(sc, "DarkMode_Explorer");
                break;
        }
    }

    // A spin box: WinForms draws its border and up/down buttons for the light theme; paint over them.
    private static void DarkSpin(NumericUpDown nud)
    {
        // (The border itself is painted by UiNumber after WinForms has drawn its light themed frame.)
        nud.Enter += (_, _) => nud.Invalidate();
        nud.Leave += (_, _) => nud.Invalidate();
        if (nud.Controls.Count == 0) return;
        Control buttons = nud.Controls[0];
        int pressed = 0;   // 1 = up, 2 = down
        buttons.MouseDown += (_, e) => { pressed = e.Y < buttons.Height / 2 ? 1 : 2; buttons.Invalidate(); };
        buttons.MouseUp += (_, _) => { pressed = 0; buttons.Invalidate(); };
        buttons.Paint += (_, e) =>
        {
            Graphics g = e.Graphics;
            int w = buttons.Width, h = buttons.Height, half = h / 2;
            using (var back = new SolidBrush(Colors.Field)) g.FillRectangle(back, 0, 0, w, h);
            using (var hl = new SolidBrush(Colors.Hover))
            {
                if (pressed == 1) g.FillRectangle(hl, 0, 0, w, half);
                if (pressed == 2) g.FillRectangle(hl, 0, half, w, h - half);
            }
            using (var sep = new Pen(Colors.Border)) g.DrawLine(sep, 0, 0, 0, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float aw = Math.Max(3f, w * 0.22f), ah = aw * 0.6f, cx = w / 2f;
            using var arrow = new SolidBrush(nud.Enabled ? Colors.Text : Colors.Disabled);
            float uy = half / 2f;
            g.FillPolygon(arrow, new[] { new PointF(cx - aw, uy + ah / 2), new PointF(cx + aw, uy + ah / 2), new PointF(cx, uy - ah / 2) });
            float dy = half + (h - half) / 2f;
            g.FillPolygon(arrow, new[] { new PointF(cx - aw, dy - ah / 2), new PointF(cx + aw, dy - ah / 2), new PointF(cx, dy + ah / 2) });
        };
    }

    // A drop-down list: the items (and the closed box's text) drawn in the dark palette.
    private static void DarkComboItems(ComboBox cb)
    {
        cb.DrawMode = DrawMode.OwnerDrawFixed;
        cb.DrawItem += (_, e) =>
        {
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Color back = sel ? Colors.AccentFill : Colors.Field;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;
            string text = cb.GetItemText(cb.Items[e.Index]) ?? string.Empty;
            var r = Rectangle.Inflate(e.Bounds, -Px(cb, 4), 0);
            TextRenderer.DrawText(e.Graphics, text, cb.Font, r, cb.Enabled ? (sel ? Color.White : Colors.Text) : Colors.Disabled, back,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };
    }

    /// <summary>Tool tips in the dark palette.</summary>
    public static void DarkToolTip(ToolTip tip)
    {
        tip.OwnerDraw = true;
        tip.Popup += (_, e) =>
        {
            Font f = e.AssociatedControl != null ? FontAt(DpiOf(e.AssociatedControl), BodyPt) : FontAt(0, BodyPt);
            int pad = e.AssociatedControl != null ? Px(e.AssociatedControl, 8) : 8;
            string text = tip.GetToolTip(e.AssociatedControl ?? new Control()) ?? string.Empty;
            if (string.IsNullOrEmpty(text)) return;
            Size s = TextRenderer.MeasureText(text, f, new Size(pad * 60, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            e.ToolTipSize = new Size(s.Width + 2 * pad, s.Height + pad + pad / 2);
        };
        tip.Draw += (_, e) => DrawTip(e, e.AssociatedControl);
    }

    private static void DrawTip(DrawToolTipEventArgs e, Control? owner)
    {
        Font f = owner != null ? FontAt(DpiOf(owner), BodyPt) : e.Font ?? FontAt(0, BodyPt);
        int pad = owner != null ? Px(owner, 8) : 8;
        using (var b = new SolidBrush(Colors.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
        using (var p = new Pen(Colors.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        var r = new Rectangle(pad, pad * 3 / 4, e.Bounds.Width - 2 * pad, e.Bounds.Height - pad);
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, f, r, Colors.Text, Colors.Surface, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    /// <summary>The tool tips a ToolStrip shows for its items (the status bar's), in the dark palette. ToolStrip keeps
    /// its ToolTip internal: reached by reflection, and left alone if that ever fails.</summary>
    public static void DarkStripToolTips(ToolStrip strip)
    {
        try
        {
            if (typeof(ToolStrip).GetProperty("ToolTip", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(strip) is not ToolTip tip) return;
            tip.OwnerDraw = true;
            tip.Popup += (_, e) =>
            {
                Font f = FontAt(DpiOf(strip), BodyPt);
                int pad = Px(strip, 8);
                string text = strip.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Selected || i.Bounds.Contains(strip.PointToClient(Cursor.Position)))?.ToolTipText ?? string.Empty;
                if (text.Length == 0) return;
                Size s = TextRenderer.MeasureText(text, f, new Size(pad * 60, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                e.ToolTipSize = new Size(s.Width + 2 * pad, s.Height + pad + pad / 2);
            };
            tip.Draw += (_, e) => DrawTip(e, strip);
        }
        catch (Exception ex) { Log.Info("ui: status bar tool tips stay light - " + ex.Message); }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- messages ---------------------------------------------------------------------------------------------------

    /// <summary>The one message box of the app, in the dark look: always titled "YuCap", owned by
    /// <paramref name="owner"/> (or the main window), icon by kind - Error for a failure that stopped something,
    /// Warning for one the user can recover from, Info for plain notices, Question for confirmations. A first
    /// paragraph followed by a blank line is shown as the headline. <paramref name="native"/> uses the system
    /// MessageBox (before the UI exists, or in the crash handler where the app's own drawing may be what failed).</summary>
    public static DialogResult Message(IWin32Window? owner, string text, MsgKind kind = MsgKind.Warning,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1,
        bool native = false)
    {
        MessageBoxIcon icon = kind switch
        {
            MsgKind.Error => MessageBoxIcon.Error,
            MsgKind.Info => MessageBoxIcon.Information,
            MsgKind.Question => MessageBoxIcon.Question,
            _ => MessageBoxIcon.Warning,
        };
        if (!native)
        {
            try { return ShowMessage(owner, text, kind, buttons, defaultButton); }
            catch (Exception ex) { Log.Info("ui: message box fell back to the system one - " + ex.Message); }
        }
        return owner != null
            ? MessageBox.Show(owner, text, "YuCap", buttons, icon, defaultButton)
            : MessageBox.Show(text, "YuCap", buttons, icon, defaultButton);
    }

    private static DialogResult ShowMessage(IWin32Window? owner, string text, MsgKind kind, MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
    {
        using UiDialog dlg = NewDialog("YuCap", 400);
        StockIconId id = kind switch
        {
            MsgKind.Error => StockIconId.Error,
            MsgKind.Info => StockIconId.Info,
            MsgKind.Question => StockIconId.Help,
            _ => StockIconId.Warning,
        };
        var icon = new UiIcon(size => SystemIcons.GetStockIcon(id, size), 32);
        var words = Stack();
        string norm = text.Replace("\r\n", "\n");
        int split = norm.IndexOf("\n\n", StringComparison.Ordinal);
        if (split > 0)
        {
            words.Add(Heading(norm[..split]));
            words.Add(Text(norm[(split + 2)..].Trim('\n')), before: 12);
        }
        else words.Add(Text(norm));
        var row = Row(icon, Stretch(words));
        Before(words, 16);
        Opt(row).Top = true;
        Opt(icon).Top = true;

        (string Text, DialogResult Result)[] defs = buttons switch
        {
            MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), (L.T("キャンセル"), DialogResult.Cancel) },
            MessageBoxButtons.YesNo => new[] { (L.T("はい"), DialogResult.Yes), (L.T("いいえ"), DialogResult.No) },
            MessageBoxButtons.YesNoCancel => new[] { (L.T("はい"), DialogResult.Yes), (L.T("いいえ"), DialogResult.No), (L.T("キャンセル"), DialogResult.Cancel) },
            MessageBoxButtons.RetryCancel => new[] { (L.T("再試行"), DialogResult.Retry), (L.T("キャンセル"), DialogResult.Cancel) },
            _ => new[] { ("OK", DialogResult.OK) },
        };
        Button[] btns = defs.Select(d => (Button)Btn(d.Text, d.Result)).ToArray();
        int def = defaultButton switch { MessageBoxDefaultButton.Button2 => 1, MessageBoxDefaultButton.Button3 => 2, _ => 0 };
        def = Math.Min(def, btns.Length - 1);
        Frame(dlg, row, btns);
        foreach (Button b in btns) if (b is UiButton u) u.Primary = false;
        if (btns[def] is UiButton primary) primary.Primary = true;
        dlg.AcceptButton = btns[def];
        dlg.CancelButton = btns.FirstOrDefault(b => b.DialogResult is DialogResult.Cancel or DialogResult.No) ?? btns[^1];
        dlg.InitialFocus = btns[def];
        if (buttons is MessageBoxButtons.YesNo) dlg.ControlBox = false;   // like the system box: answer with a button

        switch (kind)
        {
            case MsgKind.Error: SystemSounds.Hand.Play(); break;
            case MsgKind.Warning: SystemSounds.Exclamation.Play(); break;
            case MsgKind.Question: SystemSounds.Question.Play(); break;
            default: SystemSounds.Asterisk.Play(); break;
        }
        DialogResult r = dlg.ShowDialog(owner ?? MainWindow);
        return r == DialogResult.None ? DialogResult.Cancel : r;
    }
}
