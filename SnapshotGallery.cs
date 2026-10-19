using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// スナップショット一覧: the saved snapshots as thumbnails, newest first, grouped by the sub-folders the
/// per-profile option makes, with a filter, a large preview and the usual actions (copy, show in folder, open,
/// rename, delete to the Recycle Bin). The folder is scanned and the thumbnails are read off the UI thread, only
/// the tiles in view ask for one, and a file watcher keeps the list current when new snapshots are saved.
/// </summary>
internal sealed class SnapshotGalleryForm : Form
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int attrValue, int attrSize);

    private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp" };
    private static readonly Regex NameTime = new(@"^Capture_(\d{8})_(\d{6})_(\d{3})", RegexOptions.Compiled);

    // ---- model ----
    private string _root;
    private readonly bool _audit;
    private List<GalleryEntry> _all = new();
    private int _scanGen;
    private string? _pendingSelect;
    private int _afterScanSelectIndex = -1;
    internal int ScanCount { get; private set; }

    // ---- thumbnails ----
    private readonly Dictionary<string, Bitmap> _thumbs = new();
    private readonly Queue<string> _thumbOrder = new();
    private long _thumbBytes;
    private const long MaxThumbBytes = 96L * 1024 * 1024;
    private readonly Dictionary<string, long> _failed = new();
    private readonly HashSet<string> _pending = new();
    private readonly ConcurrentStack<(GalleryEntry En, string Key, Size Box)> _queue = new();
    private volatile HashSet<string>? _wanted;          // thumbnail keys still worth loading; null = everything (tests)
    private int _workers;
    private readonly CancellationTokenSource _cts = new();
    private const int MaxWorkers = 3;

    // ---- UI ----
    private readonly ThumbGrid _grid = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
    private readonly PreviewBox _preview = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label _info = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
    private readonly TextBox _filter = new();
    private readonly ComboBox _folderBox = new();
    private readonly Label _count = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Button _bCopy = new(), _bReveal = new(), _bOpen = new(), _bRename = new(), _bDelete = new();
    private readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 160 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 450 };
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 4000 };
    private readonly ContextMenuStrip _menu = new();
    private FileSystemWatcher? _fsw;
    private int _previewGen;
    private bool _building;

    private sealed class FolderItem
    {
        public string? Folder;      // null = every folder
        public string Text = string.Empty;
        public override string ToString() => Text;
    }

    public SnapshotGalleryForm(string root, bool audit = false)
    {
        _root = root;
        _audit = audit;
        Text = L.T("スナップショット一覧");
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        ClientSize = new Size(1120, 700);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        BackColor = Ui.Colors.Back;
        ForeColor = Ui.Colors.Text;

        // ---- top: filter ----
        var top = new TableLayoutPanel
        {
            ColumnCount = 5, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lblFilter = Ui.Label(L.T("フィルター:"), 0, 0);
        lblFilter.Anchor = AnchorStyles.Left; lblFilter.Margin = new Padding(0, 0, Ui.Gap, 0);
        _filter.Anchor = AnchorStyles.Left | AnchorStyles.Right; _filter.Margin = new Padding(0, 3, Ui.Gap * 2, 3);
        _filter.BackColor = Ui.Colors.Surface; _filter.ForeColor = Ui.Colors.Text; _filter.BorderStyle = BorderStyle.FixedSingle;
        var lblFolder = Ui.Label(L.T("フォルダ:"), 0, 0);
        lblFolder.Anchor = AnchorStyles.Left; lblFolder.Margin = new Padding(0, 0, Ui.Gap, 0);
        _folderBox.DropDownStyle = ComboBoxStyle.DropDownList; _folderBox.FlatStyle = FlatStyle.Flat;
        _folderBox.BackColor = Ui.Colors.Surface; _folderBox.ForeColor = Ui.Colors.Text;
        _folderBox.Width = 200; _folderBox.Anchor = AnchorStyles.Left; _folderBox.Margin = new Padding(0, 3, Ui.Gap * 2, 3);
        _count.Anchor = AnchorStyles.Right; _count.Margin = Padding.Empty; _count.ForeColor = Ui.Colors.SubText;
        top.Controls.Add(lblFilter, 0, 0);
        top.Controls.Add(_filter, 1, 0);
        top.Controls.Add(lblFolder, 2, 0);
        top.Controls.Add(_folderBox, 3, 0);
        top.Controls.Add(_count, 4, 0);

        // ---- middle: grid | preview + info ----
        var mid = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0, Ui.Gap, 0, Ui.Gap) };
        mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        var right = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _info.Height = _info.Font.Height * 4 + 8;
        _info.ForeColor = Ui.Colors.SubText;
        right.Controls.Add(_preview, 0, 0);
        right.Controls.Add(_info, 0, 1);
        mid.Controls.Add(_grid, 0, 0);
        mid.Controls.Add(right, 1, 0);

        // ---- bottom: actions + status ----
        var bottom = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty, Anchor = AnchorStyles.Left,
        };
        StyleButton(_bCopy, L.T("コピー"));
        StyleButton(_bReveal, L.T("フォルダで表示"));
        StyleButton(_bOpen, L.T("開く"));
        StyleButton(_bRename, L.T("名前を変更..."));
        StyleButton(_bDelete, L.T("削除..."));
        buttons.Controls.AddRange(new Control[] { _bCopy, _bReveal, _bOpen, _bRename, _bDelete });
        _status.Anchor = AnchorStyles.Right; _status.Margin = new Padding(Ui.Gap, 0, 0, 0); _status.ForeColor = Ui.Colors.SubText;
        bottom.Controls.Add(buttons, 0, 0);
        bottom.Controls.Add(_status, 1, 0);

        var root0 = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, Padding = new Padding(12), Margin = Padding.Empty,
        };
        root0.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root0.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root0.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root0.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root0.Controls.Add(top, 0, 0);
        root0.Controls.Add(mid, 0, 1);
        root0.Controls.Add(bottom, 0, 2);
        Controls.Add(root0);

        // ---- context menu ----
        _menu.Renderer = Ui.DarkRenderer;
        _menu.BackColor = Ui.Colors.Surface; _menu.ForeColor = Ui.Colors.Text;
        _menu.Items.Add(new ToolStripMenuItem(L.T("開く"), null, (_, _) => OpenSelected()));
        _menu.Items.Add(new ToolStripMenuItem(L.T("コピー"), null, (_, _) => CopySelected()) { ShortcutKeyDisplayString = "Ctrl+C" });
        _menu.Items.Add(new ToolStripMenuItem(L.T("フォルダで表示"), null, (_, _) => RevealSelected()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L.T("名前を変更..."), null, (_, _) => RenameSelected()) { ShortcutKeyDisplayString = "F2" });
        _menu.Items.Add(new ToolStripMenuItem(L.T("削除..."), null, (_, _) => DeleteSelected(true)) { ShortcutKeyDisplayString = "Del" });

        // ---- wiring ----
        _grid.RootTitle = L.T("（保存先フォルダ直下）");
        _grid.EmptyText = L.T("読み込み中...");
        _grid.ThumbLookup = e => _thumbs.TryGetValue(ThumbKey(e), out Bitmap? b) ? b : null;
        _grid.ThumbFailed = e => _failed.ContainsKey(ThumbKey(e));
        _grid.SubTextOf = SubTextOf;
        _grid.InViewChanged += OnVisible;
        _grid.SelectionChanged += (_, _) => { UpdateActions(); UpdateInfo(); _previewTimer.Stop(); _previewTimer.Start(); };
        _grid.ItemActivated += (_, _) => OpenSelected();
        _grid.DeleteRequested += (_, _) => DeleteSelected(true);
        _grid.CopyRequested += (_, _) => CopySelected();
        _grid.RenameRequested += (_, _) => RenameSelected();
        _grid.ContextRequested += pt =>
        {
            if (_grid.SelectedCount == 0) return;
            _menu.Show(_grid, pt);
        };
        _bCopy.Click += (_, _) => CopySelected();
        _bReveal.Click += (_, _) => RevealSelected();
        _bOpen.Click += (_, _) => OpenSelected();
        _bRename.Click += (_, _) => RenameSelected();
        _bDelete.Click += (_, _) => DeleteSelected(true);
        _filter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        _folderBox.SelectedIndexChanged += (_, _) => { if (!_building) ApplyFilter(); };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); _ = RescanAsync(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = UpdatePreviewAsync(); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _status.Text = string.Empty; };
        Activated += (_, _) =>
        {
            if (_fsw == null && !_audit) { StartWatcher(); if (_fsw != null) _ = RescanAsync(); }
        };
        UpdateActions();
        UpdateInfo();
        _preview.EmptyText = L.T("画像を選ぶとここに表示されます");
    }

    private void StyleButton(Button b, string text)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.BackColor = Ui.Colors.Surface;
        b.ForeColor = Ui.Colors.Text;
        b.FlatAppearance.BorderColor = Ui.Colors.Border;
        b.FlatAppearance.MouseOverBackColor = Ui.Colors.Hover;
        b.FlatAppearance.MouseDownBackColor = Ui.Colors.Hover;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.MinimumSize = new Size(Ui.ButtonWidth, Ui.ButtonHeight);
        b.Margin = new Padding(0, 0, Ui.Gap, 0);
    }

    // ---- window ----------------------------------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int one = 1; DwmSetWindowAttribute(Handle, 20, ref one, sizeof(int)); } catch { /* the title bar stays light */ }
    }

    /// <summary>Shown without taking the focus (test mode shows it invisibly, the dialog audit too).</summary>
    internal bool Silent;
    protected override bool ShowWithoutActivation => Silent || _audit;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // At a large scaling the requested size can exceed the screen: keep the window on it.
        try
        {
            Rectangle wa = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
            if (Width > wa.Width || Height > wa.Height)
                Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
        }
        catch (Exception) { /* no screen information: leave the size */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_scanGen == 0) Start();
    }

    /// <summary>Starts the first scan and the file watcher. Called from Shown; also by tests that show the form themselves.</summary>
    internal void Start()
    {
        if (_scanGen != 0) return;
        StartWatcher();
        _ = RescanAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            if (_filter.Focused && _filter.TextLength > 0) _filter.Clear();
            else Close();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F && e.Control) { _filter.Focus(); _filter.SelectAll(); e.Handled = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            StopWatcher();
            foreach (var t in new[] { _filterTimer, _refreshTimer, _previewTimer, _statusTimer }) { t.Stop(); t.Dispose(); }
            _menu.Dispose();
            foreach (Bitmap b in _thumbs.Values) b.Dispose();
            _thumbs.Clear();
        }
        base.Dispose(disposing);
    }

    // ---- scanning --------------------------------------------------------------------------------------

    private static DateTime? ParseNameTime(string name)
    {
        Match m = NameTime.Match(name);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value, "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t) ? t : null;
    }

    /// <summary>The pictures in the save folder and in its direct sub-folders, newest first.</summary>
    internal static List<GalleryEntry> Scan(string root)
    {
        var list = new List<GalleryEntry>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
        void AddDir(string dir, string folder)
        {
            try
            {
                foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles())
                {
                    if (Array.IndexOf(Exts, fi.Extension.ToLowerInvariant()) < 0) continue;
                    list.Add(new GalleryEntry
                    {
                        Path = fi.FullName, Folder = folder, Name = fi.Name, Length = fi.Length,
                        Time = ParseNameTime(fi.Name) ?? fi.LastWriteTime,
                        Key = fi.FullName + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length,
                    });
                }
            }
            catch (Exception ex) { Log.Info("gallery: cannot read " + dir + " - " + ex.Message); }
        }
        AddDir(root, string.Empty);
        try
        {
            foreach (DirectoryInfo sd in new DirectoryInfo(root).EnumerateDirectories())
            {
                if ((sd.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                AddDir(sd.FullName, sd.Name);
            }
        }
        catch (Exception ex) { Log.Info("gallery: cannot list " + root + " - " + ex.Message); }
        list.Sort((a, b) =>
        {
            int c = b.Time.CompareTo(a.Time);
            return c != 0 ? c : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    internal bool Scanning { get; private set; }

    private async Task RescanAsync()
    {
        int gen = ++_scanGen;
        Scanning = true;
        string root = _root;
        List<GalleryEntry> list;
        try { list = await Task.Run(() => Scan(root)); }
        catch (Exception ex) { Log.Info("gallery: scan failed - " + ex.Message); list = new List<GalleryEntry>(); }
        if (IsDisposed || gen != _scanGen) return;
        Scanning = false;

        // Tiles keep their dimensions from the earlier list, so the second line does not flicker on a refresh.
        var known = _all.Where(e => e.Dim != Size.Empty).ToDictionary(e => e.Key, e => e.Dim);
        foreach (GalleryEntry e in list) if (known.TryGetValue(e.Key, out Size d)) e.Dim = d;

        var keep = new HashSet<string>(_grid.Selected.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        _all = list;
        ScanCount++;
        _grid.EmptyText = L.T("スナップショットがありません");
        RebuildFolderBox();
        ApplyFilter(keep);

        if (_afterScanSelectIndex >= 0 && _grid.SelectedCount == 0 && _grid.View.Count > 0)
            _grid.SelectOnly(Math.Min(_afterScanSelectIndex, _grid.View.Count - 1), false);
        _afterScanSelectIndex = -1;
        if (!TrySelectPending()) _pendingSelect = null;   // not there after a fresh scan: forget it
    }

    private void RebuildFolderBox()
    {
        string? current = (_folderBox.SelectedItem as FolderItem)?.Folder;
        var folders = _all.Select(e => e.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => f.Length > 0).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        bool hasRoot = _all.Any(e => e.Folder.Length == 0);
        _building = true;
        try
        {
            _folderBox.Items.Clear();
            _folderBox.Items.Add(new FolderItem { Folder = null, Text = L.T("すべてのフォルダ") });
            if (hasRoot && folders.Count > 0) _folderBox.Items.Add(new FolderItem { Folder = string.Empty, Text = L.T("（保存先フォルダ直下）") });
            foreach (string f in folders) _folderBox.Items.Add(new FolderItem { Folder = f, Text = f });
            int sel = 0;
            for (int i = 0; i < _folderBox.Items.Count; i++)
                if (_folderBox.Items[i] is FolderItem fi && string.Equals(fi.Folder, current, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
            _folderBox.SelectedIndex = sel;
            _folderBox.Enabled = folders.Count > 0;
        }
        finally { _building = false; }
    }

    private void ApplyFilter(ICollection<string>? keepSelection = null)
    {
        keepSelection ??= new HashSet<string>(_grid.Selected.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        string q = _filter.Text.Trim();
        string? folder = (_folderBox.SelectedItem as FolderItem)?.Folder;
        var list = _all.Where(e =>
            (folder == null || string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase))
            && (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Folder.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        bool grouped = list.Select(e => e.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        if (grouped) list = list.GroupBy(e => e.Folder, StringComparer.OrdinalIgnoreCase).SelectMany(g => g).ToList();
        _grid.EmptyText = ScanCount == 0 ? L.T("読み込み中...")
            : _all.Count == 0 ? L.T("スナップショットがありません") : L.T("一致する画像がありません");
        _grid.SetView(list, grouped);
        _grid.SelectByPaths(keepSelection);
        _count.Text = list.Count == _all.Count ? L.F("{0} 枚", _all.Count) : L.F("{0} / {1} 枚", list.Count, _all.Count);
        UpdateActions();
        UpdateInfo();
    }

    // ---- selecting a file from outside ------------------------------------------------------------------

    /// <summary>Selects the given file (the OSD after a save opens the list at that picture). If the list has not
    /// seen it yet, it is looked for after the next scan.</summary>
    public void SelectFile(string path)
    {
        _pendingSelect = path;
        if (!TrySelectPending() && _scanGen != 0) _ = RescanAsync();
    }

    private bool TrySelectPending()
    {
        if (_pendingSelect == null) return true;
        string p = _pendingSelect;
        GalleryEntry? en = _all.FirstOrDefault(e => string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase));
        if (en == null) return false;
        if (!_grid.View.Contains(en))
        {
            _filter.Clear();
            _building = true; if (_folderBox.Items.Count > 0) _folderBox.SelectedIndex = 0; _building = false;
            ApplyFilter(new HashSet<string>());
        }
        _grid.SelectOnly(_grid.IndexOf(en));
        _pendingSelect = null;
        return true;
    }

    internal void SetRoot(string root)
    {
        if (string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)) return;
        _root = root;
        StopWatcher();
        if (!_audit && _scanGen != 0) { StartWatcher(); _ = RescanAsync(); }
    }

    // ---- watching the folder ---------------------------------------------------------------------------

    private void StartWatcher()
    {
        StopWatcher();
        if (_audit) return;
        try
        {
            if (!Directory.Exists(_root)) return;     // not created yet: the next activation looks again
            _fsw = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _fsw.Created += OnFsEvent;
            _fsw.Deleted += OnFsEvent;
            _fsw.Changed += OnFsEvent;
            _fsw.Renamed += (s, e) => OnFsEvent(s, e);
            _fsw.Error += (_, e) => Log.Info("gallery: watcher error - " + e.GetException().Message);
            _fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Info("gallery: cannot watch the folder - " + ex.Message);
            StopWatcher();
        }
    }

    private void StopWatcher()
    {
        FileSystemWatcher? w = _fsw;
        _fsw = null;
        if (w == null) return;
        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
    }

    private void OnFsEvent(object? sender, FileSystemEventArgs e)
    {
        // Raised on a thread-pool thread, many times while one file is written: coalesce into one refresh.
        try { BeginInvoke(new Action(() => { _refreshTimer.Stop(); _refreshTimer.Start(); })); }
        catch (Exception) { /* closing */ }
    }

    // ---- thumbnails ------------------------------------------------------------------------------------

    private string ThumbKey(GalleryEntry e) => e.Key + "|" + _grid.ThumbSize.Width;

    private void OnVisible(List<GalleryEntry> inView)
    {
        Size box = _grid.ThumbSize;
        var wanted = new HashSet<string>();
        foreach (GalleryEntry e in inView)
        {
            string key = ThumbKey(e);
            wanted.Add(key);
            if (_thumbs.ContainsKey(key)) continue;
            if (_failed.TryGetValue(key, out long when) && Environment.TickCount64 - when < 3000) continue;
            RequestThumb(e, key, box);
        }
        _wanted = wanted;
    }

    private void RequestThumb(GalleryEntry e, string key, Size box)
    {
        if (!_pending.Add(key)) return;
        _queue.Push((e, key, box));
        EnsureWorkers();
    }

    private void EnsureWorkers()
    {
        while (!_cts.IsCancellationRequested)
        {
            int w = Volatile.Read(ref _workers);
            if (w >= MaxWorkers || _queue.IsEmpty) return;
            if (Interlocked.CompareExchange(ref _workers, w + 1, w) == w) Task.Run(Worker);
        }
    }

    private void Worker()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _queue.TryPop(out var req))
            {
                HashSet<string>? wanted = _wanted;
                if (wanted != null && !wanted.Contains(req.Key)) { Post(() => _pending.Remove(req.Key)); continue; }   // scrolled away meanwhile
                Bitmap? bmp = null; Size dim = Size.Empty;
                try { bmp = LoadThumb(req.En.Path, req.Box.Width, req.Box.Height, out dim); }
                catch (Exception ex) { Log.Info("gallery: thumbnail failed for " + req.En.Name + " - " + ex.Message); }
                var r = req; Bitmap? b = bmp; Size d = dim;
                if (!Post(() => OnThumb(r.En, r.Key, b, d))) b?.Dispose();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _workers);
            if (!_queue.IsEmpty) EnsureWorkers();   // an item pushed while this worker was leaving
        }
    }

    private bool Post(Action a)
    {
        try { if (IsDisposed || !IsHandleCreated) return false; BeginInvoke(a); return true; }
        catch (Exception) { return false; }
    }

    private void OnThumb(GalleryEntry en, string key, Bitmap? bmp, Size dim)
    {
        _pending.Remove(key);
        if (IsDisposed) { bmp?.Dispose(); return; }
        if (bmp == null) { _failed[key] = Environment.TickCount64; _grid.InvalidateEntry(en); return; }
        if (_thumbs.ContainsKey(key)) { bmp.Dispose(); return; }
        _thumbs[key] = bmp;
        _thumbOrder.Enqueue(key);
        _thumbBytes += (long)bmp.Width * bmp.Height * 4;
        while (_thumbBytes > MaxThumbBytes && _thumbOrder.Count > 1)
        {
            string old = _thumbOrder.Dequeue();
            if (_thumbs.Remove(old, out Bitmap? ob)) { _thumbBytes -= (long)ob.Width * ob.Height * 4; ob.Dispose(); }
        }
        if (dim != Size.Empty) en.Dim = dim;
        _grid.InvalidateEntry(en);
    }

    private static Bitmap LoadThumb(string path, int boxW, int boxH, out Size dim)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using Image img = Image.FromStream(fs, false, false);
        dim = img.Size;
        double k = Math.Min((double)boxW / img.Width, (double)boxH / img.Height);
        if (k > 1) k = 1;
        int w = Math.Max(1, (int)Math.Round(img.Width * k)), h = Math.Max(1, (int)Math.Round(img.Height * k));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
        return bmp;
    }

    /// <summary>Test hook: load the thumbnails of everything, not just what is in view.</summary>
    internal void LoadAllThumbs()
    {
        _wanted = null;
        Size box = _grid.ThumbSize;
        foreach (GalleryEntry e in _all)
        {
            string key = ThumbKey(e);
            if (!_thumbs.ContainsKey(key)) RequestThumb(e, key, box);
        }
    }

    // ---- preview and info ------------------------------------------------------------------------------

    private static Bitmap? LoadFull(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using Image img = Image.FromStream(fs, false, false);
            return new Bitmap(img);     // a copy: the file is not held open
        }
        catch (Exception ex)
        {
            Log.Info("gallery: cannot read " + path + " - " + ex.Message);
            return null;
        }
    }

    private async Task UpdatePreviewAsync()
    {
        var sel = _grid.Selected.ToList();
        int gen = ++_previewGen;
        if (sel.Count != 1)
        {
            _preview.SetImage(null, sel.Count == 0 ? L.T("画像を選ぶとここに表示されます") : L.F("{0} 枚を選んでいます", sel.Count));
            return;
        }
        GalleryEntry en = sel[0];
        Bitmap? bmp;
        try { bmp = await Task.Run(() => LoadFull(en.Path)); }
        catch (Exception) { bmp = null; }
        if (IsDisposed || gen != _previewGen) { bmp?.Dispose(); return; }
        _preview.SetImage(bmp, bmp == null ? L.T("画像を読み込めません") : null);
        if (bmp != null && en.Dim == Size.Empty) { en.Dim = bmp.Size; UpdateInfo(); _grid.InvalidateEntry(en); }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, bytes / 1024) + " KB";

    private string SubTextOf(GalleryEntry e) =>
        e.Dim != Size.Empty ? $"{e.Dim.Width}×{e.Dim.Height}   {FormatSize(e.Length)}" : FormatSize(e.Length);

    private void UpdateInfo()
    {
        var sel = _grid.Selected.ToList();
        if (sel.Count == 0) _info.Text = string.Empty;
        else if (sel.Count == 1)
        {
            GalleryEntry e = sel[0];
            _info.Text = e.Name + "\n" + SubTextOf(e) + "\n" + e.Time.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)
                + "\n" + (Path.GetDirectoryName(e.Path) ?? string.Empty);
        }
        else _info.Text = L.F("{0} 枚を選んでいます", sel.Count) + "\n" + FormatSize(sel.Sum(e => e.Length));
    }

    private void UpdateActions()
    {
        int n = _grid.SelectedCount;
        _bCopy.Enabled = n >= 1;
        _bDelete.Enabled = n >= 1;
        _bOpen.Enabled = n == 1;
        _bReveal.Enabled = n == 1;
        _bRename.Enabled = n == 1;
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ---- actions ---------------------------------------------------------------------------------------

    /// <summary>Copies the selection: one picture goes on the clipboard as an image and as a file (so it pastes
    /// into an editor and into Explorer), several as files.</summary>
    internal bool CopySelected()
    {
        var sel = _grid.Selected.Where(e => File.Exists(e.Path)).ToList();
        if (sel.Count == 0) return false;
        try
        {
            var data = new DataObject();
            var files = new StringCollection();
            foreach (GalleryEntry e in sel) files.Add(e.Path);
            data.SetFileDropList(files);
            if (sel.Count == 1)
            {
                // The picture goes in as a ready-made DIB, not as a Bitmap object: a Bitmap is only rendered when
                // the pasting program asks, and this window (and the bitmap) may be gone by then.
                using Bitmap? bmp = LoadFull(sel[0].Path);
                if (bmp != null) data.SetData(DataFormats.Dib, false, ToDib(bmp));
            }
            Clipboard.SetDataObject(data, true, 5, 100);
            SetStatus(sel.Count == 1 ? L.T("クリップボードにコピーしました") : L.F("{0} 枚をコピーしました", sel.Count));
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("gallery: copy failed - " + ex);
            SetStatus(L.T("コピーに失敗しました"));
            return false;
        }
    }

    /// <summary>A bottom-up 32-bit DIB (the clipboard's CF_DIB layout) of the picture, opaque.</summary>
    internal static MemoryStream ToDib(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        BitmapData bd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ms = new MemoryStream(40 + w * h * 4);
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32);
                bw.Write(0); bw.Write(w * h * 4); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                var row = new byte[w * 4];
                for (int y = h - 1; y >= 0; y--)
                {
                    Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                    for (int x = 3; x < row.Length; x += 4) row[x] = 255;
                    bw.Write(row);
                }
            }
            ms.Position = 0;
            return ms;
        }
        finally { src.UnlockBits(bd); }
    }

    private GalleryEntry? Single()
    {
        var sel = _grid.Selected.ToList();
        if (sel.Count == 0) return null;
        return sel.Count == 1 ? sel[0] : (_grid.FocusIndex >= 0 && _grid.FocusIndex < _grid.View.Count ? _grid.View[_grid.FocusIndex] : sel[0]);
    }

    private void OpenSelected()
    {
        GalleryEntry? e = Single();
        if (e == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Info("gallery: open failed - " + ex);
            MessageBox.Show(this, Errors.Describe(ex), "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RevealSelected()
    {
        GalleryEntry? e = Single();
        if (e == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{e.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Info("gallery: reveal failed - " + ex);
            MessageBox.Show(this, Errors.Describe(ex), "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool RecycleSupported(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Moves the selection to the Recycle Bin (after asking, unless <paramref name="confirm"/> is false).
    /// Returns how many files went.</summary>
    internal int DeleteSelected(bool confirm)
    {
        var sel = _grid.Selected.ToList();
        if (sel.Count == 0) return 0;
        if (confirm)
        {
            string text = sel.Count == 1 ? L.F("「{0}」をごみ箱に移動しますか？", sel[0].Name) : L.F("選んだ {0} 枚をごみ箱に移動しますか？", sel.Count);
            if (sel.Any(e => !RecycleSupported(e.Path))) text += "\n\n" + L.T("ごみ箱が使えない場所にあるファイルは、完全に削除されます。");
            if (MessageBox.Show(this, text, "YuCap", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return 0;
        }
        int first = _grid.FirstSelectedIndex, done = 0;
        string? err = null;
        foreach (GalleryEntry e in sel)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(e.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                done++;
            }
            catch (Exception ex)
            {
                Log.Info("gallery: delete failed for " + e.Path + " - " + ex.Message);
                err = Errors.Describe(ex);
            }
        }
        _afterScanSelectIndex = first;
        _ = RescanAsync();
        if (err != null && confirm)
            MessageBox.Show(this, L.F("削除できませんでした。\n\n{0}", err), "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (done > 0)
            SetStatus(L.F("{0} 枚をごみ箱に移動しました", done));
        return done;
    }

    private void RenameSelected()
    {
        var sel = _grid.Selected.ToList();
        if (sel.Count != 1) return;
        GalleryEntry en = sel[0];
        using var dlg = Ui.NewDialog(L.T("名前を変更"), 420, 160);
        var stack = Ui.Stack();
        var lbl = Ui.Label(L.T("新しい名前（拡張子は変わりません）:"), 0, 0);
        lbl.Margin = new Padding(0, 0, 0, Ui.Gap / 2);
        var box = new TextBox { Text = en.Title, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, Ui.Gap) };
        var err = Ui.FlowHint(string.Empty);
        err.ForeColor = Color.Firebrick;
        stack.Controls.Add(lbl);
        stack.Controls.Add(box);
        stack.Controls.Add(err);
        Ui.FrameOkCancel(dlg, stack);
        dlg.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        dlg.FormClosing += (_, e) =>
        {
            if (dlg.DialogResult != DialogResult.OK) return;
            if (!RenameFile(en, box.Text, out string? msg))
            {
                err.Text = msg ?? string.Empty;
                e.Cancel = true;
            }
        };
        dlg.ShowDialog(this);
    }

    /// <summary>Renames the file (same folder, same extension). False with a message when the name cannot be used.</summary>
    internal bool RenameFile(GalleryEntry en, string newTitle, out string? message)
    {
        message = null;
        string t = newTitle.Trim();
        if (t.Length == 0 || t.Length > 150 || t.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || t.EndsWith('.'))
        {
            message = L.T("この名前は使えません。");
            return false;
        }
        string dir = Path.GetDirectoryName(en.Path) ?? string.Empty;
        string target = Path.Combine(dir, t + Path.GetExtension(en.Path));
        if (string.Equals(target, en.Path, StringComparison.Ordinal)) return true;
        if (File.Exists(target) && !string.Equals(target, en.Path, StringComparison.OrdinalIgnoreCase))
        {
            message = L.T("同じ名前のファイルがあります。");
            return false;
        }
        try
        {
            File.Move(en.Path, target);
        }
        catch (Exception ex)
        {
            Log.Info("gallery: rename failed - " + ex);
            message = L.F("名前を変更できませんでした。\n\n{0}", Errors.Describe(ex));
            return false;
        }
        _pendingSelect = target;
        _ = RescanAsync();
        return true;
    }

    // ---- test access -------------------------------------------------------------------------------------

    internal IReadOnlyList<GalleryEntry> Listed => _all;
    internal IReadOnlyList<GalleryEntry> ViewEntries => _grid.View;
    internal int CachedThumbs() => _all.Count(e => _thumbs.ContainsKey(ThumbKey(e)));
    internal void SelectPaths(params string[] paths) => _grid.SelectByPaths(new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase));
    internal bool PreviewHasImage => _preview.HasImage;
    internal bool HasWatcher => _fsw != null;
}

/// <summary>The large picture beside the list.</summary>
internal sealed class PreviewBox : Control
{
    private Bitmap? _img;
    private string? _msg;
    public string EmptyText = string.Empty;

    public PreviewBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Colors.Surface;
        ForeColor = Ui.Colors.SubText;
        TabStop = false;
    }

    public bool HasImage => _img != null;

    public void SetImage(Bitmap? img, string? message)
    {
        Bitmap? old = _img;
        _img = img;
        _msg = message;
        Invalidate();
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Ui.Colors.Surface);
        if (_img != null)
        {
            double k = Math.Min((double)ClientSize.Width / _img.Width, (double)ClientSize.Height / _img.Height);
            if (k > 1) k = 1;      // never enlarge past the real size
            int w = Math.Max(1, (int)Math.Round(_img.Width * k)), h = Math.Max(1, (int)Math.Round(_img.Height * k));
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_img, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }
        else
        {
            string text = string.IsNullOrEmpty(_msg) ? EmptyText : _msg!;
            TextRenderer.DrawText(g, text, Font, ClientRectangle, Ui.Colors.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _img?.Dispose(); _img = null; }
        base.Dispose(disposing);
    }
}
