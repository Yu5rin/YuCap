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
    private const int HkSnapshot = 1, HkMute = 2, HkPip = 3, HkVolUp = 4, HkVolDown = 5;
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

    // Dark title bar, matching the dark menu/status chrome (see Ui.DarkRenderer).
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int attrValue, int attrSize);
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WmszLeft = 1, WmszRight = 2, WmszTop = 3, WmszTopLeft = 4,
        WmszTopRight = 5, WmszBottom = 6, WmszBottomLeft = 7, WmszBottomRight = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    // ---- Engines & UI ----------------------------------------------------
    // Which engine this is follows AppSettings.Renderer; it is replaced (not
    // reused) when the user switches renderer - see SwitchRenderer.
    private IVideoEngine _video = null!;
    private readonly AudioEngine _audio = new();

    private readonly VideoBox _canvas = new();
    private readonly OsdOverlay _osd = new();
    private readonly InfoOverlay _info = new();   // Ctrl+Shift+I testing aid, never persisted
    private bool _compareHeld;                      // Q held: FSR temporarily swapped for Standard
    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblVideo = new();
    private readonly ToolStripStatusLabel _lblAudio = new();
    private readonly ToolStripStatusLabel _lblVolume = new();
    private readonly ToolStripStatusLabel _lblState = new();
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
    private readonly List<ToolStripMenuItem> _rendererItems = new();
    // The quality menu exists twice (View menu + context menu), so everything
    // that must follow its state is kept in lists.
    private readonly List<ToolStripMenuItem> _qualityRoots = new();
    private readonly List<ToolStripMenuItem> _qualityItems = new();
    private readonly List<ToolStripMenuItem> _interpItems = new();
    private readonly List<ToolStripMenuItem> _sharpnessItems = new();
    private readonly List<ToolStripMenuItem> _nativeResItems = new();
    private int _nativeResOsdTarget = -1;      // the auto decision last announced by the OSD (-1 = none yet)

    private ToolStripMenuItem _miAdjust = null!;       // 表示 → 映像の調整...
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
    // Bold when an update is pending, to catch the eye without a dialog the
    // user has to dismiss on every launch. Cached because a Font per UpdateChecks
    // call (which runs often — every menu open) would leak GDI handles.
    private Font? _checkUpdateNormalFont;
    private Font? _checkUpdateBoldFont;
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
    private ToolStripMenuItem _audioOutputRoot = null!;
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

    // Freeze frame. FreezeView clips the still to its own client area, so a
    // zoomed-in overflow can no longer paint over the menu/status bars.
    private readonly FreezeView _freezeView = new();
    private bool _frozen;
    private bool _mutedBeforeFreeze;
    // True when the frozen still came from the capture engine's photo sink
    // (video pixels only, no letterbox) rather than the compositor screen
    // copy (which already includes the letterbox). The two need different
    // positioning — see FreezeImageRect.
    private bool _freezeIsPhoto;
    // The still currently shown while paused. Owned by MainForm; anyone else
    // (GrabFrame) must CLONE it, never dispose it.
    private Bitmap? _freezeImage;

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

    // ---- Command-line overrides (session-only; see SaveSettings) ---------
    // --volume/--borderless/--topmost/--fullscreen used to be written straight
    // into _settings in OnLoad, which meant a one-off launch flag became a
    // permanent setting the next time the app saved. Instead: remember the
    // persisted value and whether the property is currently CLI-overridden;
    // any user action that changes the property clears the override so the
    // user's own choice sticks, and SaveSettings writes the remembered value
    // back for anything still overridden.
    private bool _volumeOverridden, _borderlessOverridden, _topmostOverridden, _fullscreenOverridden;
    private int _persistVolume;
    private bool _persistBorderless, _persistTopmost, _persistFullscreen;

    // Set when an update has already persisted settings and torn down the
    // engines; the closing handler must not run that work a second time.
    private bool _skipSaveOnClose;

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

    // Text shown in place of the video while a device is (re)starting — see
    // RunDeviceStart. Distinct from _startingUp, which covers only the very
    // first paint before the startup timer has even run.
    private string? _busyText;

    // Set (localized) when InitVideo fails, so the empty canvas can show WHY
    // instead of just "no video"; cleared on the next successful start.
    private string? _videoError;

    public MainForm()
    {
        // Settings are loaded exactly once in Program.Main; every other place
        // (including here) reuses that instance. Calling SettingsStore.Load()
        // a second time here used to move a corrupt file aside on the FIRST
        // call and then report "first run" on this second one, silently
        // swallowing the corrupt-settings notice.
        _settings = Program.Settings;
        _video = CreateVideoEngine();
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

        // Dark chrome: the picture itself is black, and the previous light
        // (system-themed) menu/status bars framed it with bright bands that
        // fought the video for attention. Dropdowns opened from a dark strip
        // inherit its Renderer automatically; ToolStripManager.Renderer is set
        // too as a safety net for any submenu built later (device/mode lists,
        // RebuildMenus).
        _menu.Renderer = Ui.DarkRenderer;
        _ctx.Renderer = Ui.DarkRenderer;
        _status.Renderer = Ui.DarkRenderer;
        ToolStripManager.Renderer = Ui.DarkRenderer;
        _menu.BackColor = _status.BackColor = Ui.Colors.Back;
        _menu.ForeColor = _status.ForeColor = Ui.Colors.Text;

        // Freeze-frame overlay: shows the captured still exactly over the canvas,
        // clipped to its own bounds so a zoomed-in still can't overflow onto the
        // menu/status bars (see FreezeView).
        _freezeView.Visible = false;
        _freezeView.ContextMenuStrip = _ctx;
        _freezeView.DoubleClick += OnCanvasDoubleClick;
        _freezeView.MouseDown += OnCanvasMouseDown;
        _freezeView.MouseMove += OnCanvasMouseMove;
        _freezeView.MouseUp += OnCanvasMouseUp;

        Controls.Add(_canvas);
        Controls.Add(_freezeView);
        Controls.Add(_menu);
        Controls.Add(_status);
        Controls.Add(_info);
        Controls.Add(_osd);
        MainMenuStrip = _menu;
        _canvas.SendToBack();
        _osd.BringToFront();

        _osdTimer.Interval = OsdMilliseconds;
        _osdTimer.Tick += (_, _) => OsdTimerTick();

        // OSD click behaviour (mute toggle, "open the saved file", etc.) is
        // driven per-item by ShowOsd's onClick — see OsdClick().
        _osd.Click += (_, _) => OsdClick();

        _uiTimer.Interval = 500;
        _uiTimer.Tick += (_, _) => { Watchdog.Beat(); UpdateStatus(); CheckNativeResChange(); if (_info.Visible) UpdateInfo(); };

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

    /// <summary>Dark title bar to match the dark menu/status chrome — a light
    /// system title bar over a dark window otherwise looks unfinished. Fails
    /// harmlessly (no-op) on Windows builds that predate this attribute.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int one = 1;
            DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref one, sizeof(int));
        }
        catch { /* older Windows without this attribute — light title bar is fine */ }
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

        string main = _busyText ?? (_startingUp ? L.T("接続しています...") : L.T("映像が入力されていません"));
        // RGB(200,200,200) on black is ~10.6:1 — the old Brushes.Gray (128,128,128)
        // was only ~4.5:1, borderline for a large-ish message like this.
        using var font = new Font("Segoe UI", 14f);
        using var mainBrush = new SolidBrush(Color.FromArgb(255, 200, 200, 200));
        var bounds = new Rectangle(0, 0, _canvas.ClientSize.Width, _canvas.ClientSize.Height);
        int wrapWidth = Math.Max(1, (int)(bounds.Width * 0.9));
        var wrapRect = new Rectangle((bounds.Width - wrapWidth) / 2, 0, wrapWidth, bounds.Height);
        var mainFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        Size mainSz = TextRenderer.MeasureText(g, main, font, new Size(wrapWidth, int.MaxValue), mainFlags);

        // Second line only while not busy: a busy message ("接続しています: X")
        // is self-explanatory and _videoError would otherwise talk about a
        // PREVIOUS failure while a new connection attempt is in flight.
        string? second = _busyText == null
            ? (_videoError ?? (_startingUp ? null : L.T("キャプチャデバイスを接続すると自動的に表示されます")))
            : null;
        using var secondFont = new Font("Segoe UI", 10f);
        // RGB(150,150,150) on black is ~7.7:1 (≥7:1 target); the old (96,96,96) was ~3.3:1.
        using var secondBrush = new SolidBrush(Color.FromArgb(255, 150, 150, 150));
        Size secondSz = second != null
            ? TextRenderer.MeasureText(g, second, secondFont, new Size(wrapWidth, int.MaxValue), mainFlags)
            : Size.Empty;

        int totalH = mainSz.Height + (second != null ? 4 + secondSz.Height : 0);
        int top = (bounds.Height - totalH) / 2;
        TextRenderer.DrawText(g, main, font, new Rectangle(wrapRect.X, top, wrapWidth, mainSz.Height),
            mainBrush.Color, mainFlags);
        if (second != null)
        {
            int secondTop = top + mainSz.Height + 4;
            TextRenderer.DrawText(g, second, secondFont, new Rectangle(wrapRect.X, secondTop, wrapWidth, secondSz.Height),
                secondBrush.Color, mainFlags);
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
        var exit = new ToolStripMenuItem(L.T("終了"), null, (_, _) => Close());
        file.DropDownOpening += (_, _) => UpdateChecks();
        file.DropDownItems.Add(snap);
        file.DropDownItems.Add(copy);
        file.DropDownItems.Add(_miBurst);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(openDir);
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
        _audioOutputRoot = new ToolStripMenuItem(L.T("音声の出力先"));
        _audioOutputRoot.DropDownOpening += (_, _) => RebuildAudioOutputList();
        _audioOutputRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        _videoModeRoot = new ToolStripMenuItem(L.T("映像モード (解像度/FPS)"));
        _videoModeRoot.DropDownOpening += (_, _) => RebuildVideoModeList(_videoModeRoot);
        _videoModeRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });

        device.DropDownItems.Add(_videoDevicesRoot);
        device.DropDownItems.Add(_audioDevicesRoot);
        device.DropDownItems.Add(_audioOutputRoot);
        device.DropDownItems.Add(_videoModeRoot);
        device.DropDownItems.Add(new ToolStripSeparator());
        _miMute = new ToolStripMenuItem(L.T("ミュート"), null, (_, _) => ToggleMute())
        {
            ShortcutKeyDisplayString = "M",
        };
        device.DropDownItems.Add(_miMute);
        device.DropDownItems.Add(new ToolStripMenuItem(L.T("入力レベルを最大にする"), null,
            (_, _) => MaximizeCaptureLevel()));
        _miRestoreLevel = new ToolStripMenuItem(L.T("入力レベルを元に戻す"), null, (_, _) => RestoreCaptureLevel());
        device.DropDownItems.Add(_miRestoreLevel);
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(new ToolStripMenuItem(L.T("音ズレ補正..."), null, (_, _) => ShowAvSyncDialog()));

        // ---- プロファイル ----
        var profiles = BuildProfilesMenu();

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
        view.DropDownItems.Add(BuildFullscreenDisplaySubmenu());
        view.DropDownItems.Add(_miPip);
        view.DropDownItems.Add(_miFreeze);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildAspectSubmenu());
        view.DropDownItems.Add(BuildQualitySubmenu());
        view.DropDownItems.Add(BuildInterpolationItem());
        _miAdjust = new ToolStripMenuItem(L.T("映像の調整..."), null, (_, _) => ShowAdjustmentsDialog());
        view.DropDownItems.Add(_miAdjust);
        _miCrop = new ToolStripMenuItem(L.T("黒い縁の切り取り..."), null, (_, _) => ShowCropDialog());
        view.DropDownItems.Add(_miCrop);
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
        var snapCfg = new ToolStripMenuItem(L.T("スナップショット設定..."), null, (_, _) => ShowSnapshotSettings());
        var audioBufCfg = new ToolStripMenuItem(L.T("音声バッファ設定..."), null, (_, _) => ShowAudioBufferSettings());
        var deviceKeywordCfg = new ToolStripMenuItem(L.T("優先デバイス設定..."), null,
            (_, _) => ShowDeviceKeywordSettings());
        var hotkeyCfg = new ToolStripMenuItem(L.T("ホットキー設定..."), null, (_, _) => ShowHotkeySettings());
        _miHotkeys = new ToolStripMenuItem(L.T("グローバルホットキーを有効化"), null, (_, _) => ToggleGlobalHotkeys());
        _miStartup = new ToolStripMenuItem(L.T("Windows起動時に自動実行"), null, (_, _) => ToggleStartup());
        _miUpdateCheck = new ToolStripMenuItem(L.T("自動で更新する"), null, (_, _) => ToggleUpdateCheck());
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
        options.DropDownItems.Add(snapCfg);
        options.DropDownItems.Add(audioBufCfg);
        options.DropDownItems.Add(deviceKeywordCfg);
        options.DropDownItems.Add(hotkeyCfg);
        options.DropDownItems.Add(BuildPipSettingsSubmenu());
        options.DropDownItems.Add(BuildRendererSubmenu());
        _miLowLatency = new ToolStripMenuItem(L.T("低遅延モード（試験版）"), null, (_, _) => ToggleLowLatency());
        options.DropDownItems.Add(_miLowLatency);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(_miHotkeys);
        options.DropDownItems.Add(_miCursorHide);
        options.DropDownItems.Add(cursorSecs);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(_miStartup);
        options.DropDownItems.Add(_miUpdateCheck);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(lang);

        // ---- ヘルプ ----
        var help = new ToolStripMenuItem(L.T("ヘルプ(&H)"));
        var shortcuts = new ToolStripMenuItem(L.T("操作一覧..."), null, (_, _) => ShowShortcuts())
        {
            ShortcutKeyDisplayString = "F1",
        };
        _miCheckUpdate = new ToolStripMenuItem(L.T("更新を確認..."), null,
            async (_, _) => await CheckForUpdatesAsync(manual: true));
        _checkUpdateNormalFont?.Dispose();
        _checkUpdateBoldFont?.Dispose();
        _checkUpdateNormalFont = new Font(_menu.Font, FontStyle.Regular);
        _checkUpdateBoldFont = new Font(_menu.Font, FontStyle.Bold);
        help.DropDownItems.Add(shortcuts);
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("テストモード（自動試験）..."), null, (_, _) => ShowTestModeDialog()));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(_miCheckUpdate);
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("バージョン情報..."), null, (_, _) => ShowAbout()));

        _menu.Items.AddRange(new ToolStripItem[] { file, device, profiles, view, options, help });
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

    /// <summary>フレーム補間 (check item). Greyed out unless the engine can interpolate (Direct3D only).</summary>
    private ToolStripMenuItem BuildInterpolationItem()
    {
        var item = new ToolStripMenuItem(L.T("フレーム補間"), null, (_, _) => ToggleInterpolation());
        _interpItems.Add(item);
        return item;
    }

    /// <summary>拡大の画質 > 標準 / 高画質（FSR）, シャープさ. Greyed out unless the active
    /// engine can do it (Direct3D only) and the display mode actually scales smoothly.</summary>
    private ToolStripMenuItem BuildQualitySubmenu()
    {
        var m = new ToolStripMenuItem(L.T("拡大の画質"));
        var std = new ToolStripMenuItem(L.T("標準"), null, (_, _) => SetScalingQuality("standard")) { Tag = "standard" };
        var fsr = new ToolStripMenuItem(L.T("高画質（FSR）"), null, (_, _) => SetScalingQuality("fsr")) { Tag = "fsr" };
        _qualityItems.Add(std);
        _qualityItems.Add(fsr);
        m.DropDownItems.Add(std);
        m.DropDownItems.Add(fsr);
        m.DropDownItems.Add(new ToolStripSeparator());
        var sharp = new ToolStripMenuItem(L.T("シャープさ"));
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            int p = pct;
            var item = new ToolStripMenuItem($"{p}%", null, (_, _) => SetSharpness(p)) { Tag = p };
            _sharpnessItems.Add(item);
            sharp.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(sharp);
        m.DropDownItems.Add(new ToolStripSeparator());
        var native = new ToolStripMenuItem(L.T("元の細かさに合わせて拡大"));
        foreach (string key in AppSettings.NativeResValues)
        {
            string k = key;
            string label = k == "off" ? L.T("オフ") : k == "auto" ? L.T("自動") : k + "p";
            var item = new ToolStripMenuItem(label, null, (_, _) => SetNativeRes(k)) { Tag = k };
            _nativeResItems.Add(item);
            native.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(native);
        _qualityRoots.Add(m);
        return m;
    }

    /// <summary>表示 > 全画面にするモニター. The items are rebuilt every time the submenu opens, so
    /// a monitor plugged in (or removed) since the last time shows up without a restart.</summary>
    private ToolStripMenuItem BuildFullscreenDisplaySubmenu()
    {
        var m = new ToolStripMenuItem(L.T("全画面にするモニター"));
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        m.DropDownOpening += (_, _) =>
        {
            m.DropDownItems.Clear();
            var own = new ToolStripMenuItem(L.T("ウィンドウがあるモニター"), null,
                (_, _) => SetFullscreenDisplay("")) { Checked = _settings.FullscreenDisplay.Length == 0 };
            m.DropDownItems.Add(own);
            string? chosen = Displays.Find(_settings.FullscreenDisplay)?.DeviceName;
            foreach (DisplayInfo d in Displays.List())
            {
                string key = d.Key;
                m.DropDownItems.Add(new ToolStripMenuItem(d.MenuLabel(), null, (_, _) => SetFullscreenDisplay(key))
                {
                    Checked = chosen != null && chosen == d.DeviceName,
                });
            }
        };
        return m;
    }

    private void SetFullscreenDisplay(string key)
    {
        _settings.FullscreenDisplay = key;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
        if (_isFullscreen) PlaceFullscreen();   // already fullscreen: move there at once
    }

    /// <summary>The screen fullscreen should cover: the chosen one, else the one the window is on.
    /// When a chosen display is not connected, says so (once per call) and uses the window's.</summary>
    private Screen FullscreenScreen()
    {
        if (_settings.FullscreenDisplay.Length == 0) return Screen.FromControl(this);
        Screen? s = Displays.Find(_settings.FullscreenDisplay);
        if (s != null) return s;
        Log.Info($"fullscreen display '{_settings.FullscreenDisplay}' not found - using the window's display");
        ShowOsd(L.T("指定のモニターが見つからないため、今のモニターで全画面にしました"), OsdLongMilliseconds);
        return Screen.FromControl(this);
    }

    /// <summary>Put the (borderless) fullscreen window over <see cref="FullscreenScreen"/>. Moving to
    /// a monitor with another DPI makes WinForms re-apply a suggested rectangle, so the bounds are
    /// set again if the first assignment did not stick.</summary>
    private void PlaceFullscreen()
    {
        Rectangle target = FullscreenScreen().Bounds;
        if (Bounds != target)
        {
            Location = target.Location;
            Bounds = target;
            if (Bounds != target) Bounds = target;
        }
        TopMost = true;
        LayoutCanvas(ModeChangeSettleMs);
    }

    /// <summary>オプション > 描画方式.</summary>
    private ToolStripMenuItem BuildRendererSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("描画方式"));
        var mf = new ToolStripMenuItem(L.T("標準（Media Foundation）"), null, (_, _) => SwitchRenderer("mf")) { Tag = "mf" };
        var d3d = new ToolStripMenuItem(L.T("Direct3D（高画質拡大・試験版）"), null, (_, _) => SwitchRenderer("d3d")) { Tag = "d3d" };
        _rendererItems.Add(mf);
        _rendererItems.Add(d3d);
        m.DropDownItems.Add(mf);
        m.DropDownItems.Add(d3d);
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
        // RenderMode/BackColor are left at their defaults here: the dark
        // chrome (Renderer + BackColor/ForeColor) is applied once, uniformly,
        // in the constructor via Ui.DarkRenderer — setting System/Control here
        // would just be overwritten.
        _lblVideo.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _lblAudio.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _lblVolume.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _lblVolume.ToolTipText = L.T("クリックでミュート切替 ／ 映像の上でホイールまたは ↑↓ で音量");
        _lblVolume.Click += (_, _) => ToggleMute();
        _lblState.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _lblState.ForeColor = Ui.Colors.Accent;
        var filler = new ToolStripStatusLabel { Spring = true, Text = string.Empty };
        _status.Items.AddRange(new ToolStripItem[] { _lblVideo, _lblAudio, _lblVolume, _lblState, filler });
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
        _ctx.Items.Add(BuildQualitySubmenu());
        _ctx.Items.Add(BuildInterpolationItem());
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

    // ---- Shortcuts help (F1) ----------------------------------------------

    private void ShowShortcuts()
    {
        using var dlg = Ui.NewDialog(L.T("操作一覧"), 520, 470);

        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            GridLines = false,
        };
        lv.Columns.Add(L.T("操作"), 300);
        lv.Columns.Add(L.T("キー・マウス"), 180);
        // Column widths follow the list's real width (about 62 / 38 %), so they hold at any display scaling.
        void FitColumns()
        {
            int w = lv.ClientSize.Width;
            if (w <= 0) return;
            lv.Columns[0].Width = (int)(w * 0.62);
            lv.Columns[1].Width = w - lv.Columns[0].Width;
        }
        lv.ClientSizeChanged += (_, _) => FitColumns();

        var gHere = new ListViewGroup(L.T("YuCap の画面で"));
        var gGlobal = new ListViewGroup(L.T("どこからでも（グローバルホットキー）"));
        lv.Groups.Add(gHere);
        lv.Groups.Add(gGlobal);

        void Row(ListViewGroup g, string action, string keys) =>
            lv.Items.Add(new ListViewItem(new[] { action, keys }) { Group = g });

        Row(gHere, L.T("全画面表示の切替"), $"F11 ／ {L.T("ダブルクリック")}");
        Row(gHere, L.T("全画面・PiP を終了"), "Esc");
        Row(gHere, L.T("一時停止 / 再開"), "Space");
        Row(gHere, L.T("ミュート"), $"M ／ {L.T("中クリック")} ／ {L.T("音量表示をクリック")}");
        Row(gHere, L.T("音量"), $"↑ ↓ ／ {L.T("ホイール")}");
        Row(gHere, L.T("ズーム"), $"Ctrl + {L.T("ホイール")}");
        Row(gHere, L.T("ズームをリセット"), "Ctrl+0");
        Row(gHere, L.T("ズーム中の表示位置"), L.T("ドラッグ"));
        Row(gHere, L.T("ウィンドウの移動"), L.T("映像をドラッグ"));
        Row(gHere, L.T("スナップショットを保存"), "Ctrl+S");
        Row(gHere, L.T("スナップショットをコピー"), "Ctrl+C");
        Row(gHere, L.T("常に前面に表示"), "Ctrl+T");
        Row(gHere, L.T("ウィンドウ枠を非表示"), "Ctrl+B");
        Row(gHere, L.T("メニューバーの表示"), "F10");
        Row(gHere, L.T("情報表示の切替"), "Ctrl+Shift+I");
        Row(gHere, L.T("FSR と標準の拡大を見比べる"), L.T("Q（押している間）"));
        Row(gHere, L.T("プロファイルを切り替える"), "Ctrl+1 – 9");
        Row(gHere, L.T("メニュー"), L.T("右クリック"));
        Row(gHere, L.T("操作一覧"), "F1");

        Row(gGlobal, L.T("スナップショットを保存"), FormatHotkey((Keys)_settings.HotkeySnapshot));
        Row(gGlobal, L.T("ミュート"), FormatHotkey((Keys)_settings.HotkeyMute));
        Row(gGlobal, L.T("PiP の切替"), FormatHotkey((Keys)_settings.HotkeyPip));
        Row(gGlobal, L.T("音量を上げる"), FormatHotkey((Keys)_settings.HotkeyVolumeUp));
        Row(gGlobal, L.T("音量を下げる"), FormatHotkey((Keys)_settings.HotkeyVolumeDown));
        if (!_settings.GlobalHotkeys)
            Row(gGlobal, string.Empty, L.T("（グローバルホットキーは無効です）"));

        var close = new Button { Text = L.T("閉じる"), DialogResult = DialogResult.OK };
        Ui.Frame(dlg, lv, true, close);
        dlg.AcceptButton = close;
        dlg.CancelButton = close;
        dlg.ShowDialog(this);
    }

    // ---- Startup / shutdown ---------------------------------------------

    private void OnLoad(object? sender, EventArgs e)
    {
        Application.AddMessageFilter(this);
        SingleInstance.MarkWindow(Handle);   // lets a second launch find us

        // Command-line switches override the saved view state, but ONLY for
        // this launch: remember what was actually persisted and mark the
        // property "still overridden" so SaveSettings can write the
        // remembered value back instead of whatever the CLI forced. A user
        // action on the same property (NudgeVolume, ToggleBorderless,
        // ToggleAlwaysOnTop, fullscreen by user action) clears the flag, since
        // at that point it's the user's own choice, not the launch switch.
        var opts = Program.Options;
        if (opts.Borderless)
        {
            _persistBorderless = _settings.Borderless;
            _borderlessOverridden = true;
            _settings.Borderless = true;
        }
        if (opts.Topmost)
        {
            _persistTopmost = _settings.AlwaysOnTop;
            _topmostOverridden = true;
            _settings.AlwaysOnTop = true;
        }
        if (opts.Fullscreen)
        {
            _persistFullscreen = _settings.Fullscreen;
            _fullscreenOverridden = true;
            _settings.Fullscreen = true;
        }
        if (opts.Volume is int vol)
        {
            _persistVolume = _settings.Volume;
            _volumeOverridden = true;
            _settings.Volume = vol;
        }
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
        if (_settings.Fullscreen && !_isFullscreen) EnterFullscreen(userAction: false);
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

        if (TestModeGuard.Recovered)
        {
            TestModeGuard.Recovered = false;
            ShowOsd(L.T("前回のテストモードが途中で終了したため、設定を元に戻しました"), 8000);
        }

        if (Program.Options.TestMode)
        {
            StartUnattendedTest();   // no update dialogs while the test runs
            return;
        }
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
        ApplyAvSync();   // the audio side now; the video side again after every engine start
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
        if (_testRunning) return;   // test mode changes settings on purpose; they are put back afterwards, never saved
        _settings.DisplayMode = (int)_displayMode;
        _settings.LockAspect = _lockAspect;
        // While in PiP the window state is temporary — persist the pre-PiP state
        // so the restored session's "exit PiP" returns to something sensible.
        // A property still under a --switch launch override writes back the
        // value that was actually persisted before the switch, not this
        // session's (CLI-forced) live state — see OnLoad.
        _settings.Borderless = _borderlessOverridden ? _persistBorderless : (_isPip ? _prePipBorderless : _isBorderless);
        _settings.AlwaysOnTop = _topmostOverridden ? _persistTopmost : (_isPip ? _prePipTopmost : _alwaysOnTop);
        _settings.MenuVisible = _isFullscreen ? _savedMenuVisible : (_isPip ? _prePipMenu : _menu.Visible);
        _settings.StatusVisible = _isFullscreen ? _savedStatusVisible : (_isPip ? _prePipStatus : _status.Visible);
        _settings.Volume = _volumeOverridden ? _persistVolume : _audio.VolumePercent;
        _settings.AudioBufferMs = _audio.BufferMilliseconds;
        _settings.Rotation = _video.Rotation;
        _settings.Mirror = _video.Mirror;
        _settings.Fullscreen = _fullscreenOverridden ? _persistFullscreen : (_isFullscreen || (_isPip && _prePipFullscreen));
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
    /// <summary>
    /// Device start (Start()) runs synchronously on the UI thread and cannot be
    /// moved off it — the capture-engine RCWs are apartment-bound (see
    /// VideoEngine's own comments on this). That means the UI genuinely does
    /// freeze for however long Start() takes; the point of this wrapper is to
    /// never let that freeze be SILENT. It paints "接続しています: X" BEFORE
    /// blocking (an explicit Invalidate+Update forces that paint through,
    /// since the message loop itself is about to stall) and shows a wait
    /// cursor, so a slow device reads as "busy" instead of "hung".
    /// </summary>
    private void RunDeviceStart(string deviceName, Action start)
    {
        _busyText = L.F("接続しています: {0}", deviceName);
        try { _video.Stop(); } catch { /* best-effort: about to (re)start anyway */ }
        _canvas.Invalidate();
        _canvas.Update();
        Cursor = Cursors.WaitCursor;
        try
        {
            // Before the start too: the low-latency choice decides the swap chain kind when the
            // engine starts, so the engine must already know it. Idempotent and cheap.
            ApplyRenderOptions();
            start();
            ApplyRenderOptions(); // every start path goes through here
        }
        finally
        {
            _busyText = null;
            Cursor = Cursors.Default;
            _canvas.Invalidate();
        }
    }

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
            RunDeviceStart(pick.Name, () => _video.Start(pick, _cliMode ?? _savedMode));
            _currentVideoInfo = pick;
            _videoError = null;
            LayoutCanvas();               // re-fit now that the resolution is known
        }
        catch (Exception ex)
        {
            Log.Info("InitVideo failed: " + ex.Message);
            _videoError = Errors.Describe(ex);
            ShowOsd(L.F("映像: {0}", Errors.Describe(ex)), OsdLongMilliseconds);
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
            _audio.Start(pick, _settings.AudioOutputDeviceId);
            _currentAudioInfo = pick;
        }
        catch (Exception ex)
        {
            Log.Info("InitAudio failed: " + ex.Message);
            ShowOsd(L.F("音声: {0}", Errors.Describe(ex)), OsdLongMilliseconds);
        }
    }

    // ---- Device hot-plug: auto-reconnect ---------------------------------

    /// <summary>Debounced WM_DEVICECHANGE handler: restart engines when their
    /// device reappears, stop them cleanly when it is yanked, and follow the
    /// system default audio output when it changes.</summary>
    private void OnDeviceChange()
    {
        if (IsDisposed || _testRunning) return;
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
                        RunDeviceStart(pick.Name, () => _video.Start(pick, _cliMode ?? _savedMode));
                        _currentVideoInfo = pick;
                        _videoError = null;
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
            // A chosen output (not "follow Windows"): leave it when it disappears, come back when it returns.
            string? wantOut = _settings.AudioOutputDeviceId;
            bool outActive = wantOut != null && AudioEngine.RenderDeviceActive(wantOut);
            bool outVanished = _audio.IsRunning && wantOut != null && !outActive && !_audio.RenderFellBack;
            bool outReturned = _audio.IsRunning && wantOut != null && outActive && _audio.RenderFellBack;
            if (_audio.IsRunning &&
                (_audio.IsFaulted || _audio.DefaultRenderChanged() || outVanished || outReturned ||
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
                        _audio.Start(pick, wantOut);
                        _currentAudioInfo = pick;
                        if (outVanished && _audio.RenderFellBack)
                            ShowOsd(L.F("音声の出力先が見つからないため、Windows の既定で再生します: {0}", _audio.RenderDeviceName ?? "?"), OsdLongMilliseconds);
                        else if (outReturned && !_audio.RenderFellBack)
                            ShowOsd(L.F("音声の出力先に戻しました: {0}", _audio.RenderDeviceName ?? "?"), OsdLongMilliseconds);
                        else
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
        if (_testRunning)
        {
            // Closing mid-test: stop the test first (it restores the settings), then the window can close.
            e.Cancel = true;
            _testCts?.Cancel();
            return;
        }
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
            // FreezeView's own Bounds is always the video area; only its
            // ImageRect (where the still is drawn within that) depends on the
            // photo-vs-screen-copy distinction — see FreezeBounds/ImageRect.
            if (_freezeView.Bounds != _canvas.Bounds) _freezeView.Bounds = _canvas.Bounds;
            _freezeView.ImageRect = FreezeImageRect();
        }
        PositionOsd();
        PositionInfo();
        PositionTestCaption();
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

    // ---- OSD queue --------------------------------------------------------
    // Startup used to be able to fire several notices back to back (settings
    // load failure, device reconnect, new-version banner, ...) and only the
    // LAST one survived, because ShowOsd just overwrote whatever was showing.
    // A queue lets every long ("read me") notice get its turn, while short
    // status flips (volume, zoom, mute) still interrupt instantly instead of
    // waiting in line behind something the user is mid-read on.
    private readonly List<(string Text, int? Ms, Action? OnClick)> _osdQueue = new();
    private (string Text, int? Ms, Action? OnClick)? _osdCurrent;
    private int _osdShownTick;

    private static bool IsLongOsd(int? ms) => ms.HasValue && ms.Value > OsdMilliseconds;

    private void ShowOsd(string text, int? ms = null, Action? onClick = null)
    {
        var item = (text, ms, onClick);

        if (IsLongOsd(ms))
        {
            if (_osd.Visible && _osdCurrent is { } cur && IsLongOsd(cur.Ms))
            {
                // A long item is already showing: queue behind it rather than
                // stomping it, but never pile up duplicates of the same text.
                if (cur.Text == text || _osdQueue.Any(q => q.Text == text)) return;
                _osdQueue.Add(item);
                return;
            }
            DisplayOsdItem(item);
            return;
        }

        // Short item: always interrupts immediately. If it's cutting off a
        // long item that has barely had time to be read, put that item back
        // at the FRONT of the queue so it resumes once the short flip is done.
        if (_osd.Visible && _osdCurrent is { } current && IsLongOsd(current.Ms)
            && Environment.TickCount - _osdShownTick < 1500)
        {
            _osdQueue.Insert(0, current);
        }
        DisplayOsdItem(item);
    }

    private void DisplayOsdItem((string Text, int? Ms, Action? OnClick) item)
    {
        _osdCurrent = item;
        _osdShownTick = Environment.TickCount;
        int maxWidth = Math.Max(120, _canvas.Width - 24);
        _osd.ShowText(item.Text, maxWidth, clickable: item.OnClick != null);
        PositionOsd();
        _osdTimer.Stop();
        _osdTimer.Interval = Math.Max(1, item.Ms ?? OsdMilliseconds);
        _osdTimer.Start();
    }

    private void OsdTimerTick()
    {
        _osdTimer.Stop();
        AdvanceOsd();
    }

    private void AdvanceOsd()
    {
        if (_osdQueue.Count > 0)
        {
            var next = _osdQueue[0];
            _osdQueue.RemoveAt(0);
            DisplayOsdItem(next);
        }
        else
        {
            _osdCurrent = null;
            _osd.Visible = false;
        }
    }

    private void OsdClick()
    {
        Action? onClick = _osdCurrent?.OnClick;
        _osdTimer.Stop();
        onClick?.Invoke();
        AdvanceOsd();
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
            ApplyRenderOptions();
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

    // ---- Volume (hover + wheel) -----------------------------------------

    /// <summary>
    /// PreFilterMessage sees EVERY WM_MOUSEWHEEL in the app, not just ones over
    /// our own window — including wheel messages bubbling up from a
    /// NumericUpDown in a settings dialog (dialogs open centred over the
    /// video) and from a scrolling dropdown menu. Restricting to our own
    /// handles, and refusing while a menu dropdown is open, stops the video's
    /// wheel handling from hijacking those.
    /// </summary>
    private bool IsOwnWheelTarget(IntPtr hwnd) =>
        hwnd == _canvas.Handle || hwnd == _freezeView.Handle || hwnd == _osd.Handle || hwnd == Handle;

    private bool IsMenuDropdownOpen()
    {
        if (_ctx.Visible) return true;
        foreach (ToolStripMenuItem item in _menu.Items.OfType<ToolStripMenuItem>())
            if (item.IsOnDropDown || item.DropDown.Visible) return true;
        return false;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel) return false;
        if (_testRunning) return true;   // no volume / zoom changes during a test
        if (!IsOwnWheelTarget(m.HWnd) || IsMenuDropdownOpen()) return false;

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
        _volumeOverridden = false; // the user is now choosing the volume themselves
        int newVol = Math.Clamp(_audio.VolumePercent + delta, 0, AudioEngine.MaxVolumePercent);
        _audio.VolumePercent = newVol;
        if (_frozen)
        {
            // Audio stays silent while paused — changing volume must not
            // unmute it out from under the pause, unlike the normal path.
            ShowOsd(L.F("音量 {0}%（一時停止中）", newVol));
        }
        else
        {
            if (_audio.Muted) { _audio.Muted = false; UpdateChecks(); } // adjusting volume unmutes
            ShowOsd(L.F("音量 {0}%", newVol));
        }
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
        // exiting. Ask first, and remember the previous value — in settings.json,
        // tied to the device it was taken on — so it can be put back via
        // "入力レベルを元に戻す" even after a restart, matching what this dialog
        // promises ("メニューから戻せます").
        if (MessageBox.Show(this,
                L.T("入力レベルを最大にしますか？\n\nWindows の録音デバイスの音量を 100% に変更します。\nこの設定は他のアプリにも影響し、YuCap を終了しても元に戻りません。\n（メニューの「入力レベルを元に戻す」で戻せます）"),
                "YuCap", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _settings.CaptureLevelBefore = before;
        _settings.CaptureLevelDeviceId = _audio.CurrentDeviceId;
        MarkSettingsDirty();
        _audio.CaptureLevelPercent = 100;
        ShowOsd(L.F("入力レベル {0}% → 100%", before));
        UpdateStatus();
        UpdateChecks();
    }

    /// <summary>Undo the Windows-wide change made by MaximizeCaptureLevel. Only
    /// offered while the CURRENT device matches the one the level was raised
    /// on (see _miRestoreLevel.Enabled in UpdateChecks) — restoring a value
    /// captured on a different device would silently mis-set it.</summary>
    private void RestoreCaptureLevel()
    {
        if (_settings.CaptureLevelBefore < 0 || !_audio.IsRunning
            || _audio.CurrentDeviceId != _settings.CaptureLevelDeviceId) return;
        _audio.CaptureLevelPercent = _settings.CaptureLevelBefore;
        ShowOsd(L.F("入力レベルを {0}% に戻しました", _settings.CaptureLevelBefore));
        _settings.CaptureLevelBefore = -1;
        _settings.CaptureLevelDeviceId = null;
        MarkSettingsDirty();
        UpdateStatus();
        UpdateChecks();
    }

    private void ToggleMute()
    {
        if (!_audio.IsRunning) { ShowOsd(L.T("音声がありません")); return; }
        if (_frozen)
        {
            // Audio is already forced silent while paused (see ToggleFreeze);
            // flip what happens on resume instead of the live mute state.
            _mutedBeforeFreeze = !_mutedBeforeFreeze;
            ShowOsd(_mutedBeforeFreeze ? L.T("再開時にミュート: オン") : L.T("再開時にミュート: オフ"));
            UpdateChecks();
            UpdateStatus();
            return;
        }
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
            _freezeView.Visible = false;
            _freezeView.Image = null;
            _freezeImage?.Dispose();
            _freezeImage = null;
            if (!_mutedBeforeFreeze && _audio.IsRunning) _audio.Muted = false;
            ShowOsd(L.T("再開"));
        }
        else
        {
            // Prefer the capture engine's own photo sink: it is source-resolution
            // video pixels only, so it cannot pick up whatever else happens to be
            // on screen over/behind the window (another overlapping window, or a
            // translucent PiP background) the way the compositor screen copy can.
            // It is also already rotated/mirrored to match the preview — no extra
            // orientation handling needed here.
            Bitmap? still = _video.PhotoSnapshot();
            _freezeIsPhoto = still != null;
            if (still == null)
                still = _video.Snapshot(cropToVideo: false); // fallback: includes letterbox → 1:1 overlay
            if (still == null)
            {
                ShowOsd(_video.LastSnapshotError ?? L.T("映像がありません"), OsdLongMilliseconds);
                return;
            }
            _freezeImage = still;
            _freezeView.Image = still;
            _freezeView.Bounds = _canvas.Bounds;
            _freezeView.ImageRect = FreezeImageRect();
            _freezeView.Visible = true;
            _freezeView.BringToFront();
            if (_info.Visible) _info.BringToFront();
            _osd.BringToFront();
            _frozen = true;
            _mutedBeforeFreeze = _audio.Muted;
            if (_audio.IsRunning) _audio.Muted = true; // 仕様: 一時停止中は音声も停止
            ShowOsd(L.T("一時停止"));
        }
        UpdateChecks();
        UpdateStatus();
    }

    /// <summary>Where the still belongs within FreezeView's own client area
    /// (FreezeView.Bounds is always the full canvas area — see LayoutCanvas).
    /// The screen-copy fallback already includes the letterbox, so it maps 1:1
    /// onto the whole view; the photo-sink still is video pixels only and must
    /// be placed at the video's own destination rectangle instead. Anything
    /// outside this rect (e.g. while zoomed in) is naturally clipped by
    /// FreezeView rather than overflowing onto the menu/status bars.</summary>
    private Rectangle FreezeImageRect() =>
        _freezeIsPhoto ? ComputeDest(_canvas.ClientSize) : new Rectangle(Point.Empty, _canvas.ClientSize);


    // ---- Burst snapshots -------------------------------------------------

    private void ShowBurstDialog()
    {
        if (_burstTimer.Enabled)
        {
            _burstTimer.Stop();
            ShowOsd(L.T("連写を停止しました"), OsdLongMilliseconds);
            return;
        }

        using var dlg = Ui.NewDialog(L.T("連写スナップショット"), 300, 130);

        var lblInt = Ui.Label(L.T("間隔 (秒):"), Ui.Pad, 20);
        var numInt = new NumericUpDown
        {
            Minimum = 0.5m, Maximum = 3600, DecimalPlaces = 1, Increment = 0.5m,
            Value = 5, Location = new Point(110, 16), Width = 100,
        };
        var lblCnt = Ui.Label(L.T("枚数:"), Ui.Pad, 56);
        var numCnt = new NumericUpDown
        {
            Minimum = 1, Maximum = 999, Value = 10,
            Location = new Point(110, 52), Width = 100,
        };
        dlg.Controls.AddRange(new Control[] { lblInt, numInt, lblCnt, numCnt });
        Ui.AddOkCancel(dlg, L.T("開始"));
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
            Log.Info("ToggleStartup failed: " + ex);
            MessageBox.Show(this, Errors.Describe(ex), "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _miStartup.Checked = IsStartupRegistered();
    }

    // ---- Preferred-device keyword ---------------------------------------

    private void ShowDeviceKeywordSettings()
    {
        using var dlg = Ui.NewDialog(L.T("優先デバイス設定"), 340, 130);

        // Table layout: sizes come from the controls themselves, so nothing overlaps at any display scaling.
        var body = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var lbl = Ui.Label(L.T("キーワード:"), 0, 0);
        lbl.Anchor = AnchorStyles.Left;
        lbl.Margin = new Padding(0, 0, Ui.Gap, Ui.Gap);
        var txt = new TextBox { Text = _settings.DeviceKeyword, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, Ui.Gap) };
        var hint = Ui.FlowHint(
            L.T("デバイス名にこの語を含む機器を起動時に自動選択します。\n（既定: JVA14）"));
        body.Controls.Add(lbl, 0, 0);
        body.Controls.Add(txt, 1, 0);
        body.Controls.Add(hint, 0, 1);
        body.SetColumnSpan(hint, 2);
        Ui.FrameOkCancel(dlg, body);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string keyword = string.IsNullOrWhiteSpace(txt.Text) ? "JVA14" : txt.Text.Trim();
        _settings.DeviceKeyword = keyword;
        SaveSettings();

        // Applying the new keyword right away (rather than only on the next
        // device/app start) matches what the dialog implies it just did.
        var videoMatch = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), keyword);
        bool switched = false;
        if (videoMatch != null && videoMatch.Id != _currentVideoInfo?.Id)
        {
            SwitchVideoDevice(videoMatch);
            switched = true;
        }
        var audioMatch = AudioEngine.PickPreferred(AudioEngine.EnumerateDevices(), keyword);
        if (audioMatch != null && audioMatch.Id != _currentAudioInfo?.Id)
        {
            SwitchAudioDevice(audioMatch);
            switched = true;
        }

        ShowOsd(switched
            ? L.F("優先デバイス: {0}", keyword)
            : L.F("優先デバイスを「{0}」に設定しました", keyword), OsdLongMilliseconds);
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
        ApplyRenderOptions(); // integer scaling switches the engine to nearest-neighbour
        UpdateChecks();
        LayoutCanvas();
    }

    // ---- Renderer / scaling quality -------------------------------------

    private IVideoEngine CreateVideoEngine() => VideoEngines.Create(_settings.Renderer);

    /// <summary>Push the user's scaling choices into the engine. Cheap and idempotent;
    /// engines without the capability ignore the values.</summary>
    private void ApplyRenderOptions()
    {
        Size displayBefore = _video.DisplayResolution;
        try
        {
            _video.Quality = _settings.ScalingQuality == "fsr" ? ScalingQuality.Fsr : ScalingQuality.Standard;
            _video.Sharpness = _settings.FsrSharpness / 100f;
            _video.NativeResScale = _settings.NativeResScale;
            _video.PixelPerfect = _displayMode == VideoDisplayMode.IntegerScale;
            _video.Adjustments = CurrentAdjustments();   // ignored by engines without the capability
            _video.Crop = new Padding(_settings.CropLeft, _settings.CropTop, _settings.CropRight, _settings.CropBottom);
            _video.LowLatency = _settings.LowLatency;    // read by the engine at its next start
            _video.Interpolation = _settings.Interpolation;
        }
        catch (Exception ex)
        {
            Log.Info("ApplyRenderOptions failed: " + ex.Message);
        }
        ApplyAvSync();

        // A crop changes the picture's size, so the aspect-fit layout (and with it the FSR factor)
        // has to follow. Only when a picture was already there: a start fills the size in itself.
        Size displayAfter = _video.DisplayResolution;
        if (displayBefore.Width > 0 && displayBefore.Height > 0 && displayAfter != displayBefore)
            LayoutCanvas();
    }

    private VideoAdjustments CurrentAdjustments() => new(
        _settings.AdjBrightness / 100f, _settings.AdjContrast / 100f,
        _settings.AdjSaturation / 100f, _settings.AdjGamma / 100f);

    /// <summary>One signed offset drives two engines: positive delays the audio; negative
    /// cannot make audio earlier (it already plays at the smallest delay its buffer allows),
    /// so it delays the VIDEO instead. The Media Foundation preview cannot delay video, so
    /// there negative values count as 0.</summary>
    private void ApplyAvSync()
    {
        try
        {
            int off = _settings.AvOffsetMs;
            _audio.ExtraDelayMs = Math.Max(0, off);
            _video.VideoDelayMs = _video.SupportsVideoDelay ? Math.Max(0, -off) : 0;
        }
        catch (Exception ex)
        {
            Log.Info("ApplyAvSync failed: " + ex.Message);
        }
    }

    // ---- 映像の調整 -------------------------------------------------------

    private void ShowAdjustmentsDialog()
    {
        if (!_video.SupportsAdjustments) return;
        // Remembered so キャンセル can put back exactly what was there; the sliders apply live.
        (int b0, int c0, int s0, int g0) = (_settings.AdjBrightness, _settings.AdjContrast,
            _settings.AdjSaturation, _settings.AdjGamma);

        using var dlg = Ui.NewDialog(L.T("映像の調整"), 440, 262);
        const int trackX = 96, trackW = 252, valueX = 356;

        TrackBar AddRow(string name, int y, int min, int max, int value, int tick,
            Func<int, string> format, Action<int> apply)
        {
            var label = Ui.Label(name, Ui.Pad, y + 6);
            var track = new TrackBar
            {
                Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                TickFrequency = tick, SmallChange = Math.Max(1, tick / 10), LargeChange = tick,
                TickStyle = TickStyle.None,
                Location = new Point(trackX, y), Size = new Size(trackW, 32),
            };
            var valueLabel = Ui.Label(format(track.Value), valueX, y + 6);
            track.ValueChanged += (_, _) =>
            {
                valueLabel.Text = format(track.Value);
                apply(track.Value);
                ApplyRenderOptions();   // live: the video behind the dialog updates as you drag
            };
            dlg.Controls.AddRange(new Control[] { label, track, valueLabel });
            return track;
        }

        TrackBar tb = AddRow(L.T("明るさ"), 16, -100, 100, b0, 25,
            v => v.ToString("+0;-0;0"), v => _settings.AdjBrightness = v);
        TrackBar tc = AddRow(L.T("コントラスト"), 62, 0, 200, c0, 25,
            v => v + "%", v => _settings.AdjContrast = v);
        TrackBar ts = AddRow(L.T("彩度"), 108, 0, 200, s0, 25,
            v => v + "%", v => _settings.AdjSaturation = v);
        TrackBar tg = AddRow(L.T("ガンマ"), 154, 50, 200, g0, 25,
            v => (v / 100.0).ToString("0.00"), v => _settings.AdjGamma = v);

        var reset = Ui.Button(L.T("既定に戻す"), Ui.Pad, dlg.ClientSize.Height - Ui.Pad - Ui.ButtonHeight, 110);
        reset.Click += (_, _) => { tb.Value = 0; tc.Value = 100; ts.Value = 100; tg.Value = 100; };
        dlg.Controls.Add(reset);
        Ui.AddOkCancel(dlg);

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            (_settings.AdjBrightness, _settings.AdjContrast, _settings.AdjSaturation, _settings.AdjGamma) = (b0, c0, s0, g0);
            ApplyRenderOptions();
            UpdateStatus();
            return;
        }
        MarkSettingsDirty();
        UpdateStatus();
    }

    // ---- 音ズレ補正 -------------------------------------------------------

    private static string AvOffsetText(int ms) =>
        ms > 0 ? L.F("音声を {0}ms 遅らせる", ms)
        : ms < 0 ? L.F("音声を {0}ms 早める", -ms)
        : L.T("補正なし");

    /// <summary>"54 ms", "約 54 ms" for an estimate, "—" when unknown (0).</summary>
    private static string LatencyText(double ms, bool estimated) =>
        ms <= 0 ? "—" : estimated ? L.F("約 {0:0} ms", ms) : $"{ms:0} ms";

    private void ShowAvSyncDialog()
    {
        int before = _settings.AvOffsetMs;   // キャンセル puts this back

        using var dlg = Ui.NewDialog(L.T("音ズレ補正"), 440, 408);
        int w = dlg.ClientSize.Width;
        int inner = w - 2 * Ui.Pad;

        var left = Ui.Label(L.T("音声を早める"), Ui.Pad, 16);
        var right = Ui.Label(L.T("音声を遅らせる"), 0, 16);
        right.Left = w - Ui.Pad - right.PreferredWidth;

        var track = new TrackBar
        {
            Minimum = -300, Maximum = 300, Value = Math.Clamp(before, -300, 300),
            TickFrequency = 50, SmallChange = 10, LargeChange = 50,
            Location = new Point(Ui.Pad, 38), Size = new Size(inner, 45),
        };
        var value = new Label
        {
            AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(Ui.Pad, 88), Size = new Size(inner, 24),
            Font = new Font(dlg.Font, FontStyle.Bold), Text = AvOffsetText(track.Value),
        };
        var hint = Ui.Hint(L.T("映像の動きと音が合う位置にしてください。手をたたく動画などで合わせると分かりやすいです。"),
            Ui.Pad, 122, inner);
        // Live numbers: how long picture and sound take inside YuCap / Windows (offset included).
        var live = new Label
        {
            AutoSize = false, Location = new Point(Ui.Pad, 170), Size = new Size(inner, 44),
            Font = new Font(dlg.Font, FontStyle.Bold),
        };
        var liveHint = Ui.Hint(L.T("キャプチャーボードやテレビの中の遅れは含みません。目安として使ってください。"),
            Ui.Pad, 218, inner);
        var match = Ui.Button(L.T("目安に合わせる"), Ui.Pad, 258, 140);
        long matchedTick = 0;
        var mfHint = Ui.Hint(L.T("音声を早める補正は Direct3D 描画のときに使えます。標準の描画では 0 として扱います。"),
            Ui.Pad, 300, inner);
        mfHint.Visible = !_video.SupportsVideoDelay && track.Value < 0;

        bool snapping = false;
        track.ValueChanged += (_, _) =>
        {
            if (snapping) return;
            int v = (int)Math.Round(track.Value / 10.0) * 10;   // the track is free, the offset moves in 10 ms steps
            if (v != track.Value) { snapping = true; track.Value = v; snapping = false; }
            value.Text = AvOffsetText(v);
            mfHint.Visible = !_video.SupportsVideoDelay && v < 0;
            _settings.AvOffsetMs = v;
            ApplyAvSync();   // live: the sound / picture shifts while you drag
        };

        var reset = Ui.Button(L.T("既定に戻す"), Ui.Pad, dlg.ClientSize.Height - Ui.Pad - Ui.ButtonHeight, 110);
        reset.Click += (_, _) => track.Value = 0;

        // audio latency - video latency; positive = the sound comes later than the picture.
        bool Diff(out double v, out double a, out double d)
        {
            v = _video.IsRunning ? _video.VideoLatencyMs : 0;
            a = _audio.IsRunning ? _audio.LatencyMs : 0;
            d = a - v;
            return v > 0 && a > 0;
        }
        void UpdateLive()
        {
            bool known = Diff(out double v, out double a, out double d);
            string line = L.F("いまの遅れ（YuCap の中だけ）: 映像 {0} ／ 音 {1}",
                LatencyText(v, _video.VideoLatencyEstimated), LatencyText(a, false));
            string verdict = !known ? L.T("→ 測定中、または測定できません")
                : Math.Abs(d) < 10 ? L.T("→ 映像と音は、ほぼ揃っています")
                : d > 0 ? L.F("→ 音が {0:0} ms 遅れています", d)
                : L.F("→ 映像が {0:0} ms 遅れています", -d);
            live.Text = line + "\n" + verdict;
            bool settling = matchedTick != 0 && Environment.TickCount64 - matchedTick < 2500;   // the numbers still show the old offset
            match.Enabled = known && !settling;
        }
        match.Click += (_, _) =>
        {
            if (!Diff(out _, out _, out double d)) return;
            // The measured numbers already contain the current offset, so the correction is added to it.
            // Positive d (sound later) lowers the offset (earlier sound / later picture); the dialog moves in 10 ms steps.
            int target = (int)Math.Round((track.Value - d) / 10.0, MidpointRounding.AwayFromZero) * 10;
            track.Value = Math.Clamp(target, track.Minimum, track.Maximum);
            matchedTick = Environment.TickCount64;
            UpdateLive();
        };
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => UpdateLive();
        dlg.FormClosed += (_, _) => timer.Dispose();
        UpdateLive();
        timer.Start();

        dlg.Controls.AddRange(new Control[] { left, right, track, value, hint, live, liveHint, match, mfHint, reset });
        Ui.AddOkCancel(dlg);

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            _settings.AvOffsetMs = before;
            ApplyAvSync();
            return;
        }
        MarkSettingsDirty();
        ShowOsd(AvOffsetText(_settings.AvOffsetMs));
    }

    private void SetScalingQuality(string quality)
    {
        _settings.ScalingQuality = quality;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        UpdateStatus();
        ShowOsd(L.F("拡大の画質: {0}", quality == "fsr" ? L.T("高画質（FSR）") : L.T("標準")));
    }

    private void SetNativeRes(string key)
    {
        _settings.NativeResScale = AppSettings.NormalizeNativeRes(key);
        _nativeResOsdTarget = -1;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        string label = key == "off" ? L.T("オフ") : key == "auto" ? L.T("自動") : key + "p";
        ShowOsd(L.F("元の細かさに合わせて拡大: {0}", label));
    }

    /// <summary>Tell the user, once per decision, what 自動 settled on (called from the UI timer).</summary>
    private void CheckNativeResChange()
    {
        if (_settings.NativeResScale != "auto" || !_video.IsRunning || !_video.SupportsNativeRes || _testRunning)
        {
            _nativeResOsdTarget = -1;
            return;
        }
        NativeResStatus? st = _video.NativeResState;
        if (st == null || st.Judging) { _nativeResOsdTarget = -1; return; }
        if (st.Target == _nativeResOsdTarget) return;
        _nativeResOsdTarget = st.Target;
        ShowOsd(st.Target > 0 ? L.F("元の細かさ: {0}p 相当に合わせて拡大", st.Target) : L.F("元の細かさ: {0}p のまま", st.SourceHeight));
    }

    /// <summary>The info overlay's 元の細かさ line.</summary>
    private string NativeResInfoText()
    {
        if (!_video.SupportsNativeRes) return "—";
        if (_settings.NativeResScale == "off") return L.T("オフ");
        NativeResStatus? st = _video.NativeResState;
        if (st == null) return "—";
        if (st.Active > 0) return L.F("{0}p 相当（{0}p から拡大）", st.Active);
        if (st.Judging) return L.T("判定中");
        if (st.Target <= 0) return L.F("{0}p のまま（縮めません）", st.SourceHeight);
        return L.F("{0}p 相当（今は使っていません）", st.Target);
    }

    private void ToggleInterpolation()
    {
        if (!_video.SupportsInterpolation) return;
        _settings.Interpolation = !_settings.Interpolation;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        ShowOsd(InterpolationOsdText(), _settings.Interpolation ? OsdLongMilliseconds : null);
    }

    /// <summary>The OSD line after toggling フレーム補間: on and running, or on but idle with the reason.</summary>
    private string InterpolationOsdText()
    {
        if (!_settings.Interpolation) return L.T("フレーム補間: オフ");
        if (!_video.SupportsInterpolation) return L.T("フレーム補間: Direct3D 描画で使えます");
        InterpolationStatus? st = _video.InterpolationState;
        if (st == null || st.Idle == InterpolationIdle.NoSignal) return L.T("フレーム補間: オン（映像が始まると働きます）");
        return st.Idle switch
        {
            InterpolationIdle.None => L.F("フレーム補間: オン（{0:0}→{1:0} fps）", st.SourceFps, st.RefreshHz),
            InterpolationIdle.LowLatency => L.T("フレーム補間: オン（低遅延モード中は使えません）"),
            InterpolationIdle.RefreshTooLow => L.F("フレーム補間: オン（画面 {0:0}Hz が映像 {1:0}fps より十分高くないため今は働きません）", st.RefreshHz, st.SourceFps),
            InterpolationIdle.Unavailable => L.T("フレーム補間: この環境では使えません"),
            _ => L.T("フレーム補間: オン"),
        };
    }

    /// <summary>The info overlay's 補間 line.</summary>
    private string InterpolationInfoText()
    {
        if (!_video.SupportsInterpolation) return "—";
        if (!_settings.Interpolation) return L.T("オフ");
        InterpolationStatus? st = _video.InterpolationState;
        if (st == null) return "—";
        return st.Idle switch
        {
            InterpolationIdle.None => $"{st.SourceFps:0}→{st.RefreshHz:0} fps",
            InterpolationIdle.LowLatency => L.T("待機（低遅延モード）"),
            InterpolationIdle.RefreshTooLow => L.F("待機（画面 {0:0}Hz）", st.RefreshHz),
            InterpolationIdle.NoSignal => L.T("待機（映像待ち）"),
            InterpolationIdle.Unavailable => L.T("使えません"),
            _ => L.T("オフ"),
        };
    }

    private void SetSharpness(int pct)
    {
        _settings.FsrSharpness = pct;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        ShowOsd(L.F("シャープさ: {0}%", pct));
    }

    private static string RendererName(string renderer) =>
        renderer == "d3d" ? L.T("Direct3D（高画質拡大・試験版）") : L.T("標準（Media Foundation）");

    /// <summary>
    /// Replace the engine with the one for <paramref name="renderer"/> and restart the
    /// current device on it. Returns null on success, else the failure (the engine is
    /// then left stopped and <c>_settings.Renderer</c> unchanged).
    /// The canvas window MUST be recreated first: both engines bind a DirectComposition
    /// target to the HWND and a window that already has one cannot be given another
    /// (same reason as RestartPreview).
    /// </summary>
    private Exception? ReplaceEngine(string renderer, CaptureMode? modeOverride = null)
    {
        try
        {
            try { _video.Stop(); } catch { /* going away anyway */ }
            try { _video.Dispose(); } catch { /* going away anyway */ }
            _canvas.ResetHandle();
            _video = VideoEngines.Create(renderer);
            _video.Attach(_canvas.Handle);
            _video.SetRotation(_settings.Rotation);
            _video.SetMirror(_settings.Mirror);
            if (_currentVideoInfo != null)
                RunDeviceStart(_currentVideoInfo.Name, () => _video.Start(_currentVideoInfo, modeOverride ?? _cliMode ?? _savedMode));
            ApplyRenderOptions();
            _settings.Renderer = renderer;
            return null;
        }
        catch (Exception ex)
        {
            Log.Info($"ReplaceEngine({renderer}) failed: {ex}");
            try { _video.Stop(); } catch { /* ignore */ }
            return ex;
        }
    }

    private void SwitchRenderer(string renderer)
    {
        if (renderer == _settings.Renderer) { UpdateChecks(); return; }
        string previous = _settings.Renderer;
        if (_frozen) ToggleFreeze();

        Exception? failure = ReplaceEngine(renderer);
        if (failure != null)
        {
            // Fall back to the engine that was working, the same way.
            string reason = Errors.Describe(failure);
            Exception? back = ReplaceEngine(previous);
            _videoError = back != null ? Errors.Describe(back) : null;
            LayoutCanvas();
            UpdateStatus();
            UpdateChecks();
            MessageBox.Show(this, reason, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowOsd(L.T("Direct3D 描画を開始できなかったため、標準の描画に戻しました"), OsdLongMilliseconds);
            return;
        }

        _videoError = null;
        LayoutCanvas();
        UpdateStatus();
        UpdateChecks();
        SaveSettings();
        ShowOsd(L.F("描画方式: {0}", RendererName(renderer)));
    }

    /// <summary>Grey out the quality menu when it cannot apply, and say why.</summary>
    private void UpdateQualityMenu()
    {
        string quality = _settings.ScalingQuality;
        bool supported = _video.SupportsScalingQuality;
        bool pixelPerfect = _displayMode == VideoDisplayMode.IntegerScale;
        string tip = !supported ? L.T("Direct3D 描画のときに使えます（オプション → 描画方式）")
            : pixelPerfect ? L.T("整数倍表示では使いません")
            : string.Empty;
        foreach (var root in _qualityRoots)
        {
            root.Enabled = supported && !pixelPerfect;
            root.ToolTipText = tip;
        }
        foreach (var item in _qualityItems) item.Checked = (string)item.Tag! == quality;
        foreach (var item in _nativeResItems) item.Checked = (string)item.Tag! == _settings.NativeResScale;
        foreach (var item in _sharpnessItems)
        {
            item.Checked = (int)item.Tag! == _settings.FsrSharpness;
            item.Enabled = quality == "fsr";
        }
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
        if (_testRunning) return;
        // F11 / double-click: a deliberate user action, so a --fullscreen
        // launch override no longer applies once the user has touched it.
        if (_isFullscreen) ExitFullscreen(userAction: true);
        else EnterFullscreen(userAction: true);
    }

    /// <param name="userAction">True when this is the user's own decision to
    /// enter fullscreen (F11, double-click, forwarded --fullscreen from a
    /// second launch) — clears a --fullscreen launch override so SaveSettings
    /// persists the user's own choice. False for internal restores (initial
    /// OnLoad restore, PiP transitions preserving pre-PiP state).</param>
    private void EnterFullscreen(bool userAction = true)
    {
        Log.Info("EnterFullscreen: begin");
        if (userAction) _fullscreenOverridden = false;
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
        Rectangle fsBounds = FullscreenScreen().Bounds;
        Location = fsBounds.Location;   // onto the chosen monitor first (DPI changes happen here)
        Bounds = fsBounds;
        if (Bounds != fsBounds) Bounds = fsBounds;
        TopMost = true;
        Watchdog.Done();
        Log.Info("EnterFullscreen: bounds set, layouting");
        LayoutCanvas(ModeChangeSettleMs);
        Log.Info("EnterFullscreen: done (video rect deferred)");
    }

    private void ExitFullscreen(bool userAction = true)
    {
        Log.Info("ExitFullscreen: begin");
        if (userAction) _fullscreenOverridden = false;
        _isFullscreen = false;
        _menu.Visible = _savedMenuVisible;
        _status.Visible = _savedStatusVisible;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = _restoreBounds;
        if (Bounds != _restoreBounds) Bounds = _restoreBounds;   // may have crossed a DPI boundary
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
        _borderlessOverridden = false; // the user is now choosing this themselves
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
        _topmostOverridden = false; // the user is now choosing this themselves
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

    /// <summary>Submenu 音声の出力先: the Windows default plus every active output, rebuilt each time it opens.</summary>
    private void RebuildAudioOutputList()
    {
        _audioOutputRoot.DropDownItems.Clear();
        string? chosen = _settings.AudioOutputDeviceId;
        try
        {
            string defName = AudioEngine.DefaultRenderName() ?? L.T("なし");
            var def = new ToolStripMenuItem(L.F("Windows の既定（{0}）", defName)) { Checked = chosen == null };
            def.Click += (_, _) => SwitchAudioOutput(null, null);
            _audioOutputRoot.DropDownItems.Add(def);
            _audioOutputRoot.DropDownItems.Add(new ToolStripSeparator());

            var outputs = AudioEngine.EnumerateRenderDevices();
            foreach (var o in outputs)
            {
                var info = o;
                var item = new ToolStripMenuItem(info.Name) { Checked = chosen != null && info.Id == chosen };
                item.Click += (_, _) => SwitchAudioOutput(info.Id, info.Name);
                _audioOutputRoot.DropDownItems.Add(item);
            }
            if (chosen != null && !outputs.Any(o => o.Id == chosen))
                _audioOutputRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（選んだ出力先は接続されていません）")) { Checked = true, Enabled = false });
            if (outputs.Count == 0)
                _audioOutputRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
        }
        catch (Exception ex)
        {
            Log.Info("RebuildAudioOutputList failed: " + ex.Message);
            _audioOutputRoot.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
        }
    }

    /// <summary>Play the capture audio on another output (null = follow the Windows default). Volume, mute and
    /// the A/V delay live in the engine / settings and carry over to the restarted stream.</summary>
    private void SwitchAudioOutput(string? id, string? name)
    {
        string? previous = _settings.AudioOutputDeviceId;
        _settings.AudioOutputDeviceId = id;
        if (_audio.IsRunning && _currentAudioInfo != null)
        {
            try { _audio.Start(_currentAudioInfo, id); }
            catch (Exception ex)
            {
                _settings.AudioOutputDeviceId = previous;
                MessageBox.Show(this, L.F("音声の出力先の切り替えに失敗しました。\n\n{0}", Errors.Describe(ex)),
                    "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                try { _audio.Start(_currentAudioInfo, previous); } catch { /* the next device change retries */ }
                UpdateStatus();
                return;
            }
            // volume / mute / ExtraDelayMs are fields of the engine, so the new stream has them; OnDataAvailable re-queues the extra delay as silence
        }
        SaveSettings();
        ShowOsd(L.F("音声の出力先: {0}", name ?? _audio.RenderDeviceName ?? AudioEngine.DefaultRenderName() ?? L.T("Windows の既定")));
        UpdateStatus();
    }

    private void SwitchVideoDevice(VideoDeviceInfo info)
    {
        if (_frozen) ToggleFreeze();
        try
        {
            RunDeviceStart(info.Name, () => _video.Start(info, _savedMode));
            _currentVideoInfo = info;
            _videoError = null;
            LayoutCanvas();
            ShowOsd(L.F("映像: {0}", info.Name));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("映像デバイスの切り替えに失敗しました。\n\n{0}", Errors.Describe(ex)),
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
            RunDeviceStart(_currentVideoInfo.Name, () => _video.Start(_currentVideoInfo, _savedMode));
            LayoutCanvas();
        }
        catch { /* the error was already reported; leave the empty canvas */ }
    }

    private void RebuildVideoModeList(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();

        // Renamed from "自動 (最大解像度)": the code keeps the device's OWN
        // default mode, it never picks the maximum resolution — the old label
        // promised something this never actually did.
        var auto = new ToolStripMenuItem(L.T("自動（機器の既定）"), null, (_, _) => ClearModePreference())
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
            var item = new ToolStripMenuItem(ModeLabel(mode))
            {
                Checked = _savedMode != null && mode.Equals(cur),
            };
            item.Click += (_, _) => ApplyVideoMode(mode);
            root.DropDownItems.Add(item);
        }
    }

    /// <summary>
    /// Human-readable mode label: common name first (1440p60), exact details after.
    /// Contains no translatable words, so it deliberately bypasses L.T.
    /// </summary>
    private static string ModeLabel(CaptureMode m)
    {
        string? name = (m.Width, m.Height) switch
        {
            (640, 360) => $"360p{m.Fps}",
            (1280, 720) => $"720p{m.Fps}",
            (1920, 1080) => $"1080p{m.Fps}",
            (2560, 1440) => $"1440p{m.Fps}",
            (3840, 2160) => $"4K{m.Fps}",
            (720, 480) => $"480p{m.Fps}",
            (640, 480) => $"480p{m.Fps}",
            _ => null,
        };
        bool hasComp = !string.IsNullOrEmpty(m.Compression);
        if (name != null)
            return hasComp ? $"{name}（{m.Width}x{m.Height} {m.Compression}）" : $"{name}（{m.Width}x{m.Height}）";
        return hasComp ? $"{m.Width}x{m.Height} {m.Fps}fps（{m.Compression}）" : $"{m.Width}x{m.Height} {m.Fps}fps";
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
            RunDeviceStart(_currentVideoInfo.Name, () => _video.Start(_currentVideoInfo, mode));
            _videoError = null;
            _savedMode = _video.CurrentMode;
            SaveSettings();
            LayoutCanvas();
            ShowOsd(ModeLabel(mode));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("映像モードの変更に失敗しました。\n\n{0}", Errors.Describe(ex)),
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
            RunDeviceStart(_currentVideoInfo.Name, () => _video.Start(_currentVideoInfo, null));
            _videoError = null;
            LayoutCanvas();
            ShowOsd(L.T("映像モード: 自動"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("映像モードの変更に失敗しました。\n\n{0}", Errors.Describe(ex)),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RecoverVideo();
        }
        UpdateStatus();
    }

    private void SwitchAudioDevice(AudioDeviceInfo info)
    {
        try
        {
            _audio.Start(info, _settings.AudioOutputDeviceId);
            _currentAudioInfo = info;
            ShowOsd(L.F("音声: {0}", info.Name));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("音声デバイスの切り替えに失敗しました。\n\n{0}", Errors.Describe(ex)),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStatus();
    }

    // ---- Audio buffer settings ------------------------------------------

    private void ShowAudioBufferSettings()
    {
        using var dlg = Ui.NewDialog(L.T("音声バッファ設定"), 360, 210);

        var lbl = Ui.Label(L.T("バッファ長 (ms):"), Ui.Pad, 22);
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
        var lblPreset = Ui.Label(L.T("プリセット:"), Ui.Pad, 58);
        var btnLow = Ui.Button(L.T("低遅延 60"), Ui.Pad, 78, 100);
        var btnMid = Ui.Button(L.T("標準 120"), 126, 78, 100);
        var btnSafe = Ui.Button(L.T("安定 250"), 236, 78, 100);
        btnLow.Click += (_, _) => num.Value = 60;
        btnMid.Click += (_, _) => num.Value = 120;
        btnSafe.Click += (_, _) => num.Value = 250;

        var hint = Ui.Hint(
            L.T("この値が実際の音声遅延の目安になります。\n小さいほど低遅延ですが、音切れが出たら上げてください。\n実測値はステータスバーの「遅延」に表示されます。"),
            Ui.Pad, 114, 360 - 2 * Ui.Pad);

        dlg.Controls.AddRange(new Control[] { lbl, num, lblPreset, btnLow, btnMid, btnSafe, hint });
        Ui.AddOkCancel(dlg);

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _audio.BufferMilliseconds = (int)num.Value;
        if (_audio.IsRunning && _currentAudioInfo != null)
        {
            try { _audio.Start(_currentAudioInfo, _settings.AudioOutputDeviceId); }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.F("音声の再初期化に失敗しました。\n\n{0}", Errors.Describe(ex)),
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
            if (_testRunning) return;
            switch ((int)m.WParam)
            {
                case HkSnapshot: SaveSnapshot(); break;
                case HkMute: ToggleMute(); break;
                case HkPip: TogglePip(); break;
                case HkVolUp: NudgeVolume(+VolumeStep); break;
                case HkVolDown: NudgeVolume(-VolumeStep); break;
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

    /// <summary>True while FSR is really resampling: the quality engine is running with FSR
    /// selected, integer scaling is off, and the picture is drawn larger than the source.</summary>
    private bool IsFsrUpscaling()
    {
        if (!_video.IsRunning || !_video.SupportsScalingQuality) return false;
        if (_video.Quality != ScalingQuality.Fsr || _video.PixelPerfect) return false;
        Size res = _video.DisplayResolution;
        if (res.Width <= 0 || res.Height <= 0) return false;
        Rectangle dest = ComputeDest(_canvas.ClientSize);
        return dest.Width > res.Width || dest.Height > res.Height;
    }

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
            if (_video.Stats is { } st)
                _lblVideo.ToolTipText += "\n" + L.F("入力 {0:0}fps / 表示 {1:0}fps / 欠落 {2}",
                    st.InputFps, st.PresentedFps, st.DroppedFrames);
        }
        else
        {
            _lblVideo.Text = $"{vLabel}: {L.T("なし")}";
            _lblVideo.ToolTipText = string.Empty;
        }

        if (_audio.CurrentDeviceName != null)
        {
            // Whole in-app audio delay (capture packet + backlog + device queue); backlog only until measured.
            double lat = _audio.LatencyMs > 0 ? _audio.LatencyMs : _audio.BufferedMs;
            int delay = (int)(Math.Round(lat / 10) * 10);
            string shortName = Ellipsize(_audio.CurrentDeviceName, 20);
            _lblAudio.Text = $"{aLabel}: {shortName}  {L.T("遅延")} ~{delay}ms";
            _lblAudio.ToolTipText = $"{aLabel}: {_audio.CurrentDeviceName}  {L.T("遅延")} ~{delay}ms"
                + "\n" + L.F("出力先: {0}", (_audio.RenderDeviceName ?? "?")
                    + (_audio.FollowingDefault ? " " + L.T("（Windows の既定）") : string.Empty));
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
        _lblVolume.ToolTipText = L.T("クリックでミュート切替 ／ 映像の上でホイールまたは ↑↓ で音量")
            + (!_audio.Muted && _audio.IsLimiting
                ? "\n" + L.T("! = 音量が大きすぎるため、音割れを防ぐ処理が働いています")
                : string.Empty);

        // Current-mode label: at most one relevant note at a time, so the bar
        // never crowds out the device labels next to it.
        var stateParts = new List<string>();
        if (_settings.ActiveProfile is { } activeProfile)
            stateParts.Add(ActiveProfileModified() ? L.F("{0}（変更あり）", activeProfile) : activeProfile);
        if (_frozen) stateParts.Add(L.T("一時停止中"));
        if (_isPip) stateParts.Add(L.T("PiP"));
        if (_burstTimer.Enabled) stateParts.Add(L.F("連写 {0}/{1}", _burstDone, _burstTotal));
        if (_zoom > 1.001) stateParts.Add(L.F("ズーム {0}%", (int)Math.Round(_zoom * 100)));
        if (IsFsrUpscaling()) stateParts.Add(L.T("FSR"));
        if (_video.SupportsAdjustments && !CurrentAdjustments().IsNeutral) stateParts.Add(L.T("色調整"));
        if (_video.SupportsCrop && HasCrop()) stateParts.Add(L.T("切り取り"));
        if (_video.SupportsLowLatency && _settings.LowLatency) stateParts.Add(L.T("低遅延"));
        _lblState.Text = string.Join("  ", stateParts);
        _lblState.Visible = stateParts.Count > 0;

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

        foreach (var item in _rendererItems)
            item.Checked = (string)item.Tag! == _settings.Renderer;
        UpdateQualityMenu();
        _miAdjust.Enabled = _video.SupportsAdjustments;
        _miAdjust.ToolTipText = _video.SupportsAdjustments
            ? string.Empty : L.T("Direct3D 描画のときに使えます（オプション → 描画方式）");

        _miCrop.Enabled = _video.SupportsCrop;
        _miCrop.ToolTipText = _video.SupportsCrop
            ? string.Empty : L.T("Direct3D 描画のときに使えます（オプション → 描画方式）");
        foreach (var it in _interpItems)
        {
            it.Enabled = _video.SupportsInterpolation;
            it.Checked = _video.SupportsInterpolation && _settings.Interpolation;
            it.ToolTipText = _video.SupportsInterpolation
                ? L.T("画面の更新が映像のコマ数より十分速いときだけ、間のコマを作って滑らかにします。映像が 1 コマぶん遅れます。")
                : L.T("Direct3D 描画のときに使えます（オプション → 描画方式）");
        }
        _miLowLatency.Enabled = _video.SupportsLowLatency;
        _miLowLatency.Checked = _video.SupportsLowLatency && _settings.LowLatency;
        _miLowLatency.ToolTipText = _video.SupportsLowLatency
            ? string.Empty : L.T("Direct3D 描画のときに使えます（オプション → 描画方式）");
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
        _miRestoreLevel.Enabled = _settings.CaptureLevelBefore >= 0 && _audio.IsRunning
            && _audio.CurrentDeviceId == _settings.CaptureLevelDeviceId;

        // A found-but-not-skipped update relabels the help menu item so it's
        // discoverable without a startup dialog the user has to dismiss.
        if (_pendingUpdate != null)
        {
            // Staged: the click re-checks and lands in the staged branch of
            // CheckForUpdatesAsync(manual: true), which asks to restart now.
            _miCheckUpdate.Text = _updateStaged
                ? L.F("再起動して更新: {0}...", _pendingUpdate.Version)
                : L.F("更新があります: {0}...", _pendingUpdate.Version);
            _miCheckUpdate.Font = _checkUpdateBoldFont;
        }
        else
        {
            _miCheckUpdate.Text = L.T("更新を確認...");
            _miCheckUpdate.Font = _checkUpdateNormalFont;
        }

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
        // Test mode: Esc aborts, every other key is swallowed so nothing changes under the test.
        if (_testRunning)
        {
            if (keyData == Keys.Escape) _testCts?.Cancel();
            return true;
        }

        // Configured hotkeys also work app-locally: if global registration
        // failed (combo owned by another app) or is disabled, the key still
        // works while YuCap has focus. When globally registered, the key never
        // reaches here, so this cannot double-fire.
        if ((keyData & Keys.KeyCode) != Keys.None)
        {
            if (keyData == (Keys)_settings.HotkeySnapshot) { SaveSnapshot(); return true; }
            if (keyData == (Keys)_settings.HotkeyMute) { ToggleMute(); return true; }
            if (keyData == (Keys)_settings.HotkeyPip) { TogglePip(); return true; }
            if (keyData == (Keys)_settings.HotkeyVolumeUp) { NudgeVolume(+VolumeStep); return true; }
            if (keyData == (Keys)_settings.HotkeyVolumeDown) { NudgeVolume(-VolumeStep); return true; }
        }

        // Bit 30 of lParam ("previous key state") is set on every WM_KEYDOWN
        // generated by the key still being held down — i.e. auto-repeat, not a
        // fresh press. Holding Ctrl+S must fire ONE snapshot, not one per
        // repeat interval.
        bool isRepeat = ((long)msg.LParam & 0x40000000) != 0;

        // Ctrl+1 .. Ctrl+9: apply the profile in that menu position.
        if (ProfileIndexForKey(keyData) is int profileIndex)
        {
            if (!isRepeat) ApplyProfileAt(profileIndex);
            return true;
        }

        switch (keyData)
        {
            case Keys.F11:
                ToggleFullscreen();
                return true;
            case Keys.F10:
                ToggleMenuBar();
                return true;
            case Keys.F1:
                ShowShortcuts();
                return true;
            case Keys.Control | Keys.S:
                if (!isRepeat) SaveSnapshot();
                return true;
            case Keys.Control | Keys.C:
                if (!isRepeat) CopySnapshotToClipboard();
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
            case Keys.Control | Keys.Shift | Keys.I:
                if (!isRepeat) ToggleInfo();
                return true;
            case Keys.Q:
                if (!isRepeat) BeginCompare();
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
                if (_isPip) { ExitPip(); return true; }
                break;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Q) EndCompare();
        base.OnKeyUp(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        EndCompare(silent: true);   // key-up never arrives once focus is gone
        base.OnDeactivate(e);
    }

    // ---- Compare FSR / Standard (hold Q) ----------------------------------

    private void BeginCompare()
    {
        if (_compareHeld) return;
        if (!_video.SupportsScalingQuality || _video.Quality != ScalingQuality.Fsr || _video.PixelPerfect)
        {
            ShowOsd(L.T("見比べは Direct3D 描画で高画質（FSR）のときに使えます"), OsdLongMilliseconds);
            return;
        }
        _compareHeld = true;
        _video.Quality = ScalingQuality.Standard;   // temporary: _settings is untouched
        ShowOsd(L.T("標準の拡大で表示中（Q を離すと FSR）"), OsdLongMilliseconds);
    }

    private void EndCompare(bool silent = false)
    {
        if (!_compareHeld) return;
        _compareHeld = false;
        try { if (_video.SupportsScalingQuality) _video.Quality = ScalingQuality.Fsr; } catch { }
        if (!silent) ShowOsd(L.T("FSR"));
    }

    // ---- Info overlay (Ctrl+Shift+I) ----------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode dm);
    private const int EnumCurrentSettings = -1;

    private void ToggleInfo()
    {
        if (_info.Visible) { _info.Visible = false; return; }
        UpdateInfo();
        _info.Visible = true;
        _info.BringToFront();
        _osd.BringToFront();
        PositionInfo();
    }

    private void PositionInfo()
    {
        if (_info.Visible)
            _info.Location = new Point(_canvas.Right - _info.Width - 12, _canvas.Top + 12);
    }

    private string DescribeScaling()
    {
        Size res = _video.DisplayResolution;
        if (!_video.IsRunning || res.Width <= 0 || res.Height <= 0) return "—";
        Rectangle d = ComputeDest(_canvas.ClientSize);
        double fx = (double)d.Width / res.Width, fy = (double)d.Height / res.Height;
        string f = Math.Abs(fx - fy) < 0.01 ? $"{fx:0.00}" : $"{fx:0.00}×{fy:0.00}";
        if (d.Width == res.Width && d.Height == res.Height) return L.T("等倍");
        // D3DRenderer uses FSR only when upscaling in BOTH dimensions.
        if (d.Width > res.Width && d.Height > res.Height)
        {
            if (_video.SupportsScalingQuality && _video.PixelPerfect) return L.T("整数倍") + $" ×{fx:0.##}";
            if (_video.SupportsScalingQuality && _video.Quality == ScalingQuality.Fsr) return "FSR ×" + f;
            return L.T("標準") + " ×" + f;
        }
        if (d.Width < res.Width && d.Height < res.Height) return L.T("縮小") + " ×" + f;
        return L.T("標準") + " ×" + f;
    }

    private void UpdateInfo()
    {
        string renderer = _video.SupportsScalingQuality ? "Direct3D" : L.T("標準（Media Foundation）");
        string input = _video.CurrentMode is { } m ? ModeLabel(m) : "—";
        string fps = _video.Stats is { } st
            ? L.F("入力 {0:0} / 表示 {1:0} / 欠落 {2}", st.InputFps, st.PresentedFps, st.DroppedFrames)
            : "—";

        string gpu = "—";
        if (_video.Stats is { GpuMsAvg: > 0 } gs)
        {
            double budget = 1000.0 / Math.Max(1, _video.NegotiatedFps);
            gpu = L.F("{0:0.00}ms（最大 {1:0.00}ms）/ 1コマ {2:0.0}ms", gs.GpuMsAvg, gs.GpuMsMax, budget);
            if (gs.GpuMsMax > budget * 0.8) gpu += " " + L.T("（余裕なし）");
        }

        string screen = "—";
        try
        {
            var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettingsW(Screen.FromControl(this).DeviceName, EnumCurrentSettings, ref dm))
            {
                int scale = (int)Math.Round(GetDpiForWindow(Handle) * 100.0 / 96.0);
                screen = $"{dm.dmPelsWidth}x{dm.dmPelsHeight} @ {dm.dmDisplayFrequency}Hz  {scale}%";
            }
        }
        catch { }

        string delay = L.F("映像 {0:0}ms / 音声 {1:0}ms", _video.VideoDelayActualMs, _audio.BufferedMs);
        if (_audio.ExtraDelayMs > 0) delay += $" (+{_audio.ExtraDelayMs}ms)";
        string color = _video.SupportsAdjustments && !CurrentAdjustments().IsNeutral ? L.T("オン") : L.T("オフ");

        _info.SetText(string.Join("\n", new[]
        {
            L.T("描画方式") + ": " + renderer,
            L.T("入力") + ": " + input,
            "fps: " + fps,
            "GPU: " + gpu,
            L.T("画面") + ": " + screen,
            L.T("拡大") + ": " + DescribeScaling(),
            L.T("遅延") + ": " + delay,
            L.T("遅延（YuCap 内）") + ": " + L.F("映像 {0} ／ 音 {1}",
                LatencyText(_video.IsRunning ? _video.VideoLatencyMs : 0, _video.VideoLatencyEstimated),
                LatencyText(_audio.IsRunning ? _audio.LatencyMs : 0, false)),
            L.T("色調整") + ": " + color,
            L.T("補間") + ": " + InterpolationInfoText(),
            L.T("元の細かさ") + ": " + NativeResInfoText(),
            L.T("表示方法") + ": " + (string.IsNullOrEmpty(_video.PresentMode) ? "—" : _video.PresentMode),
        }));
        PositionInfo();
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
            _checkUpdateNormalFont?.Dispose();
            _checkUpdateBoldFont?.Dispose();
            _freezeImage?.Dispose();
            ShowCursorIfHidden();   // belt and braces: Cursor.Hide() is process-wide
        }
        base.Dispose(disposing);
    }
}
