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

public sealed class MainForm : Form, IMessageFilter
{
    // ---- Constants -------------------------------------------------------
    private const int VolumeStep = 5;
    private const int OsdMilliseconds = 900;
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
    private Point _lastCursorPos;
    private int _lastCursorMoveTick;
    private bool _cursorHidden;
    private int _restartAttempts;                 // capped so recovery can't loop
    private const int MaxRestartAttempts = 2;

    public MainForm()
    {
        // Settings are needed BEFORE building menus (UI language).
        _settings = SettingsStore.Load();
        L.English = _settings.Language == "en";

        Text = "YuCap - キャプチャビューア";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 540 + 24 + 22);
        MinimumSize = new Size(320, 240);
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

        Load += OnLoad;
        FormClosing += OnFormClosing;
        Resize += (_, _) => LayoutCanvas();
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
        using var font = new Font("Segoe UI", 14f);
        SizeF sz = g.MeasureString(L.T("映像なし"), font);
        g.DrawString(L.T("映像なし"), font, Brushes.Gray,
            (_canvas.ClientSize.Width - sz.Width) / 2, (_canvas.ClientSize.Height - sz.Height) / 2);
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
        var burst = new ToolStripMenuItem(L.T("連写スナップショット..."), null, (_, _) => ShowBurstDialog());
        var openDir = new ToolStripMenuItem(L.T("保存先フォルダを開く"), null, (_, _) => OpenSnapshotFolder());
        var snapCfg = new ToolStripMenuItem(L.T("スナップショット設定..."), null, (_, _) => ShowSnapshotSettings());
        var exit = new ToolStripMenuItem(L.T("終了"), null, (_, _) => Close());
        file.DropDownItems.Add(snap);
        file.DropDownItems.Add(copy);
        file.DropDownItems.Add(burst);
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
        _miMenuBar = new ToolStripMenuItem(L.T("メニューバーを隠す"), null, (_, _) => ToggleMenuBar())
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
        _miPip = new ToolStripMenuItem(L.T("ピクチャインピクチャ"), null, (_, _) => TogglePip())
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+P",
        };

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
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("更新を確認..."), null,
            async (_, _) => await CheckForUpdatesAsync(manual: true)));
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
            var i2 = new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipOpacity(p, hover: true)) { Tag = p };
            _pipIdleItems.Add(i1);
            _pipHoverItems.Add(i2);
            idle.DropDownItems.Add(i1);
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
        _ctx.Items.Add(new ToolStripMenuItem(L.T("全画面切替"), null, (_, _) => ToggleFullscreen()));
        _cmiPip = new ToolStripMenuItem(L.T("ピクチャインピクチャ"), null, (_, _) => TogglePip());
        _ctx.Items.Add(_cmiPip);
        _cmiFreeze = new ToolStripMenuItem(L.T("一時停止 / 再開"), null, (_, _) => ToggleFreeze());
        _ctx.Items.Add(_cmiFreeze);
        _cmiMute = new ToolStripMenuItem(L.T("ミュート"), null, (_, _) => ToggleMute());
        _ctx.Items.Add(_cmiMute);
        _ctx.Items.Add(new ToolStripSeparator());

        _ctx.Items.Add(new ToolStripMenuItem(L.T("スナップショット保存"), null, (_, _) => SaveSnapshot()));
        _ctx.Items.Add(new ToolStripMenuItem(L.T("スナップショットをコピー"), null, (_, _) => CopySnapshotToClipboard()));
        _ctx.Items.Add(new ToolStripSeparator());

        _ctx.Items.Add(BuildAspectSubmenu());
        _ctx.Items.Add(BuildSizeSubmenu());
        _cmiVideoModeRoot = new ToolStripMenuItem(L.T("映像モード (解像度/FPS)"));
        _cmiVideoModeRoot.DropDownOpening += (_, _) => RebuildVideoModeList(_cmiVideoModeRoot);
        _cmiVideoModeRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        _ctx.Items.Add(_cmiVideoModeRoot);
        _ctx.Items.Add(new ToolStripSeparator());

        _cmiBorderless = new ToolStripMenuItem(L.T("ウィンドウ枠なし表示"), null, (_, _) => ToggleBorderless());
        _cmiTopmost = new ToolStripMenuItem(L.T("常に前面に表示"), null, (_, _) => ToggleAlwaysOnTop());
        _cmiMenuBar = new ToolStripMenuItem(L.T("メニューバー表示"), null, (_, _) => ToggleMenuBar());
        _cmiStatusBar = new ToolStripMenuItem(L.T("ステータスバー表示"), null, (_, _) => ToggleStatusBar());
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
        InitVideo();
        InitAudio();
        _audio.VolumePercent = _settings.Volume; // apply after the device starts
        if (opts.Muted && _audio.IsRunning) _audio.Muted = true;

        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys();

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
        MaybeCheckForUpdatesOnStartup();
        Log.Info($"OnLoad done: video={_video.CurrentDeviceName ?? "none"} audio={_audio.CurrentDeviceName ?? "none"} " +
                 $"borderless={_isBorderless} fs={_isFullscreen} pip={_isPip}");
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

    private void InitVideo()
    {
        while (true)
        {
            try
            {
                var devices = VideoEngine.EnumerateDevices();
                var pick = VideoEngine.PickPreferred(devices, _settings.DeviceKeyword);
                if (pick == null)
                {
                    if (MessageBox.Show(this,
                            L.T("映像入力デバイスが見つかりません。\nキャプチャデバイスの接続を確認して「再試行」を押してください。\n（キャンセルしても、後から接続すれば自動で再接続します）"),
                            "YuCap", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning)
                        == DialogResult.Retry) continue;
                    return;
                }
                LayoutCanvas();               // give the canvas a valid size before Start
                _video.Start(pick, _cliMode ?? _savedMode);
                _currentVideoInfo = pick;
                LayoutCanvas();               // re-fit now that the resolution is known
                return;
            }
            catch (Exception ex)
            {
                if (MessageBox.Show(this,
                        L.F("映像デバイスの初期化に失敗しました。\n\n{0}", ex.Message),
                        "YuCap", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning)
                    != DialogResult.Retry) return;
            }
        }
    }

    private void InitAudio()
    {
        while (true)
        {
            try
            {
                var devices = AudioEngine.EnumerateDevices();
                var pick = AudioEngine.PickPreferred(devices, _settings.DeviceKeyword);
                if (pick == null)
                {
                    if (MessageBox.Show(this,
                            L.T("音声入力デバイスが見つかりません。\n接続を確認して「再試行」を押してください。\n（キャンセルしても、後から接続すれば自動で再接続します）"),
                            "YuCap", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning)
                        == DialogResult.Retry) continue;
                    return;
                }
                _audio.Start(pick);
                _currentAudioInfo = pick;
                return;
            }
            catch (Exception ex)
            {
                if (MessageBox.Show(this,
                        L.F("音声デバイスの初期化に失敗しました。\n\n{0}", ex.Message),
                        "YuCap", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning)
                    != DialogResult.Retry) return;
            }
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
                ShowOsd(L.T("映像デバイスが切断されました"));
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
                        ShowOsd(L.F("映像を再接続しました: {0}", pick.Name));
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
                        ShowOsd(L.F("音声を再接続しました: {0}", pick.Name));
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
        if (_frozen && _freezeBox.Bounds != area) _freezeBox.Bounds = area;
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
            ShowOsd(L.T("この環境では回転に対応していません"));
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
            ShowOsd(L.T("この環境では反転に対応していません"));
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

    private void ShowOsd(string text)
    {
        _osd.ShowText(text);
        PositionOsd();
        _osdTimer.Stop();
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
        if (_audio.Muted) { _audio.Muted = false; UpdateChecks(); } // adjusting volume unmutes
        int newVol = Math.Clamp(_audio.VolumePercent + steps * VolumeStep, 0, AudioEngine.MaxVolumePercent);
        _audio.VolumePercent = newVol;
        ShowOsd(L.F("音量 {0}%", newVol));
        UpdateStatus();
        return true;
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
            ShowOsd(L.T("このデバイスは入力レベルを変更できません"));
            return;
        }
        if (before >= 100)
        {
            ShowOsd(L.T("入力レベルは既に最大です"));
            return;
        }
        _audio.CaptureLevelPercent = 100;
        ShowOsd(L.F("入力レベル {0}% → 100%", before));
        UpdateStatus();
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
            _freezeBox.Visible = false;
            Image? old = _freezeBox.Image;
            _freezeBox.Image = null;
            old?.Dispose();
            if (!_mutedBeforeFreeze && _audio.IsRunning) _audio.Muted = false;
            ShowOsd(L.T("再開"));
        }
        else
        {
            Bitmap? still = _video.Snapshot(cropToVideo: false); // includes letterbox → 1:1 overlay
            if (still == null) { ShowOsd(L.T("映像がありません")); return; }
            _freezeBox.Image = still;
            _freezeBox.Bounds = _canvas.Bounds;
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

    // ---- Picture-in-picture ---------------------------------------------

    private void TogglePip()
    {
        if (_isPip) ExitPip();
        else EnterPip();
    }

    private void EnterPip()
    {
        // Remember whether we were fullscreen so exiting PiP returns there.
        _prePipFullscreen = _isFullscreen;
        if (_isFullscreen) ExitFullscreen();

        _prePipBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _prePipBorderless = _isBorderless;
        _prePipTopmost = _alwaysOnTop;
        _prePipMenu = _menu.Visible;
        _prePipStatus = _status.Visible;
        _isPip = true;

        WindowState = FormWindowState.Normal;
        _menu.Visible = false;
        _status.Visible = false;
        if (!_isBorderless)
        {
            _isBorderless = true;
            _canvas.EnableEdgeResize = true;
            RefreshFrame();
        }
        TopMost = true;

        // Preset-sized window docked to the configured work-area corner.
        ApplyPipSize(anchorCorner: false);
        Location = PipCornerLocation();

        _pipHovered = Bounds.Contains(Cursor.Position);
        ApplyPipOpacity();
        _pipHoverTimer.Start();
        if (_settings.PipClickThrough) ApplyClickThrough(true);

        LayoutCanvas(ModeChangeSettleMs);
        UpdateChecks();
        ShowOsd(L.T("PiP: オン"));
    }

    private void ExitPip()
    {
        _isPip = false;
        _pipHoverTimer.Stop();
        ApplyClickThrough(false);
        try { Opacity = 1.0; } catch { }
        TopMost = _alwaysOnTop = _prePipTopmost;
        if (_isBorderless != _prePipBorderless)
        {
            _isBorderless = _prePipBorderless;
            _canvas.EnableEdgeResize = _isBorderless;
            RefreshFrame();
        }
        _menu.Visible = _prePipMenu;
        _status.Visible = _prePipStatus;
        if (_prePipBounds.Width > 0) Bounds = _prePipBounds;
        // Return to the mode the user was in before PiP (e.g. fullscreen).
        if (_prePipFullscreen)
        {
            _prePipFullscreen = false;
            EnterFullscreen();
        }
        LayoutCanvas(ModeChangeSettleMs);
        UpdateChecks();
        ShowOsd(L.T("PiP: オフ"));
    }

    private void SetPipSize(int pct)
    {
        _settings.PipSizePct = Math.Clamp(pct, 5, 100);
        if (_isPip) ApplyPipSize(anchorCorner: true);
        UpdateChecks();
    }

    private void SetPipCorner(int corner)
    {
        _settings.PipCorner = Math.Clamp(corner, 0, 3);
        if (_isPip) Location = PipCornerLocation();
        UpdateChecks();
    }

    /// <summary>Work-area location docking the current window to the configured
    /// corner (0=BR, 1=BL, 2=TR, 3=TL) with a 16px margin.</summary>
    private Point PipCornerLocation()
    {
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        const int m = 16;
        return _settings.PipCorner switch
        {
            1 => new Point(wa.Left + m, wa.Bottom - Height - m),
            2 => new Point(wa.Right - Width - m, wa.Top + m),
            3 => new Point(wa.Left + m, wa.Top + m),
            _ => new Point(wa.Right - Width - m, wa.Bottom - Height - m),
        };
    }

    /// <summary>Resize the PiP window to the preset % of the source resolution.
    /// With click-through on, drag-resizing is impossible, so this preset is the
    /// only sizing control. When resizing mid-session the window keeps the
    /// corner matching the configured docking corner, so a docked PiP (even one
    /// the user has dragged elsewhere) grows away from its anchor.</summary>
    private void ApplyPipSize(bool anchorCorner)
    {
        Size res = _video.DisplayResolution;
        int pct = Math.Clamp(_settings.PipSizePct, 5, 100);
        int w = res.Width > 0 ? Math.Max(160, res.Width * pct / 100) : 480;
        int h = res.Width > 0 ? (int)Math.Round((double)w * res.Height / res.Width) : 270;
        Rectangle old = Bounds;
        SetOuterForClient(new Size(w, h));
        if (anchorCorner)
        {
            Location = _settings.PipCorner switch
            {
                1 => new Point(old.Left, old.Bottom - Height),          // BL
                2 => new Point(old.Right - Width, old.Top),             // TR
                3 => new Point(old.Left, old.Top),                      // TL
                _ => new Point(old.Right - Width, old.Bottom - Height), // BR
            };
        }
    }

    private void SetPipOpacity(int pct, bool hover)
    {
        pct = Math.Clamp(pct, 0, 100);
        if (hover) _settings.PipOpacityHover = pct;
        else _settings.PipOpacity = pct;
        if (_isPip) ApplyPipOpacity();
        UpdateChecks();
    }

    /// <summary>Apply the idle/hover opacity matching the current cursor state.
    /// 0% is allowed (fully invisible): hovering — or Ctrl+Alt+P — brings it back.
    /// While click-through is on, the layered alpha is driven DIRECTLY: the
    /// Form.Opacity setter rewrites the ex-style from WinForms' own cache, which
    /// strips WS_EX_TRANSPARENT and silently disables click-through.</summary>
    private void ApplyPipOpacity()
    {
        int pct = Math.Clamp(_pipHovered ? _settings.PipOpacityHover : _settings.PipOpacity, 0, 100);
        if (_isPip && _settings.PipClickThrough && IsHandleCreated)
        {
            int ex = GetWindowLong(Handle, GwlExStyle);
            SetWindowLong(Handle, GwlExStyle, ex | WsExTransparent | WsExLayered);
            SetLayeredWindowAttributes(Handle, 0, (byte)(pct * 255 / 100), LwaAlpha);
        }
        else
        {
            try { Opacity = pct / 100.0; } catch { }
        }
    }

    private void UpdatePipHoverOpacity()
    {
        if (!_isPip) { _pipHoverTimer.Stop(); return; }
        bool hovered = Bounds.Contains(Cursor.Position);
        if (hovered == _pipHovered) return;
        _pipHovered = hovered;
        ApplyPipOpacity();
    }

    private void TogglePipClickThrough()
    {
        _settings.PipClickThrough = !_settings.PipClickThrough;
        if (_isPip) ApplyClickThrough(_settings.PipClickThrough);
        UpdateChecks();
        ShowOsd(_settings.PipClickThrough
            ? L.T("クリックスルー: オン (Ctrl+Alt+Pで解除)")
            : L.T("クリックスルー: オフ"));
    }

    private void ApplyClickThrough(bool on)
    {
        if (!IsHandleCreated) return;
        if (on)
        {
            ApplyPipOpacity(); // sets WS_EX_TRANSPARENT|LAYERED + the layered alpha
            if (!_settings.GlobalHotkeys)
            {
                // Safety hatch: with the mouse passing through, the PiP hotkey
                // must work even if the user disabled global hotkeys.
                TryRegisterHotkey(HkPip, (Keys)_settings.HotkeyPip, new List<string>());
            }
        }
        else
        {
            int ex = GetWindowLong(Handle, GwlExStyle);
            SetWindowLong(Handle, GwlExStyle, ex & ~(WsExTransparent | WsExLayered));
            // Hand opacity back to WinForms. Its cached Opacity may equal the
            // target (making the setter a no-op), so pass through 1.0 first to
            // force a real style re-apply, then restore the PiP opacity if any.
            try
            {
                Opacity = 1.0;
                if (_isPip) ApplyPipOpacity();
            }
            catch { }
            if (!_settings.GlobalHotkeys) UnregisterHotKey(Handle, HkPip);
        }
    }

    // ---- Global hotkeys --------------------------------------------------

    private void RegisterGlobalHotkeys()
    {
        var failed = new List<string>();
        TryRegisterHotkey(HkSnapshot, (Keys)_settings.HotkeySnapshot, failed);
        TryRegisterHotkey(HkMute, (Keys)_settings.HotkeyMute, failed);
        TryRegisterHotkey(HkPip, (Keys)_settings.HotkeyPip, failed);
        // Partial failure just means one combo is owned by another app — say
        // which one instead of a blanket error; the others still work.
        if (failed.Count > 0)
        {
            Log.Info("hotkey conflict: " + string.Join(", ", failed));
            ShowOsd(L.F("ホットキー使用中のため無効: {0}", string.Join(", ", failed)));
        }
    }

    private void TryRegisterHotkey(int id, Keys combo, List<string> failed)
    {
        // Defensive: an id left over from a previous register (toggle, click-
        // through safety hatch) makes a re-register fail — clear it first.
        UnregisterHotKey(Handle, id);
        if ((combo & Keys.KeyCode) == Keys.None) return; // disabled
        uint mods = ModNoRepeat;
        if (combo.HasFlag(Keys.Control)) mods |= ModControl;
        if (combo.HasFlag(Keys.Alt)) mods |= ModAlt;
        if (combo.HasFlag(Keys.Shift)) mods |= ModShift;
        if (!RegisterHotKey(Handle, id, mods, (uint)(combo & Keys.KeyCode)))
            failed.Add(FormatHotkey(combo));
    }

    private static string FormatHotkey(Keys combo)
    {
        if ((combo & Keys.KeyCode) == Keys.None) return L.T("なし");
        var parts = new List<string>();
        if (combo.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (combo.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (combo.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add((combo & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }

    private void ShowHotkeySettings()
    {
        using var dlg = new Form
        {
            Text = L.T("ホットキー設定"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 210),
            KeyPreview = true,
        };

        Keys snapCombo = (Keys)_settings.HotkeySnapshot;
        Keys muteCombo = (Keys)_settings.HotkeyMute;
        Keys pipCombo = (Keys)_settings.HotkeyPip;

        TextBox MakeRow(string label, int y, Keys initial, Action<Keys> set)
        {
            var lbl = new Label { Text = L.T(label), AutoSize = true, Location = new Point(16, y + 4) };
            var tb = new TextBox
            {
                Text = FormatHotkey(initial),
                ReadOnly = true,
                Location = new Point(150, y),
                Width = 190,
                TabStop = true,
            };
            tb.KeyDown += (_, e) =>
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (e.KeyCode == Keys.Escape)
                {
                    set(Keys.None);
                    tb.Text = FormatHotkey(Keys.None);
                    return;
                }
                // Ignore presses of a modifier alone; require Ctrl/Alt/Shift.
                if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return;
                if (e.Modifiers == Keys.None) return;
                set(e.KeyData);
                tb.Text = FormatHotkey(e.KeyData);
            };
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(tb);
            return tb;
        }

        var tbSnap = MakeRow("スナップショット:", 16, snapCombo, k => snapCombo = k);
        var tbMute = MakeRow("ミュート:", 52, muteCombo, k => muteCombo = k);
        var tbPip = MakeRow("PiP切替:", 88, pipCombo, k => pipCombo = k);

        var hint = new Label
        {
            Text = L.T("欄をクリックしてキーを押してください。Esc で無効化できます。"),
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(16, 126),
        };
        var reset = new Button { Text = L.T("既定に戻す"), Location = new Point(16, 168), Width = 100 };
        reset.Click += (_, _) =>
        {
            snapCombo = Keys.Control | Keys.Alt | Keys.S;
            muteCombo = Keys.Control | Keys.Alt | Keys.M;
            pipCombo = Keys.Control | Keys.Alt | Keys.P;
            tbSnap.Text = FormatHotkey(snapCombo);
            tbMute.Text = FormatHotkey(muteCombo);
            tbPip.Text = FormatHotkey(pipCombo);
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(164, 168), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(255, 168), Width = 90 };
        dlg.Controls.AddRange(new Control[] { hint, reset, ok, cancel });
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _settings.HotkeySnapshot = (int)snapCombo;
        _settings.HotkeyMute = (int)muteCombo;
        _settings.HotkeyPip = (int)pipCombo;
        SaveSettings();
        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys(); // re-register + report conflicts now
    }

    private void UnregisterGlobalHotkeys()
    {
        UnregisterHotKey(Handle, HkSnapshot);
        UnregisterHotKey(Handle, HkMute);
        UnregisterHotKey(Handle, HkPip);
    }

    private void ToggleGlobalHotkeys()
    {
        _settings.GlobalHotkeys = !_settings.GlobalHotkeys;
        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys();
        else UnregisterGlobalHotkeys();
        UpdateChecks();
    }

    // ---- Burst snapshots -------------------------------------------------

    private void ShowBurstDialog()
    {
        if (_burstTimer.Enabled)
        {
            _burstTimer.Stop();
            ShowOsd(L.T("連写を停止しました"));
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
        ShowOsd(L.F("連写を開始します ({0}枚 / {1}秒間隔)", _burstTotal, numInt.Value));
    }

    private void BurstTick()
    {
        if (SaveSnapshotCore(out _))
        {
            _burstDone++;
            ShowOsd(L.F("連写 {0}/{1}", _burstDone, _burstTotal));
        }
        if (_burstDone >= _burstTotal || !_video.IsRunning)
            _burstTimer.Stop();
    }

    // ---- Startup registration -------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsStartupRegistered()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("YuCap") != null;
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
        };
        var lbl = new Label { Text = L.T("キーワード:"), AutoSize = true, Location = new Point(16, 20) };
        var txt = new TextBox { Text = _settings.DeviceKeyword, Location = new Point(110, 16), Width = 210 };
        var hint = new Label
        {
            Text = L.T("デバイス名にこの語を含む機器を起動時に自動選択します。\n（既定: JVA14）"),
            AutoSize = true,
            ForeColor = Color.Gray,
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

    // ---- Update ----------------------------------------------------------

    /// <summary>
    /// Startup check, at most once a day and only if enabled. Fired and
    /// forgotten so nothing about it can delay the window appearing; the check
    /// itself stays quiet unless there is genuinely something newer.
    /// </summary>
    private void MaybeCheckForUpdatesOnStartup()
    {
        if (!_settings.UpdateCheckOnStartup) return;
        if (DateTime.TryParse(_settings.LastUpdateCheckUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last)
            && (DateTime.UtcNow - last).TotalHours < 24)
        {
            return;
        }

        // Let the capture settle before adding network work.
        var delay = new System.Windows.Forms.Timer { Interval = 4000 };
        delay.Tick += async (_, _) =>
        {
            delay.Stop();
            delay.Dispose();
            if (IsDisposed) return;
            try { await CheckForUpdatesAsync(manual: false); }
            catch (Exception ex) { Log.Info("startup update check failed: " + ex.Message); }
        };
        delay.Start();
    }

    private void ToggleUpdateCheck()
    {
        _settings.UpdateCheckOnStartup = !_settings.UpdateCheckOnStartup;
        UpdateChecks();
        ShowOsd(_settings.UpdateCheckOnStartup
            ? L.T("起動時の更新確認: オン")
            : L.T("起動時の更新確認: オフ"));
    }

    /// <summary>
    /// Look for a newer release and, with the user's consent, install it.
    /// Communication only ever happens here — from an explicit menu action, or
    /// from the once-a-day startup check the user can switch off.
    /// </summary>
    /// <param name="manual">True when the user asked; only then do we report
    /// "no update" or a failed check. The startup check stays silent.</param>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateInfo? info;
        try
        {
            info = await Updater.CheckAsync(_settings.UpdateApiUrl);
        }
        catch (Exception ex)
        {
            Log.Info("update check threw: " + ex.Message);
            info = null;
        }

        _settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o");

        if (info == null)
        {
            if (manual)
            {
                MessageBox.Show(this,
                    L.F("現在のバージョンは {0} です。\n更新はありません。", Updater.CurrentVersion),
                    "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        if (IsDisposed) return;

        var prompt = MessageBox.Show(this,
            L.F("新しいバージョン {0} があります（現在 {1}）。\n\n今すぐ更新しますか？\n更新後、YuCap は自動的に再起動します。",
                info.Version, Updater.CurrentVersion),
            "YuCap", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (prompt != DialogResult.Yes) return;

        // Under Program Files the swap cannot work; say so instead of failing
        // halfway through.
        if (!Updater.CanWriteToInstallDir())
        {
            if (MessageBox.Show(this,
                    L.T("インストール先に書き込めないため、自動更新できません。\nリリースページを開きますか？"),
                    "YuCap", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                OpenUrl(info.PageUrl);
            return;
        }

        DownloadAndApply(info);
    }

    /// <summary>Synchronous by design: the progress dialog's own modal loop
    /// drives the download, so there is nothing here to await.</summary>
    private void DownloadAndApply(UpdateInfo info)
    {
        using var dlg = new Form
        {
            Text = L.T("更新をダウンロード中"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ControlBox = false,
            ClientSize = new Size(380, 120),
        };
        var lbl = new Label
        {
            Text = L.F("{0} をダウンロードしています...", info.AssetName),
            AutoSize = true,
            Location = new Point(16, 18),
        };
        var bar = new ProgressBar { Location = new Point(16, 46), Size = new Size(348, 22), Maximum = 100 };
        var cancelBtn = new Button { Text = L.T("キャンセル"), Location = new Point(274, 80), Width = 90 };
        var cts = new CancellationTokenSource();
        cancelBtn.Click += (_, _) => { cts.Cancel(); dlg.Close(); };
        dlg.Controls.AddRange(new Control[] { lbl, bar, cancelBtn });

        string? file = null;
        Exception? failure = null;
        var progress = new Progress<int>(p => { if (!dlg.IsDisposed) bar.Value = Math.Clamp(p, 0, 100); });

        dlg.Shown += async (_, _) =>
        {
            try { file = await Updater.DownloadAsync(info, progress, cts.Token); }
            catch (OperationCanceledException) { /* user cancelled */ }
            catch (Exception ex) { failure = ex; }
            finally { if (!dlg.IsDisposed) dlg.Close(); }
        };
        dlg.ShowDialog(this);
        cts.Dispose();

        if (failure != null)
        {
            MessageBox.Show(this, L.F("更新のダウンロードに失敗しました。\n\n{0}", failure.Message),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (file == null) return;   // cancelled

        try
        {
            // Save settings and release devices before the swap: the new process
            // starts immediately and would otherwise fight over the capture card.
            SaveSettings();
            try { _video.Dispose(); } catch { /* ignore */ }
            try { _audio.Dispose(); } catch { /* ignore */ }

            Updater.Apply(file);      // rolls back internally if the swap fails
            _skipSaveOnClose = true;  // settings already written above
            Close();
        }
        catch (Exception ex)
        {
            Log.Info("update apply failed: " + ex.Message);
            MessageBox.Show(this,
                L.F("更新の適用に失敗しました。元の状態に戻しました。\n\n{0}", ex.Message),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info("open url failed: " + ex.Message); }
    }

    // ---- Language --------------------------------------------------------

    private void SetLanguage(string lang)
    {
        if (_settings.Language == lang) return;
        _settings.Language = lang;
        SaveSettings();
        MessageBox.Show(this, L.T("言語を切り替えました。次回起動時に反映されます。"),
            "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        SetOuterForClient(new Size(videoW, videoH + ChromeHeight()));
        ShowOsd($"{pct}% ({videoW}x{videoH})");
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

    // ---- Snapshot --------------------------------------------------------

    private string SnapshotDirectory =>
        string.IsNullOrWhiteSpace(_settings.SnapshotDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer")
            : _settings.SnapshotDir!;

    /// <summary>Grab the current frame with the OSD hidden (the snapshot is a
    /// compositor copy — a lingering bubble would be burned into the image).</summary>
    private Bitmap? GrabFrame()
    {
        if (_osd.Visible)
        {
            _osdTimer.Stop();
            _osd.Visible = false;
            _osd.Update();
        }
        return _video.Snapshot();
    }

    private void SaveSnapshot()
    {
        if (SaveSnapshotCore(out string file))
            ShowOsd(L.F("保存しました: {0}", file));
    }

    /// <summary>Save one snapshot; returns false (with OSD/dialog) on failure.
    /// Shared by Ctrl+S, the global hotkey, and burst mode.</summary>
    private bool SaveSnapshotCore(out string fileName)
    {
        fileName = string.Empty;
        using Bitmap? frame = GrabFrame();
        if (frame == null)
        {
            ShowOsd(L.T("映像がありません"));
            return false;
        }

        try
        {
            string dir = SnapshotDirectory;
            Directory.CreateDirectory(dir);
            bool jpg = _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
            // Milliseconds in the name so rapid consecutive shots never overwrite.
            fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{(jpg ? "jpg" : "png")}";
            string path = Path.Combine(dir, fileName);
            if (jpg)
            {
                ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                frame.Save(path, codec, ep);
            }
            else
            {
                frame.Save(path, ImageFormat.Png);
            }
            return true;
        }
        catch (Exception ex)
        {
            ShowOsd(L.T("保存に失敗しました"));
            MessageBox.Show(this, ex.Message, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void CopySnapshotToClipboard()
    {
        using Bitmap? frame = GrabFrame();
        if (frame == null)
        {
            ShowOsd(L.T("映像がありません"));
            return;
        }
        try
        {
            Clipboard.SetImage(frame);
            ShowOsd(L.T("クリップボードにコピーしました"));
        }
        catch
        {
            ShowOsd(L.T("コピーに失敗しました"));
        }
    }

    private void OpenSnapshotFolder()
    {
        try
        {
            string dir = SnapshotDirectory;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowSnapshotSettings()
    {
        using var dlg = new Form
        {
            Text = L.T("スナップショット設定"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 150),
        };

        var lblDir = new Label { Text = L.T("保存先:"), AutoSize = true, Location = new Point(16, 20) };
        var txtDir = new TextBox
        {
            Text = SnapshotDirectory,
            ReadOnly = true,
            Location = new Point(80, 16),
            Width = 240,
        };
        var browse = new Button { Text = L.T("参照..."), Location = new Point(328, 14), Width = 76 };
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog
            {
                Description = L.T("スナップショットの保存先フォルダ"),
                SelectedPath = SnapshotDirectory,
                ShowNewFolderButton = true,
            };
            if (fb.ShowDialog(dlg) == DialogResult.OK) txtDir.Text = fb.SelectedPath;
        };

        var lblFmt = new Label { Text = L.T("形式:"), AutoSize = true, Location = new Point(16, 62) };
        var cmbFmt = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(80, 58),
            Width = 140,
        };
        cmbFmt.Items.AddRange(new object[] { L.T("PNG (無劣化)"), L.T("JPEG (高画質)") });
        cmbFmt.SelectedIndex =
            _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        var reset = new Button { Text = L.T("既定に戻す"), Location = new Point(16, 108), Width = 100 };
        reset.Click += (_, _) =>
        {
            txtDir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
            cmbFmt.SelectedIndex = 0;
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(224, 108), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(315, 108), Width = 90 };

        dlg.Controls.AddRange(new Control[] { lblDir, txtDir, browse, lblFmt, cmbFmt, reset, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string defDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
        _settings.SnapshotDir = string.Equals(txtDir.Text, defDir, StringComparison.OrdinalIgnoreCase)
            ? null : txtDir.Text;
        _settings.SnapshotFormat = cmbFmt.SelectedIndex == 1 ? "jpg" : "png";
        SaveSettings();
        ShowOsd(L.F("スナップショット: {0}", cmbFmt.SelectedIndex == 1 ? "JPEG" : "PNG"));
    }

    // ---- About -----------------------------------------------------------

    private void ShowAbout()
    {
        string ver = Application.ProductVersion;
        int plus = ver.IndexOf('+'); // strip build metadata if present
        if (plus > 0) ver = ver[..plus];

        using var dlg = new Form
        {
            Text = L.T("バージョン情報"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 240),
        };

        var pic = new PictureBox
        {
            Location = new Point(20, 20),
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.StretchImage,
        };
        try { pic.Image = Icon?.ToBitmap(); } catch { /* no icon */ }

        var title = new Label
        {
            Text = "YuCap - キャプチャビューア",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(80, 22),
        };
        var version = new Label
        {
            Text = L.F("バージョン {0}", ver) + "\n© 2026 YUGO",
            AutoSize = true,
            Location = new Point(82, 50),
        };
        var libs = new Label
        {
            Text = L.T("使用ライブラリ:") + "\n" +
                   "  ・Windows Media Foundation (Capture Engine)\n" +
                   "  ・NAudio — MIT License\n" +
                   "  ・Vortice.MediaFoundation — MIT License",
            AutoSize = true,
            Location = new Point(20, 96),
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(280, 198), Width = 85 };

        dlg.Controls.AddRange(new Control[] { pic, title, version, libs, ok });
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        dlg.ShowDialog(this);
        pic.Image?.Dispose();
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
            ForeColor = Color.Gray,
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

    private void UpdateStatus()
    {
        UpdatePowerState(); // display sleep inhibition follows the running state

        string vLabel = L.T("映像"), aLabel = L.T("音声"), volLabel = L.T("音量");
        if (_video.CurrentDeviceName != null)
        {
            Size r = _video.CurrentResolution;
            string comp = _video.CurrentMode?.Compression ?? string.Empty;
            string fps = _video.IsRunning ? $"  {_video.NegotiatedFps}fps" : string.Empty;
            _lblVideo.Text = r.Width > 0
                ? $"{vLabel}: {_video.CurrentDeviceName}  {r.Width}x{r.Height} {comp}{fps}"
                : $"{vLabel}: {_video.CurrentDeviceName}{fps}";
        }
        else
        {
            _lblVideo.Text = $"{vLabel}: {L.T("なし")}";
        }

        if (_audio.CurrentDeviceName != null)
        {
            // Measured latency floor = audio currently sitting in the buffer.
            int delay = (int)(Math.Round(_audio.BufferedMs / 10) * 10);
            _lblAudio.Text = $"{aLabel}: {_audio.CurrentDeviceName}  {L.T("遅延")} ~{delay}ms";
        }
        else
        {
            _lblAudio.Text = $"{aLabel}: {L.T("なし")}";
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
            : "YuCap - キャプチャビューア";
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
        _miMenuBar.Checked = !_menu.Visible;
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
        _miClickThrough.Checked = _settings.PipClickThrough;
        _miHotkeys.Checked = _settings.GlobalHotkeys;
        _miCursorHide.Checked = _settings.CursorAutoHide;
        _miUpdateCheck.Checked = _settings.UpdateCheckOnStartup;
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
            ShowCursorIfHidden();   // belt and braces: Cursor.Hide() is process-wide
        }
        base.Dispose(disposing);
    }
}
