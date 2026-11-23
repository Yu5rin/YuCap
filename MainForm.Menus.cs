using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// The menu bar and the right-click menu.
///
/// Every menu is made by a builder that returns a brand-new item tree, so the right-click menu carries the same menus as
/// submenus, and a few of the same items (made by the same functions) as quick entries at its top. Each item gets a stable
/// id in <see cref="ToolStripItem.Name"/> (<see cref="AssignMenuIds"/>): explicit for the menus and for the items shared with
/// the quick entries, and derived for the rest from the parent's id plus the item's Tag (or its position inside its own
/// builder). Two copies made by one builder therefore carry the same ids wherever they sit, and <see cref="MirrorMenuState"/>
/// pairs the right-click copies with the menu bar's items by id - never by position in the menu - so moving or reordering a
/// menu cannot leave the right-click copy silently stale.
///
/// <see cref="UpdateChecks"/> sets the state of the menu bar's items (the single-item fields point at those: the right-click
/// menu is built first) and of every copy kept in the item lists; the mirror copies everything else.
/// </summary>
public sealed partial class MainForm
{
    // Ids of the menus, and of the items that also sit at the top of the right-click menu.
    private const string IdFile = "m.file", IdDevice = "m.device", IdVideo = "m.video", IdAudio = "m.audio", IdView = "m.view",
        IdWindow = "m.window", IdProfiles = "m.profiles", IdOptions = "m.options", IdHelp = "m.help";

    private ToolStripMenuItem _miFullscreen = null!;
    private ToolStripMenuItem _miD3dSwitch = null!;
    private readonly List<ToolStripMenuItem> _sharpRoots = new();
    private readonly List<ToolStripMenuItem> _compareItems = new();
    private readonly System.Windows.Forms.Timer _compareTimer = new() { Interval = 2000 };

    /// <summary>Marks a choose-one item: the menu renderer draws a dot instead of a tick for it (and screen readers read it as a radio button).</summary>
    private static ToolStripMenuItem Radio(ToolStripMenuItem item)
    {
        item.AccessibleRole = AccessibleRole.RadioButton;
        return item;
    }

    /// <summary>Marks every plain item (no submenu) directly under <paramref name="root"/> as a choose-one item (lists rebuilt when they open).</summary>
    private static void RadioAll(ToolStripMenuItem root)
    {
        foreach (ToolStripItem i in root.DropDownItems)
            if (i is ToolStripMenuItem m && !m.HasDropDownItems) m.AccessibleRole = AccessibleRole.RadioButton;
    }

    private static ToolStripMenuItem TopMenu(string id, string text) => new(text) { Name = id };

    /// <summary>The menus of the menu bar, in order. The right-click menu carries all of them except プロファイル (which it has at the top).</summary>
    private ToolStripMenuItem[] BuildBarMenus(bool forContext) => forContext
        ? new[] { BuildFileMenu(), BuildDeviceMenu(), BuildVideoMenu(), BuildAudioMenu(), BuildViewMenu(), BuildWindowMenu(), BuildOptionsMenu(), BuildHelpMenu() }
        : new[] { BuildFileMenu(), BuildDeviceMenu(), BuildVideoMenu(), BuildAudioMenu(), BuildViewMenu(), BuildWindowMenu(), BuildProfilesMenu(), BuildOptionsMenu(), BuildHelpMenu() };

    private void BuildMenu()
    {
        foreach (ToolStripMenuItem m in BuildBarMenus(forContext: false))
        {
            AssignMenuIds(m);
            _menu.Items.Add(m);
        }
    }

    // ---- items shared by the menu bar and the top of the right-click menu ----

    private ToolStripMenuItem MakeFullscreenItem() => _miFullscreen =
        new ToolStripMenuItem(L.T("全画面表示"), null, (_, _) => ToggleFullscreen()) { Name = "fullscreen", ShortcutKeyDisplayString = "F11" };

    private ToolStripMenuItem MakeFreezeItem() => _miFreeze =
        new ToolStripMenuItem(L.T("一時停止 / 再開"), null, (_, _) => ToggleFreeze()) { Name = "freeze", ShortcutKeyDisplayString = "Space" };

    private ToolStripMenuItem MakeMuteItem() => _miMute =
        new ToolStripMenuItem(L.T("ミュート"), null, (_, _) => ToggleMute()) { Name = "mute", ShortcutKeyDisplayString = "M" };

    private ToolStripMenuItem MakeSnapshotSaveItem() =>
        new(L.T("スナップショットを保存"), null, (_, _) => SaveSnapshot()) { Name = "snapsave", ShortcutKeyDisplayString = "Ctrl+S" };

    // The shortcut text follows the user's binding (UpdateChecks), since it can be changed in ホットキーの設定.
    private ToolStripMenuItem MakePipItem() => _miPip =
        new ToolStripMenuItem(L.T("ピクチャー イン ピクチャー（PiP）"), null, (_, _) => TogglePip()) { Name = "pip" };

    private ToolStripMenuItem MakeMenuBarItem() => _miMenuBar =
        new ToolStripMenuItem(L.T("メニューバーを表示"), null, (_, _) => ToggleMenuBar()) { Name = "menubar", ShortcutKeyDisplayString = "F10" };

    private ToolStripMenuItem MakeExitItem() =>
        new(L.T("終了"), null, (_, _) => Close()) { Name = "exit", ShortcutKeyDisplayString = "Alt+F4" };

    // ---- ファイル ----

    private ToolStripMenuItem BuildFileMenu()
    {
        var file = TopMenu(IdFile, L.T("ファイル(&F)"));
        file.DropDownOpening += (_, _) => UpdateChecks();
        _miBurst = new ToolStripMenuItem(L.T("連写スナップショット..."), null, (_, _) => ShowBurstDialog()) { Name = "burst" };
        file.DropDownItems.Add(MakeSnapshotSaveItem());
        file.DropDownItems.Add(new ToolStripMenuItem(L.T("スナップショットをコピー"), null, (_, _) => CopySnapshotToClipboard())
        {
            ShortcutKeyDisplayString = "Ctrl+C",
        });
        file.DropDownItems.Add(_miBurst);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem(L.T("スナップショット一覧"), null, (_, _) => ShowSnapshotGallery())
        {
            ShortcutKeyDisplayString = "Ctrl+G",
        });
        file.DropDownItems.Add(new ToolStripMenuItem(L.T("スナップショットのフォルダーを開く"), null, (_, _) => OpenSnapshotFolder()));
        file.DropDownItems.Add(new ToolStripMenuItem(L.T("スナップショットの設定..."), null, (_, _) => ShowSnapshotSettings()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MakeExitItem());
        return file;
    }

    // ---- デバイス: what is captured (the video and audio devices and the mode) ----

    private ToolStripMenuItem BuildDeviceMenu()
    {
        var device = TopMenu(IdDevice, L.T("デバイス(&D)"));
        _videoDevicesRoot = new ToolStripMenuItem(L.T("映像デバイス"));
        _audioDevicesRoot = new ToolStripMenuItem(L.T("音声デバイス"));
        _audioOutputRoot = new ToolStripMenuItem(L.T("音声の出力先"));
        _videoModeRoot = new ToolStripMenuItem(L.T("映像モード（解像度・FPS）"));
        var videoDevices = _videoDevicesRoot;
        var audioDevices = _audioDevicesRoot;
        var audioOutput = _audioOutputRoot;
        var videoMode = _videoModeRoot;
        videoDevices.DropDownOpening += (_, _) => { RebuildVideoDeviceList(videoDevices); RadioAll(videoDevices); };
        audioDevices.DropDownOpening += (_, _) => { RebuildAudioDeviceList(audioDevices); RadioAll(audioDevices); };
        audioOutput.DropDownOpening += (_, _) => { RebuildAudioOutputList(audioOutput); RadioAll(audioOutput); };
        videoMode.DropDownOpening += (_, _) => { RebuildVideoModeList(videoMode); RadioAll(videoMode); foreach (ToolStripItem sub in videoMode.DropDownItems) if (sub is ToolStripMenuItem { HasDropDownItems: true } subMenu) RadioAll(subMenu); };   // the low-frame-rate submenu too
        foreach (var root in new[] { videoDevices, audioDevices, audioOutput, videoMode })
            Refilled(root).DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });

        device.DropDownItems.Add(videoDevices);
        device.DropDownItems.Add(videoMode);
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(audioDevices);
        device.DropDownItems.Add(audioOutput);
        device.DropDownItems.Add(new ToolStripSeparator());
        device.DropDownItems.Add(new ToolStripMenuItem(L.T("優先デバイスの設定..."), null, (_, _) => ShowDeviceKeywordSettings()));
        return device;
    }

    // ---- 映像: how the picture looks (pause, playback style, upscaling, HDR, adjustments, renderer) ----

    private ToolStripMenuItem BuildVideoMenu()
    {
        var video = TopMenu(IdVideo, L.T("映像(&V)"));
        video.DropDownOpening += (_, _) => UpdateChecks();
        // Shown only on the compatibility renderer, where most of this menu is grey: the reason and the cure in one click.
        _miD3dSwitch = new ToolStripMenuItem(L.T("Direct3D 描画に切り替える（高画質・補間・HDR が使えます）"), null, (_, _) => SwitchRenderer("d3d"))
        {
            Name = "d3dswitch",
            ToolTipText = L.T("今は互換（Media Foundation）描画のため、グレーの項目は使えません。Direct3D 描画に切り替えると使えるようになります。"),
        };
        _miAdjust = new ToolStripMenuItem(L.T("映像の調整..."), null, (_, _) => ShowAdjustmentsDialog());
        _miCrop = new ToolStripMenuItem(L.T("黒い縁の切り取り..."), null, (_, _) => ShowCropDialog());
        video.DropDownItems.Add(_miD3dSwitch);
        video.DropDownItems.Add(MakeFreezeItem());
        video.DropDownItems.Add(new ToolStripSeparator());
        video.DropDownItems.Add(BuildStyleSubmenu());
        video.DropDownItems.Add(BuildQualitySubmenu());
        video.DropDownItems.Add(BuildTrainSubmenu());
        video.DropDownItems.Add(BuildHdrSubmenu());
        video.DropDownItems.Add(new ToolStripSeparator());
        video.DropDownItems.Add(_miAdjust);
        video.DropDownItems.Add(BuildShadowSubmenu());
        video.DropDownItems.Add(_miCrop);
        video.DropDownItems.Add(BuildRotationSubmenu());
        video.DropDownItems.Add(new ToolStripSeparator());
        video.DropDownItems.Add(BuildRendererSubmenu());
        return video;
    }

    /// <summary>映像 &gt; 映像の出し方: 標準 / なめらか（フレーム補間） / 低遅延 (the hotkey switches なめらか ⇔ 低遅延; see MainForm.Style.cs).</summary>
    private ToolStripMenuItem BuildStyleSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("映像の出し方")) { Name = "style" };
        foreach ((PlayStyle style, string label, string tip) in new[]
                 {
                     (PlayStyle.Standard, L.T("標準"), L.T("映像のコマをそのまま表示します。")),
                     (PlayStyle.Smooth, L.T("なめらか（フレーム補間）"),
                         L.T("画面の更新が映像のコマ数より十分速いときだけ、間のコマを作って滑らかにします。映像が 1 コマぶん遅れます。")),
                     (PlayStyle.LowLatency, L.T("低遅延"),
                         L.T("届いたコマをすぐに表示して遅れを減らします（フレーム補間は働きません）。切り替えると映像が一度つながり直ります。")),
                 })
        {
            PlayStyle s = style;
            var it = Radio(new ToolStripMenuItem(label, null, (_, _) => ChoosePlayStyle(s)) { Tag = s, ToolTipText = tip });
            _styleItems.Add(it);
            m.DropDownItems.Add(it);
        }
        _styleRoots.Add(m);
        return m;
    }

    /// <summary>拡大の画質 &gt; 標準 / 高画質（FSR） / ..., シャープさ, .... Greyed out unless the active
    /// engine can do it (Direct3D only) and the display mode actually scales smoothly.</summary>
    private ToolStripMenuItem BuildQualitySubmenu()
    {
        var m = new ToolStripMenuItem(L.T("拡大の画質")) { Name = "quality" };
        var std = Radio(new ToolStripMenuItem(L.T("標準"), null, (_, _) => SetScalingQuality("standard")) { Tag = "standard" });
        var fsr = Radio(new ToolStripMenuItem(L.T("高画質（FSR）"), null, (_, _) => SetScalingQuality("fsr")) { Tag = "fsr" });
        var a4k = Radio(new ToolStripMenuItem(L.T("くっきり（アニメ・イラスト向け）"), null, (_, _) => { _settings.A4kModel = ""; SetScalingQuality("anime4k"); })
        {
            Tag = "anime4k",
            ToolTipText = L.T("ニューラルネット（Anime4K）で線や文字を細くくっきり描き直します。ゲーム機の絵やイラスト向けで、GPU を多く使います。"),
        });
        var nv = Radio(new ToolStripMenuItem(L.T("AI 拡大（NVIDIA RTX）"), null, (_, _) => SetScalingQuality("nvrtx"))
        {
            Tag = "nvrtx",
            ToolTipText = L.T("NVIDIA の RTX ビデオ超解像（VSR）で拡大します。NVIDIA コントロールパネルの「RTX ビデオ強調」をオンにしてください。"),
        });
        _qualityItems.Add(std);
        _qualityItems.Add(fsr);
        _qualityItems.Add(a4k);
        _qualityItems.Add(nv);
        m.DropDownItems.Add(std);
        m.DropDownItems.Add(fsr);
        m.DropDownItems.Add(a4k);
        m.DropDownItems.Add(nv);
        AttachUserModelItems(m, a4k);
        m.DropDownItems.Add(new ToolStripSeparator());
        var sharp = new ToolStripMenuItem(L.T("シャープさ"));
        foreach (int pct in new[] { 0, 25, 50, 75, 100 })
        {
            int p = pct;
            var item = Radio(new ToolStripMenuItem($"{p}%", null, (_, _) => SetSharpness(p)) { Tag = p });
            _sharpnessItems.Add(item);
            sharp.DropDownItems.Add(item);
        }
        _sharpRoots.Add(sharp);
        m.DropDownItems.Add(sharp);
        var native = new ToolStripMenuItem(L.T("元の解像度に合わせて拡大"))
        {
            ToolTipText = L.T("ゲーム機が小さく描いて引き伸ばした絵（720p を 1080p にしたものなど）を元の大きさに戻してから拡大します。"),
        };
        foreach (string key in AppSettings.NativeResValues)
        {
            string k = key;
            string label = k == "off" ? L.T("オフ") : k == "auto" ? L.T("自動") : k + "p";
            var item = Radio(new ToolStripMenuItem(label, null, (_, _) => SetNativeRes(k)) { Tag = k });
            _nativeResItems.Add(item);
            native.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(native);
        m.DropDownItems.Add(new ToolStripSeparator());
        var chroma = new ToolStripMenuItem(L.T("色のにじみを抑える"), null, (_, _) => ToggleChromaGuided())
        {
            CheckOnClick = false,
            ToolTipText = L.T("色の境目（赤い文字など）のにじみを、映像の明るさの情報を使って抑えます。Direct3D 描画のとき働きます。"),
        };
        _chromaItems.Add(chroma);
        m.DropDownItems.Add(chroma);
        var restore = new ToolStripMenuItem(L.T("ぼやけを取ってから拡大"), null, (_, _) => ToggleRestore())
        {
            ToolTipText = RestoreTip,
        };
        _restoreItems.Add(restore);
        m.DropDownItems.Add(restore);
        m.DropDownItems.Add(new ToolStripSeparator());
        // Hold Q to compare; a click shows the plain upscaling for two seconds.
        var compare = new ToolStripMenuItem(L.T("標準の拡大と見比べる"), null, (_, _) => CompareBriefly())
        {
            Name = "compare",
            ShortcutKeyDisplayString = L.T("Q（押している間）"),
        };
        _compareItems.Add(compare);
        m.DropDownItems.Add(compare);
        _qualityRoots.Add(m);
        return m;
    }

    private static string RestoreTip => L.T("拡大の前に、ニューラルネット（Anime4K Restore）でぼやけを取ります。GPU を余分に使い、縁に輪が出やすくなります。元の解像度に合わせて拡大が働いているときは使いません。");

    /// <summary>The menu item's way into the Q comparison: the plain upscaling for two seconds, then back.</summary>
    private void CompareBriefly()
    {
        _compareTimer.Stop();
        BeginCompare();
        if (!_compareHeld) return;
        _compareTimer.Tick -= OnCompareTimer;
        _compareTimer.Tick += OnCompareTimer;
        _compareTimer.Start();
    }

    private void OnCompareTimer(object? sender, EventArgs e)
    {
        _compareTimer.Stop();
        EndCompare();
    }

    private ToolStripMenuItem BuildRotationSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("回転・反転"));
        foreach (int deg in new[] { 0, 90, 180, 270 })
        {
            int d = deg;
            var item = Radio(new ToolStripMenuItem(d == 0 ? L.T("回転なし") : L.F("{0}° 回転", d), null,
                (_, _) => SetRotationDeg(d)) { Tag = d });
            _rotationItems.Add(item);
            m.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(new ToolStripSeparator());
        _miMirror = new ToolStripMenuItem(L.T("左右反転"), null, (_, _) => ToggleMirror());
        m.DropDownItems.Add(_miMirror);
        return m;
    }

    /// <summary>映像 &gt; 描画方式.</summary>
    private ToolStripMenuItem BuildRendererSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("描画方式"));
        var d3d = Radio(new ToolStripMenuItem(L.T("Direct3D（推奨）"), null, (_, _) => SwitchRenderer("d3d"))
        {
            Tag = "d3d",
            ToolTipText = L.T("高画質の拡大・フレーム補間・HDR・映像の調整など、すべての機能が使えます。"),
        });
        var mf = Radio(new ToolStripMenuItem(L.T("互換（Media Foundation）"), null, (_, _) => SwitchRenderer("mf"))
        {
            Tag = "mf",
            ToolTipText = L.T("Windows の標準の仕組みで表示します。Direct3D 描画がうまく動かないときに使います。拡大の画質などの機能は使えません。"),
        });
        _rendererItems.Add(d3d);
        _rendererItems.Add(mf);
        m.DropDownItems.Add(d3d);
        m.DropDownItems.Add(mf);
        return m;
    }

    // ---- 音声 ----

    private ToolStripMenuItem BuildAudioMenu()
    {
        var audio = TopMenu(IdAudio, L.T("音声(&A)"));
        audio.DropDownOpening += (_, _) => UpdateChecks();
        _miRestoreLevel = new ToolStripMenuItem(L.T("入力レベルを元に戻す"), null, (_, _) => RestoreCaptureLevel());
        audio.DropDownItems.Add(MakeMuteItem());
        audio.DropDownItems.Add(new ToolStripMenuItem(L.T("音量を上げる"), null, (_, _) => NudgeVolume(+VolumeStep)) { ShortcutKeyDisplayString = "↑" });
        audio.DropDownItems.Add(new ToolStripMenuItem(L.T("音量を下げる"), null, (_, _) => NudgeVolume(-VolumeStep)) { ShortcutKeyDisplayString = "↓" });
        audio.DropDownItems.Add(new ToolStripMenuItem(L.T("音量を 100% に戻す"), null, (_, _) => NudgeVolume(100 - _audio.VolumePercent)));
        audio.DropDownItems.Add(new ToolStripSeparator());
        audio.DropDownItems.Add(new ToolStripMenuItem(L.T("音ズレ補正..."), null, (_, _) => ShowAvSyncDialog()));
        audio.DropDownItems.Add(new ToolStripMenuItem(L.T("音声バッファの設定..."), null, (_, _) => ShowAudioBufferSettings()));
        audio.DropDownItems.Add(new ToolStripSeparator());
        audio.DropDownItems.Add(new ToolStripMenuItem(L.T("入力レベルを最大にする..."), null, (_, _) => MaximizeCaptureLevel()));
        audio.DropDownItems.Add(_miRestoreLevel);
        return audio;
    }

    // ---- 表示: what is shown on the screen and how ----

    private ToolStripMenuItem BuildViewMenu()
    {
        var view = TopMenu(IdView, L.T("表示(&S)"));
        view.DropDownOpening += (_, _) => UpdateChecks();
        _miStatusBar = new ToolStripMenuItem(L.T("ステータスバーを表示"), null, (_, _) => ToggleStatusBar()) { Checked = true };

        view.DropDownItems.Add(MakeFullscreenItem());
        view.DropDownItems.Add(BuildFullscreenDisplaySubmenu());
        view.DropDownItems.Add(BuildSignalSubmenu());
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildAspectSubmenu());
        view.DropDownItems.Add(BuildZoomSubmenu());
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(BuildInfoModeSubmenu());
        view.DropDownItems.Add(BuildTextSizeSubmenu());
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(MakeMenuBarItem());
        view.DropDownItems.Add(_miStatusBar);
        return view;
    }

    /// <summary>表示 &gt; 全画面にするモニター. The items are rebuilt every time the submenu opens, so
    /// a monitor plugged in (or removed) since the last time shows up without a restart.</summary>
    private ToolStripMenuItem BuildFullscreenDisplaySubmenu()
    {
        var m = Refilled(new ToolStripMenuItem(L.T("全画面にするモニター")));
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
            RadioAll(m);
        };
        return m;
    }

    private ToolStripMenuItem BuildAspectSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("表示モード"));
        m.DropDownItems.Add(MakeAspectItem(L.T("アスペクト比を保つ"), VideoDisplayMode.AspectFit));
        m.DropDownItems.Add(MakeAspectItem(L.T("ウィンドウいっぱいに引き伸ばす"), VideoDisplayMode.Stretch));
        m.DropDownItems.Add(MakeAspectItem(L.T("原寸"), VideoDisplayMode.OneToOne));
        m.DropDownItems.Add(MakeAspectItem(L.T("整数倍（ドットそのまま）"), VideoDisplayMode.IntegerScale));
        return m;
    }

    private ToolStripMenuItem MakeAspectItem(string text, VideoDisplayMode mode)
    {
        var item = Radio(new ToolStripMenuItem(text, null, (_, _) => SetDisplayMode(mode)) { Tag = mode });
        _aspectItems.Add(item);
        return item;
    }

    /// <summary>表示 &gt; ズーム: the digital zoom that Ctrl + the wheel (and Ctrl++ / Ctrl+-) also changes.</summary>
    private ToolStripMenuItem BuildZoomSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("ズーム"));
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("ズームイン"), null, (_, _) => ZoomStep(+1)) { ShortcutKeyDisplayString = "Ctrl++" });
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("ズームアウト"), null, (_, _) => ZoomStep(-1)) { ShortcutKeyDisplayString = "Ctrl+-" });
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("ズームをリセット"), null, (_, _) => ResetZoom()) { ShortcutKeyDisplayString = "Ctrl+0" });
        m.ToolTipText = L.T("Ctrl を押しながらホイールでも変えられます。ズーム中は映像をドラッグして見る位置を動かせます。");
        return m;
    }

    /// <summary>One zoom step from the menu or the keyboard (the same 25% as one notch of Ctrl + the wheel).</summary>
    private void ZoomStep(int steps)
    {
        if (!_video.IsRunning) { ShowOsd(NoVideoText); return; }
        SetZoom(_zoom + steps * 0.25);
    }

    /// <summary>情報表示 &gt; オフ / FPS だけ / 詳しく (Ctrl+Shift+I cycles through them).</summary>
    private ToolStripMenuItem BuildInfoModeSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("情報表示")) { Name = "info", ShortcutKeyDisplayString = "Ctrl+Shift+I" };
        foreach ((string label, InfoMode mode) in new[]
                 { (L.T("オフ"), InfoMode.Off), (L.T("FPS だけ"), InfoMode.Fps), (L.T("詳しく"), InfoMode.Full) })
        {
            InfoMode md = mode;
            var item = Radio(new ToolStripMenuItem(label, null, (_, _) => ChooseInfoMode(md)) { Tag = md });
            _infoModeItems.Add(item);
            m.DropDownItems.Add(item);
        }
        return m;
    }

    // ---- ウィンドウ: the window itself (size, frame, PiP, the stream window) ----

    private ToolStripMenuItem BuildWindowMenu()
    {
        var window = TopMenu(IdWindow, L.T("ウィンドウ(&W)"));
        window.DropDownOpening += (_, _) => UpdateChecks();
        _miLockAspect = new ToolStripMenuItem(L.T("ウィンドウの縦横比を映像に合わせる"), null, (_, _) => ToggleLockAspect());
        _miBorderless = new ToolStripMenuItem(L.T("ウィンドウ枠を隠す"), null, (_, _) => ToggleBorderless()) { ShortcutKeyDisplayString = "Ctrl+B" };
        _miTopmost = new ToolStripMenuItem(L.T("常に手前に表示"), null, (_, _) => ToggleAlwaysOnTop()) { ShortcutKeyDisplayString = "Ctrl+T" };
        window.DropDownItems.Add(BuildSizeSubmenu());
        window.DropDownItems.Add(_miLockAspect);
        window.DropDownItems.Add(_miBorderless);
        window.DropDownItems.Add(_miTopmost);
        window.DropDownItems.Add(new ToolStripSeparator());
        window.DropDownItems.Add(MakePipItem());
        window.DropDownItems.Add(BuildPipSettingsSubmenu());
        window.DropDownItems.Add(new ToolStripSeparator());
        AddStreamMenu(window);
        return window;
    }

    private ToolStripMenuItem BuildSizeSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("ウィンドウサイズ"));
        foreach (int pct in new[] { 50, 75, 100, 125, 150, 200 })
        {
            int p = pct;
            m.DropDownItems.Add(new ToolStripMenuItem($"{p}%", null, (_, _) => ApplySizePreset(p)) { Tag = p });
        }
        return m;
    }

    private ToolStripMenuItem BuildPipSettingsSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("PiP の設定"));

        // Position & size first: with click-through on, the window can't be
        // dragged or resized, so these presets are the only way to control them.
        var pos = new ToolStripMenuItem(L.T("表示位置"));
        foreach ((string label, int corner) in new[]
                 { ("右下", 0), ("左下", 1), ("右上", 2), ("左上", 3) })
        {
            int c = corner;
            var item = Radio(new ToolStripMenuItem(L.T(label), null, (_, _) => SetPipCorner(c)) { Tag = c });
            _pipPosItems.Add(item);
            pos.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(pos);

        var size = new ToolStripMenuItem(L.T("サイズ（映像の原寸比）"));
        foreach (int pct in new[] { 10, 15, 20, 25, 30, 40, 50 })
        {
            int p = pct;
            var item = Radio(new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipSize(p)) { Tag = p });
            _pipSizeItems.Add(item);
            size.DropDownItems.Add(item);
        }
        m.DropDownItems.Add(size);

        var opacity = new ToolStripMenuItem(L.T("不透明度"));
        var idle = new ToolStripMenuItem(L.T("通常時"));
        var hover = new ToolStripMenuItem(L.T("マウスを乗せたとき"));
        foreach (int pct in new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            int p = pct;
            var i1 = Radio(new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipOpacity(p, hover: false)) { Tag = p });
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
            var i2 = Radio(new ToolStripMenuItem($"{p}%", null, (_, _) => SetPipOpacity(p, hover: true)) { Tag = p });
            _pipHoverItems.Add(i2);
            hover.DropDownItems.Add(i2);
        }
        opacity.DropDownItems.Add(idle);
        opacity.DropDownItems.Add(hover);
        m.DropDownItems.Add(opacity);
        m.DropDownItems.Add(new ToolStripSeparator());
        _miClickThrough = new ToolStripMenuItem(L.T("クリックを通す（クリックスルー）"), null, (_, _) => TogglePipClickThrough());
        m.DropDownItems.Add(_miClickThrough);
        return m;
    }

    // ---- オプション: settings of the application as a whole ----

    private ToolStripMenuItem BuildOptionsMenu()
    {
        var options = TopMenu(IdOptions, L.T("オプション(&O)"));
        _miHotkeys = new ToolStripMenuItem(L.T("グローバルホットキーを使う"), null, (_, _) => ToggleGlobalHotkeys())
        {
            ToolTipText = L.T("オンにすると、ほかのアプリを使っている間も YuCap のホットキーが効きます。オフでも YuCap の画面では使えます。"),
        };
        _miStartup = new ToolStripMenuItem(L.T("Windows の起動時に YuCap を起動する"), null, (_, _) => ToggleStartup());
        _miUpdateCheck = new ToolStripMenuItem(L.T("自動で更新する"), null, (_, _) => ToggleUpdateCheck());
        var lang = new ToolStripMenuItem(L.T("言語 / Language"));
        lang.DropDownItems.Add(Radio(new ToolStripMenuItem("日本語", null, (_, _) => SetLanguage("ja")) { Tag = "ja", Checked = !L.English }));
        lang.DropDownItems.Add(Radio(new ToolStripMenuItem("English", null, (_, _) => SetLanguage("en")) { Tag = "en", Checked = L.English }));
        _miCursorHide = new ToolStripMenuItem(L.T("カーソルを自動的に隠す"), null, (_, _) => ToggleCursorAutoHide());
        var cursorSecs = new ToolStripMenuItem(L.T("隠すまでの時間"));
        foreach (int sec in new[] { 1, 2, 3, 5, 10 })
        {
            int s = sec;
            var item = Radio(new ToolStripMenuItem(L.F("{0}秒", s), null, (_, _) => SetCursorHideSeconds(s)) { Tag = s });
            _cursorSecItems.Add(item);
            cursorSecs.DropDownItems.Add(item);
        }

        options.DropDownOpening += (_, _) => { _miStartup.Checked = IsStartupRegistered(); UpdateChecks(); };
        options.DropDownItems.Add(new ToolStripMenuItem(L.T("ホットキーの設定..."), null, (_, _) => ShowHotkeySettings()));
        options.DropDownItems.Add(_miHotkeys);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(_miCursorHide);
        options.DropDownItems.Add(cursorSecs);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(_miStartup);
        options.DropDownItems.Add(_miUpdateCheck);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(lang);
        return options;
    }

    // ---- ヘルプ ----

    private ToolStripMenuItem BuildHelpMenu()
    {
        var help = TopMenu(IdHelp, L.T("ヘルプ(&H)"));
        help.DropDownOpening += (_, _) => UpdateChecks();
        _miCheckUpdate = new ToolStripMenuItem(L.T("更新を確認..."), null,
            async (_, _) => await CheckForUpdatesAsync(manual: true)) { Name = "checkupdate" };
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("操作一覧"), null, (_, _) => ShowShortcuts()) { ShortcutKeyDisplayString = "F1" });
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(_miCheckUpdate);
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("バージョン情報"), null, (_, _) => ShowAbout()));
        help.DropDownItems.Add(new ToolStripSeparator());
        // Last and set apart: it starts a long automatic test that switches the picture around.
        help.DropDownItems.Add(new ToolStripMenuItem(L.T("動作テスト（自動）..."), null, (_, _) => ShowTestModeDialog())
        {
            ToolTipText = L.T("映像の設定を次々に切り替えて、正しく動くかを自動で調べます（数分かかります）。"),
        });
        return help;
    }

    // ---- the right-click menu ----

    private void BuildContextMenu(int dpi = 0)
    {
        ContextMenuStrip old = _ctx;
        _ctx = new ContextMenuStrip { Renderer = Ui.DarkRenderer };
        _ctxDpi = dpi > 0 ? dpi : DeviceDpi;
        _ctx.Opening += OnCtxOpening;
        if (IsHandleCreated) BeginInvoke(new Action(old.Dispose));   // not while one of its own handlers may still be running
        else old.Dispose();

        // At the top, what is switched while playing (each also in its menu below, with the same id); then the menu bar's
        // menus in the same order and with the same contents, so the full set is reachable with the bar hidden or in fullscreen.
        _ctx.Items.Add(MakeFullscreenItem());
        _ctx.Items.Add(MakeFreezeItem());
        _ctx.Items.Add(MakeMuteItem());
        _ctx.Items.Add(MakeSnapshotSaveItem());
        _ctx.Items.Add(new ToolStripSeparator());
        _ctx.Items.Add(BuildStyleSubmenu());
        _ctx.Items.Add(BuildQualitySubmenu());
        _ctx.Items.Add(BuildTrainSubmenu());
        _cmiProfiles = Refilled(new ToolStripMenuItem(L.T("プロファイル")) { Name = IdProfiles });
        _cmiProfiles.DropDownOpening += (_, _) => UpdateProfileMenu();
        UpdateProfileMenu();
        _ctx.Items.Add(_cmiProfiles);
        _ctx.Items.Add(BuildInfoModeSubmenu());
        _ctx.Items.Add(MakePipItem());
        _ctx.Items.Add(new ToolStripSeparator());
        foreach (ToolStripMenuItem sub in BuildBarMenus(forContext: true))
        {
            sub.Text = StripMnemonic(sub.Text ?? string.Empty);
            _ctx.Items.Add(sub);
        }
        _ctx.Items.Add(new ToolStripSeparator());
        _ctx.Items.Add(MakeMenuBarItem());
        _ctx.Items.Add(MakeExitItem());
        foreach (ToolStripItem i in _ctx.Items)
            if (i is ToolStripMenuItem m) AssignMenuIds(m);
        _canvas.ContextMenuStrip = _ctx;
        _freezeView.ContextMenuStrip = _ctx;
    }

    /// <summary>"ファイル(&amp;F)" -> "ファイル" (the right-click menu has no Alt keys; "(&amp;F)" in a context menu would collide with its own letters).</summary>
    internal static string StripMnemonic(string text)
    {
        int i = text.IndexOf("(&", StringComparison.Ordinal);
        if (i > 0 && text.EndsWith(")", StringComparison.Ordinal)) text = text.Substring(0, i);
        return text.Replace("&", string.Empty);
    }

    // ---- ids and the mirror ----

    /// <summary>Menus whose items are made anew each time they open (device and mode lists, monitors, profiles, models): each copy
    /// fills itself with the current state, so their items get no id and are not mirrored.</summary>
    private readonly HashSet<ToolStripMenuItem> _refilledMenus = new();

    private T Refilled<T>(T root) where T : ToolStripMenuItem
    {
        _refilledMenus.Add(root);
        return root;
    }

    /// <summary>Give every item under <paramref name="root"/> that has no id one made from its parent's id and its Tag (or, without a
    /// usable Tag, its position under that parent). The root must already have an id.</summary>
    private void AssignMenuIds(ToolStripMenuItem root)
    {
        if (string.IsNullOrEmpty(root.Name)) root.Name = "?" + StripMnemonic(root.Text ?? string.Empty);
        if (_refilledMenus.Contains(root)) return;
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < root.DropDownItems.Count; i++)
        {
            if (root.DropDownItems[i] is not ToolStripMenuItem c) continue;
            if (string.IsNullOrEmpty(c.Name))
            {
                string key = c.Tag is string or int or bool or Enum ? c.Tag.ToString()! : "#" + i;
                string id = root.Name + "/" + key;
                if (!used.Add(id)) { id = root.Name + "/#" + i; used.Add(id); }
                c.Name = id;
            }
            AssignMenuIds(c);
        }
    }

    /// <summary>The menu bar's items by id. <paramref name="duplicates"/> collects ids found twice (a builder mistake: the second one is not mirrored).</summary>
    private Dictionary<string, ToolStripMenuItem> IndexBarItems(List<string>? duplicates = null)
    {
        var map = new Dictionary<string, ToolStripMenuItem>(StringComparer.Ordinal);
        void Walk(ToolStripItemCollection items)
        {
            foreach (ToolStripItem i in items)
            {
                if (i is not ToolStripMenuItem m) continue;
                if (!string.IsNullOrEmpty(m.Name) && !map.TryAdd(m.Name, m)) duplicates?.Add(m.Name);
                Walk(m.DropDownItems);
            }
        }
        Walk(_menu.Items);
        return map;
    }

    /// <summary>Every item of the right-click menu (at any depth) with an id, together with the menu bar's item of the same id (null when there is none).</summary>
    private IEnumerable<(ToolStripMenuItem ctx, ToolStripMenuItem? bar)> MenuTwins()
    {
        Dictionary<string, ToolStripMenuItem> bar = IndexBarItems();
        var list = new List<(ToolStripMenuItem, ToolStripMenuItem?)>();
        void Walk(ToolStripItemCollection items)
        {
            foreach (ToolStripItem i in items)
            {
                if (i is not ToolStripMenuItem m) continue;
                if (!string.IsNullOrEmpty(m.Name)) list.Add((m, bar.TryGetValue(m.Name, out var b) ? b : null));
                Walk(m.DropDownItems);
            }
        }
        Walk(_ctx.Items);
        return list;
    }

    /// <summary>Copy the state of the menu bar's items to their twins in the right-click menu (paired by id, see the class comment):
    /// check mark, enabled, shown or hidden, text (not of the menus themselves: the right-click copies have no Alt keys), shortcut
    /// text and tool tip.</summary>
    private void MirrorMenuState()
    {
        if (_ctx.Items.Count == 0 || _menu.Items.Count == 0) return;
        foreach ((ToolStripMenuItem y, ToolStripMenuItem? x) in MenuTwins())
        {
            if (x == null) continue;
            if (y.Checked != x.Checked) y.Checked = x.Checked;
            if (y.Enabled != x.Enabled) y.Enabled = x.Enabled;
            if (y.Available != x.Available) y.Available = x.Available;
            if (x.OwnerItem != null && y.Text != x.Text) y.Text = x.Text;
            if (y.ShortcutKeyDisplayString != x.ShortcutKeyDisplayString) y.ShortcutKeyDisplayString = x.ShortcutKeyDisplayString;
            if (y.ToolTipText != x.ToolTipText) y.ToolTipText = x.ToolTipText;
            if (x.Name == "checkupdate")
            {
                Font f = MenuFont(_ctxDpi, _pendingUpdate != null);
                if (y.Font != f) y.Font = f;
            }
        }
    }

    // ---- self-checks (test mode, --dump-menus) ----

    /// <summary>Right-click items whose twin on the menu bar shows another check mark, enabled or shown state (after
    /// <see cref="UpdateChecks"/> there must be none), and right-click ids with no twin at all. Also lists ids the menu bar
    /// has twice (<paramref name="problems"/>).</summary>
    internal int CountMirrorMismatches(List<string>? problems = null)
    {
        var dup = new List<string>();
        IndexBarItems(dup);
        foreach (string d in dup.Distinct()) problems?.Add($"メニューバーに同じ id の項目が2つあります: {d}");
        int n = dup.Count;
        foreach ((ToolStripMenuItem y, ToolStripMenuItem? x) in MenuTwins())
        {
            if (x == null)
            {
                n++;
                problems?.Add($"右クリックの「{StripMnemonic(y.Text ?? "")}」（{y.Name}）にメニューバー側の項目がありません");
                continue;
            }
            if (x.Checked != y.Checked || x.Enabled != y.Enabled || x.Available != y.Available)
            {
                n++;
                problems?.Add($"「{StripMnemonic(y.Text ?? "")}」（{y.Name}）: メニューバーはチェック {x.Checked}・有効 {x.Enabled}・表示 {x.Available}、右クリックはチェック {y.Checked}・有効 {y.Enabled}・表示 {y.Available}");
            }
        }
        return n;
    }

    /// <summary>With the English UI on: every menu text or tool tip that still contains Japanese (an L.T key that has no English
    /// entry - usually a Japanese string that was changed without its key in Strings.cs). Names the user typed (profiles, models)
    /// and the language menu (written in both languages on purpose) are not counted.</summary>
    internal List<string> UntranslatedMenuTexts()
    {
        var found = new List<string>();
        var user = new List<string>();
        user.AddRange(Profiles.Select(p => p.Name));
        try { user.AddRange(UserModels.List()); } catch { /* no model folder */ }
        static bool HasJapanese(string t) => t.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uff00' and <= '\uffef');
        string Clean(string t)
        {
            foreach (string u in user) if (u.Length > 0) t = t.Replace(u.Replace("&", "&&"), "").Replace(u, "");
            return t.Replace("言語 / Language", "").Replace("日本語", "");
        }
        void Walk(ToolStripItemCollection items, string path)
        {
            foreach (ToolStripItem i in items)
            {
                if (i is not ToolStripMenuItem m) continue;
                string here = path + StripMnemonic(m.Text ?? "");
                if (HasJapanese(Clean(m.Text ?? ""))) found.Add(here);
                if (HasJapanese(Clean(m.ToolTipText ?? ""))) found.Add(here + "（ツールチップ）: " + m.ToolTipText);
                if (HasJapanese(Clean(m.ShortcutKeyDisplayString ?? ""))) found.Add(here + "（キー表示）: " + m.ShortcutKeyDisplayString);
                Walk(m.DropDownItems, here + " > ");
            }
        }
        Walk(_menu.Items, "");
        Walk(_ctx.Items, "右クリック > ");
        return found.Distinct().ToList();
    }

    // ---- state ----

    private const string D3dOnlyTip = "Direct3D 描画のときに使えます（映像 → 描画方式）";

    private void UpdateChecks()
    {
        string d3dTip = L.T(D3dOnlyTip);

        foreach (var item in _aspectItems)
            item.Checked = (VideoDisplayMode)item.Tag! == _displayMode;

        foreach (var item in _rendererItems)
            item.Checked = (string)item.Tag! == _settings.Renderer;
        _miD3dSwitch.Available = _settings.Renderer != "d3d";
        UpdateQualityMenu();
        UpdateHdrMenu(probeMonitor: false);
        _miAdjust.Enabled = _video.SupportsAdjustments;
        _miAdjust.ToolTipText = _video.SupportsAdjustments ? string.Empty : d3dTip;
        _miShadow.Enabled = _video.SupportsAdjustments;
        _miShadow.ToolTipText = _video.SupportsAdjustments
            ? L.T("暗い場面の暗い部分だけを持ち上げます。明るい部分と真っ黒はそのままです。")
            : d3dTip;
        foreach (var it in _shadowItems) it.Checked = (int)it.Tag! == _settings.ShadowBoost;

        _miCrop.Enabled = _video.SupportsCrop;
        _miCrop.ToolTipText = _video.SupportsCrop ? string.Empty : d3dTip;
        UpdateStyleItems();
        UpdateStreamChecks();
        UpdateSignalChecks();
        _miFullscreen.Checked = _isFullscreen;
        _miLockAspect.Checked = _lockAspect;
        _miBorderless.Checked = _isBorderless;
        _miTopmost.Checked = _alwaysOnTop;
        _miMenuBar.Checked = _menu.Visible;
        _miStatusBar.Checked = _status.Visible;
        _miMute.Checked = _audio.Muted;
        _miMirror.Checked = _video.Mirror;
        foreach (var item in _rotationItems)
            item.Checked = (int)item.Tag! == _video.Rotation;
        _miFreeze.Checked = _frozen;
        _miPip.Checked = _isPip;
        // The displayed shortcut must track the user's actual binding — it can
        // be changed in ホットキーの設定, and a stale "Ctrl+Alt+P" would lie.
        _miPip.ShortcutKeyDisplayString = HotkeyMenuText((Keys)_settings.HotkeyPip);
        _miClickThrough.Checked = _settings.PipClickThrough;
        _miHotkeys.Checked = _settings.GlobalHotkeys;
        _miCursorHide.Checked = _settings.CursorAutoHide;
        _miUpdateCheck.Checked = _settings.UpdateCheckOnStartup;
        bool canRestore = _settings.CaptureLevelBefore >= 0 && _audio.IsRunning
            && _audio.CurrentDeviceId == _settings.CaptureLevelDeviceId;
        _miRestoreLevel.Enabled = canRestore;
        _miRestoreLevel.ToolTipText = canRestore ? string.Empty
            : L.T("「入力レベルを最大にする」で変えたあと、同じ音声デバイスを使っているときに使えます");

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
            ? L.F("連写を停止（{0}/{1}）", _burstDone, _burstTotal)
            : L.T("連写スナップショット...");

        // Every state toggle funnels through here, so this is the one place
        // that reliably catches "something the user changed" for persistence.
        // Not counted as a user change, though: this also runs whenever a menu
        // merely opens, and that must not keep pushing back the automatic
        // profile switch (the setters of profile values note the change themselves).
        MarkSettingsDirty(userChange: false);
        foreach (var item in _cursorSecItems)
            item.Checked = _settings.CursorAutoHide && (int)item.Tag! == _settings.CursorHideSeconds;
        foreach (var item in _textSizeItems)
            item.Checked = (int)item.Tag! == _settings.OverlayTextPct;
        UpdateInfoModeChecks();
        foreach (var item in _pipIdleItems)
            item.Checked = (int)item.Tag! == _settings.PipOpacity;
        foreach (var item in _pipHoverItems)
            item.Checked = (int)item.Tag! == _settings.PipOpacityHover;
        foreach (var item in _pipSizeItems)
            item.Checked = (int)item.Tag! == _settings.PipSizePct;
        foreach (var item in _pipPosItems)
            item.Checked = (int)item.Tag! == _settings.PipCorner;
        UpdateTrainChecks();
        MirrorMenuState();
    }
}
