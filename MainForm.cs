using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

public sealed partial class MainForm : Form, IMessageFilter
{
    // ---- Constants -------------------------------------------------------
    private const int VolumeStep = 5;
    // Terse status flips (volume, zoom, PiP on/off) — glanceable, gone fast.
    private const int OsdMilliseconds = 900;
    // Messages that carry information to read or act on (a clickable "saved"
    // notice, a hotkey-conflict warning, a device-disconnect notice): 900ms
    // vanishes before the text can be read, let alone clicked.
    private const int OsdLongMilliseconds = 4000;
    private const int WmMouseWheel = 0x020A;
    private const int WmSizing = 0x0214;
    private const int WmDeviceChange = 0x0219;
    private const int WmHotkey = 0x0312;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12;

    // Global hotkeys (RegisterHotKey).
    private const int HkSnapshot = 1, HkMute = 2, HkPip = 3;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Display/system sleep inhibition while the video is playing.
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);

    // Click-through (PiP).
    private const int WsExTransparent = 0x20, WsExLayered = 0x80000;
    private const uint LwaAlpha = 0x2;
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4,
        WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7, WmszBottomRight = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    // ---- Engines & UI ----------------------------------------------------
    private readonly VideoEngine _video = new();
    private readonly AudioEngine _audio = new();

    private readonly VideoBox _canvas = new();
    private readonly OsdOverlay _osd = new();
    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblVideo = new();
    private readonly ToolStripStatusLabel _lblAudio = new();
    private readonly ToolStripStatusLabel _lblVolume = new();
    private readonly ContextMenuStrip _ctx = new();
    private readonly System.Windows.Forms.Timer _osdTimer = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly System.Windows.Forms.Timer _devTimer = new(); // WM_DEVICECHANGE debounce
    private readonly System.Windows.Forms.Timer _resumeTimer = new(); // post-resume engine restart
    // One-shot: fires almost immediately after the window is shown, so device
    // enumeration/Start() (each up to a 5s MF timeout) never delays the window
    // appearing in the first place. See OnLoad / the tick handler below.
    private readonly System.Windows.Forms.Timer _startupTimer = new();
    private bool _startingUp;

    private readonly List<ToolStripMenuItem> _aspectItems = new();
    private readonly List<ToolStripMenuItem> _rotationItems = new();

    private ToolStripMenuItem _miLockAspect = null!;
    private ToolStripMenuItem _miBorderless = null!;
    private ToolStripMenuItem _miTopmost = null!;
    private ToolStripMenuItem _miStatusBar = null!;
    private ToolStripMenuItem _miMenuBar = null!;
    private ToolStripMenuItem _miMute = null!;
    private ToolStripMenuItem _cmiMute = null!;
    private ToolStripMenuItem _miMirror = null!;
    private ToolStripMenuItem _miFreeze = null!;
    private ToolStripMenuItem _cmiFreeze = null!;
    private ToolStripMenuItem _miPip = null!;
    private ToolStripMenuItem _cmiPip = null!;
    private ToolStripMenuItem _miClickThrough = null!;
    private ToolStripMenuItem _miHotkeys = null!;
    private ToolStripMenuItem _miStartup = null!;
    private ToolStripMenuItem _miCursorHide = null!;
    private ToolStripMenuItem _miUpdateCheck = null!;
    private ToolStripMenuItem _miCheckUpdate = null!; // help menu "更新を確認..."; disabled while a check is in flight
    private ToolStripMenuItem _miBurst = null!;
    private ToolStripMenuItem _miRestoreLevel = null!;
    private readonly List<ToolStripMenuItem> _cursorSecItems = new();
    private readonly List<ToolStripMenuItem> _pipIdleItems = new();
    private readonly List<ToolStripMenuItem> _pipHoverItems = new();
    private readonly List<ToolStripMenuItem> _pipSizeItems = new();
    private readonly List<ToolStripMenuItem> _pipPosItems = new();
    private ToolStripMenuItem _cmiBorderless = null!;
    private ToolStripMenuItem _cmiTopmost = null!;
    private ToolStripMenuItem _cmiStatusBar = null!;
    private ToolStripMenuItem _cmiMenuBar = null!;

    private ToolStripMenuItem _videoDevicesRoot = null!;
    private ToolStripMenuItem _audioDevicesRoot = null!;
    private ToolStripMenuItem _videoModeRoot = null!;
    private ToolStripMenuItem _cmiVideoModeRoot = null!;

    // ---- State -----------------------------------------------------------
    private VideoDisplayMode _displayMode = VideoDisplayMode.AspectFit;
    private bool _lockAspect = true; // keep window ratio matched to the video by default
    private bool _isFullscreen;
    private bool _isBorderless;
    private bool _alwaysOnTop;

    private Rectangle _restoreBounds;
    private FormWindowState _restoreWindowState = FormWindowState.Normal;
    private bool _savedMenuVisible = true;
    private bool _savedStatusVisible = true;

    private AudioDeviceInfo? _currentAudioInfo;
    private VideoDeviceInfo? _currentVideoInfo;
    private CaptureMode? _savedMode; // last manually chosen capture mode (persisted)
    private AppSettings _settings = new();

    // Digital zoom/pan (not persisted).
    private double _zoom = 1.0;
    private int _panX, _panY;
    private bool _panning;
    private Point _panOrigin;

    // Freeze frame.
    private readonly PictureBox _freezeBox = new();
    private bool _frozen;
    private bool _mutedBeforeFreeze;
    // True when the frozen still came from the capture engine's photo sink
    // (video pixels only, no letterbox) rather than the compositor screen
    // copy (which already includes the letterbox). The two need different
    // positioning — see FreezeBounds.
    private bool _freezeIsPhoto;

    // Sleep inhibition.
    private bool _keepAwake;

    // Picture-in-picture.
    private bool _isPip;
    private Rectangle _prePipBounds;
    private bool _prePipBorderless, _prePipTopmost, _prePipMenu, _prePipStatus, _prePipFullscreen;
    private readonly System.Windows.Forms.Timer _pipHoverTimer = new(); // hover-opacity polling
    private bool _pipHovered;

    // Burst snapshots.
    private readonly System.Windows.Forms.Timer _burstTimer = new();
    private int _burstTotal, _burstDone;

    // Command-line video mode (session-only; never persisted).
    private CaptureMode? _cliMode;

    // Set when an update has already persisted settings and torn down the
    // engines; the closing handler must not run that work a second time.
    private bool _skipSaveOnClose;

    // Most recent snapshot file name, so the OSD can act as a shortcut to it.
    private string? _lastSavedSnapshot;

    // Where the most recent snapshot actually landed. Not the configured
    // folder: a save can fall back to the default one when the configured
    // folder is on a drive that has gone away.
    private string? _lastSaveDir;

    // Interactive (border-drag) resize in progress: keep the video streaming
    // smoothly instead of clear+reblit on every move message.
    private bool _inSizeMove;
    private int _lastLiveRectTick;

    // NOTE: a periodic "keep-warm" UpdateVideo used to live here, on the theory
    // that an idle D3D pipeline was behind slow resizes. It was removed: the
    // logs showed a keep-warm call itself blocking for 14 SECONDS, and because
    // the sink is driven by a single worker, the user's fullscreen resize sat
    // queued behind it — the delay it was meant to prevent. The resize itself
    // completes in ~0ms once the worker is free. Do not reintroduce it.

    // Settle time between a window-mode change and the swap-chain resize, so MF
    // is not asked to rebuild while the DWM transition is still in flight.
    private const int ModeChangeSettleMs = 120;

    // MF's UpdateVideo sometimes never returns (logs show "begin rect=1920x1080"
    // with no matching "end" for the rest of the session). The UI survives — the
    // call is on a worker — but the video would stay at the old size forever.
    // Restarting the preview rebuilds the pipeline and is the only known cure;
    // Start() has completed reliably in every log, so this is the safe way out.
    private readonly System.Windows.Forms.Timer _resizeGuard = new();
    private const int ResizeStuckMs = 900;

    // Auto-hide the pointer while it rests over the video, like a video player.
    // 3s matches what most players and streaming sites use. Detection polls the
    // cursor position rather than using mouse events: once the cursor is hidden
    // and the user stops moving it, no further events arrive to reason about.
    private readonly System.Windows.Forms.Timer _cursorTimer = new();
    private readonly System.Windows.Forms.Timer _settingsSaveTimer = new();
    private Point _lastCursorPos;
    private int _lastCursorMoveTick;
    private bool _cursorHidden;
    private int _restartAttempts;                 // capped so recovery can't loop
    private const int MaxRestartAttempts = 2;

    // The normal-mode minimum window size. MainForm.Pip.cs lowers MinimumSize
    // below this while entering PiP (small preset sizes would otherwise be
    // clamped up to a 4:3 window with black bars) and restores it via this
    // field on exit.
    private static readonly Size NormalMinimumSize = new(320, 240);

    // The Windows-wide recording level before "入力レベルを最大にする" changed
    // it, so "入力レベルを元に戻す" has something to restore to. -1 = nothing
    // to restore (either never raised, or already restored this session).
    private int _captureLevelBefore = -1;

    public MainForm()
    {
        // Settings are needed BEFORE building menus (UI language).
        _settings = SettingsStore.Load();
        L.English = _settings.Language == "en";

        Text = L.T("YuCap - キャプチャビューア");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 540 + 24 + 22);
        MinimumSize = NormalMinimumSize;
        BackColor = Color.Black;
        KeyPreview = true;

        BuildCanvas();
        BuildMenu();
        BuildStatusBar();
        BuildContextMenu();

        // Freeze-frame overlay: shows the captured still exactly over the canvas.
        _freezeBox.Visible = false;
        _freezeBox.BackColor = Color.Black;
        _freezeBox.SizeMode = PictureBoxSizeMode.StretchImage;
        _freezeBox.ContextMenuStrip = _ctx;
        _freezeBox.DoubleClick += OnCanvasDoubleClick;
        _freezeBox.MouseDown += OnCanvasMouseDown;
        _freezeBox.MouseMove += OnCanvasMouseMove;
        _freezeBox.MouseUp += OnCanvasMouseUp;

        Controls.Add(_canvas);
        Controls.Add(_freezeBox);
        Controls.Add(_menu);
        Controls.Add(_status);
        Controls.Add(_osd);
        MainMenuStrip = _menu;
        _canvas.SendToBack();
        _osd.BringToFront();

        _osdTimer.Interval = OsdMilliseconds;
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); _osd.Visible = false; };

        // Clicking a "saved" notice reveals the file in Explorer.
        _osd.Cursor = Cursors.Hand;
        _osd.Click += (_, _) =>
        {
            if (_lastSavedSnapshot == null) return;
            OpenFolder(_lastSaveDir ?? SnapshotDirectory, _lastSavedSnapshot);
            _osdTimer.Stop();
            _osd.Visible = false;
        };

        _uiTimer.Interval = 500;
        _uiTimer.Tick += (_, _) => { Watchdog.Beat(); UpdateStatus(); };

        // Device arrivals come in bursts; settle before re-enumerating.
        _devTimer.Interval = 900;
        _devTimer.Tick += (_, _) => { _devTimer.Stop(); OnDeviceChange(); };

        _burstTimer.Tick += (_, _) => BurstTick();

        _resizeGuard.Interval = 400;
        _resizeGuard.Tick += (_, _) => CheckResizeStuck();

        _cursorTimer.Interval = 250;
        _cursorTimer.Tick += (_, _) => UpdateCursorVisibility();

        // Settings used to be written only on exit, so a crash lost everything
        // changed that session. Coalesce changes and flush a few seconds later.
        _settingsSaveTimer.Interval = 3000;
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            try { SaveSettings(); } catch (Exception ex) { Log.Info("deferred save failed: " + ex.Message); }
        };

        // Mouse-over detection works even with click-through (no mouse events
        // reach the window then), so poll the cursor position while in PiP.
        _pipHoverTimer.Interval = 150;
        _pipHoverTimer.Tick += (_, _) => UpdatePipHoverOpacity();

        // After system resume the MF pipeline can be left wedged (COM calls then
        // block the UI thread — seen as a freeze on fullscreen toggle). Restart
        // both engines from scratch once devices have settled.
        _resumeTimer.Interval = 2500;
        _resumeTimer.Tick += (_, _) =>
        {
            _resumeTimer.Stop();
            Log.Info("resume: restarting engines");
            try { _video.Stop(); } catch { /* ignore */ }
            try { _audio.Stop(); } catch { /* ignore */ }
            OnDeviceChange(); // re-opens video + audio with the saved device/mode
        };

        // Interval 1: fires on the very next message-loop tick, i.e. after the
        // window has already been shown, rather than blocking OnLoad itself.
        _startupTimer.Interval = 1;
        _startupTimer.Tick += OnStartupTimerTick;

        Load += OnLoad;
        FormClosing += OnFormClosing;
        Resize += (_, _) => { LayoutCanvas(); MarkSettingsDirty(); };  // debounced: one save per drag
        Shown += (_, _) => LayoutCanvas(); // ensure correct fit once fully laid out
    }

    // ---- UI construction -------------------------------------------------

    private void BuildCanvas()
    {
        // Positioned (not docked) to the aspect-fit rectangle between the menu and
        // status bars; the black form background around it is the letterbox. This
        // keeps the video off the menu and resizes it with the window.
        _canvas.BackColor = Color.Black;
        _canvas.Paint += OnCanvasPaint;
        _canvas.DoubleClick += OnCanvasDoubleClick;
        _canvas.MouseDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseUp += OnCanvasMouseUp;
    }

    private void OnCanvasPaint(object? sender, PaintEventArgs e)
    {
        if (_video.IsRunning) return; // MF owns the surface while running
        Graphics g = e.Graphics;
        g.Clear(Color.Black);

        string main = _startingUp ? L.T("接続しています...") : L.T("映像なし");
        using var font = new Font("Segoe UI", 14f);
        SizeF sz = g.MeasureString(main, font);
        float mainX = (_canvas.ClientSize.Width - sz.Width) / 2;
        float mainY = (_canvas.ClientSize.Height - sz.Height) / 2;
        g.DrawString(main, font, Brushes.Gray, mainX, mainY);

        if (!_startingUp)
        {
            string hint = L.T("キャプチャデバイスを接続すると自動的に表示されます");
            using var hintFont = new Font("Segoe UI", 9f);
            SizeF hintSz = g.MeasureString(hint, hintFont);
            using var dimBrush = new SolidBrush(Color.FromArgb(255, 96, 96, 96));
            g.DrawString(hint, hintFont, dimBrush,
                (_canvas.ClientSize.Width - hintSz.Width) / 2, mainY + sz.Height + 4);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectExForDpi(ref Rect rc, int style, bool menu, int exStyle, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    private const int GwlStyle = -16, GwlExStyle = -20;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13,
        HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
    private const int ResizeBorder = 6;
    private const uint SwpFrameChanged = 0x0020, SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    /// <summary>Resize hit code for a point near the client edges, else HTCLIENT.</summary>
    internal static int EdgeHit(Point p, Size client)
    {
        bool l = p.X < ResizeBorder, r = p.X >= client.Width - ResizeBorder;
        bool t = p.Y < ResizeBorder, b = p.Y >= client.Height - ResizeBorder;
        if (t && l) return HtTopLeft;
        if (t && r) return HtTopRight;
        if (b && l) return HtBottomLeft;
        if (b && r) return HtBottomRight;
        if (l) return HtLeft;
        if (r) return HtRight;
        if (t) return HtTop;
        if (b) return HtBottom;
        return HtClient;
    }

    /// <summary>
    /// Borderless mode keeps the resizable frame styles and hides the chrome via
    /// WM_NCCALCSIZE.
    /// </summary>
    private void RefreshFrame()
    {
        if (_isFullscreen) return;
        if (FormBorderStyle != FormBorderStyle.Sizable) FormBorderStyle = FormBorderStyle.Sizable;
        if (IsHandleCreated)
        {
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            // SWP_NOMOVE|SWP_NOSIZE makes WinForms skip its bounds refresh even
            // though the CLIENT area just changed (chrome added/removed), leaving
            // ClientSize stale — the cause of letterbox-band layout bugs. Re-read.
            UpdateBounds();
        }
    }

    // ---- Drag the window by the video area, with edge/window snapping ----
    private const int SnapDistance = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect r);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    private const int DwmwaCloaked = 14;

    /// <summary>UWP/system windows can be "cloaked": IsWindowVisible-true yet not
    /// on screen. They must not become snap targets (phantom magnetism).</summary>
    private static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private bool _dragging;
    private bool _snapCollected;
    private Point _dragOrigin;
    private Point _formOrigin;
    private List<(Rectangle rect, bool screen)> _snapTargets = new();

    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        // Clicking counts as activity: bring the pointer back at once rather
        // than waiting for the next poll.
        _lastCursorMoveTick = Environment.TickCount;
        ShowCursorIfHidden();
        _canvas.Focus();
        if (e.Button == MouseButtons.Middle)
        {
            ToggleMute();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_zoom > 1.001)
        {
            // Zoomed in: dragging pans the video instead of moving the window.
            _panning = true;
            _dragOrigin = Cursor.Position;
            _panOrigin = new Point(_panX, _panY);
        }
        else if (!_isFullscreen && WindowState == FormWindowState.Normal)
        {
            _dragging = true;
            _dragOrigin = Cursor.Position;
            _formOrigin = Location;
            // Snap targets are NOT collected here: EnumWindows walks every
            // top-level window and can stall on hung/suspended apps. A plain
            // click — and both clicks of the fullscreen double-click — must
            // never pay that cost; it is deferred to the first real drag move.
            _snapCollected = false;
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (_panning)
        {
            Point cur = Cursor.Position;
            _panX = _panOrigin.X + (cur.X - _dragOrigin.X);
            _panY = _panOrigin.Y + (cur.Y - _dragOrigin.Y);
            ClampPan();
            LayoutCanvas();
            return;
        }
        if (!_dragging) return;
        Point p = Cursor.Position;
        int dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;
        if (!_snapCollected)
        {
            // Ignore sub-4px jitter (e.g. between double-click presses); only a
            // deliberate drag pays the EnumWindows cost.
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
            _snapTargets = CollectSnapTargets();
            _snapCollected = true;
        }
        Location = Snap(_formOrigin.X + dx, _formOrigin.Y + dy, Width, Height);
    }

    private void OnCanvasMouseUp(object? sender, MouseEventArgs e)
    {
        _dragging = false;
        _panning = false;
    }

    private List<(Rectangle rect, bool screen)> CollectSnapTargets()
    {
        long t0 = Environment.TickCount64;
        var list = new List<(Rectangle, bool)>();
        foreach (Screen s in Screen.AllScreens) list.Add((s.WorkingArea, true));
        IntPtr self = Handle;
        EnumWindows((h, _) =>
        {
            if (h != self && IsWindowVisible(h) && !IsIconic(h) && GetWindowTextLength(h) > 0
                && !IsCloaked(h) && GetWindowRect(h, out Rect r))
            {
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w > 80 && ht > 60) list.Add((new Rectangle(r.Left, r.Top, w, ht), false));
            }
            return true;
        }, IntPtr.Zero);
        long dt = Environment.TickCount64 - t0;
        if (dt > 100) Log.Info($"CollectSnapTargets SLOW: {dt}ms ({list.Count} targets)");
        return list;
    }

    private Point Snap(int x, int y, int w, int h)
    {
        foreach (var (t, isScreen) in _snapTargets)
        {
            int left = x, right = x + w, top = y, bottom = y + h;

            if (isScreen) // snap window edges to the inside of the work area
            {
                if (Math.Abs(left - t.Left) < SnapDistance) x = t.Left;
                if (Math.Abs(right - t.Right) < SnapDistance) x = t.Right - w;
                if (Math.Abs(top - t.Top) < SnapDistance) y = t.Top;
                if (Math.Abs(bottom - t.Bottom) < SnapDistance) y = t.Bottom - h;
            }
            else // snap to another window: align edges or dock adjacent
            {
                if (Math.Abs(left - t.Left) < SnapDistance) x = t.Left;
                else if (Math.Abs(right - t.Right) < SnapDistance) x = t.Right - w;
                else if (Math.Abs(right - t.Left) < SnapDistance) x = t.Left - w;
                else if (Math.Abs(left - t.Right) < SnapDistance) x = t.Right;

                if (Math.Abs(top - t.Top) < SnapDistance) y = t.Top;
                else if (Math.Abs(bottom - t.Bottom) < SnapDistance) y = t.Bottom - h;
                else if (Math.Abs(bottom - t.Top) < SnapDistance) y = t.Top - h;
                else if (Math.Abs(top - t.Bottom) < SnapDistance) y = t.Bottom;
            }
        }
        return new Point(x, y);
    }

    private void BuildMenu()
    {
        // ---- ファイル: スナップショット操作と終了 ----
        var file = new ToolStripMenuItem(L.T("ファイル(&F)"));
        var snap = new ToolStripMenuItem(L.T("スナップショットを保存"), null, (_, _) => SaveSnapshot())
        {
            ShortcutKeyDisplayString = "Ctrl+S",
        };
        var copy = new ToolStripMenuItem(L.T("スナップショットをコピー"), null, (_, _) => CopySnapshotToClipboard())
        {
            ShortcutKeyDisplayString = "Ctrl+C",
        };
        _miBurst = new ToolStripMenuItem(L.T("連写スナップショット..."), null, (_, _) => ShowBurstDialog());
        var openDir = new ToolStripMenuItem(L.T("保存先フォルダを開く"), null, (_, _) => OpenSnapshotFolder());
        var snapCfg = new ToolStripMenuItem(L.T("スナップショット設定..."), null, (_, _) => ShowSnapshotSettings());
        var exit = new ToolStripMenuItem(L.T("終了"), null, (_, _) => Close());
        file.DropDownOpening += (_, _) => UpdateChecks();
        file.DropDownItems.Add(snap);
        file.DropDownItems.Add(copy);
        file.DropDownItems.Add(_miBurst);
        file.DropDownItems.Add(openDir);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(snapCfg);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(exit);

        // ---- デバイス ----
        var device = new ToolStripMenuItem(L.T("デバイス(&D)"));
        _videoDevicesRoot = new ToolStripMenuItem(L.T("映像デバイス"));
        _audioDevicesRoot = new ToolStripMenuItem(L.T("音声デバイス"));
        _videoDevicesRoot.DropDownOpening += (_, _) => RebuildVideoDeviceList();
        _audioDevicesRoot.DropDownOpening += (_, _) => RebuildAudioDeviceList();
        _videoDevicesRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        _audioDevicesRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        _videoModeRoot = new ToolStripMenuItem(L.T("映像モード (解像度/FPS)"));
        _videoModeRoot.DropDownOpening += (_, _) => RebuildVideoModeList(_videoModeRoot);
        _videoModeRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });

        device.DropDownItems.Add(_videoDevicesRoot);
        device.DropDownItems.Add(_audioDevicesRoot);
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(_videoModeRoot);
        device.DropDownItems.Add(new ToolStripMenuItem(L.T("優先デバイス設定..."), null,
            (_, _) => ShowDeviceKeywordSettings()));
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(new ToolStripMenuItem(L.T("音声バッファ設定..."), null,
            (_, _) => ShowAudioBufferSettings()));
        device.DropDownItems.Add(new ToolStripMenuItem(L.T("入力レベルを最大にする"), null,
            (_, _) => MaximizeCaptureLevel()));
        _miRestoreLevel = new ToolStripMenuItem(L.T("入力レベルを元に戻す"), null, (_, _) => RestoreCaptureLevel());
        device.DropDownItems.Add(_miRestoreLevel);
        _miMute = new ToolStripMenuItem(L.T("ミュート"), null, (_, _) => ToggleMute())
        {
            ShortcutKeyDisplayString = "M",
        };
        device.DropDownItems.Add(_miMute);

        // ---- 表示 ----
        var view = new ToolStripMenuItem(L.T("表示(&V)"));
        view.DropDownOpening += (_, _) => UpdateChecks();

        _miLockAspect = new ToolStripMenuItem(L.T("ウィンドウ比率を映像に固定"), null, (_, _) => ToggleLockAspect());
        _miBorderless = new ToolStripMenuItem(L.T("ウィンドウ枠を非表示"), null, (_, _) => ToggleBorderless())
        {
            ShortcutKeyDisplayString = "Ctrl+B",
        };
        _miTopmost = new ToolStripMenuItem(L.T("常に前面に表示"), null, (_, _) => ToggleAlwaysOnTop())
        {
            ShortcutKeyDisplayString = "Ctrl+T",
        };
        _miMenuBar = new ToolStripMenuItem(L.T("メニューバーを表示"), null, (_, _) => ToggleMenuBar())
        {
            ShortcutKeyDisplayString = "F10",
        };
        _miStatusBar = new ToolStripMenuItem(L.T("ステータスバーを表示"), null, (_, _) => ToggleStatusBar())
        {
            Checked = true,
        };
        var fullscreen = new ToolStripMenuItem(L.T("全画面表示"), null, (_, _) => ToggleFullscreen())
        {
            ShortcutKeyDisplayString = "F11",
        };

        var zoomReset = new ToolStripMenuItem(L.T("ズームをリセット"), null, (_, _) => ResetZoom())
        {
            ShortcutKeyDisplayString = "Ctrl+0",
        };
        _miFreeze = new ToolStripMenuItem(L.T("一時停止 / 再開"), null, (_, _) => ToggleFreeze())
        {
            ShortcutKeyDisplayString = "Space",
        };
        // ShortcutKeyDisplayString is set from the real setting in UpdateChecks
        // (FormatHotkey((Keys)_settings.HotkeyPip)) rather than hard-coded here,
        // since the user can rebind it in ホットキー設定.
        _miPip = new ToolStripMenuItem(L.T("ピクチャインピクチャ"), null, (_, _) => TogglePip());

        // 上から: 画面の使い方 → 再生操作 → 映像の見え方 → ウィンドウの振る舞い
        view.DropDownItems.Add(fullscreen);
        view.DropDownItems.Add(_miPip);
        view.DropDownItems.Add(BuildPipSettingsSubmenu());
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_miFreeze);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildAspectSubmenu());
        view.DropDownItems.Add(BuildRotationSubmenu());
        view.DropDownItems.Add(zoomReset);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildSizeSubmenu());
        view.DropDownItems.Add(_miLockAspect);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_miBorderless);
        view.DropDownItems.Add(_miTopmost);
        view.DropDownItems.Add(_miMenuBar);
        view.DropDownItems.Add(_miStatusBar);

        // ---- オプション: アプリ全体の動作設定 ----
        var options = new ToolStripMenuItem(L.T("オプション(&O)"));
        _miHotkeys = new ToolStripMenuItem(L.T("グローバルホットキーを有効化"), null, (_, _) => ToggleGlobalHotkeys());
        var hotkeyCfg = new ToolStripMenuItem(L.T("ホットキー設定..."), null, (_, _) => ShowHotkeySettings());
        _miStartup = new ToolStripMenuItem(L.T("Windows起動時に自動実行"), null, (_, _) => ToggleStartup());
        _miUpdateCheck = new ToolStripMenuItem(L.T("起動時に更新を確認"), null, (_, _) => ToggleUpdateCheck());
        var lang = new ToolStripMenuItem(L.T("言語 / Language"));
        var langJa = new ToolStripMenuItem("日本語", null, (_, _) => SetLanguage("ja")) { Checked = !L.English };
        var langEn = new ToolStripMenuItem("English", null, (_, _) => SetLanguage("en")) { Checked = L.English };
        lang.DropDownItems.Add(langJa);
        lang.DropDownItems.Add(langEn);
        _miCursorHide = new ToolStripMenuItem(L.T("カーソルを自動的に隠す"), null,
            (_, _) => ToggleCursorAutoHide());
        var cursorSecs = new ToolStripMenuItem(L.T("隠すまでの時間"));
        foreach (int sec in new[] { 1, 2, 3, 5, 10 })
        {
            int s = sec;
            var item = new ToolStripMenuItem(L.F("{0}秒", s), null,
                (_, _) => SetCursorHideSeconds(s)) { Tag = s };
            _cursorSecItems.Add(item);
            cursorSecs.DropDownItems.Add(item);
        }

        options.DropDownOpening += (_, _) => { _miStartup.Checked = IsStartupRegistered(); UpdateChecks(); };
        options.DropDownItems.Add(_miHotkeys);
        options.DropDownItems.Add(hotkeyCfg);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(_miCursorHide);
        options.DropDownItems.Add(cursorSecs);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(_miStartup);
        options.DropDownItems.Add(_miUpdateCheck);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(lang);

        // ---- ヘルプ ----
        var help = new ToolStripMenuItem(L.T("ヘルプ(&H)"));
        _miCheckUpdate = new ToolStripMenuItem(L.T("更新を確認..."), null,
            async (_, _) => await CheckForUpdatesAsync(manual: true));
        help.DropDownItems.Add(_miCheckUpdate);
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("バージョン情報..."), null, (_, _) => ShowAbout()));

        _menu.Items.AddRange(new ToolStripItem[] { file, device, view, options, help });
    }

    private ToolStripMenuItem BuildRotationSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("回転 / 反転"));
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            int d = deg;
            var item = new ToolStripMenuItem(d == 0 ? L.T("回転なし") : L.F("{0}° 回転", d), null,
                (_, _) => SetRotationDeg(d)) { Tag = d };
            _rotationItems.Add(item);
            m.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(new ToolStripSeparator());
        _miMirror = new ToolStripMenuItem(L.T("左右反転"), null, (_, _) => ToggleMirror());
        m.DropDownItems.Add(_miMirror);
        return m;
    }

    private ToolStripMenuItem BuildPipSettingsSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("PiP設定"));

        // Position & size first: with click-through on, the window can't be
        // dragged or resized, so these presets are the only way to control them.
        var pos = new ToolStripMenuItem(L.T("表示位置"));
        foreach ((string label, int corner) in new[]
                 { ("右下", 0), ("左下", 1), ("右上", 2), ("左上", 3) })
        {
            int c = corner;
            var item = new ToolStripMenuItem(L.T(label), null, (_, _) => SetPipCorner(c)) { Tag = c };
            _pipPosItems.Add(item);
            pos.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(pos);

        var size = new ToolStripMenuItem(L.T("サイズ（映像原寸比）"));
        foreach (int pct in new[] { 10, 15, 20, 25, 30, 40, 50 })
        {
            int p = pct;
            var item = new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipSize(p)) { Tag = p };
            _pipSizeItems.Add(item);
            size.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(size);

        var idle = new ToolStripMenuItem(L.T("不透明度（通常時）"));
        var hover = new ToolStripMenuItem(L.T("不透明度（マウスオーバー時）"));
        foreach (int pct in new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            int p = pct;
            var i1 = new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipOpacity(p, hover: false)) { Tag = p };
            _pipIdleItems.Add(i1);
            idle.DropDownItems.Add(i1);
        }
        // No 0% here (unlike the idle submenu above): a PiP window that is
        // invisible both at rest AND under the pointer can never be found
        // again to bring it back — hovering must remain the guaranteed way to
        // locate it. SetPipOpacity enforces the same floor.
        foreach (int pct in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            int p = pct;
            var i2 = new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipOpacity(p, hover: true)) { Tag = p };
            _pipHoverItems.Add(i2);
            hover.DropDownItems.Add(i2);
        }
        m.DropDownItems.Add(idle);
        m.DropDownItems.Add(hover);
        m.DropDownItems.Add(new ToolStripSeparator());
        _miClickThrough = new ToolStripMenuItem(L.T("クリックスルー"), null, (_, _) => TogglePipClickThrough());
        m.DropDownItems.Add(_miClickThrough);
        return m;
    }

    private ToolStripMenuItem BuildAspectSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("アスペクト比 / 表示モード"));
        m.DropDownItems.Add(MakeAspectItem(L.T("アスペクト比を保持"), VideoDisplayMode.AspectFit));
        m.DropDownItems.Add(MakeAspectItem(L.T("ウィンドウに引き伸ばし"), VideoDisplayMode.Stretch));
        m.DropDownItems.Add(MakeAspectItem(L.T("原寸表示"), VideoDisplayMode.OneToOne));
        m.DropDownItems.Add(MakeAspectItem(L.T("整数倍表示 (くっきり)"), VideoDisplayMode.IntegerScale));
        return m;
    }

    private ToolStripMenuItem MakeAspectItem(string text, VideoDisplayMode mode)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => SetDisplayMode(mode)) { Tag = mode };
        _aspectItems.Add(item);
        return item;
    }

    private ToolStripMenuItem BuildSizeSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("ウィンドウサイズ"));
        foreach (int pct in new[] { 50, 75, 100, 125, 150, 200 })
        {
            int p = pct;
            m.DropDownItems.Add(new ToolStripMenuItem($"{p}%", null, (_, _) => ApplySizePreset(p)));
        }
        return m;
    }

    private void BuildStatusBar()
    {
        _status.RenderMode = ToolStripRenderMode.System;
        _status.BackColor = SystemColors.Control;
        _lblVideo.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _lblAudio.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _lblVolume.BorderSides = ToolStripStatusLabelBorderSides.Right;
        var filler = new ToolStripStatusLabel { Spring = true, Text = string.Empty };
        _status.Items.AddRange(new ToolStripItem[] { _lblVideo, _lblAudio, _lblVolume, filler });
        _status.SizingGrip = true;
    }

    private void BuildContextMenu()
    {
        _ctx.Opening += (_, _) => UpdateChecks();

        // 使用頻度順: 画面切替系 → 撮影系 → 映像調整系 → ウィンドウ挙動系 → 終了
        _ctx.Items.Add(new ToolStripMenuItem(L.T("全画面表示"), null, (_, _) => ToggleFullscreen()));
        _cmiPip = new ToolStripMenuItem(L.T("ピクチャインピクチャ"), null, (_, _) => TogglePip());
        _ctx.Items.Add(_cmiPip);
        _cmiFreeze = new ToolStripMenuItem(L.T("一時停止 / 再開"), null, (_, _) => ToggleFreeze());
        _ctx.Items.Add(_cmiFreeze);
        _cmiMute = new ToolStripMenuItem(L.T("ミュート"), null, (_, _) => ToggleMute());
        _ctx.Items.Add(_cmiMute);
        _ctx.Items.Add(new ToolStripSeparator());

        _ctx.Items.Add(new ToolStripMenuItem(L.T("スナップショットを保存"), null, (_, _) => SaveSnapshot()));
        _ctx.Items.Add(new ToolStripMenuItem(L.T("スナップショットをコピー"), null, (_, _) => CopySnapshotToClipboard()));
        _ctx.Items.Add(new ToolStripSeparator());

        _ctx.Items.Add(BuildAspectSubmenu());
        _ctx.Items.Add(BuildSizeSubmenu());
        _cmiVideoModeRoot = new ToolStripMenuItem(L.T("映像モード (解像度/FPS)"));
        _cmiVideoModeRoot.DropDownOpening += (_, _) => RebuildVideoModeList(_cmiVideoModeRoot);
        _cmiVideoModeRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        _ctx.Items.Add(_cmiVideoModeRoot);
        _ctx.Items.Add(new ToolStripSeparator());

        _cmiBorderless = new ToolStripMenuItem(L.T("ウィンドウ枠を非表示"), null, (_, _) => ToggleBorderless());
        _cmiTopmost = new ToolStripMenuItem(L.T("常に前面に表示"), null, (_, _) => ToggleAlwaysOnTop());
        _cmiMenuBar = new ToolStripMenuItem(L.T("メニューバーを表示"), null, (_, _) => ToggleMenuBar());
        _cmiStatusBar = new ToolStripMenuItem(L.T("ステータスバーを表示"), null, (_, _) => ToggleStatusBar());
        _ctx.Items.Add(_cmiBorderless);
        _ctx.Items.Add(_cmiTopmost);
        _ctx.Items.Add(_cmiMenuBar);
        _ctx.Items.Add(_cmiStatusBar);
        _ctx.Items.Add(new ToolStripSeparator());
        _ctx.Items.Add(new ToolStripMenuItem(L.T("終了"), null, (_, _) => Close()));
        _canvas.ContextMenuStrip = _ctx;
    }

    // ---- Startup / shutdown ---------------------------------------------

    private void OnLoad(object? sender, EventArgs e)
    {
        Application.AddMessageFilter(this);
        SingleInstance.MarkWindow(Handle);   // lets a second launch find us

        // Command-line switches override the saved view state for this launch.
        var opts = Program.Options;
        if (opts.Borderless) _settings.Borderless = true;
        if (opts.Topmost) _settings.AlwaysOnTop = true;
        if (opts.Fullscreen) _settings.Fullscreen = true;
        if (opts.Volume is int vol) _settings.Volume = vol;
        _cliMode = opts.Mode;

        ApplyLoadedSettings();
        _video.Attach(_canvas.Handle); // force handle creation + give MF its window
        _savedMode = _settings.ToMode();

        // Device init is deferred to _startupTimer (see below): enumeration and
        // Start() can each block up to 5s on an MF timeout, which used to keep
        // the window from appearing at all — it read as "the app didn't start".
        _startingUp = true;
        _startupTimer.Start();

        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys(announce: false);

        // Restore how the last session was being viewed. Both can be set: a PiP
        // entered FROM fullscreen — EnterPip records the fullscreen state so
        // exiting PiP returns there.
        if (_settings.Fullscreen && !_isFullscreen) EnterFullscreen();
        if (_settings.Pip && !_isPip) EnterPip();

        UpdateStatus();
        UpdateChecks();
        _uiTimer.Start();
        _lastCursorPos = Cursor.Position;
        _lastCursorMoveTick = Environment.TickCount;
        _cursorTimer.Start();
        Log.Info($"OnLoad done: video={_video.CurrentDeviceName ?? "none"} audio={_audio.CurrentDeviceName ?? "none"} " +
                 $"borderless={_isBorderless} fs={_isFullscreen} pip={_isPip}");
    }

    /// <summary>
    /// Runs devices init once the window is already on screen. The resolution
    /// is only known after this completes, so PiP/layout are re-fit here too.
    /// </summary>
    private void OnStartupTimerTick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        if (IsDisposed) return;
        _startingUp = false;

        InitVideo();
        InitAudio();
        _audio.VolumePercent = _settings.Volume; // apply after the device starts
        if (Program.Options.Muted && _audio.IsRunning) _audio.Muted = true;

        // The resolution/aspect is only known now that the device has started —
        // re-fit whatever geometry depends on it.
        if (_isPip)
        {
            ApplyPipSize(anchorCorner: false);
            Location = PipCornerLocation();
        }
        LayoutCanvas();
        UpdateStatus();
        UpdateChecks();
        _canvas.Invalidate();

        if (SettingsStore.LastLoadFailed)
            ShowOsd(L.T("設定ファイルを読み込めませんでした。既定値で起動しています。"), OsdLongMilliseconds);

        AnnounceNewVersion();
        MaybeCheckForUpdatesOnStartup();
    }

    private void ApplyLoadedSettings()
    {
        _displayMode = (VideoDisplayMode)Math.Clamp(_settings.DisplayMode, 0, 3);
        _lockAspect = _settings.LockAspect;
        _isBorderless = _settings.Borderless;
        _alwaysOnTop = _settings.AlwaysOnTop;
        _audio.BufferMilliseconds = _settings.AudioBufferMs;
        _video.SetRotation(_settings.Rotation); // stored; applied when preview starts
        _video.SetMirror(_settings.Mirror);
        // A build before the 10% hover-opacity floor existed could have saved
        // 0 here; clamp so an old settings.json can't reintroduce the
        // unfindable-PiP state the floor exists to prevent.
        _settings.PipOpacityHover = Math.Clamp(_settings.PipOpacityHover, 10, 100);

        // Restore window placement (only if it lands on a visible screen).
        if (_settings.WindowW is int ww && _settings.WindowH is int hh && ww >= MinimumSize.Width && hh >= MinimumSize.Height)
        {
            var b = new Rectangle(_settings.WindowX ?? Location.X, _settings.WindowY ?? Location.Y, ww, hh);
            if (IsMostlyOnScreen(b))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = b;
            }
        }

        FormBorderStyle = FormBorderStyle.Sizable;
        _canvas.EnableEdgeResize = _isBorderless;
        RefreshFrame(); // hides chrome via WM_NCCALCSIZE when borderless
        TopMost = _alwaysOnTop;
        _menu.Visible = _settings.MenuVisible;
        _status.Visible = _settings.StatusVisible;
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    private static bool IsMostlyOnScreen(Rectangle b)
    {
        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle i = Rectangle.Intersect(s.WorkingArea, b);
            if (i.Width > 100 && i.Height > 100) return true;
        }
        return false;
    }

    private void SaveSettings()
    {
        _settings.DisplayMode = (int)_displayMode;
        _settings.LockAspect = _lockAspect;
        // While in PiP the window state is temporary — persist the pre-PiP state
        // so the restored session's "exit PiP" returns to something sensible.
        _settings.Borderless = _isPip ? _prePipBorderless : _isBorderless;
        _settings.AlwaysOnTop = _isPip ? _prePipTopmost : _alwaysOnTop;
        _settings.MenuVisible = _isFullscreen ? _savedMenuVisible : (_isPip ? _prePipMenu : _menu.Visible);
        _settings.StatusVisible = _isFullscreen ? _savedStatusVisible : (_isPip ? _prePipStatus : _status.Visible);
        _settings.Volume = _audio.VolumePercent;
        _settings.AudioBufferMs = _audio.BufferMilliseconds;
        _settings.Rotation = _video.Rotation;
        _settings.Mirror = _video.Mirror;
        _settings.Fullscreen = _isFullscreen || (_isPip && _prePipFullscreen);
        _settings.Pip = _isPip;
        _settings.SetMode(_savedMode);

        _settings.WindowMaximized = !_isPip && WindowState == FormWindowState.Maximized;
        Rectangle b = _isFullscreen ? _restoreBounds
            : _isPip ? _prePipBounds
            : (WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
        _settings.WindowX = b.X;
        _settings.WindowY = b.Y;
        _settings.WindowW = b.Width;
        _settings.WindowH = b.Height;

        SettingsStore.Save(_settings);
    }

    // The modal Retry/Cancel loops that used to live in InitVideo/InitAudio were
    // removed: the app already reconnects automatically on WM_DEVICECHANGE (see
    // OnDeviceChange below), so blocking the user with a question the app can
    // answer itself was pure friction — and since InitVideo and InitAudio each
    // asked it, a single missing capture card (which carries both video and
    // audio) meant answering the SAME question twice in a row. The empty canvas
    // state (OnCanvasPaint) and the hot-plug handler cover the "no device yet"
    // case without a dialog.
    private void InitVideo()
    {
        try
        {
            var devices = VideoEngine.EnumerateDevices();
            var pick = VideoEngine.PickPreferred(devices, _settings.DeviceKeyword);
            if (pick == null)
            {
                Log.Info("InitVideo: no video device found");
                return;
            }
            LayoutCanvas();               // give the canvas a valid size before Start
            _video.Start(pick, _cliMode ?? _savedMode);
            _currentVideoInfo = pick;
            LayoutCanvas();               // re-fit now that the resolution is known
        }
        catch (Exception ex)
        {
            Log.Info("InitVideo failed: " + ex.Message);
            ShowOsd(Errors.Describe(ex), OsdLongMilliseconds);
        }
    }

    private void InitAudio()
    {
        try
        {
            var devices = AudioEngine.EnumerateDevices();
            var pick = AudioEngine.PickPreferred(devices, _settings.DeviceKeyword);
            if (pick == null)
            {
                Log.Info("InitAudio: no audio device found");
                return;
            }
            _audio.Start(pick);
            _currentAudioInfo = pick;
        }
        catch (Exception ex)
        {
            Log.Info("InitAudio failed: " + ex.Message);
            ShowOsd(Errors.Describe(ex), OsdLongMilliseconds);
        }
    }

    // ---- Device hot-plug: auto-reconnect ---------------------------------

    /// <summary>Debounced WM_DEVICECHANGE handler: restart engines when their
    /// device reappears, stop them cleanly when it is yanked, and follow the
    /// system default audio output when it changes.</summary>
    private void OnDeviceChange()
    {
        if (IsDisposed) return;
        Log.Info($"OnDeviceChange: videoRunning={_video.IsRunning} audioRunning={_audio.IsRunning}");

        try // ---- video ----
        {
            var devices = VideoEngine.EnumerateDevices();
            bool present = _currentVideoInfo != null && devices.Any(d => d.Id == _currentVideoInfo.Id);
            if (_video.IsRunning && _currentVideoInfo != null && !present)
            {
                if (_frozen) ToggleFreeze();
                _video.Stop();
                _canvas.Invalidate();
                ShowOsd(L.T("映像デバイスが切断されました"), OsdLongMilliseconds);
            }
            if (!_video.IsRunning)
            {
                var pick = (present ? _currentVideoInfo : null)
                    ?? VideoEngine.PickPreferred(devices, _settings.DeviceKeyword);
                if (pick != null)
                {
                    try
                    {
                        _video.Start(pick, _cliMode ?? _savedMode);
                        _currentVideoInfo = pick;
                        LayoutCanvas();
                        ShowOsd(L.F("映像を再接続しました: {0}", pick.Name), OsdLongMilliseconds);
                    }
                    catch { /* device still settling — the next change event retries */ }
                }
            }
        }
        catch { /* enumeration hiccup — ignore */ }

        try // ---- audio ----
        {
            var devices = AudioEngine.EnumerateDevices();
            bool present = _currentAudioInfo != null && devices.Any(d => d.Id == _currentAudioInfo.Id);
            if (_audio.IsRunning &&
                (_audio.IsFaulted || _audio.DefaultRenderChanged() ||
                 (_currentAudioInfo != null && !present)))
            {
                _audio.Stop();
            }
            if (!_audio.IsRunning)
            {
                var pick = (present ? _currentAudioInfo : null)
                    ?? AudioEngine.PickPreferred(devices, _settings.DeviceKeyword);
                if (pick != null)
                {
                    try
                    {
                        _audio.Start(pick);
                        _currentAudioInfo = pick;
                        ShowOsd(L.F("音声を再接続しました: {0}", pick.Name), OsdLongMilliseconds);
                    }
                    catch { /* retried on the next change event */ }
                }
            }
        }
        catch { /* ignore */ }

        UpdateStatus();
        UpdateChecks();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // An update has already saved settings; re-saving here would capture the
        // torn-down state (no device, window mid-close) over the real settings.
        if (!_skipSaveOnClose)
        {
            try { SaveSettings(); } catch { /* ignore */ }
        }
        _uiTimer.Stop();
        _osdTimer.Stop();
        _devTimer.Stop();
        _burstTimer.Stop();
        _pipHoverTimer.Stop();
        _resumeTimer.Stop();
        _resizeGuard.Stop();
        _cursorTimer.Stop();
        _settingsSaveTimer.Stop();
        _startupTimer.Stop();
        try { if (IsHandleCreated) SingleInstance.UnmarkWindow(Handle); } catch { /* ignore */ }
        ShowCursorIfHidden();   // never leave the user without a pointer
        try { UnregisterGlobalHotkeys(); } catch { /* ignore */ }
        SetThreadExecutionState(EsContinuous); // release the sleep inhibition
        Application.RemoveMessageFilter(this);
        try { _video.Dispose(); } catch { /* ignore */ }
        try { _audio.Dispose(); } catch { /* ignore */ }
    }

    // ---- Rendering host --------------------------------------------------

    /// <summary>
    /// Size the render window to the aspect-fit (or stretch / 1:1) rectangle
    /// between the menu and status bars. The black form background forms the
    /// letterbox, and resizing the window resizes the video with it.
    /// </summary>
    /// <param name="settleMs">Delay before the video rect is pushed to MF. Used
    /// by the fullscreen/PiP transitions so the swap-chain resize happens after
    /// the window-mode change has settled rather than in the middle of it.</param>
    private void LayoutCanvas(int settleMs = 0)
    {
        int menuH = _menu.Visible ? _menu.Height : 0;
        int statusH = _status.Visible ? _status.Height : 0;
        var area = new Rectangle(0, menuH, ClientSize.Width,
            Math.Max(0, ClientSize.Height - menuH - statusH));
        if (area.Width <= 0 || area.Height <= 0) return;

        // Canvas fills the whole area below the menu; MF scales the video into the
        // aspect-fit destination rectangle (letterboxed by the black background).
        if (_canvas.Bounds != area) _canvas.Bounds = area;
        if (_video.IsRunning)
        {
            ClampPan(); // keep the zoom viewport valid after any size change
            if (_inSizeMove)
            {
                // Live border-drag: rate-limit and skip the black border clear so
                // the stream keeps playing instead of flashing black/stale frames.
                int now = Environment.TickCount;
                if (now - _lastLiveRectTick >= 50)
                {
                    _lastLiveRectTick = now;
                    _video.SetVideoRect(ComputeDest(_canvas.ClientSize), clearBorder: false);
                }
            }
            else
            {
                _video.SetVideoRect(ComputeDest(_canvas.ClientSize), clearBorder: true, settleMs: settleMs);
                // Window-mode changes are where UpdateVideo has been seen wedging;
                // watch this one through to completion.
                if (settleMs > 0)
                {
                    _restartAttempts = 0;   // fresh transition, fresh retry budget
                    _resizeGuard.Stop();
                    _resizeGuard.Start();
                }
            }
        }
        if (_frozen)
        {
            Rectangle freezeArea = FreezeBounds();
            if (_freezeBox.Bounds != freezeArea) _freezeBox.Bounds = freezeArea;
        }
        PositionOsd();
    }

    private Rectangle ComputeDest(Size client)
    {
        Rectangle b = ComputeDestBase(client);
        if (_zoom <= 1.001) return b;
        int w = (int)Math.Round(b.Width * _zoom);
        int h = (int)Math.Round(b.Height * _zoom);
        return new Rectangle((client.Width - w) / 2 + _panX, (client.Height - h) / 2 + _panY, w, h);
    }

    private Rectangle ComputeDestBase(Size client)
    {
        Size res = _video.DisplayResolution; // width/height swap under 90°/270°
        if (_displayMode == VideoDisplayMode.Stretch || res.Width <= 0 || res.Height <= 0)
            return new Rectangle(0, 0, client.Width, client.Height);
        if (_displayMode == VideoDisplayMode.OneToOne && res.Width <= client.Width && res.Height <= client.Height)
            return new Rectangle((client.Width - res.Width) / 2, (client.Height - res.Height) / 2, res.Width, res.Height);
        if (_displayMode == VideoDisplayMode.IntegerScale)
        {
            // Whole-number scale factor → source pixels map to exact n×n blocks,
            // so nothing is blurred by fractional GPU interpolation. Falls back
            // to aspect-fit when the window is smaller than the source.
            int n = Math.Min(client.Width / res.Width, client.Height / res.Height);
            if (n >= 1)
            {
                int iw = res.Width * n, ih = res.Height * n;
                return new Rectangle((client.Width - iw) / 2, (client.Height - ih) / 2, iw, ih);
            }
        }
        double scale = Math.Min((double)client.Width / res.Width, (double)client.Height / res.Height);
        int w = (int)Math.Round(res.Width * scale);
        int h = (int)Math.Round(res.Height * scale);
        return new Rectangle((client.Width - w) / 2, (client.Height - h) / 2, w, h);
    }

    // ---- Rotation / mirror ----------------------------------------------

    private void SetRotationDeg(int deg)
    {
        if (!_video.SetRotation(deg))
        {
            ShowOsd(L.T("この環境では回転に対応していません"), OsdLongMilliseconds);
            return;
        }
        ShowOsd(deg == 0 ? L.T("回転なし") : L.F("回転 {0}°", deg));
        if (_lockAspect) ApplyLockedAspect();
        UpdateChecks();
        LayoutCanvas();
    }

    private void ToggleMirror()
    {
        if (!_video.SetMirror(!_video.Mirror))
        {
            ShowOsd(L.T("この環境では反転に対応していません"), OsdLongMilliseconds);
            return;
        }
        ShowOsd(_video.Mirror ? L.T("左右反転: オン") : L.T("左右反転: オフ"));
        UpdateChecks();
    }

    private void PositionOsd()
    {
        if (_osd.Visible)
            _osd.Location = new Point(_canvas.Left + 12, _canvas.Top + 12);
    }

    // ---- Idle cursor hiding ---------------------------------------------

    /// <summary>
    /// Hide the pointer after <see cref="CursorHideMs"/> of stillness over the
    /// video, and bring it straight back on any movement. Only hides while this
    /// window is the active one and no menu is open, so the pointer can never be
    /// lost over a dialog or another application.
    /// </summary>
    private void UpdateCursorVisibility()
    {
        if (!_settings.CursorAutoHide)
        {
            ShowCursorIfHidden();
            return;
        }

        Point pos = Cursor.Position;
        if (pos != _lastCursorPos)
        {
            _lastCursorPos = pos;
            _lastCursorMoveTick = Environment.TickCount;
            ShowCursorIfHidden();
            return;
        }

        bool overVideo = _canvas.Visible
            && _canvas.RectangleToScreen(_canvas.ClientRectangle).Contains(pos);
        bool eligible = overVideo
            && ActiveForm == this          // not another app, not a modal dialog
            && !_ctx.Visible               // context menu open → keep it usable
            && !_dragging && !_panning;

        int hideMs = Math.Clamp(_settings.CursorHideSeconds, 1, 60) * 1000;
        if (eligible && Environment.TickCount - _lastCursorMoveTick >= hideMs)
            HideCursorIfShown();
        else if (!eligible)
            ShowCursorIfHidden();
    }

    private void ToggleCursorAutoHide()
    {
        _settings.CursorAutoHide = !_settings.CursorAutoHide;
        if (!_settings.CursorAutoHide) ShowCursorIfHidden();
        _lastCursorMoveTick = Environment.TickCount;
        UpdateChecks();
        ShowOsd(_settings.CursorAutoHide
            ? L.F("カーソル自動非表示: {0}秒", _settings.CursorHideSeconds)
            : L.T("カーソル自動非表示: オフ"));
    }

    private void SetCursorHideSeconds(int seconds)
    {
        _settings.CursorHideSeconds = Math.Clamp(seconds, 1, 60);
        // Choosing a time implies wanting the feature on.
        _settings.CursorAutoHide = true;
        _lastCursorMoveTick = Environment.TickCount;
        UpdateChecks();
        ShowOsd(L.F("カーソル自動非表示: {0}秒", _settings.CursorHideSeconds));
    }

    private void HideCursorIfShown()
    {
        if (_cursorHidden) return;
        _cursorHidden = true;
        Cursor.Hide();      // refcounted — must stay paired with Show()
    }

    private void ShowCursorIfHidden()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        Cursor.Show();
    }

    /// <summary>Watch a just-issued resize; if MF's UpdateVideo is wedged, rebuild
    /// the preview so the video actually reaches the new size. See _resizeGuard.
    /// </summary>
    private void CheckResizeStuck()
    {
        if (!_video.IsRunning) { _resizeGuard.Stop(); return; }
        if (!_video.IsRectUpdateStuck(ResizeStuckMs))
        {
            // Nothing in flight for a while → the resize landed; stop watching.
            if (!_video.IsRectUpdateStuck(0)) _resizeGuard.Stop();
            return;
        }
        _resizeGuard.Stop();
        if (_restartAttempts >= MaxRestartAttempts)
        {
            Log.Info("resize guard: wedged again after restarts — giving up for this transition");
            return;
        }
        _restartAttempts++;
        Log.Info($"resize guard: UpdateVideo wedged — restarting preview (attempt {_restartAttempts})");
        RestartPreview();
        // Watch the restarted session too: its first resize can wedge as well.
        _resizeGuard.Start();
    }

    /// <summary>Rebuild the capture preview to recover from a wedged UpdateVideo.
    /// The render window MUST be recreated first: the abandoned session's
    /// DirectComposition target is still bound to the old HWND, so reusing it
    /// fails with DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED.</summary>
    private void RestartPreview()
    {
        if (_currentVideoInfo == null) return;
        try
        {
            _canvas.ResetHandle();
            _video.Attach(_canvas.Handle);
            _video.Start(_currentVideoInfo, _cliMode ?? _savedMode);
            LayoutCanvas();
            Log.Info("resize guard: preview restarted on a fresh window");
        }
        catch (Exception ex)
        {
            Log.Info("resize guard: restart failed — " + ex.Message);
        }
        UpdateStatus();
    }

    /// <summary>Grow/shrink the window height so the video area stays the same
    /// when a menu/status bar is shown/hidden (no black band appears).
    /// Uses SetOuterForClient: WinForms' ClientSize setter computes the outer
    /// size from the STYLE (assuming normal chrome), which is wrong under our
    /// borderless WM_NCCALCSIZE and leaves stale bands at the right/bottom.</summary>
    private void GrowClientHeight(int delta)
    {
        if (WindowState != FormWindowState.Normal || delta == 0) return;
        SetOuterForClient(new Size(ClientSize.Width,
            Math.Max(MinimumSize.Height, ClientSize.Height + delta)));
    }

    /// <summary>Note that something worth persisting changed; the write happens
    /// a few seconds later so a burst of changes costs one save.</summary>
    private void MarkSettingsDirty()
    {
        if (_skipSaveOnClose) return;   // an update is mid-flight
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void ShowOsd(string text, int? ms = null)
    {
        _lastSavedSnapshot = null;   // only a "saved" notice is clickable
        _osd.ShowText(text);
        PositionOsd();
        _osdTimer.Stop();
        // Set on every call (not just when ms is given): otherwise a long
        // message would leave the timer at 4000ms and leak that duration into
        // whatever short status flip shows next.
        _osdTimer.Interval = ms ?? OsdMilliseconds;
        _osdTimer.Start();
    }

    // ---- Volume (hover + wheel) -----------------------------------------

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel) return false;

        Rectangle screenRect = _canvas.RectangleToScreen(_canvas.ClientRectangle);
        if (!screenRect.Contains(Cursor.Position)) return false;

        int wparam = (int)(m.WParam.ToInt64() & 0xFFFFFFFF);
        short delta = (short)((wparam >> 16) & 0xFFFF);
        int steps = delta / 120;
        if (steps == 0) return true;

        if ((ModifierKeys & Keys.Control) != 0)
        {
            if (_video.IsRunning) SetZoom(_zoom + steps * 0.25);
            return true;
        }

        if (!_audio.IsRunning) return false;
        NudgeVolume(steps * VolumeStep);
        return true;
    }

    /// <summary>Shared by the wheel and the arrow keys.</summary>
    private void NudgeVolume(int delta)
    {
        if (!_audio.IsRunning) { ShowOsd(L.T("音声がありません")); return; }
        if (_audio.Muted) { _audio.Muted = false; UpdateChecks(); } // adjusting volume unmutes
        int newVol = Math.Clamp(_audio.VolumePercent + delta, 0, AudioEngine.MaxVolumePercent);
        _audio.VolumePercent = newVol;
        ShowOsd(L.F("音量 {0}%", newVol));
        UpdateStatus();
        MarkSettingsDirty();
    }

    // ---- Mute ------------------------------------------------------------

    /// <summary>Push the capture endpoint's own level to 100%. This is gain
    /// applied before any software processing, so it is the one way to get
    /// louder audio with no added distortion at all.</summary>
    private void MaximizeCaptureLevel()
    {
        if (!_audio.IsRunning) { ShowOsd(L.T("音声がありません")); return; }
        int before = _audio.CaptureLevelPercent;
        if (before < 0)
        {
            ShowOsd(L.T("このデバイスは入力レベルを変更できません"), OsdLongMilliseconds);
            return;
        }
        if (before >= 100)
        {
            ShowOsd(L.T("入力レベルは既に最大です"));
            return;
        }

        // This is a Windows-wide recording-level change, not an app setting: it
        // affects every other application using this device and survives YuCap
        // exiting. Ask first, and remember the previous value so it can be put
        // back via "入力レベルを元に戻す".
        if (MessageBox.Show(this,
                L.T("入力レベルを最大にしますか？\n\nWindows の録音デバイスの音量を 100% に変更します。\nこの設定は他のアプリにも影響し、YuCap を終了しても元に戻りません。\n（メニューの「入力レベルを元に戻す」で戻せます）"),
                "YuCap", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _captureLevelBefore = before;
        _audio.CaptureLevelPercent = 100;
        ShowOsd(L.F("入力レベル {0}% → 100%", before));
        UpdateStatus();
        UpdateChecks();
    }

    /// <summary>Undo the Windows-wide change made by MaximizeCaptureLevel.</summary>
    private void RestoreCaptureLevel()
    {
        if (_captureLevelBefore < 0 || !_audio.IsRunning) return;
        _audio.CaptureLevelPercent = _captureLevelBefore;
        ShowOsd(L.F("入力レベルを {0}% に戻しました", _captureLevelBefore));
        _captureLevelBefore = -1;
        UpdateStatus();
        UpdateChecks();
    }

    private void ToggleMute()
    {
        if (!_audio.IsRunning) { ShowOsd(L.T("音声がありません")); return; }
        _audio.Muted = !_audio.Muted;
        ShowOsd(_audio.Muted ? L.T("ミュート") : L.F("ミュート解除 ({0}%)", _audio.VolumePercent));
        UpdateChecks();
        UpdateStatus();
    }

    // ---- Freeze frame ----------------------------------------------------

    private void ToggleFreeze()
    {
        if (_frozen)
        {
            _frozen = false;
            _freezeIsPhoto = false;
            _freezeBox.Visible = false;
            Image? old = _freezeBox.Image;
            _freezeBox.Image = null;
            old?.Dispose();
            if (!_mutedBeforeFreeze && _audio.IsRunning) _audio.Muted = false;
            ShowOsd(L.T("再開"));
        }
        else
        {
            // Prefer the capture engine's own photo sink: it is source-resolution
            // video pixels only, so it cannot pick up whatever else happens to be
            // on screen over/behind the window (another overlapping window, or a
            // translucent PiP background) the way the compositor screen copy can.
            Bitmap? still = _video.PhotoSnapshot();
            _freezeIsPhoto = still != null;
            if (still == null)
                still = _video.Snapshot(cropToVideo: false); // fallback: includes letterbox → 1:1 overlay
            if (still == null) { ShowOsd(L.T("映像がありません")); return; }
            _freezeBox.Image = still;
            _freezeBox.Bounds = FreezeBounds();
            _freezeBox.Visible = true;
            _freezeBox.BringToFront();
            _osd.BringToFront();
            _frozen = true;
            _mutedBeforeFreeze = _audio.Muted;
            if (_audio.IsRunning) _audio.Muted = true; // 仕様: 一時停止中は音声も停止
            ShowOsd(L.T("一時停止"));
        }
        UpdateChecks();
        UpdateStatus();
    }

    /// <summary>Where the freeze overlay belongs, in form coordinates. The
    /// screen-copy fallback already includes the letterbox, so it maps 1:1 onto
    /// the whole canvas; the photo-sink still is video pixels only and must be
    /// placed at the video's own destination rectangle instead.</summary>
    private Rectangle FreezeBounds()
    {
        if (!_freezeIsPhoto) return _canvas.Bounds;
        Rectangle dest = ComputeDest(_canvas.ClientSize);
        return new Rectangle(dest.X + _canvas.Location.X, dest.Y + _canvas.Location.Y, dest.Width, dest.Height);
    }


    // ---- Burst snapshots -------------------------------------------------

    private void ShowBurstDialog()
    {
        if (_burstTimer.Enabled)
        {
            _burstTimer.Stop();
            ShowOsd(L.T("連写を停止しました"), OsdLongMilliseconds);
            return;
        }

        using var dlg = new Form
        {
            Text = L.T("連写スナップショット"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(300, 130),
            // (7, 15) is the metric of the default Segoe UI 9pt these layouts
            // were drawn against at 100% — WinForms then scales every
            // Location/Size by the same factor as the font, so the layout
            // still holds together at 125-200% display scaling.
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };
        var lblInt = new Label { Text = L.T("間隔 (秒):"), AutoSize = true, Location = new Point(16, 20) };
        var numInt = new NumericUpDown
        {
            Minimum = 0.5m, Maximum = 3600, DecimalPlaces = 1, Increment = 0.5m,
            Value = 5, Location = new Point(110, 16), Width = 100,
        };
        var lblCnt = new Label { Text = L.T("枚数:"), AutoSize = true, Location = new Point(16, 56) };
        var numCnt = new NumericUpDown
        {
            Minimum = 1, Maximum = 999, Value = 10,
            Location = new Point(110, 52), Width = 100,
        };
        var ok = new Button { Text = L.T("開始"), DialogResult = DialogResult.OK, Location = new Point(104, 92), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(195, 92), Width = 90 };
        dlg.Controls.AddRange(new Control[] { lblInt, numInt, lblCnt, numCnt, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _burstTotal = (int)numCnt.Value;
        _burstDone = 0;
        _burstTimer.Interval = Math.Max(100, (int)(numInt.Value * 1000));
        _burstTimer.Start();
        ShowOsd(L.F("連写を開始します ({0}枚 / {1}秒間隔)", _burstTotal, numInt.Value), OsdLongMilliseconds);
    }

    private void BurstTick()
    {
        // An unattended timer must stop on the FIRST failure: whatever broke
        // one save (full disk, a removed/unwritable save folder) will break
        // every later shot too, so continuing just repeats a modal dialog on
        // every tick — an endless storm the user cannot dismiss ahead of.
        if (SaveSnapshotCore(out _, out string? error))
        {
            _burstDone++;
            ShowOsd(L.F("連写 {0}/{1}", _burstDone, _burstTotal));
        }
        else
        {
            _burstTimer.Stop();
            ShowOsd(L.T("連写を中止しました（保存に失敗）"), OsdLongMilliseconds);
            MessageBox.Show(this, L.F("保存に失敗しました。\n\n{0}", error), "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateChecks();
            return;
        }
        if (_burstDone >= _burstTotal || !_video.IsRunning) { _burstTimer.Stop(); UpdateChecks(); }
    }

    // ---- Startup registration -------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// True only when the registered command line still points at THIS
    /// executable. Just checking "the value exists" left the menu showing
    /// registered after the user moved/reinstalled YuCap to a new path — the
    /// stale entry pointed Windows at a file that no longer exists there.
    /// </summary>
    private static bool IsStartupRegistered()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            string? value = key?.GetValue("YuCap") as string;
            return value != null &&
                string.Equals(value, $"\"{Application.ExecutablePath}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void ToggleStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (IsStartupRegistered()) key.DeleteValue("YuCap", false);
            else key.SetValue("YuCap", $"\"{Application.ExecutablePath}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _miStartup.Checked = IsStartupRegistered();
    }

    // ---- Preferred-device keyword ---------------------------------------

    private void ShowDeviceKeywordSettings()
    {
        using var dlg = new Form
        {
            Text = L.T("優先デバイス設定"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(340, 130),
            // (7, 15) is the metric of the default Segoe UI 9pt these layouts
            // were drawn against at 100% — WinForms then scales every
            // Location/Size by the same factor as the font, so the layout
            // still holds together at 125-200% display scaling.
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };
        var lbl = new Label { Text = L.T("キーワード:"), AutoSize = true, Location = new Point(16, 20) };
        var txt = new TextBox { Text = _settings.DeviceKeyword, Location = new Point(110, 16), Width = 210 };
        var hint = new Label
        {
            Text = L.T("デバイス名にこの語を含む機器を起動時に自動選択します。\n（既定: JVA14）"),
            AutoSize = true,
            // SystemColors.GrayText (not Color.Gray, ~2.9:1) is the theme's
            // intended hint colour and reads at a proper contrast ratio.
            ForeColor = SystemColors.GrayText,
            Location = new Point(16, 50),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(144, 92), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(235, 92), Width = 90 };
        dlg.Controls.AddRange(new Control[] { lbl, txt, hint, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.DeviceKeyword = string.IsNullOrWhiteSpace(txt.Text) ? "JVA14" : txt.Text.Trim();
        SaveSettings();
    }

    /// <summary>
    /// Act on switches passed to a second launch, which forwarded them here
    /// rather than starting a rival instance. Only the view-affecting ones make
    /// sense to apply live; device/mode changes would disrupt a running session.
    /// </summary>
    private void ApplyForwardedArgs(string[] args)
    {
        Log.Info("forwarded args: " + string.Join(" ", args));
        foreach (string a in args)
        {
            switch (a.ToLowerInvariant())
            {
                case "--fullscreen": if (!_isFullscreen) EnterFullscreen(); break;
                case "--borderless": if (!_isBorderless) ToggleBorderless(); break;
                case "--topmost": if (!_alwaysOnTop) ToggleAlwaysOnTop(); break;
                case "--muted": if (_audio.IsRunning && !_audio.Muted) ToggleMute(); break;
            }
        }
        UpdateChecks();
        ShowOsd(L.T("既に起動しています"));
    }

    // ---- Update ----------------------------------------------------------

    // ---- Language --------------------------------------------------------

    /// <summary>
    /// Switch language and rebuild the menus in place. The menus are built in
    /// code, so re-running the builders against the new language is enough —
    /// no restart, which is what this used to demand.
    /// </summary>
    private void SetLanguage(string lang)
    {
        if (_settings.Language == lang) return;
        _settings.Language = lang;
        L.English = lang == "en";
        SaveSettings();
        RebuildMenus();
        ShowOsd(lang == "en" ? "Language: English" : "言語: 日本語");
    }

    /// <summary>Discard and re-create every menu so new-language text is used.</summary>
    private void RebuildMenus()
    {
        bool menuWasVisible = _menu.Visible;

        // The builders append to these collections, so clear them first —
        // otherwise every switch would leave the previous language's items
        // behind and the check-state lists would grow without bound.
        _menu.Items.Clear();
        _ctx.Items.Clear();
        _aspectItems.Clear();
        _rotationItems.Clear();
        _pipIdleItems.Clear();
        _pipHoverItems.Clear();
        _pipSizeItems.Clear();
        _pipPosItems.Clear();
        _cursorSecItems.Clear();

        BuildMenu();
        BuildContextMenu();
        MainMenuStrip = _menu;
        _menu.Visible = menuWasVisible;

        UpdateChecks();
        UpdateStatus();
        LayoutCanvas();
    }

    // ---- Sleep inhibition ------------------------------------------------

    /// <summary>Keep the display/system awake while video is playing, like a
    /// media player. Cleared when the preview stops or the app closes.</summary>
    private void UpdatePowerState()
    {
        bool want = _video.IsRunning;
        if (want == _keepAwake) return;
        _keepAwake = want;
        SetThreadExecutionState(want
            ? EsContinuous | EsDisplayRequired | EsSystemRequired
            : EsContinuous);
    }

    // ---- Digital zoom / pan ---------------------------------------------

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 1.0, 4.0);
        if (_zoom <= 1.001)
        {
            _zoom = 1.0;
            _panX = _panY = 0;
        }
        ClampPan();
        LayoutCanvas();
        ShowOsd(L.F("ズーム {0}%", (int)Math.Round(_zoom * 100)));
    }

    private void ResetZoom() => SetZoom(1.0);

    private void ClampPan()
    {
        if (_zoom <= 1.001) { _panX = _panY = 0; return; }
        Size client = _canvas.ClientSize;
        Rectangle b = ComputeDestBase(client);
        int w = (int)Math.Round(b.Width * _zoom);
        int h = (int)Math.Round(b.Height * _zoom);
        int mx = Math.Max(0, (w - client.Width) / 2);
        int my = Math.Max(0, (h - client.Height) / 2);
        _panX = Math.Clamp(_panX, -mx, mx);
        _panY = Math.Clamp(_panY, -my, my);
    }

    // ---- Display mode / lock aspect -------------------------------------

    private void SetDisplayMode(VideoDisplayMode mode)
    {
        _displayMode = mode;
        UpdateChecks();
        LayoutCanvas();
    }

    private void ToggleLockAspect()
    {
        _lockAspect = !_lockAspect;
        UpdateChecks();
        if (_lockAspect) ApplyLockedAspect();
    }

    private void ApplyLockedAspect()
    {
        Size res = _video.DisplayResolution;
        if (res.Width <= 0 || res.Height <= 0 || _isFullscreen) return;
        if (WindowState != FormWindowState.Normal) return;

        int chrome = ChromeHeight();
        int videoW = _canvas.ClientSize.Width;
        int videoH = (int)Math.Round((double)videoW * res.Height / res.Width);
        SetOuterForClient(new Size(videoW, videoH + chrome));
    }

    private int ChromeHeight() =>
        (_menu.Visible ? _menu.Height : 0) + (_status.Visible ? _status.Height : 0);

    // ---- Window size presets --------------------------------------------

    private void ApplySizePreset(int pct)
    {
        if (_isFullscreen) ExitFullscreen();
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

        Size res = _video.DisplayResolution;
        if (res.Width <= 0 || res.Height <= 0)
        {
            ShowOsd(L.T("解像度が未確定です"));
            return;
        }

        int videoW = (int)Math.Round(res.Width * pct / 100.0);
        int videoH = (int)Math.Round(res.Height * pct / 100.0);

        // A preset on a large source (e.g. 200% of a 4K signal asks for
        // 7680x4320) can exceed the screen, leaving the window partly
        // off-screen and undraggable back. Clamp to the working area, keeping
        // the video's aspect ratio, and say so when that changed the request.
        int chrome = ChromeHeight();
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        int maxClientW = wa.Width;
        int maxClientH = Math.Max(1, wa.Height - chrome);
        bool clamped = false;
        if (videoW > maxClientW || videoH > maxClientH)
        {
            double scale = Math.Min((double)maxClientW / videoW, (double)maxClientH / videoH);
            videoW = Math.Max(1, (int)Math.Floor(videoW * scale));
            videoH = Math.Max(1, (int)Math.Floor(videoH * scale));
            clamped = true;
        }

        SetOuterForClient(new Size(videoW, videoH + chrome));
        ShowOsd(clamped
            ? $"{pct}% ({videoW}x{videoH}) — {L.T("画面に収まるサイズに調整しました")}"
            : $"{pct}% ({videoW}x{videoH})");
    }

    // ---- Fullscreen ------------------------------------------------------

    private void OnCanvasDoubleClick(object? sender, EventArgs e)
    {
        Log.Info("double-click → toggle fullscreen");
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        Log.Info("EnterFullscreen: begin");
        // PiP's opacity/topmost/small-window state must not leak into fullscreen.
        if (_isPip) ExitPip();
        // The second click of the fullscreen double-click also started a drag;
        // cancel it so mouse movement can't drag the fullscreen window around.
        _dragging = false;
        _isFullscreen = true;
        _savedMenuVisible = _menu.Visible;
        _savedStatusVisible = _status.Visible;
        _restoreWindowState = WindowState;
        _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        _menu.Visible = false;
        _status.Visible = false;
        Log.Info("EnterFullscreen: style set, applying bounds");
        Watchdog.Mark("EnterFullscreen/Bounds");
        Bounds = Screen.FromControl(this).Bounds;
        TopMost = true;
        Watchdog.Done();
        Log.Info("EnterFullscreen: bounds set, layouting");
        LayoutCanvas(ModeChangeSettleMs);
        Log.Info("EnterFullscreen: done (video rect deferred)");
    }

    private void ExitFullscreen()
    {
        Log.Info("ExitFullscreen: begin");
        _isFullscreen = false;
        _menu.Visible = _savedMenuVisible;
        _status.Visible = _savedStatusVisible;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = _restoreBounds;
        WindowState = _restoreWindowState;
        TopMost = _alwaysOnTop;
        RefreshFrame();
        UpdateChecks();

        PerformLayout();
        _status.Invalidate(true);
        _menu.Invalidate(true);
        Refresh();
        LayoutCanvas(ModeChangeSettleMs);
        Log.Info("ExitFullscreen: done (video rect deferred)");
    }

    // ---- Borderless / always-on-top / menu / status ---------------------

    private void ToggleBorderless()
    {
        // Capture the video (client) area to preserve BEFORE changing the frame,
        // while ClientSize is still accurate for the current frame style.
        bool normal = !_isFullscreen && WindowState == FormWindowState.Normal;
        Size wantClient = ClientSize;

        _isBorderless = !_isBorderless;
        _canvas.EnableEdgeResize = _isBorderless;
        if (!_isFullscreen)
        {
            RefreshFrame();
            // A FRAMECHANGED with no size change does NOT emit WM_SIZE, so WinForms'
            // cached ClientSize goes stale while the real client area changed by the
            // border/caption size. Resize the outer window explicitly so WM_SIZE
            // fires and the video area ends up exactly wantClient (no black band).
            if (normal) SetOuterForClient(wantClient);
        }
        UpdateChecks();
        LayoutCanvas();
    }

    /// <summary>Resize the outer window so the client (video) area equals
    /// <paramref name="wantClient"/> under the current frame style. This is the
    /// single sizing path — Form.ClientSize's own setter derives the outer size
    /// from the window STYLE, which is wrong for our borderless WM_NCCALCSIZE
    /// (client == whole window) and produces right/bottom letterbox bands.</summary>
    private void SetOuterForClient(Size wantClient)
    {
        if (!IsHandleCreated) { ClientSize = wantClient; return; }
        int w, h;
        if (_isBorderless)
        {
            // Our WM_NCCALCSIZE makes the client fill the whole window.
            w = wantClient.Width;
            h = wantClient.Height;
        }
        else
        {
            var rc = new Rect { Left = 0, Top = 0, Right = wantClient.Width, Bottom = wantClient.Height };
            // DPI-aware variant: plain AdjustWindowRectEx assumes 96 DPI and gives
            // wrong caption/border sizes under display scaling (PerMonitorV2).
            AdjustWindowRectExForDpi(ref rc, GetWindowLong(Handle, GwlStyle), false,
                GetWindowLong(Handle, GwlExStyle), GetDpiForWindow(Handle));
            w = rc.Right - rc.Left;
            h = rc.Bottom - rc.Top;
        }
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h, SwpNoMove | SwpNoZOrder | SwpNoActivate);
    }

    private void ToggleAlwaysOnTop()
    {
        _alwaysOnTop = !_alwaysOnTop;
        if (!_isFullscreen)
            TopMost = _alwaysOnTop;
        UpdateChecks();
    }

    private void ToggleMenuBar()
    {
        if (_menu.Visible)
        {
            int h = _menu.Height;                 // read while visible
            _menu.Visible = false;
            if (_isFullscreen) _savedMenuVisible = false; else GrowClientHeight(-h);
        }
        else
        {
            _menu.Visible = true;
            if (_isFullscreen) _savedMenuVisible = true; else GrowClientHeight(_menu.Height);
        }
        UpdateChecks();
        LayoutCanvas();
    }

    private void ToggleStatusBar()
    {
        if (_status.Visible)
        {
            int h = _status.Height;
            _status.Visible = false;
            if (_isFullscreen) _savedStatusVisible = false; else GrowClientHeight(-h);
        }
        else
        {
            _status.Visible = true;
            if (_isFullscreen) _savedStatusVisible = true; else GrowClientHeight(_status.Height);
        }
        UpdateChecks();
        LayoutCanvas();
    }

    // ---- Device / mode switching ----------------------------------------

    private void RebuildVideoDeviceList()
    {
        _videoDevicesRoot.DropDownItems.Clear();
        var devices = VideoEngine.EnumerateDevices();
        if (devices.Count == 0)
        {
            _videoDevicesRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
            return;
        }
        foreach (var d in devices)
        {
            var info = d;
            var item = new ToolStripMenuItem(info.Name)
            {
                Checked = string.Equals(info.Name, _video.CurrentDeviceName, StringComparison.Ordinal),
            };
            item.Click += (_, _) => SwitchVideoDevice(info);
            _videoDevicesRoot.DropDownItems.Add(item);
        }
    }

    private void RebuildAudioDeviceList()
    {
        _audioDevicesRoot.DropDownItems.Clear();
        var devices = AudioEngine.EnumerateDevices();
        if (devices.Count == 0)
        {
            _audioDevicesRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
            return;
        }
        foreach (var d in devices)
        {
            var info = d;
            var item = new ToolStripMenuItem(info.Name)
            {
                Checked = string.Equals(info.Name, _audio.CurrentDeviceName, StringComparison.Ordinal),
            };
            item.Click += (_, _) => SwitchAudioDevice(info);
            _audioDevicesRoot.DropDownItems.Add(item);
        }
    }

    private void SwitchVideoDevice(VideoDeviceInfo info)
    {
        if (_frozen) ToggleFreeze();
        try
        {
            _video.Start(info, _savedMode);
            _currentVideoInfo = info;
            LayoutCanvas();
            ShowOsd(L.F("映像: {0}", info.Name));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("映像デバイスの切り替えに失敗しました。\n\n{0}", ex.Message),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RecoverVideo(); // best-effort: bring the previous device back
        }
        UpdateStatus();
    }

    /// <summary>After a failed switch (Start() stops the engine first), try to
    /// restart the last-known-good device so the preview doesn't stay dead.</summary>
    private void RecoverVideo()
    {
        if (_currentVideoInfo == null || _video.IsRunning) return;
        try
        {
            _video.Start(_currentVideoInfo, _savedMode);
            LayoutCanvas();
        }
        catch { /* the error was already reported; leave the "映像なし" canvas */ }
    }

    private void RebuildVideoModeList(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();

        var auto = new ToolStripMenuItem(L.T("自動 (最大解像度)"), null, (_, _) => ClearModePreference())
        {
            Checked = _savedMode == null,
        };
        root.DropDownItems.Add(auto);

        var modes = _video.GetModes();
        if (modes.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem(L.T("（利用可能なモードなし）")) { Enabled = false });
            return;
        }
        root.DropDownItems.Add(new ToolStripSeparator());

        var cur = _video.CurrentMode;
        foreach (var m in modes)
        {
            var mode = m;
            var item = new ToolStripMenuItem($"{mode.Width}x{mode.Height}  {mode.Compression}  {mode.Fps}fps")
            {
                Checked = _savedMode != null && mode.Equals(cur),
            };
            item.Click += (_, _) => ApplyVideoMode(mode);
            root.DropDownItems.Add(item);
        }
    }

    private void ApplyVideoMode(CaptureMode mode)
    {
        if (_currentVideoInfo == null)
        {
            ShowOsd(L.T("映像がありません"));
            return;
        }
        if (_frozen) ToggleFreeze();
        _cliMode = null; // an explicit choice overrides the --mode switch
        try
        {
            _video.Start(_currentVideoInfo, mode);
            _savedMode = _video.CurrentMode;
            SaveSettings();
            LayoutCanvas();
            ShowOsd($"{mode.Width}x{mode.Height} {mode.Fps}fps");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("映像モードの変更に失敗しました。\n\n{0}", ex.Message),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RecoverVideo();
        }
        UpdateStatus();
    }

    private void ClearModePreference()
    {
        _savedMode = null;
        _cliMode = null;
        SaveSettings();
        if (_currentVideoInfo == null) return;
        if (_frozen) ToggleFreeze();
        try
        {
            _video.Start(_currentVideoInfo, null);
            LayoutCanvas();
            ShowOsd(L.T("映像モード: 自動"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("映像モードの変更に失敗しました。\n\n{0}", ex.Message),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RecoverVideo();
        }
        UpdateStatus();
    }

    private void SwitchAudioDevice(AudioDeviceInfo info)
    {
        try
        {
            _audio.Start(info);
            _currentAudioInfo = info;
            ShowOsd(L.F("音声: {0}", info.Name));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("音声デバイスの切り替えに失敗しました。\n\n{0}", ex.Message),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStatus();
    }

    // ---- Audio buffer settings ------------------------------------------

    private void ShowAudioBufferSettings()
    {
        using var dlg = new Form
        {
            Text = L.T("音声バッファ設定"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 210),
            // (7, 15) is the metric of the default Segoe UI 9pt these layouts
            // were drawn against at 100% — WinForms then scales every
            // Location/Size by the same factor as the font, so the layout
            // still holds together at 125-200% display scaling.
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };

        var lbl = new Label { Text = L.T("バッファ長 (ms):"), AutoSize = true, Location = new Point(16, 22) };
        var num = new NumericUpDown
        {
            Minimum = AudioEngine.MinBufferMs,
            Maximum = 1000,
            Increment = 10,
            Value = Math.Clamp(_audio.BufferMilliseconds, AudioEngine.MinBufferMs, 1000),
            Location = new Point(190, 20),
            Width = 150,
        };

        // Presets: this value is now an actively-held latency target, so these
        // map directly to the delay you hear.
        var lblPreset = new Label { Text = L.T("プリセット:"), AutoSize = true, Location = new Point(16, 58) };
        var btnLow = new Button { Text = L.T("低遅延 60"), Location = new Point(16, 78), Width = 100 };
        var btnMid = new Button { Text = L.T("標準 120"), Location = new Point(126, 78), Width = 100 };
        var btnSafe = new Button { Text = L.T("安定 250"), Location = new Point(236, 78), Width = 100 };
        btnLow.Click += (_, _) => num.Value = 60;
        btnMid.Click += (_, _) => num.Value = 120;
        btnSafe.Click += (_, _) => num.Value = 250;

        var hint = new Label
        {
            Text = L.T("この値が実際の音声遅延の目安になります。\n小さいほど低遅延ですが、音切れが出たら上げてください。\n実測値はステータスバーの「遅延」に表示されます。"),
            AutoSize = true,
            // SystemColors.GrayText (not Color.Gray, ~2.9:1) is the theme's
            // intended hint colour and reads at a proper contrast ratio.
            ForeColor = SystemColors.GrayText,
            Location = new Point(16, 114),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(164, 170), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(255, 170), Width = 90 };

        dlg.Controls.AddRange(new Control[] { lbl, num, lblPreset, btnLow, btnMid, btnSafe, hint, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _audio.BufferMilliseconds = (int)num.Value;
        if (_audio.IsRunning && _currentAudioInfo != null)
        {
            try { _audio.Start(_currentAudioInfo); }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.F("音声の再初期化に失敗しました。\n\n{0}", ex.Message),
                    "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        ShowOsd(L.F("音声バッファ {0}ms", _audio.BufferMilliseconds));
        UpdateStatus();
    }

    // ---- Resize (ratio lock) --------------------------------------------

    protected override void WndProc(ref Message m)
    {
        // A second launch handed us its command line instead of failing.
        if (m.Msg == SingleInstance.WmCopyData)
        {
            string[]? forwarded = SingleInstance.ReadForwardedArgs(m.LParam);
            if (forwarded != null) ApplyForwardedArgs(forwarded);
            m.Result = (IntPtr)1;
            return;
        }

        // Hot-plug: debounce the burst of WM_DEVICECHANGE messages, then rescan.
        if (m.Msg == WmDeviceChange)
        {
            _devTimer.Stop();
            _devTimer.Start();
        }

        // Suspend/resume: stop the engine cleanly before sleep and rebuild it
        // after resume, so no wedged MF pipeline is left to block the UI thread.
        if (m.Msg == WmPowerBroadcast)
        {
            int evt = (int)m.WParam;
            Log.Info($"power broadcast 0x{evt:X}");
            if (evt == PbtApmSuspend)
            {
                try { _video.Stop(); } catch { /* ignore */ }
                try { _audio.Stop(); } catch { /* ignore */ }
                _canvas.Invalidate();
            }
            else if (evt is PbtApmResumeSuspend or PbtApmResumeAutomatic)
            {
                _resumeTimer.Stop();
                _resumeTimer.Start();
            }
        }

        // Interactive resize: suppress the per-move black-border repaints that
        // make the video flicker; do one clean full layout when the drag ends.
        if (m.Msg == WmEnterSizeMove)
        {
            _inSizeMove = true;
        }
        else if (m.Msg == WmExitSizeMove)
        {
            _inSizeMove = false;
            LayoutCanvas();       // final, with border clear
            _canvas.Invalidate();
        }

        // Global hotkeys (work even when another app has focus).
        if (m.Msg == WmHotkey)
        {
            switch ((int)m.WParam)
            {
                case HkSnapshot: SaveSnapshot(); break;
                case HkMute: ToggleMute(); break;
                case HkPip: TogglePip(); break;
            }
            return;
        }

        // Borderless: remove the non-client chrome while keeping snap-eligible styles.
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero && _isBorderless && !_isFullscreen)
        {
            if (WindowState == FormWindowState.Maximized)
            {
                // Constrain the (client == whole window) rect to the work area so
                // the taskbar stays visible when maximized/snapped.
                Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                var rc = new Rect { Left = wa.Left, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom };
                Marshal.StructureToPtr(rc, m.LParam, false); // rgrc[0]
            }
            m.Result = IntPtr.Zero; // client fills the window: no border/caption drawn
            return;
        }

        // Borderless: report resize edges so the window can be resized by its
        // borders (the canvas passes edge hits through to us via HTTRANSPARENT).
        if (m.Msg == WmNcHitTest && _isBorderless && !_isFullscreen
            && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)(long)m.Result == HtClient)
            {
                int lp = (int)(long)m.LParam;
                Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = (IntPtr)EdgeHit(p, ClientSize);
            }
            return;
        }

        if (m.Msg == WmSizing && _lockAspect && !_isFullscreen
            && WindowState == FormWindowState.Normal)
        {
            Size res = _video.DisplayResolution;
            if (res.Width > 0 && res.Height > 0)
            {
                var rc = Marshal.PtrToStructure<Rect>(m.LParam);
                int edge = (int)m.WParam;

                int ncW = Width - ClientSize.Width;
                int ncH = Height - ClientSize.Height;
                int chrome = ChromeHeight();
                double aspect = (double)res.Width / res.Height;

                bool drivenByHeight = edge is WmszTop or WmszBottom;

                int videoW, videoH;
                if (drivenByHeight)
                {
                    videoH = Math.Max(1, (rc.Bottom - rc.Top) - ncH - chrome);
                    videoW = (int)Math.Round(videoH * aspect);
                }
                else
                {
                    videoW = Math.Max(1, (rc.Right - rc.Left) - ncW);
                    videoH = (int)Math.Round(videoW / aspect);
                }

                int newWinW = videoW + ncW;
                int newWinH = videoH + chrome + ncH;

                switch (edge)
                {
                    case WmszLeft:
                        rc.Left = rc.Right - newWinW; rc.Bottom = rc.Top + newWinH; break;
                    case WmszRight:
                        rc.Right = rc.Left + newWinW; rc.Bottom = rc.Top + newWinH; break;
                    case WmszTop:
                        rc.Top = rc.Bottom - newWinH; rc.Right = rc.Left + newWinW; break;
                    case WmszBottom:
                        rc.Bottom = rc.Top + newWinH; rc.Right = rc.Left + newWinW; break;
                    case WmszTopLeft:
                        rc.Left = rc.Right - newWinW; rc.Top = rc.Bottom - newWinH; break;
                    case WmszTopRight:
                        rc.Right = rc.Left + newWinW; rc.Top = rc.Bottom - newWinH; break;
                    case WmszBottomLeft:
                        rc.Left = rc.Right - newWinW; rc.Bottom = rc.Top + newWinH; break;
                    case WmszBottomRight:
                        rc.Right = rc.Left + newWinW; rc.Bottom = rc.Top + newWinH; break;
                }

                Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
        }

        base.WndProc(ref m);
    }

    // ---- Status & checks -------------------------------------------------

    /// <summary>Truncate to at most <paramref name="max"/> characters, appending
    /// "…" when shortened. Used for status-bar device names, which would
    /// otherwise push the volume label out of view at the minimum window width.</summary>
    private static string Ellipsize(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private void UpdateStatus()
    {
        UpdatePowerState(); // display sleep inhibition follows the running state

        string vLabel = L.T("映像"), aLabel = L.T("音声"), volLabel = L.T("音量");
        if (_video.CurrentDeviceName != null)
        {
            Size r = _video.CurrentResolution;
            string comp = _video.CurrentMode?.Compression ?? string.Empty;
            string fps = _video.IsRunning ? $"  {_video.NegotiatedFps}fps" : string.Empty;
            string shortName = Ellipsize(_video.CurrentDeviceName, 20);
            _lblVideo.Text = r.Width > 0
                ? $"{vLabel}: {shortName}  {r.Width}x{r.Height} {comp}{fps}"
                : $"{vLabel}: {shortName}{fps}";
            // At the minimum window width the full text is pushed off the
            // status bar entirely; the tooltip is the only place it survives.
            _lblVideo.ToolTipText = r.Width > 0
                ? $"{vLabel}: {_video.CurrentDeviceName}  {r.Width}x{r.Height} {comp}{fps}"
                : $"{vLabel}: {_video.CurrentDeviceName}{fps}";
        }
        else
        {
            _lblVideo.Text = $"{vLabel}: {L.T("なし")}";
            _lblVideo.ToolTipText = string.Empty;
        }

        if (_audio.CurrentDeviceName != null)
        {
            // Measured latency floor = audio currently sitting in the buffer.
            int delay = (int)(Math.Round(_audio.BufferedMs / 10) * 10);
            string shortName = Ellipsize(_audio.CurrentDeviceName, 20);
            _lblAudio.Text = $"{aLabel}: {shortName}  {L.T("遅延")} ~{delay}ms";
            _lblAudio.ToolTipText = $"{aLabel}: {_audio.CurrentDeviceName}  {L.T("遅延")} ~{delay}ms";
        }
        else
        {
            _lblAudio.Text = $"{aLabel}: {L.T("なし")}";
            _lblAudio.ToolTipText = string.Empty;
        }

        // A "!" marks that the limiter is shaving peaks — louder settings past
        // this point trade clarity for volume.
        _lblVolume.Text = _audio.Muted
            ? $"{volLabel}: {L.T("ミュート")}"
            : $"{volLabel}: {_audio.VolumePercent}%{(_audio.IsLimiting ? " !" : string.Empty)}";

        // Title bar mirrors the negotiated mode (handy when the bars are hidden).
        Size res = _video.CurrentResolution;
        string title = _video.IsRunning && res.Width > 0
            ? $"YuCap - {res.Width}x{res.Height} {_video.NegotiatedFps}fps"
            : L.T("YuCap - キャプチャビューア");
        if (Text != title) Text = title;
    }

    private void UpdateChecks()
    {
        foreach (var item in _aspectItems)
            item.Checked = (VideoDisplayMode)item.Tag! == _displayMode;

        _miLockAspect.Checked = _lockAspect;
        _miBorderless.Checked = _isBorderless;
        _cmiBorderless.Checked = _isBorderless;
        _miTopmost.Checked = _alwaysOnTop;
        _cmiTopmost.Checked = _alwaysOnTop;
        _miMenuBar.Checked = _menu.Visible;
        _cmiMenuBar.Checked = _menu.Visible;
        _miStatusBar.Checked = _status.Visible;
        _cmiStatusBar.Checked = _status.Visible;
        _miMute.Checked = _audio.Muted;
        _cmiMute.Checked = _audio.Muted;
        _miMirror.Checked = _video.Mirror;
        foreach (var item in _rotationItems)
            item.Checked = (int)item.Tag! == _video.Rotation;
        _miFreeze.Checked = _frozen;
        _cmiFreeze.Checked = _frozen;
        _miPip.Checked = _isPip;
        _cmiPip.Checked = _isPip;
        // The displayed shortcut must track the user's actual binding — it can
        // be changed in ホットキー設定, and a stale "Ctrl+Alt+P" would lie.
        _miPip.ShortcutKeyDisplayString = FormatHotkey((Keys)_settings.HotkeyPip);
        _cmiPip.ShortcutKeyDisplayString = FormatHotkey((Keys)_settings.HotkeyPip);
        _miClickThrough.Checked = _settings.PipClickThrough;
        _miHotkeys.Checked = _settings.GlobalHotkeys;
        _miCursorHide.Checked = _settings.CursorAutoHide;
        _miUpdateCheck.Checked = _settings.UpdateCheckOnStartup;
        _miRestoreLevel.Enabled = _captureLevelBefore >= 0 && _audio.IsRunning;

        // Burst mode is started and stopped by the same menu item; say which.
        _miBurst.Text = _burstTimer.Enabled
            ? L.F("連写を停止 ({0}/{1})", _burstDone, _burstTotal)
            : L.T("連写スナップショット...");

        // Every state toggle funnels through here, so this is the one place
        // that reliably catches "something the user changed" for persistence.
        MarkSettingsDirty();
        foreach (var item in _cursorSecItems)
            item.Checked = _settings.CursorAutoHide && (int)item.Tag! == _settings.CursorHideSeconds;
        foreach (var item in _pipIdleItems)
            item.Checked = (int)item.Tag! == _settings.PipOpacity;
        foreach (var item in _pipHoverItems)
            item.Checked = (int)item.Tag! == _settings.PipOpacityHover;
        foreach (var item in _pipSizeItems)
            item.Checked = (int)item.Tag! == _settings.PipSizePct;
        foreach (var item in _pipPosItems)
            item.Checked = (int)item.Tag! == _settings.PipCorner;
    }

    // ---- Keyboard --------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Configured hotkeys also work app-locally: if global registration
        // failed (combo owned by another app) or is disabled, the key still
        // works while YuCap has focus. When globally registered, the key never
        // reaches here, so this cannot double-fire.
        if ((keyData & Keys.KeyCode) != Keys.None)
        {
            if (keyData == (Keys)_settings.HotkeySnapshot) { SaveSnapshot(); return true; }
            if (keyData == (Keys)_settings.HotkeyMute) { ToggleMute(); return true; }
            if (keyData == (Keys)_settings.HotkeyPip) { TogglePip(); return true; }
        }

        switch (keyData)
        {
            case Keys.F11:
                ToggleFullscreen();
                return true;
            case Keys.F10:
                ToggleMenuBar();
                return true;
            case Keys.Control | Keys.S:
                SaveSnapshot();
                return true;
            case Keys.Control | Keys.C:
                CopySnapshotToClipboard();
                return true;
            case Keys.Control | Keys.T:
                ToggleAlwaysOnTop();
                ShowOsd(_alwaysOnTop ? L.T("常に前面: オン") : L.T("常に前面: オフ"));
                return true;
            case Keys.Control | Keys.B:
                ToggleBorderless();
                return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0:
                ResetZoom();
                return true;
            case Keys.M:
                ToggleMute();
                return true;
            case Keys.Space:
                ToggleFreeze();
                return true;
            // Volume from the keyboard, matching the wheel's step. Handy in
            // fullscreen, where the pointer is hidden anyway.
            case Keys.Up:
                NudgeVolume(+VolumeStep);
                return true;
            case Keys.Down:
                NudgeVolume(-VolumeStep);
                return true;
            case Keys.Escape:
                if (_isFullscreen) { ExitFullscreen(); return true; }
                break;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _video.Dispose();
            _audio.Dispose();
            _osdTimer.Dispose();
            _uiTimer.Dispose();
            _devTimer.Dispose();
            _burstTimer.Dispose();
            _pipHoverTimer.Dispose();
            _resumeTimer.Dispose();
            _resizeGuard.Dispose();
            _cursorTimer.Dispose();
            _settingsSaveTimer.Dispose();
            _startupTimer.Dispose();
            ShowCursorIfHidden();   // belt and braces: Cursor.Hide() is process-wide
        }
        base.Dispose(disposing);
    }
}
