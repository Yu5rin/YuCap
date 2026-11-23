using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private const int WmDisplayChange = 0x007E;
    private const int WmHotkey = 0x0312;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmEndSession = 0x0016;
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>A modal dialog or message box of ours is up: Windows disables the owner window for as long as it is
    /// open. Automatic actions (signal fullscreen, profile switch) and hotkeys wait or stand down meanwhile, so they
    /// do not change what the dialog is showing or editing.</summary>
    private bool ModalOpen => IsHandleCreated && !IsWindowEnabled(Handle);
    private const int PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12;

    // Global hotkeys (RegisterHotKey).
    private const int HkSnapshot = 1, HkMute = 2, HkPip = 3, HkVolUp = 4, HkVolDown = 5, HkStyle = 6;
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
    private readonly InfoOverlay _info = new();   // Ctrl+Shift+I: 情報表示 (off / FPS だけ / 詳しく)
    private bool _compareHeld;                      // Q held: FSR temporarily swapped for Standard
    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblVideo = new();
    private readonly ToolStripStatusLabel _lblAudio = new();
    private readonly ToolStripStatusLabel _lblVolume = new();
    private readonly ToolStripStatusLabel _lblState = new();
    private readonly ToolStripStatusLabel _lblInterp = new(), _lblScale = new(), _lblCrisp = new();
    // Replaced by a new instance whenever the display scaling changes (see RebuildMenus / OnCtxOpening).
    private ContextMenuStrip _ctx = new();
    private int _ctxDpi;               // the scaling _ctx was built for
    private bool _ctxFixPending;       // a rebuild is queued
    private readonly System.Windows.Forms.Timer _osdTimer = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly System.Windows.Forms.Timer _fpsTimer = new() { Interval = 250 };   // FPS だけ: ~4 updates a second
    private readonly System.Windows.Forms.Timer _devTimer = new(); // WM_DEVICECHANGE debounce
    private readonly System.Windows.Forms.Timer _resumeTimer = new(); // post-resume engine restart
    private readonly System.Windows.Forms.Timer _displayTimer = new() { Interval = 700 }; // WM_DISPLAYCHANGE debounce
    // One-shot: fires almost immediately after the window is shown, so device
    // enumeration/Start() (each up to a 5s MF timeout) never delays the window
    // appearing in the first place. See OnLoad / the tick handler below.
    private readonly System.Windows.Forms.Timer _startupTimer = new();
    private bool _startingUp;

    private readonly List<ToolStripMenuItem> _aspectItems = new();
    private readonly List<ToolStripMenuItem> _rotationItems = new();
    private readonly List<ToolStripMenuItem> _rendererItems = new();
    // The quality menu exists twice (Video menu + context menu), so everything
    // that must follow its state is kept in lists.
    private readonly List<ToolStripMenuItem> _qualityRoots = new();
    private readonly List<ToolStripMenuItem> _qualityItems = new();
    private readonly List<ToolStripMenuItem> _hdrRoots = new();
    private readonly List<ToolStripMenuItem> _hdrItems = new();
    private readonly List<ToolStripMenuItem> _sharpnessItems = new();
    private readonly List<ToolStripMenuItem> _nativeResItems = new();
    private readonly List<ToolStripMenuItem> _chromaItems = new();
    private readonly List<ToolStripMenuItem> _restoreItems = new();
    private int _nativeResOsdTarget = -1;      // the auto decision last announced by the OSD (-1 = none yet)

    private ToolStripMenuItem _miAdjust = null!;       // 映像 → 映像の調整...
    private ToolStripMenuItem _miShadow = null!;       // 映像 → 暗部補正
    private readonly List<ToolStripMenuItem> _shadowItems = new();
    private ToolStripMenuItem _miLockAspect = null!;
    private ToolStripMenuItem _miBorderless = null!;
    private ToolStripMenuItem _miTopmost = null!;
    private ToolStripMenuItem _miStatusBar = null!;
    private ToolStripMenuItem _miMenuBar = null!;
    private ToolStripMenuItem _miMute = null!;
    private ToolStripMenuItem _miMirror = null!;
    private ToolStripMenuItem _miFreeze = null!;
    private ToolStripMenuItem _miPip = null!;
    private ToolStripMenuItem _cmiProfiles = null!;
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
    private readonly List<ToolStripMenuItem> _textSizeItems = new();
    private readonly List<ToolStripMenuItem> _infoModeItems = new();
    private readonly List<ToolStripMenuItem> _pipIdleItems = new();
    private readonly List<ToolStripMenuItem> _pipHoverItems = new();
    private readonly List<ToolStripMenuItem> _pipSizeItems = new();
    private readonly List<ToolStripMenuItem> _pipPosItems = new();

    private ToolStripMenuItem _videoDevicesRoot = null!;
    private ToolStripMenuItem _audioDevicesRoot = null!;
    private ToolStripMenuItem _audioOutputRoot = null!;
    private ToolStripMenuItem _videoModeRoot = null!;

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
    private bool _fsMenuVisible, _fsStatusVisible;   // the user's bar choices while fullscreen

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
    private FormWindowState _prePipWindowState = FormWindowState.Normal;
    private bool _prePipBorderless, _prePipTopmost, _prePipMenu, _prePipStatus, _prePipFullscreen;
    private readonly System.Windows.Forms.Timer _pipHoverTimer = new(); // hover-opacity polling
    private bool _pipHovered;

    // Burst snapshots.
    private readonly System.Windows.Forms.Timer _burstTimer = new();
    private int _burstTotal, _burstDone;
    private int _burstSkips;   // ticks skipped in a row because the video was restarting

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
    private const int ResumeFirstMs = 2500;
    private int _resumeTries;
    private bool _resumeWantVideo, _resumeWantAudio;

    // The normal-mode minimum window size. MainForm.Pip.cs lowers MinimumSize
    // below this while entering PiP (small preset sizes would otherwise be
    // clamped up to a 4:3 window with black bars) and restores it via this
    // field on exit.
    // In pixels of the monitor the window is on (320 x 240 at 100%): re-applied when the window changes monitor.
    private Size NormalMinimumSize => new(Dpi(320), Dpi(240));

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
        BuildContextMenu();     // before BuildMenu: both are made by the same builders, and the fields must end up on the menu bar's items
        BuildMenu();
        BuildStatusBar();
        ApplyMenuFonts();

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
        Ui.DarkStripToolTips(_status);
        // A window narrower than the menu bar (a small window, or one moved to a 200% monitor) keeps every menu
        // reachable through the overflow button at the right end instead of silently dropping the last ones.
        _menu.CanOverflow = true;
        _menu.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
        foreach (ToolStripItem top in _menu.Items) top.Overflow = ToolStripItemOverflow.AsNeeded;   // a menu item's default is Never
        _menu.ItemAdded += (_, e) => e.Item!.Overflow = ToolStripItemOverflow.AsNeeded;            // the bar is rebuilt on language/DPI changes

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
        _info.ContentChanged += () => OverlayChanged(OvInfo);
        _osd.ContentChanged += () => OverlayChanged(OvOsd);
        UpdateOverlayMode();

        _osdTimer.Interval = OsdMilliseconds;
        _osdTimer.Tick += (_, _) => OsdTimerTick();

        // OSD click behaviour (mute toggle, "open the saved file", etc.) is
        // driven per-item by ShowOsd's onClick — see OsdClick().
        _osd.Click += (_, _) => OsdClick();

        InitAvAuto();
        _uiTimer.Interval = 500;
        _uiTimer.Tick += (_, _) =>
        {
            Watchdog.Beat();
            // Each step on its own: one that keeps failing must not stop the others (signal, auto profile ...) and
            // must not raise an error box twice a second. Its first failure is logged; repeats are counted.
            UiStep("health", CheckEngineHealth);
            UiStep("status", UpdateStatus);
            UiStep("native-res", CheckNativeResChange);
            UiStep("guard-osd", GuardOsdTick);
            UiStep("auto-profile", AutoProfileTick);
            UiStep("display-rate", DisplayRateTick);
            UiStep("signal", SignalTick);
            UiStep("stream", StreamTick);
            if (_info.Showing && _info.Mode == InfoMode.Full) UiStep("info", UpdateInfo);
        };
        _fpsTimer.Tick += (_, _) => { if (_info.Showing && _info.Mode == InfoMode.Fps) UpdateInfo(); };

        // Device arrivals come in bursts; settle before re-enumerating.
        _devTimer.Interval = 900;
        _devTimer.Tick += (_, _) => { _devTimer.Stop(); _ = OnDeviceChangeAsync(); };

        _burstTimer.Tick += (_, _) => BurstTick();
        _displayTimer.Tick += (_, _) => { _displayTimer.Stop(); OnDisplaysChanged(); };

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
        // Devices often enumerate late after resume: if something is still not running, try again
        // (3 tries over about 6 s: after 2.5 s, +1.5 s, +2 s) before leaving it to the normal recovery.
        _resumeTimer.Interval = ResumeFirstMs;
        _resumeTimer.Tick += async (_, _) => { try { await OnResumeTickAsync(); } catch (Exception ex) { Log.Info("resume: " + ex.Message); } };

        // Interval 1: fires on the very next message-loop tick, i.e. after the
        // window has already been shown, rather than blocking OnLoad itself.
        _startupTimer.Interval = 1;
        _startupTimer.Tick += OnStartupTimerTick;

        Load += OnLoad;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
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

        string main = _busyText ?? (_startingUp ? L.T("接続しています...") : (_recoveryMain ?? L.T("映像が入力されていません")));
        // RGB(200,200,200) on black is ~10.6:1 — the old Brushes.Gray (128,128,128)
        // was only ~4.5:1, borderline for a large-ish message like this.
        using var font = TextScale.MakeFont(Ui.FamilyName, 14f * 96 / 72, _msgScale, FontStyle.Regular);
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
            ? (_videoError ?? (_startingUp ? null : L.T("キャプチャーボードを接続すると自動的に表示されます")))
            : null;
        using var secondFont = TextScale.MakeFont(Ui.FamilyName, 10f * 96 / 72, _msgScale, FontStyle.Regular);
        // RGB(150,150,150) on black is ~7.7:1 (≥7:1 target); the old (96,96,96) was ~3.3:1.
        using var secondBrush = new SolidBrush(Color.FromArgb(255, 150, 150, 150));
        Size secondSz = second != null
            ? TextRenderer.MeasureText(g, second, secondFont, new Size(wrapWidth, int.MaxValue), mainFlags)
            : Size.Empty;

        int totalH = mainSz.Height + (second != null ? TextScale.Px(4, _msgScale) + secondSz.Height : 0);
        // The app mark, dimmed, above the message (when the window is tall enough for it to be more than clutter).
        int mark = TextScale.Px(56, _msgScale), markGap = TextScale.Px(20, _msgScale);
        bool withMark = bounds.Height >= totalH + 2 * (mark + markGap);
        int top = (bounds.Height - totalH + (withMark ? mark + markGap : 0)) / 2;
        if (withMark && CanvasMark(mark) is { } markBmp)
            g.DrawImageUnscaled(markBmp, (bounds.Width - mark) / 2, top - markGap - mark);
        TextRenderer.DrawText(g, main, font, new Rectangle(wrapRect.X, top, wrapWidth, mainSz.Height),
            mainBrush.Color, mainFlags);
        if (second != null)
        {
            int secondTop = top + mainSz.Height + TextScale.Px(4, _msgScale);
            TextRenderer.DrawText(g, second, secondFont, new Rectangle(wrapRect.X, secondTop, wrapWidth, secondSz.Height),
                secondBrush.Color, mainFlags);
        }
    }

    private Bitmap? _canvasMark;

    /// <summary>The app icon at <paramref name="size"/> pixels at 35% opacity, for the empty canvas (made once per size).</summary>
    private Bitmap? CanvasMark(int size)
    {
        if (_canvasMark != null && _canvasMark.Width == size) return _canvasMark;
        _canvasMark?.Dispose();
        _canvasMark = null;
        using Icon? icon = Ui.AppIcon(size);
        if (icon == null) return null;
        using Bitmap full = icon.ToBitmap();
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        using (var attrs = new System.Drawing.Imaging.ImageAttributes())
        {
            attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, new Rectangle(0, 0, size, size), 0, 0, full.Width, full.Height, GraphicsUnit.Pixel, attrs);
        }
        return _canvasMark = bmp;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectExForDpi(ref Rect rc, int style, bool menu, int exStyle, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    private const int GwlStyle = -16, GwlExStyle = -20;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13,
        HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
    private const int ResizeBorder = 6;   // at 96 dpi; scaled with Dpi()
    private const uint SwpFrameChanged = 0x0020, SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    /// <summary>Resize hit code for a point near the client edges, else HTCLIENT.</summary>
    internal static int EdgeHit(Point p, Size client, int border = ResizeBorder)
    {
        bool l = p.X < border, r = p.X >= client.Width - border;
        bool t = p.Y < border, b = p.Y >= client.Height - border;
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
    private int SnapDistance => Dpi(14);   // 14 px at 96 dpi

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
    private bool _dragFromMax;   // pressed on the video of a maximised window: a drag hands over to Windows' own move (it restores the window)
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WmNcLButtonDown = 0x00A1, HtCaption = 2;
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
        if (e.Button == MouseButtons.Left && TryClickVirtualOsd(e.Location)) return;
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
        else if (!_isFullscreen && !_isPip && WindowState == FormWindowState.Maximized)
        {
            _dragFromMax = true;
            _dragOrigin = Cursor.Position;
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging && !_panning && sender is Control hostCtl && ReferenceEquals(hostCtl, _canvas))
        {
            bool overOsd = _osd.Clickable && VirtualOsdRect() is { } osdR && osdR.Contains(e.Location);
            Cursor want = overOsd ? Cursors.Hand : Cursors.Default;
            if (!ReferenceEquals(_canvas.Cursor, want)) _canvas.Cursor = want;
        }
        if (_panning)
        {
            Point cur = Cursor.Position;
            _panX = _panOrigin.X + (cur.X - _dragOrigin.X);
            _panY = _panOrigin.Y + (cur.Y - _dragOrigin.Y);
            ClampPan();
            LayoutCanvas();
            return;
        }
        if (_dragFromMax)
        {
            Point at = Cursor.Position;
            if (Math.Abs(at.X - _dragOrigin.X) < 4 && Math.Abs(at.Y - _dragOrigin.Y) < 4) return;   // a click, not a drag
            // Maximised: let Windows move it as if the title bar were dragged - it restores the window under the
            // pointer and offers its own snapping. Our own drag only knows the normal state.
            _dragFromMax = false;
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
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
        _dragFromMax = false;
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
        ApplyTopmost();
        LayoutCanvas(ModeChangeSettleMs);
    }

    // ---- Fullscreen z-order --------------------------------------------

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint evtMin, uint evtMax, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")] private static extern IntPtr MonitorOfWindow(IntPtr hwnd, uint flags);
    private const uint EventSystemForeground = 0x0003, WinEventOutOfContext = 0x0000;
    private const uint MonitorDefaultToNull = 0, MonitorDefaultToNearest = 2;
    private const int WsExTopmost = 0x0008;
    private static readonly IntPtr HwndTopmost = new(-1), HwndNoTopmost = new(-2);
    private const int WmActivateApp = 0x001C;
    private WinEventProc? _foregroundProc;   // kept referenced: the hook calls it from native code
    private IntPtr _foregroundHook;

    /// <summary>Topmost in PiP and when the user asked for it. Fullscreen is topmost too (it must cover the taskbar),
    /// but not while the user works in another app on the same monitor: Alt+Tab, or Explorer / the browser opened from
    /// fullscreen, would otherwise open hidden behind the video. An app on another monitor leaves it topmost, so the
    /// taskbar there does not cut into the picture while the user works elsewhere.</summary>
    private void ApplyTopmost()
    {
        if (!IsHandleCreated || IsDisposed || _topmostHeld) return;
        bool want = _isPip || _alwaysOnTop || (_isFullscreen && !ForegroundAppOnOurMonitor(out _));
        // Never use the WinForms TopMost setter here: it calls SetWindowPos without SWP_NOACTIVATE, so every call
        // activates this window - even while it is disabled under a modal dialog, which then loses the keyboard.
        // Wanted: re-assert every time (cheap; lifts owned windows opened since, e.g. dialogs).
        if (want) { SetTopmost(true); return; }
        if (!IsTopmostNow()) return;
        SetTopmost(false);
        // Leaving the topmost band puts the window at the top of the normal band - over the app the user just
        // switched to. Slot it back in behind that app.
        if (ForegroundAppOnOurMonitor(out IntPtr fg) && (GetWindowLong(fg, GwlExStyle) & WsExTopmost) == 0)
            SetWindowPos(Handle, fg, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Whether this window really carries WS_EX_TOPMOST (the WinForms TopMost getter reads the same style once the handle exists).</summary>
    internal bool IsTopmostNow() => IsHandleCreated ? (GetWindowLong(Handle, GwlExStyle) & WsExTopmost) != 0 : TopMost;

    /// <summary>Enter or leave the topmost band without activating the window (the WinForms TopMost setter activates it).
    /// Before the handle exists the property is set instead, so CreateParams picks it up.</summary>
    private void SetTopmost(bool on)
    {
        if (!IsHandleCreated) { TopMost = on; return; }
        SetWindowPos(Handle, on ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private bool _topmostHeld;   // a system dialog is open: ApplyTopmost stands down

    /// <summary>Run a system common dialog (folder picker, ...) from <paramref name="owner"/>. Such a dialog is not made
    /// topmost by us, so while this window (or the owner dialog) is topmost it can open behind it. Both leave the topmost
    /// band meanwhile (without activating anything) and get it back afterwards - the dialog then stays on top of them as
    /// their owned window.</summary>
    internal T ShowSystemDialog<T>(Form? owner, Func<T> show)
    {
        bool mainTop = IsTopmostNow();
        bool ownerTop = owner != null && owner != this && owner.IsHandleCreated && (GetWindowLong(owner.Handle, GwlExStyle) & WsExTopmost) != 0;
        if (!mainTop && !ownerTop) return show();
        _topmostHeld = true;
        if (ownerTop) SetWindowPos(owner!.Handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        if (mainTop) SetTopmost(false);
        try { return show(); }
        finally
        {
            _topmostHeld = false;
            if (!IsDisposed)
            {
                if (mainTop) ApplyTopmost();
                if (ownerTop && !owner!.IsDisposed && owner.IsHandleCreated)
                    SetWindowPos(owner.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
    }

    /// <summary>True when the foreground window belongs to another process and sits on the monitor this window covers.</summary>
    private bool ForegroundAppOnOurMonitor(out IntPtr fg)
    {
        fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;     // mid-switch, or us
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;    // our own dialogs, menus and owned windows
        IntPtr mon = MonitorOfWindow(fg, MonitorDefaultToNull);
        return mon != IntPtr.Zero && mon == MonitorOfWindow(Handle, MonitorDefaultToNearest);
    }

    /// <summary>Follow foreground changes while fullscreen (WM_ACTIVATEAPP alone does not say where the other app is).</summary>
    private void HookForeground(bool on)
    {
        if (on && _foregroundHook == IntPtr.Zero)
        {
            _foregroundProc ??= (_, _, _, _, _, _, _) =>
            {
                try { if (_isFullscreen) ApplyTopmost(); }
                catch (Exception ex) { Log.Info("foreground follow failed: " + ex.Message); }
            };
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _foregroundProc, 0, 0, WinEventOutOfContext);
        }
        else if (!on && _foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    private void BuildStatusBar()
    {
        // RenderMode/BackColor are left at their defaults here: the dark
        // chrome (Renderer + BackColor/ForeColor) is applied once, uniformly,
        // in the constructor via Ui.DarkRenderer — setting System/Control here
        // would just be overwritten. No label draws a 3D border: the renderer puts
        // one thin separator between visible segments (none after the last one).
        _lblVideo.BorderSides = ToolStripStatusLabelBorderSides.None;
        _lblAudio.BorderSides = ToolStripStatusLabelBorderSides.None;
        _lblVolume.BorderSides = ToolStripStatusLabelBorderSides.None;
        _lblVolume.ToolTipText = L.T("クリックでミュート切替 ／ 映像の上でホイールまたは ↑↓ で音量");
        _lblVolume.Click += (_, _) => ToggleMute();
        _lblState.BorderSides = ToolStripStatusLabelBorderSides.None;
        _lblState.ForeColor = Ui.Colors.Accent;
        _lblTrain.Click += (_, _) => { if (TrainRunning) ShowTrainWindow(); };
        var filler = new ToolStripStatusLabel { Spring = true, Text = string.Empty };
        foreach (var l in new[] { _lblInterp, _lblScale, _lblCrisp, _lblTrain })
        {
            l.BorderSides = ToolStripStatusLabelBorderSides.None;
            l.Visible = false;
        }
        _status.Items.AddRange(new ToolStripItem[] { _lblVideo, _lblAudio, _lblVolume, _lblInterp, _lblScale, _lblCrisp, _lblTrain, _lblState, filler });
        _status.SizingGrip = true;
        foreach (ToolStripItem item in _status.Items) item.Padding = new Padding(6, 0, 6, 0);   // room on both sides of the separators
    }

    /// <summary>Scaling (dpi) of the monitor under a screen point; the form's own scaling when it cannot be asked.</summary>
    internal int DpiAtPoint(Point screen)
    {
        try
        {
            IntPtr mon = MonitorFromPoint(screen, 2);   // MONITOR_DEFAULTTONEAREST
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) return (int)dx;
        }
        catch { /* fall through */ }
        return DeviceDpi;
    }

    /// <summary>
    /// A ContextMenuStrip is not a child of the form, so WinForms does not rescale it (and its drop-downs) in step with
    /// the window: a menu shown at 100% and later at 200% ends up with a sub-menu laid out for the other scaling, with
    /// items half or twice as tall as the rest. A menu that only ever lives on one monitor scaling has no such problem,
    /// so the whole menu is built anew for the scaling it is going to be shown at.
    /// </summary>
    private void OnCtxOpening(object? sender, CancelEventArgs e)
    {
        if (_testRunning && !_ctxAudit) { e.Cancel = true; return; }
        if (!_ctxAudit)
        {
            int dpi = DpiAtPoint(Cursor.Position);
            if (dpi != _ctxDpi)
            {
                // Too late to rebuild the menu that is opening: cancel, rebuild, and open the new one in the same place.
                Log.Info($"context menu: built for {_ctxDpi} dpi, opening at {dpi} dpi - rebuilding");
                e.Cancel = true;
                Point at = Cursor.Position;
                if (!_ctxFixPending && IsHandleCreated)
                {
                    _ctxFixPending = true;
                    BeginInvoke(new Action(() =>
                    {
                        _ctxFixPending = false;
                        RebuildMenus(dpi);
                        _ctx.Show(at);
                    }));
                }
                return;
            }
        }
        UpdateChecks();
    }

    // ---- Shortcuts help (F1) ----------------------------------------------

    private void ShowShortcuts()
    {
        using var dlg = Ui.NewDialog(L.T("操作一覧"), 560);

        // One read-only table: the action and its keys in two columns that wrap (nothing is cut off in either
        // language), under a heading per group. As tall as its rows; it scrolls only on a screen too short for it.
        var table = new UiTable();
        string? lastGroup = null;

        string gHere = L.T("YuCap の画面で");
        string gGlobal = L.T("割り当てを変えられるキー（どこからでも）");

        void Row(string g, string action, string keys)
        {
            if (g != lastGroup) { table.AddGroup(g); lastGroup = g; }
            table.AddRow(action, keys);
        }

        Row(gHere, L.T("全画面表示の切り替え"), $"F11{L.Or}Alt+Enter{L.Or}{L.T("ダブルクリック")}");
        Row(gHere, L.T("全画面・PiP を終了"), "Esc");
        Row(gHere, L.T("一時停止 / 再開"), "Space");
        Row(gHere, L.T("ミュート"), $"M{L.Or}{L.T("中クリック")}{L.Or}{L.T("音量表示をクリック")}");
        Row(gHere, L.T("音量"), $"↑ ↓{L.Or}{L.T("ホイール")}");
        Row(gHere, L.T("ズーム"), $"Ctrl + {L.T("ホイール")}{L.Or}Ctrl++ Ctrl+-");
        Row(gHere, L.T("ズームをリセット"), "Ctrl+0");
        Row(gHere, L.T("ズーム中の表示位置"), L.T("ドラッグ"));
        Row(gHere, L.T("ウィンドウの移動"), L.T("映像をドラッグ"));
        Row(gHere, L.T("スナップショットを保存"), "Ctrl+S");
        Row(gHere, L.T("スナップショットをコピー"), "Ctrl+C");
        Row(gHere, L.T("スナップショット一覧"), "Ctrl+G");
        Row(gHere, L.T("常に手前に表示"), "Ctrl+T");
        Row(gHere, L.T("ウィンドウ枠を隠す"), "Ctrl+B");
        Row(gHere, L.T("メニューバーの表示"), "F10");
        Row(gHere, L.T("情報表示の切り替え（オフ → FPS だけ → 詳しく）"), "Ctrl+Shift+I");
        Row(gHere, L.T("標準の拡大と見比べる"), L.T("Q（押している間）"));
        Row(gHere, L.T("プロファイルを切り替える"), "Ctrl+1 – 9");
        Row(gHere, L.T("メニュー"), L.T("右クリック"));
        Row(gHere, L.T("操作一覧"), "F1");

        Row(gGlobal, L.T("スナップショットを保存"), FormatHotkey((Keys)_settings.HotkeySnapshot));
        Row(gGlobal, L.T("ミュート"), FormatHotkey((Keys)_settings.HotkeyMute));
        Row(gGlobal, L.T("PiP の切り替え"), FormatHotkey((Keys)_settings.HotkeyPip));
        Row(gGlobal, L.T("音量を上げる"), FormatHotkey((Keys)_settings.HotkeyVolumeUp));
        Row(gGlobal, L.T("音量を下げる"), FormatHotkey((Keys)_settings.HotkeyVolumeDown));
        Row(gGlobal, L.T("映像の出し方（なめらか ⇔ 低遅延）"), FormatHotkey((Keys)_settings.HotkeyStyle));
        if (!_settings.GlobalHotkeys)
            Row(gGlobal, string.Empty, L.T("（グローバルホットキーは無効です。YuCap の画面では使えます）"));

        Ui.FrameClose(dlg, new UiScroll(table), fill: true);
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
        if (ParseInfoMode(_settings.InfoMode) is var savedInfo && savedInfo != InfoMode.Off && !_info.Showing) SetInfoMode(savedInfo);
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
    private async void OnStartupTimerTick(object? sender, EventArgs e)
    {
        _startupTimer.Stop();
        if (IsDisposed) return;
        _startingUp = false;
        try { await StartupDevicesAsync(); }
        catch (Exception ex) { Log.Info("startup: " + ex); }
    }

    private async Task StartupDevicesAsync()
    {
        // The video opens in the background (a card can need seconds); the audio, which is quick, starts meanwhile.
        Task video = InitVideoAsync();
        InitAudio();
        await video;
        if (IsDisposed) return;
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

        if (SettingsStore.SaveBlocked)
        {
            // The real file is intact but could not be read (held by another program): nothing is written over it this session,
            // so every change made now is lost on exit. Say so plainly, and keep saying it in the status bar.
            ShowOsd(L.T("設定ファイルをほかのプログラムが使っていたため読み込めず、既定の設定で起動しました。今回変えた設定は保存されません（YuCap を起動し直すと元の設定に戻ります）。"), 10000);
        }
        else if (SettingsStore.LastLoadFailed)
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
            // On a screen that is gone: same size on the primary screen rather than somewhere unreachable.
            b = EnsureOnScreen(b);
            StartPosition = FormStartPosition.Manual;
            Bounds = b;
        }

        FormBorderStyle = FormBorderStyle.Sizable;
        _canvas.EnableEdgeResize = _isBorderless;
        RefreshFrame(); // hides chrome via WM_NCCALCSIZE when borderless
        SetTopmost(_alwaysOnTop);
        _menu.Visible = _settings.MenuVisible;
        _status.Visible = _settings.StatusVisible;
        _fsMenuVisible = _settings.FullscreenMenuVisible;
        _fsStatusVisible = _settings.FullscreenStatusVisible;
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

    /// <summary><paramref name="b"/> when it is on a connected screen, else the same size (shrunk to fit if needed)
    /// centred in the primary work area. Used wherever bounds remembered earlier are applied again: the monitor
    /// they were on may have been unplugged or reconfigured since.</summary>
    private static Rectangle EnsureOnScreen(Rectangle b)
    {
        if (b.Width > 0 && b.Height > 0 && IsMostlyOnScreen(b)) return b;
        Rectangle wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        int w = b.Width > 0 ? Math.Min(b.Width, wa.Width) : Math.Min(960, wa.Width);
        int h = b.Height > 0 ? Math.Min(b.Height, wa.Height) : Math.Min(540, wa.Height);
        return new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
    }

    private int Dpi(int px96) => Math.Max(1, (int)Math.Round(px96 * DeviceDpi / 96.0));

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
        _settings.FullscreenMenuVisible = _fsMenuVisible;
        _settings.FullscreenStatusVisible = _fsStatusVisible;
        _settings.InfoMode = InfoModeKey(CurrentInfoMode);
        _settings.InfoVisible = _info.Showing;   // kept in step for older versions reading the same file
        _settings.Volume = _volumeOverridden ? _persistVolume : _audio.VolumePercent;
        _settings.AudioBufferMs = _audio.BufferMilliseconds;
        _settings.Rotation = _video.Rotation;
        _settings.Mirror = _video.Mirror;
        _settings.Fullscreen = _fullscreenOverridden ? _persistFullscreen : (_isFullscreen || (_isPip && _prePipFullscreen));
        _settings.Pip = _isPip;
        _settings.SetMode(_savedMode);

        // Fullscreen and PiP are temporary: persist the state they will return to (maximized included).
        _settings.WindowMaximized = _isFullscreen ? _restoreWindowState == FormWindowState.Maximized
            : _isPip ? _prePipWindowState == FormWindowState.Maximized
            : WindowState == FormWindowState.Maximized;
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
        NoteVideoStopped();   // a deliberate stop: the health check must not "recover" it behind our back
        _devOpBusy++;
        try { _video.Stop(); } catch { /* best-effort: about to (re)start anyway */ }
        _canvas.Invalidate();
        _canvas.Update();
        Cursor = Cursors.WaitCursor;
        try
        {
            // Before the start too: the low-latency choice decides the swap chain kind when the
            // engine starts, so the engine must already know it. Idempotent and cheap.
            ApplyRenderOptions();
            try { start(); }
            catch (Exception ex) when (IsWindowAlreadyComposed(ex))
            {
                // The window still carries the DirectComposition target of a previous session
                // (DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED). A fresh window has none: same cure as RestartPreview.
                Log.Info("start: window already composed - recreating the render window and trying once more");
                try { _video.Stop(); } catch { /* ignore */ }
                _canvas.ResetHandle();
                _video.Attach(_canvas.Handle);
                ApplyRenderOptions();
                start();
            }
            ApplyRenderOptions(); // every start path goes through here
        }
        finally
        {
            _devOpBusy--;
            _busyText = null;
            Cursor = Cursors.Default;
            _canvas.Invalidate();
        }
    }

    private string? _videoPickName;   // the video device InitVideoAsync chose (InitAudio runs before it has started)

    private async Task InitVideoAsync()
    {
        try
        {
            var devices = VideoEngine.EnumerateDevices();
            var pick = ChooseVideoDevice(devices, _settings.VideoDeviceId, _settings.DeviceKeyword);
            if (pick == null)
            {
                // Nothing the user chose, nothing matching the keyword, and (first run) no device that is not a camera:
                // never open a webcam or a virtual camera. Say so and wait for WM_DEVICECHANGE / the device menu.
                Log.Info($"InitVideo: no capture device found ({devices.Count} other video device(s))");
                SetNoCaptureDeviceState(devices.Count > 0);
                if (devices.Count > 0)
                    ShowOsd(L.T("キャプチャーボードが見つかりません。「デバイス」メニューから映像デバイスを選んでください"), OsdLongMilliseconds);
                return;
            }
            _videoPickName = pick.Name;
            LayoutCanvas();               // give the canvas a valid size before Start
            await StartVideoAsync(pick, _cliMode ?? _savedMode);
            _currentVideoInfo = pick;
            _videoError = null;
            _recoveryMain = null;
            LayoutCanvas();               // re-fit now that the resolution is known
        }
        catch (OperationCanceledException) { /* stopped on purpose meanwhile */ }
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
            var pick = ChooseAudioDevice(devices, _settings.AudioDeviceId, _settings.DeviceKeyword, _currentVideoInfo?.Name ?? _videoPickName);
            if (pick == null)
            {
                Log.Info("InitAudio: no capture audio device found (not opening an unrelated input)");
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
    /// system default audio output when it changes. The video start runs in the
    /// background (see MainForm.VideoStart.cs); this pass completes when it has.</summary>
    private async Task OnDeviceChangeOnceAsync()
    {
        if (IsDisposed || _testRunning) return;
        Log.Info($"OnDeviceChange: videoRunning={_video.IsRunning} audioRunning={_audio.IsRunning}");
        ResetRecovery();   // a plug event is a new chance: the automatic retries may arm again

        Task videoDone = Task.CompletedTask;
        try // ---- video ----
        {
            var devices = VideoEngine.EnumerateDevices();
            bool present = _currentVideoInfo != null && devices.Any(d => d.Id == _currentVideoInfo.Id);
            if (_video.IsRunning && _currentVideoInfo != null && !present && !_video.EndedUnexpectedly)
            {
                // One missing enumeration is not an unplug: a stick whose audio interface re-enumerates briefly hides its
                // video interface too. Look again before stopping a picture that is running fine.
                string wantId = _currentVideoInfo.Id;
                Log.Info("OnDeviceChange: the video device is not listed - checking again in 1.5 s");
                await Task.Delay(1500);
                if (IsDisposed || _testRunning) return;
                devices = VideoEngine.EnumerateDevices();
                present = devices.Any(d => d.Id == wantId);
                if (present) Log.Info("OnDeviceChange: the video device is back in the list - keeping the picture");
                // The user may have switched device or started one during the wait: `present` describes the old device
                // only, so never stop or judge the current one by it.
                if (_currentVideoInfo?.Id != wantId || _vsRunning != null || _vsPending != null)
                {
                    Log.Info("OnDeviceChange: the video device changed during the recheck - leaving it to the new start");
                    present = true;
                }
            }
            if (_video.IsRunning && _currentVideoInfo != null && !present)
            {
                if (_frozen) ToggleFreeze();
                NoteVideoStopped();
                StopVideo();
                _canvas.Invalidate();
                ShowOsd(L.T("映像デバイスが切断されました"), OsdLongMilliseconds);
            }
            else if (_video.IsRunning && _video.EndedUnexpectedly)
            {
                NoteVideoStopped();
                StopVideo();   // dead already: the branch below reopens it
            }
            // A start in flight or pending already decides the device (it may be a switch to another one): do not queue the old device over it.
            if (!_video.IsRunning && _vsRunning == null && _vsPending == null)
            {
                var pick = (present ? _currentVideoInfo : null)
                    ?? ChooseVideoDevice(devices, _settings.VideoDeviceId, _settings.DeviceKeyword);
                if (pick != null) videoDone = ReconnectVideoOnDeviceChangeAsync(pick);
                else if (_currentVideoInfo == null || !present)
                {
                    SetNoCaptureDeviceState(devices.Count > 0);
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
                NoteAudioStopped();
                _audio.Stop();
            }
            if (!_audio.IsRunning)
            {
                var pick = (present ? _currentAudioInfo : null)
                    ?? ChooseAudioDevice(devices, _settings.AudioDeviceId, _settings.DeviceKeyword, _currentVideoInfo?.Name);
                if (pick != null)
                {
                    try
                    {
                        _audio.Start(pick, wantOut);
                        _currentAudioInfo = pick;
                        if (outVanished && _audio.RenderFellBack)
                            Log.Info($"audio output not found - playing on the Windows default: {_audio.RenderDeviceName ?? "?"}");
                        else if (outReturned && !_audio.RenderFellBack)
                            Log.Info($"audio output restored: {_audio.RenderDeviceName ?? "?"}");
                        else
                            Log.Info($"audio reconnected: {pick.Name}");
                    }
                    catch (Exception ex) { Log.Info("audio reconnect (device change) failed: " + ex.Message); /* retried on the next change event */ }
                }
            }
        }
        catch { /* ignore */ }

        UpdateStatus();
        UpdateChecks();
        await videoDone;   // the audio above did not wait for the (slow) video start
        if (IsDisposed) return;
        UpdateStatus();
        UpdateChecks();
    }

    private async Task ReconnectVideoOnDeviceChangeAsync(VideoDeviceInfo pick)
    {
        try
        {
            await StartVideoAsync(pick, _cliMode ?? _savedMode);
            _currentVideoInfo = pick;
            _videoError = null;
            _recoveryMain = null;
            LayoutCanvas();
            Log.Info($"video reconnected: {pick.Name}");
        }
        catch (OperationCanceledException) { /* stopped on purpose meanwhile */ }
        catch (Exception ex) { Log.Info("video reconnect (device change) failed: " + ex.Message); /* device still settling — the next change event retries */ }
    }

    /// <summary>Post-resume restart: the first pass restarts both engines from scratch (a wedged Media Foundation
    /// pipeline is common after sleep); later passes only retry what still is not running.</summary>
    private async Task OnResumeTickAsync()
    {
        _resumeTimer.Stop();
        if (IsDisposed) return;
        _resumeTries++;
        Log.Info($"resume: restarting engines (try {_resumeTries})");
        if (_resumeTries == 1)
        {
            NoteVideoStopped(); NoteAudioStopped();
            StopVideo();
            try { _audio.Stop(); } catch { /* ignore */ }
        }
        if (!_testRunning) await OnDeviceChangeAsync();   // re-opens video + audio with the saved device/mode
        if (IsDisposed) return;

        bool missing = (_resumeWantVideo && !_video.IsRunning) || (_resumeWantAudio && !_audio.IsRunning);
        if (missing && _resumeTries < 3 && !_testRunning)
        {
            _resumeTimer.Interval = _resumeTries == 1 ? 1500 : 2000;
            _resumeTimer.Start();
            return;
        }
        _resumeTimer.Interval = ResumeFirstMs;
        _suspended = false;
        // Whatever was running and is not any more is the normal recovery's job from here on.
        if (_resumeWantVideo && !_video.IsRunning && _currentVideoInfo != null) _videoWasRunning = true;
        if (_resumeWantAudio && !_audio.IsRunning && _currentAudioInfo != null) _audioWasRunning = true;
        ResetRecovery();
        UpdateStatus();
    }

    /// <summary>After the set of monitors changed: fullscreen goes back over its (possibly different) monitor, a PiP
    /// that is no longer on a screen is docked again, and a normal window that ended up off-screen is brought back.</summary>
    private void OnDisplaysChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        Log.Info("displays changed: " + string.Join(", ", Screen.AllScreens.Select(s => $"{s.DeviceName} {s.Bounds}")));
        try
        {
            if (_isFullscreen)
            {
                PlaceFullscreen();
                _restoreBounds = EnsureOnScreen(_restoreBounds);
            }
            else if (_isPip)
            {
                if (!IsMostlyOnScreen(Bounds)) Location = PipCornerLocation();
                _prePipBounds = EnsureOnScreen(_prePipBounds);
                if (_prePipFullscreen) { /* the fullscreen it will return to is placed when it is entered */ }
            }
            else if (WindowState == FormWindowState.Normal && !IsMostlyOnScreen(Bounds))
            {
                Bounds = EnsureOnScreen(Bounds);
            }
        }
        catch (Exception ex) { Log.Info("OnDisplaysChanged failed: " + ex.Message); }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_testRunning && e.CloseReason == CloseReason.UserClosing && !ConfirmCloseDuringTraining())
        {
            e.Cancel = true;
            _skipSaveOnClose = false;   // a close requested by "update now" was refused: this process lives on and must keep saving
            return;
        }
        // An update has already saved settings; re-saving here would capture the
        // torn-down state (no device, window mid-close) over the real settings.
        if (_testRunning)
        {
            bool mustClose = e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing
                or CloseReason.ApplicationExitCall;
            _testCts?.Cancel();
            if (!mustClose)
            {
                // Closing mid-test (Alt+F4, X): stop the test first (it puts everything back), then the window
                // closes by itself - one Alt+F4 is enough.
                e.Cancel = true;
                _closeAfterTest = true;
                return;
            }
            // Windows is shutting down / the process is being ended: never hold that up. Put the settings back
            // right now (the test's own restore needs the message loop, which may not run again) and go on closing.
            try { if (_testSaved != null) CopySettings(_testSaved, _settings); }
            catch (Exception ex) { Log.Info("closing during the test: restoring the settings failed - " + ex.Message); }
            try { if (_audio.IsRunning && _audio.Muted != _testMutedBefore) _audio.Muted = _testMutedBefore; } catch { /* ignore */ }
            _testRunning = false;
            try { TestModeGuard.ClearMarker(); } catch { /* ignore */ }
        }
        if (!_skipSaveOnClose)
        {
            try { SaveSettings(); } catch { /* ignore */ }
        }
        // Nothing is torn down here: Windows raises this for WM_QUERYENDSESSION too, and a shutdown the user then
        // cancels (another app blocked it) never closes the form. Tearing down here would leave a live window with
        // disposed engines. The teardown runs in OnFormClosed, or on WM_ENDSESSION when the session really ends.
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e) => TearDown();

    private readonly Dictionary<string, int> _uiStepFailures = new();

    /// <summary>Run one periodic UI step, keeping its exception from reaching the application-wide error box.</summary>
    private void UiStep(string name, Action step)
    {
        try { step(); }
        catch (Exception ex)
        {
            _uiStepFailures.TryGetValue(name, out int n);
            _uiStepFailures[name] = ++n;
            if (n == 1) Log.Info($"ui tick '{name}' failed: {ex}");
            else if (n is 10 or 100 or 1000 || n % 10000 == 0) Log.Info($"ui tick '{name}' failed {n} times: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool _tornDown;

    /// <summary>Release everything the window holds. Runs once: from FormClosed, or from WM_ENDSESSION when Windows
    /// ends the session (the process may be killed before FormClosed is ever raised).</summary>
    private void TearDown()
    {
        if (_tornDown) return;
        _tornDown = true;
        HookForeground(false);
        _uiTimer.Stop();
        StopTrainingOnClose();
        StreamOnMainClosing();
        _osdTimer.Stop();
        _devTimer.Stop();
        _burstTimer.Stop();
        _pipHoverTimer.Stop();
        _resumeTimer.Stop();
        _displayTimer.Stop();
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
        ApplyOverlayScale();
        PositionOsd();
        PositionInfo();
        PositionTestCaption();
    }

    // ---- Text size over the video ------------------------------------------
    // The notices / info panel / caption / "no video" message follow the DPI of the monitor the
    // window is on and the height of the video area (TextScale), times the user's 表示の文字の大きさ.

    private float _msgScale = 1f;

    private float OverlayScale(double k) => TextScale.For(DeviceDpi, Math.Max(1, _canvas.Height), k, _settings.OverlayTextPct);

    private void ApplyOverlayScale()
    {
        _osd.ApplyScale(OverlayScale(TextScale.KOsd));
        _info.ApplyScale(OverlayScale(TextScale.KInfo));
        _testCaption?.ApplyScale(OverlayScale(TextScale.KCaption));
        float m = OverlayScale(TextScale.KMessage);
        if (Math.Abs(m - _msgScale) > 0.001f) { _msgScale = m; _canvas.Invalidate(); }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!_isPip) MinimumSize = NormalMinimumSize;
        // The context menu is laid out for one scaling only (see OnCtxOpening): make a new one for this monitor.
        if (e.DeviceDpiNew != _ctxDpi && IsHandleCreated && !_ctxFixPending)
        {
            _ctxFixPending = true;
            int dpiNew = e.DeviceDpiNew;
            BeginInvoke(new Action(() => { _ctxFixPending = false; RebuildMenus(dpiNew); }));
        }
        ApplyOverlayScale();
        PositionOsd();
        PositionInfo();
        PositionTestCaption();
        // The menu and status bars change height with the scaling, so a locked aspect drifts (a thin letterbox) until
        // the next manual resize. Put it right once the window has settled on the new monitor (after the drag, if any).
        if (_lockAspect && IsHandleCreated)
        {
            _aspectAfterDpi = true;
            BeginInvoke(new Action(() => { if (!_inSizeMove) ApplyAspectAfterDpi(); }));
        }
    }

    private bool _aspectAfterDpi;

    private void ApplyAspectAfterDpi()
    {
        if (!_aspectAfterDpi) return;
        _aspectAfterDpi = false;
        if (_lockAspect && !_isPip) ApplyLockedAspect();
    }

    private ToolStripMenuItem BuildTextSizeSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("情報表示・通知の文字の大きさ"));
        foreach ((string label, int pct) in new[] { (L.T("自動"), 100), (L.T("小（75%）"), 75), (L.T("大（125%）"), 125), (L.T("特大（150%）"), 150) })
        {
            int p = pct;
            var item = Radio(new ToolStripMenuItem(label, null, (_, _) => SetOverlayTextPct(p)) { Tag = p });
            _textSizeItems.Add(item);
            m.DropDownItems.Add(item);
        }
        return m;
    }

    private void SetOverlayTextPct(int pct)
    {
        _settings.OverlayTextPct = pct;
        ApplyOverlayScale();
        PositionInfo();
        UpdateChecks();
        ShowOsd(pct == 100 ? L.T("情報表示・通知の文字の大きさ: 自動") : L.F("情報表示・通知の文字の大きさ: {0}%", pct));
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
        if (_osd.Showing)
        {
            _osd.Location = new Point(_canvas.Left + _osd.EdgeGap, _canvas.Top + _osd.EdgeGap);
            MarkOverlaysDirty();
        }
    }

    // ---- Overlays drawn into the video (Direct3D renderer) ------------------
    // A child window over the swap chain stops direct flip: DWM composes, and with mixed refresh rates it composes
    // a secondary monitor at the primary's rate (a 144 Hz TV fell to ~60 fps while the info panel or a notice was up).
    // So with the Direct3D renderer the notice / info panel / test caption controls are never shown as windows: they stay
    // the layout, state and painting source, are rendered to premultiplied BGRA when they change, and the engine blends
    // the images into the back buffer. Stills are copied before that blend, so they never contain them.
    private const int OvInfo = 1, OvCaption = 2, OvOsd = 3;
    private readonly Dictionary<int, (byte[] Px, int W, int H, long Ver)> _ovImg = new();
    private readonly HashSet<int> _ovStale = new();
    private long _ovVersion;
    private bool _ovPushQueued;
    internal long OverlayPushes;

    private void OverlayChanged(int id)
    {
        _ovStale.Add(id);
        MarkOverlaysDirty();
    }

    private void MarkOverlaysDirty()
    {
        if (_ovPushQueued || !IsHandleCreated) return;
        _ovPushQueued = true;
        BeginInvoke(new Action(PushOverlays));
    }

    /// <summary>Virtual (drawn into the video) when the engine can; windows while paused (the pause image is a window
    /// above the video, so a layer inside the video would be hidden behind it) and with the Media Foundation renderer.</summary>
    private void UpdateOverlayMode()
    {
        bool v = _video != null && _video.SupportsOverlays && !_frozen;
        _osd.Virtual = v;
        _info.Virtual = v;
        if (_testCaption != null) _testCaption.Virtual = v;
        if (!v) _video?.SetOverlays(null);
        PositionOsd();
        PositionInfo();
        PositionTestCaption();
        MarkOverlaysDirty();
    }

    private void PushOverlays()
    {
        _ovPushQueued = false;
        if (_video == null || !_video.SupportsOverlays) return;
        var list = new List<OverlayLayer>();
        void Add(int id, OverlayControl? c)
        {
            if (c is not { Virtual: true, Showing: true }) { _ovImg.Remove(id); return; }
            if (!_ovImg.ContainsKey(id) || _ovStale.Contains(id))
            {
                var img = c.RenderImage();
                if (img == null) { _ovImg.Remove(id); return; }
                _ovImg[id] = (img.Value.Pixels, img.Value.W, img.Value.H, ++_ovVersion);
            }
            _ovStale.Remove(id);
            var o = _ovImg[id];
            list.Add(new OverlayLayer(id, c.Left - _canvas.Left, c.Top - _canvas.Top, o.W, o.H, o.Px, o.Ver));
        }
        Add(OvInfo, _info);
        Add(OvCaption, _testCaption);
        Add(OvOsd, _osd);
        OverlayPushes++;
        _video.SetOverlays(list);
    }

    /// <summary>Rectangle of the notice in canvas client pixels, when it is a layer inside the video.</summary>
    private Rectangle? VirtualOsdRect() =>
        _osd is { Virtual: true, Showing: true } ? new Rectangle(_osd.Left - _canvas.Left, _osd.Top - _canvas.Top, _osd.Width, _osd.Height) : null;

    /// <summary>A click on the notice while it is drawn into the video (no window to receive it). True when handled.</summary>
    private bool TryClickVirtualOsd(Point canvasPoint)
    {
        if (VirtualOsdRect() is not { } r || !r.Contains(canvasPoint)) return false;
        if (_osd.Clickable) OsdClick();
        return _osd.Clickable;
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
            if (_osd.Showing && _osdCurrent is { } cur && IsLongOsd(cur.Ms))
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
        if (_osd.Showing && _osdCurrent is { } current && IsLongOsd(current.Ms)
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
        int maxWidth = Math.Max(120, _canvas.Width - _osd.EdgeGap * 2);
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
            _osd.Showing = false;
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
            && !IsMenuDropdownOpen()       // right-click menu or a menu-bar drop-down (it may lie over the video) → keep it usable
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
    }

    /// <summary>Rebuild the capture preview to recover from a wedged UpdateVideo.
    /// The render window MUST be recreated first: the abandoned session's
    /// DirectComposition target is still bound to the old HWND, so reusing it
    /// fails with DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED.</summary>
    private async void RestartPreview()
    {
        if (_currentVideoInfo == null) return;
        try
        {
            _canvas.ResetHandle();
            _video.Attach(_canvas.Handle);
            // The shared start path: a Direct3D start runs off the UI thread instead of freezing it for the old session's teardown.
            await StartVideoAsync(_currentVideoInfo, _cliMode ?? _savedMode);
            ApplyRenderOptions();
            LayoutCanvas();
            Log.Info("resize guard: preview restarted on a fresh window");
            _resizeGuard.Start();   // watch the restarted session too: its first resize can wedge as well
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
    private void MarkSettingsDirty(bool userChange = true)
    {
        if (_skipSaveOnClose) return;   // an update is mid-flight
        if (userChange) NoteUserChange();
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
        hwnd == _canvas.Handle || hwnd == _freezeView.Handle || hwnd == _osd.Handle || hwnd == Handle
        || (_info.IsHandleCreated && hwnd == _info.Handle);

    private int _wheelAccum;

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
        // Touchpads and smooth-scroll wheels send many small deltas (< 120): collect them until a notch is full.
        if (Math.Sign(delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += delta;
        int steps = _wheelAccum / 120;
        _wheelAccum -= steps * 120;
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
        if (Ui.Message(this,
                L.T("入力レベルを最大にしますか？\n\nWindows の録音デバイスの音量を 100% に変更します。\nこの設定は他のアプリにも影響し、YuCap を終了しても元に戻りません。\n（メニューの「入力レベルを元に戻す」で戻せます）"),
                MsgKind.Question, MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;

        // A second "maximise" on the same device must not replace the remembered ORIGINAL level with
        // an intermediate one (that would make "元に戻す" restore the wrong value).
        bool keepOriginal = _settings.CaptureLevelBefore >= 0 && _settings.CaptureLevelDeviceId == _audio.CurrentDeviceId;
        if (!keepOriginal)
        {
            _settings.CaptureLevelBefore = before;
            _settings.CaptureLevelDeviceId = _audio.CurrentDeviceId;
        }
        MarkSettingsDirty();
        _audio.CaptureLevelPercent = 100;
        int after = _audio.CaptureLevelPercent;
        if (after < 99)
        {
            // The setter swallows errors; read it back so a refused change is not announced as done.
            if (!keepOriginal) { _settings.CaptureLevelBefore = -1; _settings.CaptureLevelDeviceId = null; }
            ShowOsd(L.T("入力レベルを変更できませんでした"), OsdLongMilliseconds);
            UpdateStatus();
            UpdateChecks();
            return;
        }
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
        int want = _settings.CaptureLevelBefore;
        _audio.CaptureLevelPercent = want;
        int got = _audio.CaptureLevelPercent;
        if (got < 0 || Math.Abs(got - want) > 2)
        {
            // Not restored (the setter swallows errors): keep the remembered value so it can be tried again.
            ShowOsd(L.T("入力レベルを元に戻せませんでした"), OsdLongMilliseconds);
            UpdateStatus();
            UpdateChecks();
            return;
        }
        ShowOsd(L.F("入力レベルを {0}% に戻しました", want));
        _settings.CaptureLevelBefore = -1;
        _settings.CaptureLevelDeviceId = null;
        MarkSettingsDirty();
        UpdateStatus();
        UpdateChecks();
    }

    private void ToggleMute()
    {
        if (_testRunning) return;   // the test measures the audio as it is; the mute state is put back afterwards anyway
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
            UpdateOverlayMode();
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
                ShowOsd(_video.LastSnapshotError ?? NoVideoText, OsdLongMilliseconds);
                return;
            }
            _freezeImage = still;
            _freezeView.Image = still;
            _freezeView.Bounds = _canvas.Bounds;
            _freezeView.ImageRect = FreezeImageRect();
            _freezeView.Visible = true;
            _freezeView.BringToFront();
            if (_info.Showing) _info.BringToFront();
            _osd.BringToFront();
            _frozen = true;
            UpdateOverlayMode();
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

        using var dlg = Ui.NewDialog(L.T("連写スナップショット"), 300);

        var numInt = Ui.Number(0.5m, 3600, 5, 100);
        numInt.DecimalPlaces = 1;
        numInt.Increment = 0.5m;
        var numCnt = Ui.Number(1, 999, 10, 100);
        var grid = Ui.Grid()
            .AddRow(Ui.Caption(L.T("間隔（秒）:")), numInt)
            .AddRow(Ui.Caption(L.T("枚数:")), numCnt);
        Ui.FrameOkCancel(dlg, grid, L.T("開始"));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _burstTotal = (int)numCnt.Value;
        _burstDone = 0;
        _burstSkips = 0;
        _burstTimer.Interval = Math.Max(100, (int)(numInt.Value * 1000));
        _burstTimer.Start();
        ShowOsd(L.F("連写を開始します（{0} 枚・{1} 秒間隔）", _burstTotal, numInt.Value), OsdLongMilliseconds);
    }

    /// <summary>How many ticks in a row a burst waits for a video restart (renderer or style switch, reconnection) before it gives up.</summary>
    private const int BurstMaxSkips = 5;

    /// <summary>The video is being restarted on purpose or by the automatic recovery, and is expected back shortly.</summary>
    private bool VideoRestarting =>
        _engineSwapping || _engineSwitchBusy || _suspended || _resumeTimer.Enabled || _busyText != null || (_vRecovering && !_vGaveUp);

    private void StopBurst(string osd)
    {
        _burstTimer.Stop();
        ShowOsd(osd, OsdLongMilliseconds);
        UpdateChecks();
    }

    private void BurstTick()
    {
        // The video is restarting (a renderer or style switch, a reconnection): there is no picture to take for a moment.
        // Skip this shot rather than end the burst over it, but not forever.
        if (!_video.IsRunning || VideoRestarting)
        {
            if (VideoRestarting && _burstSkips < BurstMaxSkips)
            {
                _burstSkips++;
                Log.Info($"burst: video restarting - shot skipped ({_burstSkips}/{BurstMaxSkips})");
                return;
            }
            StopBurst(L.F("映像が止まったため、連写を終えました（{0}/{1} 枚）", _burstDone, _burstTotal));
            return;
        }

        // An unattended timer must stop on the FIRST real failure: whatever broke
        // one save (full disk, a removed/unwritable save folder) will break
        // every later shot too, so continuing just repeats a modal dialog on
        // every tick — an endless storm the user cannot dismiss ahead of.
        if (SaveSnapshotCore(out _, out string? error))
        {
            _burstSkips = 0;
            _burstDone++;
            ShowOsd(L.F("連写 {0}/{1}", _burstDone, _burstTotal));
        }
        else if (VideoRestarting && _burstSkips < BurstMaxSkips)
        {
            _burstSkips++;   // the restart began between the check above and the grab
            Log.Info($"burst: no picture while the video restarts - shot skipped ({_burstSkips}/{BurstMaxSkips})");
            return;
        }
        else if (!_video.IsRunning)
        {
            StopBurst(L.F("映像が止まったため、連写を終えました（{0}/{1} 枚）", _burstDone, _burstTotal));
            return;
        }
        else
        {
            StopBurst(L.T("連写を中止しました（保存に失敗）"));
            Ui.Message(this, L.F("保存に失敗しました。\n\n{0}", error), MsgKind.Error);
            return;
        }
        if (_burstDone >= _burstTotal) { _burstTimer.Stop(); UpdateChecks(); }
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
            Ui.Message(this, Errors.Describe(ex, L.T("Windows の自動実行の設定を変更できませんでした（アクセスが拒否されました）。")), MsgKind.Warning);
        }
        _miStartup.Checked = IsStartupRegistered();
    }

    // ---- Preferred-device keyword ---------------------------------------

    private void ShowDeviceKeywordSettings()
    {
        using var dlg = Ui.NewDialog(L.T("優先デバイスの設定"), 380);

        var txt = new TextBox { Text = _settings.DeviceKeyword };
        var grid = Ui.Grid()
            .AddRow(Ui.Caption(L.T("キーワード:")), txt)
            .AddSpan(Ui.Hint(L.T("デバイス名にこの語を含む機器を起動時に自動選択します。\n（既定: JVA14）")), underField: false);
        Ui.FrameOkCancel(dlg, grid);
        dlg.InitialFocus = txt;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string keyword = string.IsNullOrWhiteSpace(txt.Text) ? "JVA14" : txt.Text.Trim();
        bool keywordChanged = !string.Equals(_settings.DeviceKeyword, keyword, StringComparison.Ordinal);
        _settings.DeviceKeyword = keyword;
        // A new keyword is a new choice of device: the id picked earlier must not outrank it.
        if (keywordChanged) { _settings.VideoDeviceId = null; _settings.AudioDeviceId = null; }
        SaveSettings();

        // Applying the new keyword right away (rather than only on the next
        // device/app start) matches what the dialog implies it just did.
        var videoMatch = ChooseVideoDevice(VideoEngine.EnumerateDevices(), null, keyword, firstRunFallback: false);   // keyword only; no "first device" fallback
        bool switched = false;
        if (videoMatch != null && videoMatch.Id != _currentVideoInfo?.Id)
        {
            SwitchVideoDevice(videoMatch);
            switched = true;
        }
        var audioMatch = ChooseAudioDevice(AudioEngine.EnumerateDevices(), null, keyword);
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

    // ---- Menu fonts ------------------------------------------------------
    // Menus are made of several separately-scaled pieces: the bar is a child of the form, but the context menu and every
    // drop-down are top-level windows that start at 96 dpi and are rescaled (font included) by WinForms when they are
    // first shown on a monitor with another scaling. Left to inheritance, bar, drop-downs and single items (the update
    // item has a font of its own) end up with fonts made for different scalings. So one font per scaling is set on every
    // strip, drop-down and item, always from the DeviceDpi of the window that is going to show it; and each drop-down
    // checks itself again once it is open, after WinForms has done its own rescaling.

    private const float MenuBasePt = 9f;
    private readonly Dictionary<(int dpi, bool bold), Font> _menuFonts = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripMenuItem, object> _fontHooked = new();

    internal Font MenuFont(int dpi, bool bold = false)
    {
        dpi = Math.Max(96, dpi);
        if (!_menuFonts.TryGetValue((dpi, bold), out Font? f))
            _menuFonts[(dpi, bold)] = f = new Font(Ui.FamilyName, MenuBasePt * dpi / 96f,
                bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        return f;
    }

    private void ApplyMenuFonts()
    {
        _menu.Font = MenuFont(_menu.DeviceDpi);
        ApplyFontTree(_menu.Items, _menu.DeviceDpi);
        _ctx.Font = MenuFont(_ctx.DeviceDpi);
        ApplyFontTree(_ctx.Items, _ctx.DeviceDpi);
        _ctx.Opened -= OnCtxFontOpened;
        _ctx.Opened += OnCtxFontOpened;
        StyleMenuOverflow();

        _checkUpdateNormalFont = MenuFont(_menu.DeviceDpi);
        _checkUpdateBoldFont = MenuFont(_menu.DeviceDpi, true);
        if (_miCheckUpdate != null) _miCheckUpdate.Font = _pendingUpdate != null ? _checkUpdateBoldFont : _checkUpdateNormalFont;
    }

    private void OnCtxFontOpened(object? sender, EventArgs e) => FixDropDownFont(_ctx);

    /// <summary>The menu bar's overflow drop-down (the » button of a narrow window) gets the bar's colours and font.</summary>
    /// <remarks>It already draws with the bar's renderer, but its colours are its own: the overflowed menus keep the bar as
    /// their Owner while the drop-down becomes their Parent, so their (inherited) BackColor is the drop-down's default
    /// SystemColors.Control - and the professional renderer fills a drop-down item whose BackColor differs from its Owner's
    /// with that colour, which painted every menu not pointed at light under the light text. With the bar's BackColor nothing
    /// is filled and the renderer draws the drop-down's own dark background, hover and border. The drop-down lives as long as
    /// the bar, so this is run again (harmlessly) on every menu rebuild.</remarks>
    private void StyleMenuOverflow()
    {
        ToolStripDropDown overflow = _menu.OverflowButton.DropDown;
        overflow.BackColor = Ui.Colors.Back;   // = _menu.BackColor (set later in the constructor)
        overflow.ForeColor = Ui.Colors.Text;
        overflow.Font = MenuFont(_menu.DeviceDpi);
        overflow.Opened -= OnOverflowFontOpened;
        overflow.Opened += OnOverflowFontOpened;
    }

    private void OnOverflowFontOpened(object? sender, EventArgs e) => FixDropDownFont(_menu.OverflowButton.DropDown);

    /// <summary>Items of one strip/drop-down get the font for <paramref name="dpi"/>; the update item keeps its bold when an update is pending.</summary>
    private void ApplyFontTree(ToolStripItemCollection items, int dpi)
    {
        foreach (ToolStripItem i in items)
        {
            i.Font = i == _miCheckUpdate || i.Name == "checkupdate" ? MenuFont(dpi, _pendingUpdate != null) : MenuFont(dpi);
            if (i is ToolStripMenuItem m)
            {
                m.DropDown.Font = MenuFont(dpi);
                if (!_fontHooked.TryGetValue(m, out _))
                {
                    _fontHooked.Add(m, this);
                    // before it opens: sub-menus built just now (device lists, profiles); after: undo WinForms' own rescaling
                    m.DropDownOpening += (_, _) => { m.DropDown.Font = MenuFont(m.DropDown.DeviceDpi); ApplyFontTree(m.DropDownItems, m.DropDown.DeviceDpi); };
                    m.DropDownOpened += (_, _) => FixDropDownFont(m.DropDown);
                }
                if (m.HasDropDownItems) ApplyFontTree(m.DropDownItems, dpi);
            }
        }
    }

    /// <summary>The drop-down is open and has its final scaling: make its font and its items' the one for that scaling.</summary>
    private void FixDropDownFont(ToolStripDropDown dd)
    {
        int dpi = dd.DeviceDpi;
        bool wrong = Math.Abs(dd.Font.SizeInPoints - MenuFont(dpi).SizeInPoints) > 0.05f;
        foreach (ToolStripItem i in dd.Items)
            if (Math.Abs(i.Font.SizeInPoints - MenuFont(dpi).SizeInPoints) > 0.05f) { wrong = true; break; }
        if (!wrong) return;
        Log.Info($"menu font: {dd.Font.SizeInPoints:0.#}pt at {dpi} dpi - set to {MenuFont(dpi).SizeInPoints:0.#}pt");
        dd.Font = MenuFont(dpi);
        ApplyFontTree(dd.Items, dpi);
    }

    /// <summary>Discard and re-create every menu so new-language text is used.</summary>
    private void RebuildMenus(int dpi = 0)
    {
        bool menuWasVisible = _menu.Visible;
        if (_ctx.Visible) _ctx.Close();

        // The builders append to these collections, so clear them first —
        // otherwise every switch would leave the previous language's items
        // behind and the check-state lists would grow without bound.
        _menu.Items.Clear();
        _refilledMenus.Clear();
        _aspectItems.Clear();
        _rotationItems.Clear();
        _pipIdleItems.Clear();
        _pipHoverItems.Clear();
        _pipSizeItems.Clear();
        _pipPosItems.Clear();
        _cursorSecItems.Clear();
        _textSizeItems.Clear();
        _infoModeItems.Clear();
        _rendererItems.Clear();
        _qualityRoots.Clear();
        _qualityItems.Clear();
        _hdrRoots.Clear();
        _hdrItems.Clear();
        _styleItems.Clear();
        _styleRoots.Clear();
        _sharpRoots.Clear();
        _compareItems.Clear();
        _sharpnessItems.Clear();
        _nativeResItems.Clear();
        _chromaItems.Clear();
        _restoreItems.Clear();
        _shadowItems.Clear();
        _streamSizeItems.Clear();
        _sigOnItems.Clear();
        _sigOffItems.Clear();
        _trainCapItems.Clear();
        _trainStartItems.Clear();
        _trainSetupItems.Clear();
        _trainPauseItems.Clear();
        _trainCountItems.Clear();
        _trainChooseRoots.Clear();

        BuildContextMenu(dpi);
        BuildMenu();
        ApplyMenuFonts();
        MainMenuStrip = _menu;
        _menu.Visible = menuWasVisible;

        UpdateChecks();
        UpdateStatus();
        LayoutCanvas();
    }

    // ---- Sleep inhibition ------------------------------------------------

    /// <summary>How long the card may show its own No Signal screen before the display is allowed to sleep again
    /// (a console that is merely restarting comes back well within this).</summary>
    private const long NoSignalAwakeMs = 60_000;
    private long _noSignalSince;   // Environment.TickCount64 when No Signal began; 0 = there is a signal (or it is unknown)

    /// <summary>Keep the display/system awake while video is playing, like a
    /// media player. Cleared when the preview stops or the app closes, while the
    /// window is minimised, and once the card has shown No Signal for a while (the
    /// console is off: the card still delivers frames, so "running" alone would keep
    /// the monitor and the PC awake for good). No Signal is only known on Direct3D.</summary>
    private void UpdatePowerState()
    {
        if (_tornDown) return;
        long now = Environment.TickCount64;
        if (NoSignalNow) { if (_noSignalSince == 0) _noSignalSince = now; }
        else _noSignalSince = 0;
        bool want = _video.IsRunning
            && WindowState != FormWindowState.Minimized
            && (_noSignalSince == 0 || now - _noSignalSince < NoSignalAwakeMs);
        if (want == _keepAwake) return;
        Log.Info(want ? "keeping the display awake" : "letting the display sleep");
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
            _video.Quality = _trainHold ? ScalingQuality.Standard : QualityOf(_settings.ScalingQuality);   // _trainHold: 学習中は映像の重い処理を止める
            _video.Sharpness = _settings.FsrSharpness / 100f;
            _video.NativeResScale = _settings.NativeResScale;
            _video.ChromaGuided = _settings.ChromaGuided;
            _video.RestoreMode = _trainHold ? "off" : _settings.RestoreMode;
            _video.HdrMode = _settings.HdrMode;
            _video.PixelPerfect = _displayMode == VideoDisplayMode.IntegerScale;
            _video.Adjustments = CurrentAdjustments();   // ignored by engines without the capability
            _video.Crop = new Padding(_settings.CropLeft, _settings.CropTop, _settings.CropRight, _settings.CropBottom);
            _video.LowLatency = _settings.LowLatency;    // read by the engine at its next start
            _video.Interpolation = _settings.Interpolation && !_trainHold;
            _video.PowerSaveEnabled = !_testRunning || _testPowerSave;   // the test mode often runs on a black source: it must not be put to sleep
        }
        catch (Exception ex)
        {
            Log.Info("ApplyRenderOptions failed: " + ex.Message);
        }
        ApplyTrainFeatures();
        ApplyAvSync();

        // A crop changes the picture's size, so the aspect-fit layout (and with it the FSR factor)
        // has to follow. Only when a picture was already there: a start fills the size in itself.
        Size displayAfter = _video.DisplayResolution;
        if (displayBefore.Width > 0 && displayBefore.Height > 0 && displayAfter != displayBefore)
            LayoutCanvas();
    }

    private VideoAdjustments CurrentAdjustments() => new(
        _settings.AdjBrightness / 100f, _settings.AdjContrast / 100f,
        _settings.AdjSaturation / 100f, _settings.AdjGamma / 100f, _settings.ShadowBoost);

    /// <summary>One signed offset drives two engines: positive delays the audio; negative
    /// cannot make audio earlier (it already plays at the smallest delay its buffer allows),
    /// so it delays the VIDEO instead. The Media Foundation preview cannot delay video, so
    /// there negative values count as 0.</summary>
    private void ApplyAvSync()
    {
        try
        {
            if (_settings.AvAuto)
            {
                // automatic: the engines work the change off in small steps (see MainForm.AvAuto.cs)
                int tgt = AvTotalTarget();
                _audio.SetExtraDelaySlewed(Math.Max(0, tgt));
                _avVideoTarget = _video.SupportsVideoDelay ? Math.Max(0, -tgt) : 0;
                if (!_video.SupportsVideoDelay) _avVideoCur = 0;
                _video.VideoDelayMs = _avVideoCur;   // a fresh engine starts at 0: put the delay in force back
            }
            else
            {
                int off = _settings.AvOffsetMs;
                _audio.ExtraDelayMs = Math.Max(0, off);
                _avVideoCur = _avVideoTarget = _video.SupportsVideoDelay ? Math.Max(0, -off) : 0;
                _video.VideoDelayMs = _avVideoCur;
            }
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
        int sh0 = _settings.ShadowBoost;

        using var dlg = Ui.NewDialog(L.T("映像の調整"), 440);
        var grid = Ui.Grid();

        UiSlider AddRow(string name, int min, int max, int value, int tick,
            Func<int, string> format, Action<int> apply)
        {
            var track = new UiSlider
            {
                Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                SmallChange = Math.Max(1, tick / 10), LargeChange = tick,
            };
            // The read-out has a fixed width, so the slider does not move while the number changes.
            var valueLabel = Ui.Fixed(Ui.Text(format(track.Value)), 56);
            valueLabel.TextAlign = ContentAlignment.MiddleRight;
            track.ValueChanged += (_, _) =>
            {
                valueLabel.Text = format(track.Value);
                apply(track.Value);
                ApplyRenderOptions();   // live: the video behind the dialog updates as you drag
            };
            grid.AddRow(Ui.Caption(name), track, valueLabel);
            return track;
        }

        UiSlider tb = AddRow(L.T("明るさ"), -100, 100, b0, 25,
            v => v.ToString("+0;-0;0"), v => _settings.AdjBrightness = v);
        UiSlider tc = AddRow(L.T("コントラスト"), 0, 200, c0, 25,
            v => v + "%", v => _settings.AdjContrast = v);
        UiSlider ts = AddRow(L.T("彩度"), 0, 200, s0, 25,
            v => v + "%", v => _settings.AdjSaturation = v);
        UiSlider tg = AddRow(L.T("ガンマ"), 50, 200, g0, 25,
            v => (v / 100.0).ToString("0.00"), v => _settings.AdjGamma = v);

        UiSlider tsh = AddRow(L.T("暗部補正"), 0, VideoAdjustments.MaxShadow, sh0, 1,
            ShadowLevelText, v => _settings.ShadowBoost = v);

        var reset = Ui.Btn(L.T("既定に戻す"));
        reset.Click += (_, _) => { tb.Value = 0; tc.Value = 100; ts.Value = 100; tg.Value = 100; tsh.Value = 0; };
        Ui.FrameOkCancel(dlg, grid, left: new Button[] { reset });

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            (_settings.AdjBrightness, _settings.AdjContrast, _settings.AdjSaturation, _settings.AdjGamma) = (b0, c0, s0, g0);
            _settings.ShadowBoost = sh0;
            ApplyRenderOptions();
            UpdateStatus();
            return;
        }
        MarkSettingsDirty();
        UpdateStatus();
    }

    private static string ShadowLevelText(int level) => level switch
    {
        1 => L.T("弱"), 2 => L.T("中"), 3 => L.T("強"), _ => L.T("オフ"),
    };

    private ToolStripMenuItem BuildShadowSubmenu()
    {
        _miShadow = new ToolStripMenuItem(L.T("暗部補正"));
        for (int level = 0; level <= VideoAdjustments.MaxShadow; level++)
        {
            int lv = level;
            var item = Radio(new ToolStripMenuItem(ShadowLevelText(lv), null, (_, _) => SetShadowBoost(lv)) { Tag = lv });
            _shadowItems.Add(item);
            _miShadow.DropDownItems.Add(item);
        }
        return _miShadow;
    }

    private void SetShadowBoost(int level)
    {
        if (!_video.SupportsAdjustments) return;
        _settings.ShadowBoost = Math.Clamp(level, 0, VideoAdjustments.MaxShadow);
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        UpdateStatus();
        ShowOsd(L.F("暗部補正: {0}", ShadowLevelText(_settings.ShadowBoost)));
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
        int before = _settings.AvOffsetMs;   // キャンセル puts these back
        bool autoBefore = _settings.AvAuto;
        double autoMsBefore = _avCtl.AutoMs;
        // The slider is the manual offset, or (自動) the trim; each keeps its own value while the box is toggled.
        int manualVal = autoBefore ? 0 : before, trimVal = autoBefore ? before : 0;

        using var dlg = Ui.NewDialog(L.T("音ズレ補正"), 440);

        var chkAuto = Ui.Check(L.T("自動で合わせる"), autoBefore);
        chkAuto.Enabled = _video.SupportsVideoDelay || autoBefore;
        var autoHint = Ui.Hint(L.T("映像と音の遅れの差を測り、少しずつ自動で合わせ続けます。テレビやスピーカーの中の遅れは測れないので、下の微調整で足してください。"));

        var ends = Ui.Row(Ui.Caption(L.T("音声を早める")), Ui.Spring(), Ui.Caption(L.T("音声を遅らせる")));
        var track = new UiSlider
        {
            Minimum = -300, Maximum = 300, Value = Math.Clamp(before, -300, 300),
            TickFrequency = 50, SmallChange = 10, LargeChange = 50,
        };
        var value = Ui.Styled(string.Empty, Ui.HeadingPt, FontStyle.Bold, wrap: true);
        value.TextAlign = ContentAlignment.MiddleCenter;
        // What is in force right now (and, with 自動, how the automatic part is doing).
        var current = Ui.Text(string.Empty);
        var hint = Ui.Hint(L.T("映像の動きと音が合う位置にしてください。手をたたく動画などで合わせると分かりやすいです。"));
        // Live numbers: how long picture and sound take inside YuCap / Windows (offset included).
        var live = Ui.Styled(string.Empty, Ui.BodyPt, FontStyle.Bold, wrap: true);
        var liveHint = Ui.Hint(L.T("キャプチャーボードやテレビの中の遅れは含みません。目安として使ってください。"));
        var match = Ui.Btn(L.T("目安に合わせる"));
        long matchedTick = 0;
        var mfHint = Ui.Hint(L.T("音声を早める補正は Direct3D 描画のときに使えます。標準の描画では 0 として扱います。"));

        var stack = Ui.Stack();
        stack.Add(chkAuto);
        stack.Add(autoHint, before: 4, indent: 24);
        stack.Add(ends, before: Ui.Pad + 4);
        stack.Add(track, before: 4);
        stack.Add(value, before: 4);
        stack.Add(current, before: Ui.Gap);
        stack.Add(hint, before: 4);
        stack.Add(live, before: Ui.Pad + 4);
        stack.Add(liveHint, before: 4);
        stack.Add(match, before: Ui.Gap + 4);
        stack.Add(mfHint, before: Ui.Gap);

        bool snapping = false;
        void ShowMode()
        {
            bool auto = chkAuto.Checked;
            value.Text = auto ? L.F("微調整: {0} ms", AvSigned(track.Value)) : AvOffsetText(track.Value);
            Ui.Show(match, !auto);
            Ui.Show(mfHint, !_video.SupportsVideoDelay && track.Value < 0 && !auto);
        }
        void UpdateCurrent()
        {
            current.Text = AvCorrectionText() + (_settings.AvAuto ? Environment.NewLine + AvAutoStatusText() : string.Empty);
        }

        track.ValueChanged += (_, _) =>
        {
            if (snapping) return;
            int v = (int)Math.Round(track.Value / 10.0) * 10;   // the track is free, the offset moves in 10 ms steps
            if (v != track.Value) { snapping = true; track.Value = v; snapping = false; }
            _settings.AvOffsetMs = v;
            if (chkAuto.Checked) trimVal = v; else manualVal = v;
            ShowMode();
            if (_settings.AvAuto) AvAutoChanged(false); else ApplyAvSync();   // live: the sound / picture shifts while you drag
            UpdateCurrent();
        };
        chkAuto.CheckedChanged += (_, _) =>
        {
            bool auto = chkAuto.Checked;
            snapping = true; track.Value = Math.Clamp(auto ? trimVal : manualVal, track.Minimum, track.Maximum); snapping = false;
            _settings.AvOffsetMs = track.Value;
            _settings.AvAuto = auto;
            AvAutoChanged(true);
            ShowMode();
            UpdateCurrent();
        };

        var reset = Ui.Btn(L.T("既定に戻す"));
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
            live.Text = line + Environment.NewLine + verdict;
            bool settling = matchedTick != 0 && Environment.TickCount64 - matchedTick < 2500;   // the numbers still show the old offset
            match.Enabled = known && !settling;
            UpdateCurrent();
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
        dlg.FormClosed += (_, _) => timer.Dispose();   // (the bold fonts are the shared ones of Ui.FontAt)
        ShowMode();
        UpdateLive();
        timer.Start();

        Ui.FrameOkCancel(dlg, stack, left: new Button[] { reset });

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            _settings.AvOffsetMs = before;
            _settings.AvAuto = autoBefore;
            if (autoBefore) _avCtl.SetAuto(autoMsBefore); else _avCtl.SetAuto(0);
            ApplyAvSync();
            UpdateStatus();
            return;
        }
        MarkSettingsDirty();
        UpdateStatus();
        ShowOsd(_settings.AvAuto ? L.T("音ズレ 自動") : AvOffsetText(_settings.AvOffsetMs));
    }

    private void SetScalingQuality(string quality)
    {
        _settings.ScalingQuality = quality;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        UpdateStatus();
        ShowOsd(L.F("拡大の画質: {0}", quality == "anime4k" && UserModelActive ? L.F("自分専用モデル（{0}）", _settings.A4kModel) : QualityLabel(quality)));
    }

    private static ScalingQuality QualityOf(string setting) =>
        setting == "fsr" ? ScalingQuality.Fsr : setting == "anime4k" ? ScalingQuality.Anime4K : setting == "nvrtx" ? ScalingQuality.NvRtx : ScalingQuality.Standard;

    private static string QualityLabel(string setting) =>
        setting == "fsr" ? L.T("高画質（FSR）") : setting == "anime4k" ? L.T("くっきり（アニメ・イラスト向け）") : setting == "nvrtx" ? L.T("AI 拡大（NVIDIA RTX）") : L.T("標準");

    /// <summary>The short name of the engine's scaling quality for the status bar / info panel (FSR, くっきり, 標準).</summary>
    private string ScalerShortName() =>
        _video.Quality == ScalingQuality.Fsr ? "FSR" : _video.Quality == ScalingQuality.Anime4K ? (UserModelActive ? L.T("自分専用") : L.T("くっきり")) : _video.Quality == ScalingQuality.NvRtx ? L.T("AI 拡大") : L.T("標準");

    private void ToggleChromaGuided()
    {
        _settings.ChromaGuided = !_settings.ChromaGuided;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        ShowOsd(L.F("色のにじみを抑える: {0}", _settings.ChromaGuided ? L.T("オン") : L.T("オフ")));
    }

    private long _guardSerialSeen;
    /// <summary>The GPU budget guard moved: say so (OSD) only when the user changed a setting a moment ago, otherwise the status bar tooltip and
    /// the info panel carry it silently.</summary>
    private bool _nvHintShown;
    private bool _nvBrokenShown;           // the hint shown was the "fell back to FSR" one (re-armed once the scaler works again)
    private int _hdrKindSeen;
    private string? _scalerNoteSeen, _streamNoteSeen, _hdrNoteSeen;

    /// <summary>Why the HDR request is not met, but only while HDR was asked for and SDR is shown (localized); null otherwise.</summary>
    private string? HdrShortfallNote() =>
        _settings.HdrMode != "off" && _video.HdrKind == 0 && _video.HdrNote is { Length: > 0 } n ? L.T(n) : null;

    /// <summary>The engine's notes on what it could not do as asked (localized), for the status bar tooltip and the info panel.</summary>
    private IEnumerable<string> EngineNotes()
    {
        if (_video.ScalerFallbackNote is { Length: > 0 } sc) yield return L.T(sc);
        if (_video.StreamWindowNote is { Length: > 0 } sw) yield return L.T(sw);
        if (HdrShortfallNote() is { } hdr) yield return L.F("HDR 表示: {0}", hdr);
    }

    /// <summary>Notices from the engine, each shown once when it changes: the NVIDIA RTX scaler fell back to FSR or the driver is not
    /// applying it; what HDR shows (and why it is not HDR when HDR was asked for); a scaler that could not run as chosen; the stream
    /// window that lost its picture.</summary>
    private void NvHdrOsdTick()
    {
        if (_testRunning) return;
        if (_settings.ScalingQuality == "nvrtx" && _video.IsRunning)
        {
            string? why = _video.NvRtxBrokenWhy;
            // Working again (the engine retries by itself): a later fallback deserves its own notice.
            if (why == null && _nvBrokenShown) { _nvHintShown = false; _nvBrokenShown = false; }
            if (!_nvHintShown && why != null)
            {
                _nvHintShown = _nvBrokenShown = true;
                Log.Info("NVIDIA RTX scaler fell back to FSR: " + why);
                ShowOsd(L.T("AI 拡大が使えないため、FSR にしています"), OsdLongMilliseconds);
            }
            else if (!_nvHintShown && _video.NvRtxIneffective)
            {
                _nvHintShown = true;
                ShowOsd(L.T("NVIDIA コントロールパネルで「RTX ビデオ強調」をオンにしてください"), OsdLongMilliseconds);
            }
        }
        else _nvHintShown = _nvBrokenShown = false;

        int k = _video.HdrKind;
        string? hdrNote = HdrShortfallNote();
        if (k != _hdrKindSeen)
        {
            _hdrKindSeen = k;
            _hdrNoteSeen = hdrNote;
            if (_settings.HdrMode != "off" || k != 0)
            {
                string text = k == 1 ? L.T("HDR 表示: RTX Video HDR") : k == 2 ? L.T("HDR 表示: 簡易") : L.T("HDR 表示: オフ");
                ShowOsd(hdrNote != null ? text + L.Par(hdrNote) : text, OsdLongMilliseconds);
            }
        }
        else if (hdrNote != _hdrNoteSeen)
        {
            _hdrNoteSeen = hdrNote;
            if (hdrNote != null) ShowOsd(L.F("HDR 表示: {0}", hdrNote), OsdLongMilliseconds);
        }

        string? scaler = _video.IsRunning ? _video.ScalerFallbackNote : null;
        if (scaler != _scalerNoteSeen)
        {
            _scalerNoteSeen = scaler;
            if (scaler != null) ShowOsd(L.T(scaler), OsdLongMilliseconds);
        }
        string? stream = _video.StreamWindowNote;
        if (stream != _streamNoteSeen)
        {
            _streamNoteSeen = stream;
            if (stream != null) ShowOsd(L.T(stream), OsdLongMilliseconds);
        }
    }

    private void SetHdrMode(string mode)
    {
        _settings.HdrMode = AppSettings.NormalizeHdr(mode);
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        ShowOsd(L.F("HDR 表示: {0}", mode == "rtx" ? L.T("HDR で表示（NVIDIA RTX）") : mode == "simple" ? L.T("HDR で表示（簡易）") : L.T("オフ")));
    }

    /// <summary>映像 &gt; HDR 表示: off / RTX Video HDR / simple. The two HDR items need Windows HDR on for the monitor the window is on.</summary>
    private ToolStripMenuItem BuildHdrSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("HDR 表示"));
        foreach (var (mode, label) in new[] { ("off", L.T("オフ")), ("rtx", L.T("HDR で表示（NVIDIA RTX）")), ("simple", L.T("HDR で表示（簡易）")) })
        {
            string md = mode;
            var item = Radio(new ToolStripMenuItem(label, null, (_, _) => SetHdrMode(md)) { Tag = md });
            _hdrItems.Add(item);
            m.DropDownItems.Add(item);
        }
        m.DropDownOpening += (_, _) => UpdateHdrMenu(probeMonitor: true);
        _hdrRoots.Add(m);
        return m;
    }

    private bool MonitorHdrOn()
    {
        try
        {
            string dev = Displays.DeviceNameOfWindow(_canvas.Handle);
            return Program.QueryOutputsHdr().Any(o => o.HdrOn && string.Equals(o.DeviceName, dev, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private bool _hdrMonitorOn;   // the last look at whether the window's monitor has Windows HDR on (taken when the HDR menu opens)

    /// <summary>The HDR menu's state. Its parent is enabled from the renderer alone - it must follow a renderer switch even while
    /// closed (a disabled submenu cannot be opened, so state worked out only on opening would stick). Whether the monitor has HDR
    /// on is asked only when <paramref name="probeMonitor"/> (the submenu opening): that query talks to every output.</summary>
    private void UpdateHdrMenu(bool probeMonitor)
    {
        bool supported = _video.SupportsScalingQuality;
        if (probeMonitor) _hdrMonitorOn = supported && MonitorHdrOn();
        bool hdr = supported && _hdrMonitorOn;
        bool rtx = hdr && D3DRenderer.NvAdapterPresent();
        string hint = !supported ? L.T(D3dOnlyTip)
            : !hdr ? L.T("Windows の設定でこのモニターの HDR をオンにしてください") : string.Empty;
        foreach (var root in _hdrRoots) { root.Enabled = supported; root.ToolTipText = supported ? string.Empty : hint; }
        foreach (var item in _hdrItems)
        {
            string md = (string)item.Tag!;
            item.Checked = md == _settings.HdrMode;
            item.Enabled = md == "off" || (md == "rtx" ? rtx : hdr);
            item.ToolTipText = md == "off" ? string.Empty : md == "rtx" && hdr && !rtx ? L.T("NVIDIA の GPU で使えます") : hint;
        }
    }

    private void GuardOsdTick()
    {
        NvHdrOsdTick();
        long s = _video.GuardSerial;
        if (s == _guardSerialSeen) return;
        _guardSerialSeen = s;
        if (_testRunning || Environment.TickCount64 - _lastUserChangeTick > 20000) return;
        ShowOsd(_video.GuardStatus is { } t ? t : L.T("負荷が下がったため、元の画質に戻しました"));
    }

    private void ToggleRestore()
    {
        _settings.RestoreMode = _settings.RestoreMode == "on" ? "off" : "on";
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        ShowOsd(L.F("ぼやけを取ってから拡大: {0}", _settings.RestoreMode == "on" ? L.T("オン") : L.T("オフ")));
    }

    private void SetNativeRes(string key)
    {
        _settings.NativeResScale = AppSettings.NormalizeNativeRes(key);
        _nativeResOsdTarget = -1;
        MarkSettingsDirty();
        ApplyRenderOptions();
        UpdateChecks();
        string label = key == "off" ? L.T("オフ") : key == "auto" ? L.T("自動") : key + "p";
        ShowOsd(L.F("元の解像度に合わせて拡大: {0}", label));
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
        // Automatic decision: logged only. It stays visible in the info overlay (元の細かさ line).
        Log.Info(st.Target > 0 ? $"native-res auto: {st.Target}p equivalent" : $"native-res auto: {st.SourceHeight}p as is");
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

    /// <summary>The info overlay's 補間 line.</summary>
    // ---- 補間 / 拡大 / くっきり度: status bar (windowed) and info overlay (also in fullscreen) ----

    /// <summary>Whether frame interpolation is working right now. The status bar's 補間 label and the FPS だけ panel both use this,
    /// so they cannot disagree.</summary>
    private bool InterpolationRunning(InterpolationStatus? st) =>
        st is { Active: true, Idle: InterpolationIdle.None } && _video.SupportsInterpolation && _settings.Interpolation && _video.IsRunning;

    /// <summary>"30→144 fps" while interpolating (measured source rate and the rate actually presented),
    /// "待機（理由）" when it is on but idle, null when off or unsupported.</summary>
    private string? InterpMetric(out string tip)
    {
        tip = string.Empty;
        if (!_video.SupportsInterpolation || !_settings.Interpolation || !_video.IsRunning) return null;
        InterpolationStatus? st = _video.InterpolationState;
        if (st == null) return null;
        if (InterpolationRunning(st))
        {
            double shown = _video.Stats?.PresentedFps ?? st.RefreshHz;
            double src = _video.GameFpsReliable && _video.GameFps > 0 ? _video.GameFps : st.SourceFps;
            tip = L.F("フレーム補間: 映像 {0:0.#} fps を、画面に実際に出ている {1:0.#} fps にしています（画面は {2:0.#} Hz）", src, shown, st.RefreshHz);
            return $"{src:0}→{shown.ToString("0")} fps";
        }
        string why = InterpolationInfoText();
        tip = L.F("フレーム補間は今は働いていません: {0}", why);
        return why;
    }

    /// <summary>The detail height the picture really has: the "元の細かさ" height when that is in use, else the
    /// capture (display) height. Estimated is true for the former.</summary>
    private int EffectiveDetailHeight(out bool estimated)
    {
        estimated = false;
        Size res = _video.DisplayResolution;
        if (_video.SupportsNativeRes && _settings.NativeResScale != "off" && _video.NativeResState is { } nr)
        {
            if (nr.Active > 0) { estimated = true; return nr.Active; }
            if (nr.Target > 0 && nr.Target < res.Height) { estimated = true; return nr.Target; }
        }
        return res.Height;
    }

    private string ScalerName()
    {
        Size res = _video.DisplayResolution;
        Rectangle d = ComputeDest(_canvas.ClientSize);
        if (d.Width == res.Width && d.Height == res.Height) return L.T("等倍");
        if (d.Width > res.Width && d.Height > res.Height)
        {
            string name = _video.SupportsScalingQuality && _video.PixelPerfect ? L.T("整数倍")
                : _video.SupportsScalingQuality ? ScalerShortName() : L.T("標準");
            if (_video.SupportsNativeRes && _video.NativeResState is { Active: > 0 }) name += L.Sep + L.T("元の解像度");
            return name;
        }
        if (d.Width < res.Width && d.Height < res.Height) return L.T("縮小");
        return L.T("標準");
    }

    /// <summary>"720p 相当 → 3840x2160（FSR）": detail of the source, size drawn, and the scaler. Null when nothing runs.</summary>
    private string? ScaleMetric(out string tip)
    {
        tip = string.Empty;
        Size res = _video.DisplayResolution;
        if (!_video.IsRunning || res.Width <= 0 || res.Height <= 0) return null;
        Rectangle d = ComputeDest(_canvas.ClientSize);
        if (d.Width <= 0 || d.Height <= 0) return null;
        int h = EffectiveDetailHeight(out bool est);
        string detail = est ? L.F("{0}p 相当", h) : $"{h}p";
        string text = $"{detail} → {d.Width}x{d.Height}{L.Par(ScalerName())}";
        tip = L.F("拡大: 元の絵の細かさ {0} から、画面に出す大きさ {1}x{2} へ（{3}）。キャプチャの大きさは {4}x{5} です。",
            detail, d.Width, d.Height, ScalerName(), res.Width, res.Height);
        return text;
    }

    /// <summary>"くっきり +28%" (YuCap's pipeline vs plain bilinear, measured every few seconds); "くっきり —" until measured;
    /// null when it does not apply (not Direct3D, or the picture is not drawn larger than the source).</summary>
    private string? CrispMetric(out string tip)
    {
        tip = string.Empty;
        if (!_video.SupportsCrispness || !_video.IsRunning) return null;
        Size res = _video.DisplayResolution;
        Rectangle d = ComputeDest(_canvas.ClientSize);
        if (res.Width <= 0 || d.Width <= res.Width || d.Height <= res.Height) return null;
        tip = L.T("くっきり度: 今の拡大処理が、ふつうの拡大（バイリニア）より絵の輪郭をどれだけくっきりさせているか。数秒ごとに、画面の一部分を両方の方法で描いて比べています。");
        if (_video.Crispness is not { } c || Environment.TickCount64 - c.TickMs > 30000) return "—";
        return $"{(c.Ratio >= 0 ? "+" : "−")}{Math.Abs(c.Ratio) * 100:0}%";
    }

    private void UpdateMetricLabels()
    {
        // Sampling costs a little GPU time, so it runs only while someone can see the result.
        bool want = _video.SupportsCrispness && _video.IsRunning && !_frozen && WindowState != FormWindowState.Minimized
                    && ((_status.Visible && !_isPip) || _info.Showing);
        if (_video.SupportsCrispness && _video.CrispnessSampling != want) _video.CrispnessSampling = want;

        void Set(ToolStripStatusLabel l, string? text, string tip)
        {
            bool show = !string.IsNullOrEmpty(text);
            if (l.Visible != show) l.Visible = show;
            if (show) { if (l.Text != text) l.Text = text; l.ToolTipText = tip; }
        }
        string? i = InterpMetric(out string it), sc = ScaleMetric(out string st), cr = CrispMetric(out string ct);
        Set(_lblInterp, i == null ? null : L.F("補間 {0}", i), it); Set(_lblScale, sc, st); Set(_lblCrisp, cr == null ? null : L.F("くっきり {0}", cr), ct);
        UpdateTrainLabel();
    }

    private string InterpolationInfoText()
    {
        if (!_video.SupportsInterpolation) return "—";
        if (!_settings.Interpolation) return L.T("オフ");
        InterpolationStatus? st = _video.InterpolationState;
        if (st == null) return "—";
        return st.Idle switch
        {
            InterpolationIdle.None => $"{st.SourceFps:0}→{st.RefreshHz:0} fps",
            InterpolationIdle.LowLatency => L.T("待機（低遅延）"),
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
        renderer == "d3d" ? "Direct3D" : L.T("互換（Media Foundation）");

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
            NoteVideoStopped();
            try { _video.Stop(); } catch { /* going away anyway */ }
            try { _video.Dispose(); } catch { /* going away anyway */ }
            _canvas.ResetHandle();
            _video = VideoEngines.Create(renderer);
            UpdateOverlayMode();
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

    /// <summary>True for the whole of a renderer / profile / low-latency switch, including the start on the new engine
    /// (<see cref="_engineSwapping"/> only covers the engine replacement itself). A second switch asked for meanwhile
    /// would stop the first one's start halfway.</summary>
    private bool _engineSwitchBusy;

    private async void SwitchRenderer(string renderer)
    {
        if (_engineSwapping || _engineSwitchBusy) { Log.Info("SwitchRenderer: another switch is running"); UpdateChecks(); return; }
        _engineSwitchBusy = true;
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { await SwitchRendererCoreAsync(renderer); }
        catch (Exception ex) { Log.Info("SwitchRenderer: " + ex); }
        finally { _devOpBusy--; _engineSwitchBusy = false; }
    }

    private async Task SwitchRendererCoreAsync(string renderer)
    {
        if (renderer == _settings.Renderer) { UpdateChecks(); return; }
        string previous = _settings.Renderer;
        if (_frozen) ToggleFreeze();

        Exception? failure = await ReplaceEngineAsync(renderer);
        if (failure != null)
        {
            // Fall back to the engine that was working, the same way.
            string reason = Errors.Describe(failure);
            Exception? back = await ReplaceEngineAsync(previous);
            _videoError = back != null ? Errors.Describe(back) : null;
            LayoutCanvas();
            UpdateStatus();
            UpdateChecks();
            Ui.Message(this, reason, MsgKind.Warning);
            if (back == null)
                ShowOsd(L.F("描画方式「{0}」を開始できなかったため、「{1}」に戻しました", RendererName(renderer), RendererName(previous)), OsdLongMilliseconds);
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
        string tip = !supported ? L.T(D3dOnlyTip)
            : pixelPerfect ? L.T("表示モードが整数倍のときは使いません")
            : string.Empty;
        foreach (var root in _qualityRoots)
        {
            root.Enabled = supported && !pixelPerfect;
            root.ToolTipText = tip;
        }
        foreach (var item in _qualityItems)
        {
            item.Checked = (string)item.Tag! == quality && !((string)item.Tag! == "anime4k" && UserModelActive);
            if ((string)item.Tag! == "nvrtx")
            {
                bool nvOk = _video.NvRtxSupported;
                item.Enabled = nvOk;
                item.ToolTipText = nvOk ? L.T("NVIDIA の RTX ビデオ超解像（VSR）で拡大します。NVIDIA コントロールパネルの「RTX ビデオ強調」をオンにしてください。")
                    : L.T("NVIDIA の GPU（Direct3D 描画）で使えます");
            }
        }
        UpdateUserModelChecks();
        foreach (var item in _hdrItems) item.Checked = (string)item.Tag! == _settings.HdrMode;
        foreach (var item in _nativeResItems) item.Checked = (string)item.Tag! == _settings.NativeResScale;
        foreach (var item in _chromaItems) item.Checked = _settings.ChromaGuided;
        bool sharpens = quality is "fsr" or "anime4k";
        string sharpTip = sharpens ? string.Empty : L.T("拡大の画質が 高画質（FSR）か くっきり のときに使えます");
        foreach (var item in _restoreItems)
        {
            item.Checked = _settings.RestoreMode == "on";
            item.Enabled = sharpens;
            item.ToolTipText = sharpens ? RestoreTip : sharpTip;
        }
        foreach (var root in _sharpRoots) { root.Enabled = sharpens; root.ToolTipText = sharpTip; }
        foreach (var item in _sharpnessItems) item.Checked = (int)item.Tag! == _settings.FsrSharpness;
        bool canCompare = supported && !pixelPerfect && quality != "standard";
        foreach (var item in _compareItems)
        {
            item.Enabled = canCompare;
            item.ToolTipText = canCompare
                ? L.T("Q を押している間（クリックなら 2 秒間）、標準の拡大で表示して違いを見比べます。")
                : L.T("拡大の画質が 標準 以外のときに使えます");
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

    /// <summary>Window size minus client size under the current frame style (zero when borderless: the client
    /// fills the whole window, see WM_NCCALCSIZE).</summary>
    private Size NonClientSize()
    {
        if (_isBorderless || !IsHandleCreated) return Size.Empty;
        var rc = new Rect { Left = 0, Top = 0, Right = 100, Bottom = 100 };
        AdjustWindowRectExForDpi(ref rc, GetWindowLong(Handle, GwlStyle), false,
            GetWindowLong(Handle, GwlExStyle), GetDpiForWindow(Handle));
        return new Size(Math.Max(0, rc.Right - rc.Left - 100), Math.Max(0, rc.Bottom - rc.Top - 100));
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
        Size nc = NonClientSize();   // title bar and borders count against the work area too
        int maxClientW = Math.Max(1, wa.Width - nc.Width);
        int maxClientH = Math.Max(1, wa.Height - chrome - nc.Height);
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
        if (sender is Control dc && ReferenceEquals(dc, _canvas) && VirtualOsdRect() is { } osdRect
            && osdRect.Contains(_canvas.PointToClient(Cursor.Position))) return;   // a click on the notice, not on the video
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
        // PiP's opacity/topmost/small-window state must not leak into fullscreen. We are going fullscreen
        // now, so ExitPip must not "return to fullscreen" itself (it would re-enter this method).
        if (_isPip) { _prePipFullscreen = false; ExitPip(); }
        // The second click of the fullscreen double-click also started a drag;
        // cancel it so mouse movement can't drag the fullscreen window around.
        _dragging = false;
        _dragFromMax = false;
        _isFullscreen = true;
        _savedMenuVisible = _menu.Visible;
        _savedStatusVisible = _status.Visible;
        _restoreWindowState = WindowState;
        _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        _menu.Visible = _fsMenuVisible;
        _status.Visible = _fsStatusVisible;
        Log.Info("EnterFullscreen: style set, applying bounds");
        Watchdog.Mark("EnterFullscreen/Bounds");
        Rectangle fsBounds = FullscreenScreen().Bounds;
        Location = fsBounds.Location;   // onto the chosen monitor first (DPI changes happen here)
        Bounds = fsBounds;
        if (Bounds != fsBounds) Bounds = fsBounds;
        HookForeground(true);
        ApplyTopmost();
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
        _restoreBounds = EnsureOnScreen(_restoreBounds);   // the screen it came from may be gone
        Bounds = _restoreBounds;
        if (Bounds != _restoreBounds) Bounds = _restoreBounds;   // may have crossed a DPI boundary
        WindowState = _restoreWindowState;
        HookForeground(false);
        SetTopmost(_alwaysOnTop);
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
        // PiP has its own frame and bars, and ExitPip puts back the ones from before PiP: a change made here would be
        // thrown away without a word. Say so instead.
        if (_isPip) { ShowOsd(L.T("PiP のあいだは切り替えられません")); return; }
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
        // In PiP the window is topmost whatever the setting says; the choice made now belongs to the
        // state PiP will return to (ExitPip restores _prePipTopmost), otherwise it would be lost on exit.
        if (_isPip) _prePipTopmost = _alwaysOnTop;
        if (!_isPip) ApplyTopmost();   // in fullscreen too: "always on top" also keeps it over other apps there
        UpdateChecks();
    }

    private void ToggleMenuBar()
    {
        // PiP has its own frame and bars, and ExitPip puts back the ones from before PiP: a change made here would be
        // thrown away without a word. Say so instead.
        if (_isPip) { ShowOsd(L.T("PiP のあいだは切り替えられません")); return; }
        if (_menu.Visible)
        {
            int h = _menu.Height;                 // read while visible
            _menu.Visible = false;
            if (_isFullscreen) _fsMenuVisible = false; else GrowClientHeight(-h);
            // Hidden, the bar's Alt keys stop working too: say how to get it back.
            if (!_testRunning) ShowOsd(L.T("メニューバーを隠しました。F10 か右クリックで戻せます"));
        }
        else
        {
            _menu.Visible = true;
            if (_isFullscreen) _fsMenuVisible = true; else GrowClientHeight(_menu.Height);
        }
        UpdateChecks();
        LayoutCanvas();
    }

    private void ToggleStatusBar()
    {
        // PiP has its own frame and bars, and ExitPip puts back the ones from before PiP: a change made here would be
        // thrown away without a word. Say so instead.
        if (_isPip) { ShowOsd(L.T("PiP のあいだは切り替えられません")); return; }
        if (_status.Visible)
        {
            int h = _status.Height;
            _status.Visible = false;
            if (_isFullscreen) _fsStatusVisible = false; else GrowClientHeight(-h);
        }
        else
        {
            _status.Visible = true;
            if (_isFullscreen) _fsStatusVisible = true; else GrowClientHeight(_status.Height);
        }
        UpdateChecks();
        LayoutCanvas();
    }

    // ---- Device / mode switching ----------------------------------------

    private void RebuildVideoDeviceList(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        var devices = VideoEngine.EnumerateDevices();
        if (devices.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
            return;
        }
        foreach (var d in devices)
        {
            var info = d;
            var item = new ToolStripMenuItem(info.Name)
            {
                // By id: two cards of the same model have the same name, and both were ticked.
                Checked = _video.CurrentDeviceName != null && _currentVideoInfo != null && info.Id == _currentVideoInfo.Id,
            };
            item.Click += (_, _) => SwitchVideoDevice(info);
            root.DropDownItems.Add(item);
        }
    }

    private void RebuildAudioDeviceList(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        var devices = AudioEngine.EnumerateDevices();
        if (devices.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
            return;
        }
        foreach (var d in devices)
        {
            var info = d;
            var item = new ToolStripMenuItem(info.Name)
            {
                // By id: two cards of the same model have the same name, and both were ticked.
                Checked = _audio.CurrentDeviceName != null && _currentAudioInfo != null && info.Id == _currentAudioInfo.Id,
            };
            item.Click += (_, _) => SwitchAudioDevice(info);
            root.DropDownItems.Add(item);
        }
    }

    /// <summary>Submenu 音声の出力先: the Windows default plus every active output, rebuilt each time it opens.</summary>
    private void RebuildAudioOutputList(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        string? chosen = _settings.AudioOutputDeviceId;
        try
        {
            string defName = AudioEngine.DefaultRenderName() ?? L.T("なし");
            var def = new ToolStripMenuItem(L.F("Windows の既定（{0}）", defName)) { Checked = chosen == null };
            def.Click += (_, _) => SwitchAudioOutput(null, null);
            root.DropDownItems.Add(def);
            root.DropDownItems.Add(new ToolStripSeparator());

            var outputs = AudioEngine.EnumerateRenderDevices();
            foreach (var o in outputs)
            {
                var info = o;
                var item = new ToolStripMenuItem(info.Name) { Checked = chosen != null && info.Id == chosen };
                item.Click += (_, _) => SwitchAudioOutput(info.Id, info.Name);
                root.DropDownItems.Add(item);
            }
            if (chosen != null && !outputs.Any(o => o.Id == chosen))
                root.DropDownItems.Add(new ToolStripMenuItem(L.T("（選んだ出力先は接続されていません）")) { Checked = true, Enabled = false });
            if (outputs.Count == 0)
                root.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
        }
        catch (Exception ex)
        {
            Log.Info("RebuildAudioOutputList failed: " + ex.Message);
            root.DropDownItems.Add(new ToolStripMenuItem(L.T("（デバイスなし）")) { Enabled = false });
        }
    }

    /// <summary>Play the capture audio on another output (null = follow the Windows default). Volume, mute and
    /// the A/V delay live in the engine / settings and carry over to the restarted stream.</summary>
    private void SwitchAudioOutput(string? id, string? name)
    {
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { SwitchAudioOutputCore(id, name); }
        finally { _devOpBusy--; }
    }

    private void SwitchAudioOutputCore(string? id, string? name)
    {
        string? previous = _settings.AudioOutputDeviceId;
        _settings.AudioOutputDeviceId = id;
        if (_audio.IsRunning && _currentAudioInfo != null)
        {
            try { _audio.Start(_currentAudioInfo, id); }
            catch (Exception ex)
            {
                _settings.AudioOutputDeviceId = previous;
                Ui.Message(this, L.F("音声の出力先の切り替えに失敗しました。\n\n{0}", Errors.Describe(ex)),
                    MsgKind.Error);
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

    private async void SwitchVideoDevice(VideoDeviceInfo info)
    {
        if (_engineSwapping) return;
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { await SwitchVideoDeviceCoreAsync(info); }
        catch (Exception ex) { Log.Info("SwitchVideoDevice: " + ex); }
        finally { _devOpBusy--; }
    }

    private async Task SwitchVideoDeviceCoreAsync(VideoDeviceInfo info)
    {
        if (_frozen) ToggleFreeze();
        try
        {
            await StartVideoAsync(info, _savedMode);
            if (_currentVideoInfo?.Id != info.Id)
            {
                // The start that completed was another device's (a later request won): not this switch's success.
                Log.Info($"SwitchVideoDevice: {info.Name} was superseded by {_currentVideoInfo?.Name}");
                UpdateStatus();
                return;
            }
            _videoError = null;
            _recoveryMain = null;
            ResetRecovery();
            _settings.VideoDeviceId = info.Id;   // the user's own choice: startup and hot-plug prefer it from now on
            MarkSettingsDirty();
            LayoutCanvas();
            ShowOsd(L.F("映像: {0}", info.Name));
        }
        catch (OperationCanceledException) { /* stopped on purpose meanwhile */ }
        catch (Exception ex)
        {
            Ui.Message(this, L.F("映像デバイスの切り替えに失敗しました。\n\n{0}", Errors.Describe(ex)),
                MsgKind.Error);
            await RecoverVideoAsync(); // best-effort: bring the previous device back
        }
        UpdateStatus();
    }

    /// <summary>After a failed switch (Start() stops the engine first), try to
    /// restart the last-known-good device so the preview doesn't stay dead.</summary>
    private async Task RecoverVideoAsync()
    {
        if (_currentVideoInfo == null || _video.IsRunning) return;
        try
        {
            await StartVideoAsync(_currentVideoInfo, _savedMode);
            LayoutCanvas();
        }
        catch { /* the error was already reported; leave the empty canvas */ }
    }

    private void RebuildVideoModeList(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();

        // Auto keeps the device's own current type when it runs at 24 fps or more, otherwise it takes the
        // largest size, then the highest rate (CaptureFormats.PickAuto): the label and tip say so.
        var auto = new ToolStripMenuItem(L.T("自動"), null, (_, _) => ClearModePreference())
        {
            Checked = _savedMode == null,
            ToolTipText = L.T("機器が今使っている形式が 24fps 以上ならそのまま使い、そうでなければ 24fps 以上で最大の解像度・最高のフレームレートを選びます。"),
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
        // A hidden NV12 twin of an MJPG entry (the device itself may be running it) is shown as that MJPG entry.
        if (cur is { Compression: "NV12" } && !modes.Any(x => x.Equals(cur)))
        {
            var twin = modes.FirstOrDefault(x => x.Compression == "MJPG" && x.Width == cur.Width && x.Height == cur.Height && x.Fps == cur.Fps);
            if (twin != null) cur = twin;
        }
        ToolStripMenuItem? low = null;   // types under 20 fps (a stick's leftover YUY2 modes) go in their own submenu at the end
        foreach (var m in modes)
        {
            var mode = m;
            var item = new ToolStripMenuItem(ModeLabel(mode))
            {
                Checked = _savedMode != null && mode.Equals(cur),
            };
            item.Click += (_, _) => ApplyVideoMode(mode);
            if (mode.Fps < 20)
            {
                low ??= new ToolStripMenuItem(L.T("低フレームレート"));
                low.DropDownItems.Add(item);
                if (item.Checked) low.Checked = true;
            }
            else root.DropDownItems.Add(item);
        }
        if (low != null) root.DropDownItems.Add(low);
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
            return hasComp ? $"{name}{L.Par($"{m.Width}x{m.Height} {m.Compression}")}" : $"{name}{L.Par($"{m.Width}x{m.Height}")}";
        return hasComp ? $"{m.Width}x{m.Height} {m.Fps}fps{L.Par(m.Compression)}" : $"{m.Width}x{m.Height} {m.Fps}fps";
    }

    private async void ApplyVideoMode(CaptureMode mode)
    {
        if (_engineSwapping) return;
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { await ApplyVideoModeCoreAsync(mode); }
        catch (Exception ex) { Log.Info("ApplyVideoMode: " + ex); }
        finally { _devOpBusy--; }
    }

    private async Task ApplyVideoModeCoreAsync(CaptureMode mode)
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
            await StartVideoAsync(_currentVideoInfo, mode);
            _videoError = null;
            _savedMode = _video.CurrentMode;
            SaveSettings();
            LayoutCanvas();
            ShowOsd(ModeLabel(mode));
        }
        catch (OperationCanceledException) { /* stopped on purpose meanwhile */ }
        catch (Exception ex)
        {
            Ui.Message(this, L.F("映像モードの変更に失敗しました。\n\n{0}", Errors.Describe(ex)),
                MsgKind.Error);
            await RecoverVideoAsync();
        }
        UpdateStatus();
    }

    private async void ClearModePreference()
    {
        if (_engineSwapping) return;
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { await ClearModePreferenceCoreAsync(); }
        catch (Exception ex) { Log.Info("ClearModePreference: " + ex); }
        finally { _devOpBusy--; }
    }

    private async Task ClearModePreferenceCoreAsync()
    {
        _savedMode = null;
        _cliMode = null;
        SaveSettings();
        if (_currentVideoInfo == null) return;
        if (_frozen) ToggleFreeze();
        try
        {
            await StartVideoAsync(_currentVideoInfo, null);
            _videoError = null;
            LayoutCanvas();
            ShowOsd(L.T("映像モード: 自動"));
        }
        catch (OperationCanceledException) { /* stopped on purpose meanwhile */ }
        catch (Exception ex)
        {
            Ui.Message(this, L.F("映像モードの変更に失敗しました。\n\n{0}", Errors.Describe(ex)),
                MsgKind.Error);
            await RecoverVideoAsync();
        }
        UpdateStatus();
    }

    private void SwitchAudioDevice(AudioDeviceInfo info)
    {
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { SwitchAudioDeviceCore(info); }
        finally { _devOpBusy--; }
    }

    private void SwitchAudioDeviceCore(AudioDeviceInfo info)
    {
        AudioDeviceInfo? previous = _currentAudioInfo;
        bool wasRunning = _audio.IsRunning;
        try
        {
            _audio.Start(info, _settings.AudioOutputDeviceId);
            _currentAudioInfo = info;
            ResetRecovery();
            _settings.AudioDeviceId = info.Id;   // the user's own choice: startup and hot-plug prefer it from now on
            MarkSettingsDirty();
            ShowOsd(L.F("音声: {0}", info.Name));
        }
        catch (Exception ex)
        {
            Ui.Message(this, L.F("音声デバイスの切り替えに失敗しました。\n\n{0}", Errors.Describe(ex)),
                MsgKind.Error);
            // Start() leaves the engine stopped on failure: bring the previous device back, like SwitchAudioOutput.
            if (wasRunning && previous != null)
            {
                try { _audio.Start(previous, _settings.AudioOutputDeviceId); _currentAudioInfo = previous; }
                catch (Exception back) { Log.Info("audio: could not restore the previous device - " + back.Message); }
            }
        }
        UpdateStatus();
    }

    // ---- Audio buffer settings ------------------------------------------

    private void ShowAudioBufferSettings()
    {
        _devOpBusy++;   // a user operation (it may show a dialog that pumps messages): no automatic recovery meanwhile
        try { ShowAudioBufferSettingsCore(); }
        finally { _devOpBusy--; }
    }

    private void ShowAudioBufferSettingsCore()
    {
        using var dlg = Ui.NewDialog(L.T("音声バッファの設定"), 440);

        var num = Ui.Number(AudioEngine.MinBufferMs, AudioEngine.MaxBufferMs,
            Math.Clamp(_audio.BufferMilliseconds, AudioEngine.MinBufferMs, AudioEngine.MaxBufferMs), 100);
        num.Increment = 10;

        // Presets: the value is the safety margin YuCap keeps queued (the output device's own queue is small and fixed).
        var btnLow = Ui.Btn(L.T("低遅延 20"));
        var btnMid = Ui.Btn(L.T("標準 40"));
        var btnSafe = Ui.Btn(L.T("安定 100"));
        btnLow.Click += (_, _) => num.Value = 20;
        btnMid.Click += (_, _) => num.Value = 40;
        btnSafe.Click += (_, _) => num.Value = 100;

        var hint = Ui.Hint(
            L.T("音を出す前に YuCap がためておく量です。\n小さいほど音の遅れが減りますが、小さすぎると音が途切れます。\n途切れるときは大きくしてください（既定は 40）。\n実際の遅れは、ステータスバーの「遅延」や情報表示で確かめられます。"));

        var grid = Ui.Grid()
            .AddRow(Ui.Caption(L.T("安全のための余裕 (ms):")), num)
            .AddRow(Ui.Caption(L.T("プリセット:")), Ui.Stretch(Ui.Row(btnLow, btnMid, btnSafe), false))
            .AddSpan(hint, underField: false, before: Ui.Pad);
        Ui.FrameOkCancel(dlg, grid);
        dlg.InitialFocus = num;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int oldBuffer = _audio.BufferMilliseconds;
        _audio.BufferMilliseconds = (int)num.Value;
        if (_audio.IsRunning && _currentAudioInfo != null)
        {
            try { _audio.Start(_currentAudioInfo, _settings.AudioOutputDeviceId); }
            catch (Exception ex)
            {
                Ui.Message(this, L.F("音声の再初期化に失敗しました。\n\n{0}", Errors.Describe(ex)),
                    MsgKind.Error);
                // Back to the setting that worked, and bring the sound back with it.
                _audio.BufferMilliseconds = oldBuffer;
                try { _audio.Start(_currentAudioInfo, _settings.AudioOutputDeviceId); }
                catch (Exception back) { Log.Info("audio: could not restore the previous buffer - " + back.Message); }
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

        // The session really ends (wParam != 0): release the devices first thing (WinForms then closes the form on this
        // same message, and FormClosed finds everything done). wParam == 0: the shutdown was cancelled after
        // WM_QUERYENDSESSION - nothing was torn down (see OnFormClosing) and the window keeps running.
        if (m.Msg == WmEndSession)
        {
            Log.Info(m.WParam != IntPtr.Zero ? "session ending" : "session end cancelled");
            if (m.WParam != IntPtr.Zero) { try { TearDown(); } catch (Exception ex) { Log.Info("teardown at session end failed: " + ex.Message); } }
        }

        // Our app gained or lost the foreground: fullscreen gives up / takes back the topmost band.
        if (m.Msg == WmActivateApp && _isFullscreen && !_isPip)
        {
            BeginInvoke(() => { try { if (_isFullscreen) ApplyTopmost(); } catch (Exception ex) { Log.Info("activate-app topmost failed: " + ex.Message); } });
        }

        // Monitors added / removed / re-arranged / resolution changed: put fullscreen, PiP and off-screen windows right.
        if (m.Msg == WmDisplayChange)
        {
            _displayTimer.Stop();
            _displayTimer.Start();
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
                _resumeWantVideo = _video.IsRunning || _videoWasRunning;
                _resumeWantAudio = _audio.IsRunning || _audioWasRunning;
                _suspended = true;
                NoteVideoStopped(); NoteAudioStopped();
                StopVideo();
                try { _audio.Stop(); } catch { /* ignore */ }
                _canvas.Invalidate();
            }
            else if (evt is PbtApmResumeSuspend or PbtApmResumeAutomatic)
            {
                if (!_suspended)
                {
                    _resumeWantVideo = _video.IsRunning || _videoWasRunning;
                    _resumeWantAudio = _audio.IsRunning || _audioWasRunning;
                    _suspended = true;
                }
                _resumeTimer.Stop();
                _resumeTries = 0;
                _resumeTimer.Interval = ResumeFirstMs;
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
            ApplyAspectAfterDpi();   // a drag onto a monitor with other scaling: the locked aspect is put right now
            LayoutCanvas();       // final, with border clear
            _canvas.Invalidate();
        }

        // Global hotkeys (work even when another app has focus).
        if (m.Msg == WmHotkey)
        {
            if (_testRunning) return;
            int id = (int)m.WParam;
            // A dialog or message box of ours is open: only the sound keys act. A PiP or style switch would change
            // the window / restart the engine under the dialog, and a snapshot could stack a second box on it.
            if (ModalOpen && id is not (HkMute or HkVolUp or HkVolDown))
            {
                Log.Info($"hotkey {id} ignored: a dialog is open");
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            switch (id)
            {
                case HkSnapshot: SaveSnapshot(); break;
                case HkMute: ToggleMute(); break;
                case HkPip: TogglePip(); break;
                case HkVolUp: NudgeVolume(+VolumeStep); break;
                case HkVolDown: NudgeVolume(-VolumeStep); break;
                case HkStyle: ToggleStyle(); break;
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
                m.Result = (IntPtr)EdgeHit(p, ClientSize, Dpi(ResizeBorder));
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
        if (_video.Quality == ScalingQuality.Standard || _video.PixelPerfect) return false;
        Size res = _video.DisplayResolution;
        if (res.Width <= 0 || res.Height <= 0) return false;
        Rectangle dest = ComputeDest(_canvas.ClientSize);
        return dest.Width > res.Width || dest.Height > res.Height;
    }

    private void UpdateStatus()
    {
        UpdatePowerState(); // display sleep inhibition follows the running state
        UpdateMetricLabels();

        string vLabel = L.T("映像"), aLabel = L.T("音声"), volLabel = L.T("音量");
        if (_video.CurrentDeviceName != null)
        {
            Size r = _video.CurrentResolution;
            string comp = _video.CurrentMode?.Compression ?? string.Empty;
            string fps = _video.IsRunning ? $"  {_video.NegotiatedFps}fps{GameFpsNote()}" : string.Empty;
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
            if (_video.GuardStatus is { } guardTip) _lblVideo.ToolTipText += "\n" + guardTip;
            foreach (string note in EngineNotes()) _lblVideo.ToolTipText += "\n" + note;
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
        if (_vGaveUp) stateParts.Add(L.T("映像が停止中"));
        else if (_vRecovering) stateParts.Add(L.T("映像をつなぎ直し中"));
        if (_aGaveUp) stateParts.Add(L.T("音声が停止中"));
        else if (_aRecovering) stateParts.Add(L.T("音声をつなぎ直し中"));
        if (_frozen) stateParts.Add(L.T("一時停止中"));
        if (_isPip) stateParts.Add(L.T("PiP"));
        if (_burstTimer.Enabled) stateParts.Add(L.F("連写 {0}/{1}", _burstDone, _burstTotal));
        if (_zoom > 1.001) stateParts.Add(L.F("ズーム {0}%", (int)Math.Round(_zoom * 100)));
        if (IsFsrUpscaling()) stateParts.Add(ScalerShortName());
        if (_video.SupportsAdjustments && !CurrentAdjustments().IsNeutral) stateParts.Add(L.T("色調整"));
        if (_video.SupportsCrop && HasCrop()) stateParts.Add(L.T("切り取り"));
        if (_video.SupportsLowLatency && _settings.LowLatency) stateParts.Add(L.T("低遅延"));
        if (_settings.AvAuto) stateParts.Add(L.T("音ズレ 自動"));
        if (SettingsStore.SaveBlocked) stateParts.Add(L.T("設定は保存されません"));
        _lblState.Text = string.Join("  ", stateParts);
        _lblState.Visible = stateParts.Count > 0;

        // Title bar mirrors the negotiated mode (handy when the bars are hidden).
        Size res = _video.CurrentResolution;
        string title = _video.IsRunning && res.Width > 0
            ? $"YuCap - {res.Width}x{res.Height} {_video.NegotiatedFps}fps"
            : L.T("YuCap - キャプチャビューア");
        if (Text != title) Text = title;
    }

    // ---- Keyboard --------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // A modal dialog of ours is up (this window is disabled): app shortcuts must not act behind it.
        if (ModalOpen) return base.ProcessCmdKey(ref msg, keyData);

        // Test mode: Esc aborts, every other key is swallowed so nothing changes under the test.
        if (_testRunning)
        {
            if (keyData == Keys.Escape) _testCts?.Cancel();
            return true;
        }

        // Bit 30 of lParam ("previous key state") is set on every WM_KEYDOWN
        // generated by the key still being held down — i.e. auto-repeat, not a
        // fresh press. Holding a key must fire ONE action (one snapshot, one
        // fullscreen toggle), not one per repeat interval. Only the volume keys
        // repeat on purpose.
        bool isRepeat = ((long)msg.LParam & 0x40000000) != 0;

        // Configured hotkeys also work app-locally: if global registration
        // failed (combo owned by another app) or is disabled, the key still
        // works while YuCap has focus. When globally registered, the key never
        // reaches here, so this cannot double-fire.
        if ((keyData & Keys.KeyCode) != Keys.None)
        {
            if (keyData == (Keys)_settings.HotkeyVolumeUp) { NudgeVolume(+VolumeStep); return true; }
            if (keyData == (Keys)_settings.HotkeyVolumeDown) { NudgeVolume(-VolumeStep); return true; }
            if (keyData == (Keys)_settings.HotkeySnapshot) { if (!isRepeat) SaveSnapshot(); return true; }
            if (keyData == (Keys)_settings.HotkeyMute) { if (!isRepeat) ToggleMute(); return true; }
            if (keyData == (Keys)_settings.HotkeyPip) { if (!isRepeat) TogglePip(); return true; }
            if (keyData == (Keys)_settings.HotkeyStyle) { if (!isRepeat) ToggleStyle(); return true; }
        }

        // Ctrl+1 .. Ctrl+9: apply the profile in that menu position.
        if (ProfileIndexForKey(keyData) is int profileIndex)
        {
            if (!isRepeat) ApplyProfileAt(profileIndex);
            return true;
        }

        switch (keyData)
        {
            case Keys.F11:
            case Keys.Alt | Keys.Enter:     // the usual video-player key for it
                if (!isRepeat) ToggleFullscreen();
                return true;
            case Keys.F10:
                if (!isRepeat) ToggleMenuBar();
                return true;
            case Keys.F1:
                if (!isRepeat) ShowShortcuts();
                return true;
            case Keys.Control | Keys.S:
                if (!isRepeat) SaveSnapshot();
                return true;
            case Keys.Control | Keys.C:
                if (!isRepeat) CopySnapshotToClipboard();
                return true;
            case Keys.Control | Keys.G:
                if (!isRepeat) ShowSnapshotGallery();
                return true;
            case Keys.Control | Keys.T:
                if (isRepeat) return true;
                ToggleAlwaysOnTop();
                ShowOsd(_alwaysOnTop ? L.T("常に手前に表示: オン") : L.T("常に手前に表示: オフ"));
                return true;
            case Keys.Control | Keys.B:
                if (!isRepeat) ToggleBorderless();
                return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0:
                if (!isRepeat) ResetZoom();
                return true;
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Add:
                ZoomStep(+1);
                return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract:
                ZoomStep(-1);
                return true;
            case Keys.Control | Keys.Shift | Keys.I:
                if (!isRepeat) CycleInfoMode();
                return true;
            case Keys.Q:
                if (isRepeat) return true;
                _compareTimer.Stop();   // held now: the key-up ends it, not the menu's two seconds
                BeginCompare();
                return true;
            case Keys.M:
                if (!isRepeat) ToggleMute();
                return true;
            case Keys.Space:
                if (!isRepeat) ToggleFreeze();
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
                if (_isFullscreen) { if (!isRepeat) ExitFullscreen(); return true; }
                if (_isPip) { if (!isRepeat) ExitPip(); return true; }
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
        if (!_video.SupportsScalingQuality || _video.Quality == ScalingQuality.Standard || _video.PixelPerfect)
        {
            ShowOsd(L.T("見比べは Direct3D 描画で高画質（FSR）かくっきりのときに使えます"), OsdLongMilliseconds);
            return;
        }
        _compareHeld = true;
        _video.Quality = ScalingQuality.Standard;   // temporary: _settings is untouched
        ShowOsd(L.T("標準の拡大で表示中（Q を離すと元に戻ります）"), OsdLongMilliseconds);
    }

    private void EndCompare(bool silent = false)
    {
        if (!_compareHeld) return;
        _compareHeld = false;
        try { if (_video.SupportsScalingQuality) _video.Quality = QualityOf(_settings.ScalingQuality); } catch { }
        if (!silent) ShowOsd(ScalerShortName());
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

    private InfoMode CurrentInfoMode => _info.Showing ? _info.Mode : InfoMode.Off;

    /// <summary>On/off for the detailed panel (what the test mode uses to put "the real info display" up and take it away).</summary>
    private void ToggleInfo() => SetInfoMode(_info.Showing ? InfoMode.Off : InfoMode.Full);

    private void SetInfoMode(InfoMode mode)
    {
        if (mode == InfoMode.Off)
        {
            _fpsTimer.Stop();
            _info.Showing = false;
            UpdateInfoModeChecks();
            return;
        }
        _info.SetMode(mode);
        UpdateInfo();
        _info.Showing = true;
        _info.BringToFront();
        _osd.BringToFront();
        PositionInfo();
        if (mode == InfoMode.Fps) _fpsTimer.Start(); else _fpsTimer.Stop();
        UpdateInfoModeChecks();
    }

    /// <summary>Ctrl+Shift+I: オフ → FPS だけ → 詳しく → オフ.</summary>
    private void CycleInfoMode() => ChooseInfoMode(CurrentInfoMode switch
    {
        InfoMode.Off => InfoMode.Fps,
        InfoMode.Fps => InfoMode.Full,
        _ => InfoMode.Off,
    });

    /// <summary>A change the user made (key or menu): set it and say so.</summary>
    private void ChooseInfoMode(InfoMode mode)
    {
        SetInfoMode(mode);
        ShowOsd(L.F("情報表示: {0}", InfoModeName(mode)));
    }

    private static string InfoModeName(InfoMode mode) => mode switch
    {
        InfoMode.Fps => L.T("FPS だけ"),
        InfoMode.Full => L.T("詳しく"),
        _ => L.T("オフ"),
    };

    private void UpdateInfoModeChecks()
    {
        InfoMode cur = CurrentInfoMode;
        foreach (var item in _infoModeItems) item.Checked = (InfoMode)item.Tag! == cur;
    }

    private static string InfoModeKey(InfoMode mode) => mode switch { InfoMode.Fps => "fps", InfoMode.Full => "full", _ => "off" };
    private static InfoMode ParseInfoMode(string? key) => key switch { "fps" => InfoMode.Fps, "full" => InfoMode.Full, _ => InfoMode.Off };

    private void PositionInfo()
    {
        if (_info.Showing)
        {
            _info.Location = new Point(_canvas.Right - _info.Width - _info.EdgeGap, _canvas.Top + _info.EdgeGap);
            MarkOverlaysDirty();
        }
    }

    private string DescribeScaling()
    {
        Size res = _video.DisplayResolution;
        if (!_video.IsRunning || res.Width <= 0 || res.Height <= 0) return "—";
        Rectangle d = ComputeDest(_canvas.ClientSize);
        double fx = (double)d.Width / res.Width, fy = (double)d.Height / res.Height;
        string f = Math.Abs(fx - fy) < 0.01 ? $"{fx:0.00}" : $"{fx:0.00}×{fy:0.00}";
        if (d.Width == res.Width && d.Height == res.Height) return L.T("等倍");
        // D3DRenderer uses FSR / the CNN only when upscaling in BOTH dimensions.
        if (d.Width > res.Width && d.Height > res.Height)
        {
            if (_video.SupportsScalingQuality && _video.PixelPerfect) return L.T("整数倍") + $" ×{fx:0.##}";
            if (_video.SupportsScalingQuality && _video.Quality != ScalingQuality.Standard) return ScalerShortName() + " ×" + f;
            return L.T("標準") + " ×" + f;
        }
        if (d.Width < res.Width && d.Height < res.Height) return L.T("縮小") + " ×" + f;
        return L.T("標準") + " ×" + f;
    }

    /// <summary>「（ゲーム 30 fps）」: the game's own frame rate (unique pictures per second) when it was measured; 「（静止）」 while the
    /// picture does not change; empty when unknown (Media Foundation renderer, no frames yet).</summary>
    private string GameFpsNote()
    {
        if (!_video.IsRunning) return string.Empty;
        if (_video.GameFpsReliable && _video.GameFps > 0) return L.F("（ゲーム {0:0} fps）", _video.GameFps);
        if (NoSignalNow) return L.T("（信号なし）");
        if (_video.PictureStill) return L.T("（静止）");
        return string.Empty;
    }

    /// <summary>What the FPS だけ panel shows right now (also used by the test mode).</summary>
    internal void FpsPanelContent(out string big, out string small, out bool interpolating)
    {
        big = "—"; small = string.Empty; interpolating = false;
        if (!_video.IsRunning || _video.Stats is not { } st) return;
        bool gameKnown = _video.GameFpsReliable && _video.GameFps > 0;
        InterpolationStatus? ist = _video.SupportsInterpolation && _settings.Interpolation ? _video.InterpolationState : null;
        interpolating = InterpolationRunning(ist);
        if (interpolating)
        {
            big = st.PresentedFps.ToString("0");
            small = gameKnown ? L.F("ゲーム {0:0}", _video.GameFps)
                : st.InputFps > 0 ? L.F("入力 {0:0}", st.InputFps) : string.Empty;
            return;
        }
        if (NoSignalNow) big = L.T("信号なし");
        else if (_video.PictureStill) big = L.T("静止");
        else if (gameKnown) big = _video.GameFps.ToString("0");
        else big = st.InputFps.ToString("0");
        // 補間 is on but waiting: say why under the number, or the bare game rate looks like a broken 補間
        if (ist != null && !_video.PictureStill)
            small = ist.Idle switch
            {
                InterpolationIdle.RefreshTooLow => L.F("補間待機 {0:0}Hz", ist.RefreshHz),
                InterpolationIdle.LowLatency => L.T("補間待機 低遅延"),
                InterpolationIdle.Unavailable => L.T("補間 使えません"),
                _ => string.Empty,
            };
    }

    private string _fpsLogKey = string.Empty;

    /// <summary>FPS だけ. Interpolation off: the game's real rate (unique pictures per second) alone; the input rate when the
    /// game rate is unknown, 「静止」 for a still picture. Interpolation on and working: the pictures shown per second (each
    /// refresh is a distinct interpolated picture) and 「ゲーム N」 under it. Interpolation on but idle: the game rate and why.</summary>
    private void UpdateFpsInfo()
    {
        FpsPanelContent(out string big, out string small, out bool interp);
        InterpolationStatus? ist = _video.InterpolationState;
        string key = $"{interp}/{ist?.Idle}/{ist?.RefreshHz:0}/{_settings.Interpolation}";
        if (key != _fpsLogKey)
        {
            _fpsLogKey = key;
            Log.Info($"info panel (FPS): interpolation setting={_settings.Interpolation} state={(ist == null ? "none" : $"active={ist.Active} idle={ist.Idle} source={ist.SourceFps:0.0} refresh={ist.RefreshHz:0.0}")} -> shows {(interp ? "shown rate" : "game rate")} {big}");
        }
        string before = _info.Text;
        _info.SetFps(big, small);
        if (_info.Text != before || !_info.Showing) PositionInfo();
    }

    private void UpdateInfo()
    {
        if (_info.Mode == InfoMode.Fps) { UpdateFpsInfo(); return; }
        string renderer = _video.SupportsScalingQuality ? "Direct3D" : L.T("互換（Media Foundation）");
        string input = _video.CurrentMode is { } m ? ModeLabel(m) : "—";
        string fps = _video.Stats is { } st
            ? L.F("入力 {0:0} / 表示 {1:0} / 欠落 {2}", st.InputFps, st.PresentedFps, st.DroppedFrames) + GameFpsNote()
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
            L.T("拡大") + ": " + (ScaleMetric(out _) is { } scm ? scm + " " + DescribeScaling() : DescribeScaling()),
            L.T("くっきり度") + ": " + (CrispMetric(out _) ?? "—"),
            L.T("遅延") + ": " + delay,
            L.T("遅延（YuCap 内）") + ": " + L.F("映像 {0} ／ 音 {1}",
                LatencyText(_video.IsRunning ? _video.VideoLatencyMs : 0, _video.VideoLatencyEstimated),
                LatencyText(_audio.IsRunning ? _audio.LatencyMs : 0, false)),
            L.T("色調整") + ": " + color,
            L.T("音ズレ補正") + ": " + AvInfoText(),
            L.T("補間") + ": " + (InterpMetric(out _) ?? InterpolationInfoText()),
            L.T("動き検出") + ": " + (_settings.Interpolation && _video.SupportsInterpolation ? (D3DRenderer.MotionLabel.StartsWith("NVIDIA", StringComparison.Ordinal) ? L.T("NVIDIA OFA（専用回路）") : L.T("ブロックマッチング（GPU）")) : "—"),
            L.T("元の解像度") + ": " + NativeResInfoText(),
            L.T("表示方法") + ": " + (string.IsNullOrEmpty(_video.PresentMode) ? "—" : _video.PresentMode),
        }) + (_video.GuardStatus is { } guardNote ? "\n" + guardNote : string.Empty)
           + string.Concat(EngineNotes().Select(n => "\n" + n)));
        PositionInfo();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trainCap?.Dispose();
            _video.Dispose();
            _audio.Dispose();
            _osdTimer.Dispose();
            _uiTimer.Dispose();
            _avTimer.Dispose();
            _fpsTimer.Dispose();
            _devTimer.Dispose();
            _burstTimer.Dispose();
            _pipHoverTimer.Dispose();
            _resumeTimer.Dispose();
            _displayTimer.Dispose();
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
