using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// ゲーム機の電源に合わせる. The Direct3D engine tells whether the console is sending a picture (<see cref="IVideoEngine.SignalState"/>:
/// the capture card's own black "No Signal" screen versus a real picture). Two optional actions hang on the changes:
/// signal back after no signal (the console was switched on) -> fullscreen on the 全画面にするモニター, and signal gone again ->
/// back to the window. Only changes trigger anything, and only what this code did itself is undone: whatever the user does by hand
/// (F11, leaving fullscreen) is never fought. Cheap property reads on the UI timer.
/// </summary>
public sealed partial class MainForm
{
    private SignalState _sigLast = SignalState.Unknown;   // the last known state (NoSignal / Signal): Unknown in between does not count as a change
    private bool _sigAutoFullscreen;                      // fullscreen was entered by the signal action and is still on
    private readonly List<ToolStripMenuItem> _sigOnItems = new();
    private readonly List<ToolStripMenuItem> _sigOffItems = new();
    private ToolStripMenuItem? _miSignal;

    private ToolStripMenuItem BuildSignalSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("ゲーム機の電源に合わせる"));
        _miSignal = m;
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("信号が来たとき")) { Enabled = false });
        foreach ((string label, bool on) in new[] { (L.T("何もしない"), false), (L.T("全画面にする（「全画面にするモニター」に）"), true) })
        {
            bool v = on;
            var it = new ToolStripMenuItem(label, null, (_, _) => { _settings.SignalFullscreen = v; UpdateChecks(); }) { Tag = v };
            _sigOnItems.Add(it);
            m.DropDownItems.Add(it);
        }
        m.DropDownItems.Add(new ToolStripSeparator());
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("信号がなくなったとき")) { Enabled = false });
        foreach ((string label, bool on) in new[] { (L.T("そのまま"), false), (L.T("ウィンドウに戻す（自動で全画面にしたときだけ）"), true) })
        {
            bool v = on;
            var it = new ToolStripMenuItem(label, null, (_, _) => { _settings.SignalLeaveFullscreen = v; UpdateChecks(); }) { Tag = v };
            _sigOffItems.Add(it);
            m.DropDownItems.Add(it);
        }
        return m;
    }

    private void UpdateSignalChecks()
    {
        if (_miSignal == null) return;
        bool ok = _video.SupportsStreamWindow;   // Direct3D only, like the signal detection itself
        _miSignal.Enabled = ok;
        _miSignal.ToolTipText = ok
            ? L.T("ゲーム機の電源を入れて映像が来たら全画面にします。映像が来ないあいだ（カードの「No Signal」画面）は、描画をほとんど止めて GPU を休ませます。")
            : L.T("Direct3D 描画のときに使えます（映像 → 描画方式）");
        foreach (var it in _sigOnItems) it.Checked = (bool)it.Tag! == _settings.SignalFullscreen;
        foreach (var it in _sigOffItems) it.Checked = (bool)it.Tag! == _settings.SignalLeaveFullscreen;
    }

    /// <summary>UI timer: act on a change of the signal state.</summary>
    private void SignalTick()
    {
        if (!_isFullscreen) _sigAutoFullscreen = false;           // the user left it (or it was never entered by us)
        SignalState st = _video.IsRunning ? _video.SignalState : SignalState.Unknown;
        if (st == SignalState.Unknown || st == _sigLast) return;
        SignalState prev = _sigLast;
        _sigLast = st;
        Log.Info($"signal: {prev} -> {st}");
        if (_testRunning) return;
        if (prev == SignalState.NoSignal && st == SignalState.Signal && _settings.SignalFullscreen && !_isFullscreen)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            EnterFullscreen(userAction: false);
            _sigAutoFullscreen = true;
            ShowOsd(L.T("映像が来たので全画面にしました"));
        }
        else if (prev == SignalState.Signal && st == SignalState.NoSignal && _settings.SignalLeaveFullscreen
                 && _sigAutoFullscreen && _isFullscreen)
        {
            _sigAutoFullscreen = false;
            ExitFullscreen(userAction: false);
        }
    }

    /// <summary>The card is showing its own No Signal screen (「信号なし」 in the info panel / status bar instead of 「静止」).</summary>
    private bool NoSignalNow => _video.IsRunning && _video.SignalState == SignalState.NoSignal;
}
