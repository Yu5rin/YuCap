using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>One picture in the snapshot list (スナップショット一覧).</summary>
internal sealed class GalleryEntry
{
    public string Path = string.Empty;
    /// <summary>Name of the sub-folder of the save folder it is in; empty for the save folder itself.</summary>
    public string Folder = string.Empty;
    public string Name = string.Empty;          // with extension
    /// <summary>Path + time stamp + size: a changed file gets a new key, so a stale thumbnail is never shown for it.</summary>
    public string Key = string.Empty;
    public DateTime Time;                        // taken from the file name (date, time, ms); the file time when renamed
    public long Length;
    public Size Dim;                             // known once the thumbnail or the preview has been read

    public string Title => System.IO.Path.GetFileNameWithoutExtension(Name);
}

/// <summary>
/// A virtualised thumbnail grid in the dark palette of the main window: only the tiles in view are painted and
/// only those ask for a thumbnail, so a folder of thousands of pictures costs nothing until it is scrolled to.
/// Groups (one per sub-folder) get a header row; selection is by click, Ctrl/Shift and the keyboard.
/// </summary>
internal sealed class ThumbGrid : Control
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    private readonly record struct Grp(string Title, int Start, int Count);

    private readonly VScrollBar _vs = new() { Dock = DockStyle.Right, Visible = false };
    private List<GalleryEntry> _view = new();
    private List<Grp> _groups = new();
    private bool _showHeaders;
    private Rectangle[] _rects = Array.Empty<Rectangle>();
    private readonly List<(Rectangle Rect, string Text)> _headers = new();
    private readonly HashSet<int> _sel = new();
    private int _focus = -1, _anchor = -1, _scroll, _contentH;
    private int _pad, _thumbW, _thumbH, _tileW, _tileH, _headerH, _margin, _textH;
    private Font? _small, _bold;

    /// <summary>Returns the cached thumbnail of an entry, or null (the owner is told what is in view through
    /// <see cref="VisibleChanged"/> and loads what is missing).</summary>
    public Func<GalleryEntry, Bitmap?>? ThumbLookup;
    public Func<GalleryEntry, string>? SubTextOf;
    public Func<GalleryEntry, bool>? ThumbFailed;
    public string RootTitle = string.Empty;
    public string EmptyText = string.Empty;

    public event EventHandler? SelectionChanged;
    public event EventHandler? ItemActivated;
    public event EventHandler? DeleteRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? RenameRequested;
    public event Action<Point>? ContextRequested;
    /// <summary>Raised after painting with the entries in view (and half a screen around, for prefetching).</summary>
    public event Action<List<GalleryEntry>>? InViewChanged;

    public ThumbGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Ui.Colors.Back;
        ForeColor = Ui.Colors.Text;
        _vs.Scroll += (_, _) => { _scroll = _vs.Value; Invalidate(); };
        _vs.ValueChanged += (_, _) => { _scroll = _vs.Value; Invalidate(); };
        Controls.Add(_vs);
        AccessibleName = "Snapshots";
    }

    /// <summary>The thumbnail size (device pixels) at the current scaling.</summary>
    public Size ThumbSize { get { EnsureMetrics(); return new Size(_thumbW, _thumbH); } }

    public IReadOnlyList<GalleryEntry> View => _view;
    public IEnumerable<GalleryEntry> Selected => _sel.OrderBy(i => i).Where(i => i < _view.Count).Select(i => _view[i]);
    public int SelectedCount => _sel.Count;
    public int IndexOf(GalleryEntry e) => _view.IndexOf(e);
    public int FocusIndex => _focus;
    public int FirstSelectedIndex => _sel.Count == 0 ? -1 : _sel.Min();

    // ---- metrics and layout ----------------------------------------------------------------------------

    private int _metricsDpi = -1;
    private void EnsureMetrics()
    {
        int dpi = DeviceDpi <= 0 ? 96 : DeviceDpi;
        if (dpi == _metricsDpi && _small != null) return;
        _metricsDpi = dpi;
        float s = dpi / 96f;
        _small?.Dispose(); _bold?.Dispose();
        _small = new Font(Font.FontFamily, Math.Max(7f, Font.Size * 0.9f), FontStyle.Regular);
        _bold = new Font(Font, FontStyle.Bold);
        _pad = (int)Math.Round(8 * s);
        _thumbW = (int)Math.Round(176 * s);
        _thumbH = (int)Math.Round(99 * s);
        _textH = Font.Height + _small.Height + (int)Math.Round(2 * s);
        _tileW = _thumbW + _pad * 2;
        _tileH = _pad + _thumbH + (int)Math.Round(4 * s) + _textH + _pad;
        _headerH = _bold.Height + (int)Math.Round(14 * s);
        _margin = (int)Math.Round(10 * s);
    }

    protected override void OnFontChanged(EventArgs e) { _metricsDpi = -1; base.OnFontChanged(e); LayoutTiles(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { _metricsDpi = -1; base.OnDpiChangedAfterParent(e); LayoutTiles(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutTiles(); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowTheme(_vs.Handle, "DarkMode_Explorer", null); } catch { /* an old Windows: the light scroll bar stays */ }
        LayoutTiles();
    }

    public void SetView(List<GalleryEntry> view, bool grouped)
    {
        _view = view;
        _groups = new List<Grp>();
        for (int i = 0; i < view.Count;)
        {
            int j = i + 1;
            while (j < view.Count && string.Equals(view[j].Folder, view[i].Folder, StringComparison.OrdinalIgnoreCase)) j++;
            _groups.Add(new Grp(view[i].Folder.Length == 0 ? RootTitle : view[i].Folder, i, j - i));
            i = j;
        }
        _showHeaders = grouped && _groups.Count > 0;
        _sel.RemoveWhere(i => i >= view.Count);
        if (_focus >= view.Count) _focus = view.Count - 1;
        if (_anchor >= view.Count) _anchor = view.Count - 1;
        LayoutTiles();
        Invalidate();
    }

    private void LayoutTiles()
    {
        if (ClientSize.Width <= 0) return;
        EnsureMetrics();
        if (_view.Count == 0)
        {
            _rects = Array.Empty<Rectangle>(); _headers.Clear(); _contentH = 0; ApplyScroll();
            return;
        }
        int sbw = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi <= 0 ? 96 : DeviceDpi);
        PlaceTiles(ClientSize.Width);
        _needScroll = _contentH > ClientSize.Height;
        if (_needScroll) PlaceTiles(ClientSize.Width - sbw);   // the scroll bar takes the right edge: fewer columns may fit
        ApplyScroll();
    }

    private bool _needScroll;

    private void PlaceTiles(int avail)
    {
        _headers.Clear();
        _rects = new Rectangle[_view.Count];
        int cols = Math.Max(1, (avail - _margin * 2) / _tileW);
        int y = _margin;
        foreach (Grp g in _groups)
        {
            if (_showHeaders)
            {
                _headers.Add((new Rectangle(_margin, y, Math.Max(10, avail - _margin * 2), _headerH), $"{g.Title}  ({g.Count})"));
                y += _headerH;
            }
            for (int k = 0; k < g.Count; k++)
                _rects[g.Start + k] = new Rectangle(_margin + (k % cols) * _tileW, y + (k / cols) * _tileH, _tileW, _tileH);
            y += ((g.Count + cols - 1) / cols) * _tileH + (_showHeaders ? _margin : 0);
        }
        _contentH = y + _margin;
    }

    private void ApplyScroll()
    {
        int viewH = Math.Max(1, ClientSize.Height);
        if (_vs.Visible != _needScroll) _vs.Visible = _needScroll;
        if (_needScroll)
        {
            _vs.SmallChange = Math.Max(1, _tileH / 3);
            _vs.LargeChange = viewH;
            _vs.Maximum = Math.Max(0, _contentH - 1);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - viewH));
            int v = Math.Min(_scroll, Math.Max(_vs.Minimum, _vs.Maximum - _vs.LargeChange + 1));
            if (_vs.Value != v) _vs.Value = v;
        }
        else _scroll = 0;
    }

    public void ScrollTo(int y)
    {
        int max = Math.Max(0, _contentH - Math.Max(1, ClientSize.Height));
        _scroll = Math.Clamp(y, 0, max);
        if (_vs.Visible && _vs.Value != _scroll) _vs.Value = Math.Min(_scroll, Math.Max(_vs.Minimum, _vs.Maximum - _vs.LargeChange + 1));
        Invalidate();
    }

    public void EnsureVisible(int i)
    {
        if (i < 0 || i >= _rects.Length) return;
        Rectangle r = _rects[i];
        int h = ClientSize.Height;
        if (r.Top < _scroll) ScrollTo(r.Top - _margin - (_showHeaders ? _headerH : 0));
        else if (r.Bottom > _scroll + h) ScrollTo(r.Bottom - h + _margin);
    }

    // ---- selection ---------------------------------------------------------------------------------------

    private void SetSelection(IEnumerable<int> idx, int focus)
    {
        var next = new HashSet<int>(idx);
        bool changed = !next.SetEquals(_sel);
        if (changed) { _sel.Clear(); foreach (int i in next) _sel.Add(i); }
        _focus = focus;
        Invalidate();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectOnly(int i, bool ensureVisible = true)
    {
        if (i < 0 || i >= _view.Count) { SetSelection(Array.Empty<int>(), -1); return; }
        _anchor = i;
        SetSelection(new[] { i }, i);
        if (ensureVisible) EnsureVisible(i);
    }

    public void SelectByPaths(ICollection<string> paths)
    {
        var idx = new List<int>();
        for (int i = 0; i < _view.Count; i++)
            if (paths.Contains(_view[i].Path)) idx.Add(i);
        if (idx.Count == 0) { SetSelection(Array.Empty<int>(), Math.Min(_focus, _view.Count - 1)); return; }
        _anchor = idx[0];
        SetSelection(idx, idx[0]);
    }

    public void SelectAll()
    {
        if (_view.Count == 0) return;
        SetSelection(Enumerable.Range(0, _view.Count), _focus < 0 ? 0 : _focus);
    }

    private int HitTest(Point client)
    {
        int y = client.Y + _scroll;
        for (int i = 0; i < _rects.Length; i++)
            if (_rects[i].Contains(client.X, y)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
        int i = HitTest(e.Location);
        bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
        if (e.Button == MouseButtons.Right)
        {
            if (i >= 0 && !_sel.Contains(i)) SelectOnly(i, false);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (i < 0) { if (!ctrl && !shift) SetSelection(Array.Empty<int>(), _focus); return; }
        if (shift && _anchor >= 0)
        {
            int a = Math.Min(_anchor, i), b = Math.Max(_anchor, i);
            var range = Enumerable.Range(a, b - a + 1);
            SetSelection(ctrl ? _sel.Concat(range) : range, i);
        }
        else if (ctrl)
        {
            var next = new HashSet<int>(_sel);
            if (!next.Add(i)) next.Remove(i);
            _anchor = i;
            SetSelection(next, i);
        }
        else
        {
            _anchor = i;
            SetSelection(new[] { i }, i);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) ContextRequested?.Invoke(e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location) >= 0) ItemActivated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_scroll - (int)Math.Round(e.Delta / 120.0 * Math.Max(1, _tileH) * 0.6));
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
            case Keys.Return: case Keys.Delete:
                return true;
        }
        return base.IsInputKey(keyData);
    }

    private int Neighbour(int from, Keys key)
    {
        if (_view.Count == 0) return -1;
        if (from < 0) return 0;
        switch (key)
        {
            case Keys.Left: return Math.Max(0, from - 1);
            case Keys.Right: return Math.Min(_view.Count - 1, from + 1);
            case Keys.Home: return 0;
            case Keys.End: return _view.Count - 1;
        }
        Rectangle cur = _rects[from];
        int cx = cur.X + cur.Width / 2;
        int targetY = key switch
        {
            Keys.Up => cur.Y - 1,
            Keys.Down => cur.Bottom,
            Keys.PageUp => cur.Y - ClientSize.Height,
            _ => cur.Y + ClientSize.Height,
        };
        bool up = key is Keys.Up or Keys.PageUp;
        int best = -1; long bestScore = long.MaxValue;
        for (int i = 0; i < _rects.Length; i++)
        {
            Rectangle r = _rects[i];
            if (up ? r.Bottom > cur.Top + 1 : r.Top < cur.Bottom - 1) continue;      // not in that direction
            long dy = key is Keys.PageUp or Keys.PageDown ? Math.Abs(r.Y - targetY) : Math.Abs(r.Y - targetY);
            long dx = Math.Abs(r.X + r.Width / 2 - cx);
            long score = dy * 100000 + dx;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return key is Keys.PageUp ? 0 : key is Keys.PageDown ? _view.Count - 1 : from;
        return best;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
            {
                int to = Neighbour(_focus, e.KeyCode);
                if (to < 0) return;
                if (e.Shift && _anchor >= 0)
                {
                    int a = Math.Min(_anchor, to), b = Math.Max(_anchor, to);
                    SetSelection(Enumerable.Range(a, b - a + 1), to);
                }
                else
                {
                    _anchor = to;
                    SetSelection(new[] { to }, to);
                }
                EnsureVisible(to);
                e.Handled = e.SuppressKeyPress = true;
                break;
            }
            case Keys.A when e.Control: SelectAll(); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.C when e.Control: CopyRequested?.Invoke(this, EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Return: if (_sel.Count > 0) ItemActivated?.Invoke(this, EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Delete: if (_sel.Count > 0) DeleteRequested?.Invoke(this, EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.F2: if (_sel.Count == 1) RenameRequested?.Invoke(this, EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; break;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_focus < 0 && _view.Count > 0 && _sel.Count == 0) { _focus = 0; _anchor = 0; }
        Invalidate();
    }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    // ---- painting ----------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureMetrics();
        Graphics g = e.Graphics;
        g.Clear(Ui.Colors.Back);
        if (_view.Count == 0)
        {
            if (EmptyText.Length > 0)
                TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, Ui.Colors.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        int viewH = ClientSize.Height;
        var vis = new Rectangle(0, _scroll, ClientSize.Width, viewH);
        g.TranslateTransform(0, -_scroll);

        foreach (var (rect, text) in _headers)
        {
            if (!rect.IntersectsWith(vis)) continue;
            TextRenderer.DrawText(g, text, _bold!, new Rectangle(rect.X, rect.Y, rect.Width, rect.Height - 4), Ui.Colors.Text,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            using var pen = new Pen(Ui.Colors.Border);
            g.DrawLine(pen, rect.X, rect.Bottom - 2, rect.Right, rect.Bottom - 2);
        }

        var prefetch = new List<GalleryEntry>();
        var near = new Rectangle(0, _scroll - viewH / 2, ClientSize.Width, viewH * 2);
        for (int i = 0; i < _rects.Length; i++)
        {
            Rectangle r = _rects[i];
            if (!r.IntersectsWith(near)) continue;
            prefetch.Add(_view[i]);
            if (!r.IntersectsWith(vis)) continue;
            DrawTile(g, i, r);
        }
        g.ResetTransform();
        InViewChanged?.Invoke(prefetch);
    }

    private void DrawTile(Graphics g, int i, Rectangle r)
    {
        GalleryEntry en = _view[i];
        bool sel = _sel.Contains(i);
        if (sel)
        {
            using var br = new SolidBrush(Ui.Colors.Hover);
            g.FillRectangle(br, r);
        }
        if (i == _focus && Focused)
        {
            using var pen = new Pen(Ui.Colors.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }
        else if (sel)
        {
            using var pen = new Pen(Ui.Colors.Accent);
            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        var box = new Rectangle(r.X + _pad, r.Y + _pad, _thumbW, _thumbH);
        using (var br = new SolidBrush(Ui.Colors.Surface)) g.FillRectangle(br, box);
        Bitmap? bmp = ThumbLookup?.Invoke(en);
        if (bmp != null)
        {
            int x = box.X + (box.Width - bmp.Width) / 2, y = box.Y + (box.Height - bmp.Height) / 2;
            g.DrawImageUnscaled(bmp, x, y);
        }
        else if (ThumbFailed?.Invoke(en) == true)
        {
            TextRenderer.DrawText(g, "×", Font, box, Ui.Colors.SubText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int ty = box.Bottom + (int)Math.Round(4 * (_metricsDpi / 96.0));
        const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, en.Title, Font, new Rectangle(box.X, ty, _thumbW, Font.Height), Ui.Colors.Text, tf);
        string sub = SubTextOf?.Invoke(en) ?? string.Empty;
        TextRenderer.DrawText(g, sub, _small!, new Rectangle(box.X, ty + Font.Height, _thumbW, _small!.Height), Ui.Colors.SubText, tf);
    }

    /// <summary>Repaint after a thumbnail arrived (only the tile that has it, if in view).</summary>
    public void InvalidateEntry(GalleryEntry en)
    {
        int i = _view.IndexOf(en);
        if (i < 0 || i >= _rects.Length) return;
        Rectangle r = _rects[i];
        r.Offset(0, -_scroll);
        Invalidate(r);
    }

    /// <summary>Where the tile of <paramref name="i"/> is, in client coordinates (for a context menu).</summary>
    public Rectangle TileBounds(int i)
    {
        if (i < 0 || i >= _rects.Length) return Rectangle.Empty;
        Rectangle r = _rects[i];
        r.Offset(0, -_scroll);
        return r;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _small?.Dispose(); _bold?.Dispose(); }
        base.Dispose(disposing);
    }
}
