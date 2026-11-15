using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// 映像の出し方: 標準 / なめらか / 低遅延, one choose-one menu over the two settings underneath (kept as they are on disk and in
/// profiles, so nothing saved earlier changes meaning):
/// 標準 = interpolation off, low latency off; なめらか = interpolation on, low latency off (every refresh shows a new picture,
/// about one source frame later); 低遅延 = low latency on (each frame is shown at once; the interpolation setting is kept but
/// has nothing to fill and idles).
/// A change of low latency restarts the engine on a fresh window (the swap chain kind is fixed at start), so it never blocks
/// the UI thread and keeps fullscreen; a change of interpolation alone does not restart anything.
/// The hotkey (Ctrl+Alt+L by default) switches なめらか ⇔ 低遅延; from 標準 it goes to 低遅延, and the menu reaches all three.
/// </summary>
public sealed partial class MainForm
{
    private readonly List<ToolStripMenuItem> _styleRoots = new();
    private readonly List<ToolStripMenuItem> _styleItems = new();

    private enum PlayStyle { Smooth, LowLatency, Standard }

    private PlayStyle CurrentStyle =>
        _settings.LowLatency ? PlayStyle.LowLatency : _settings.Interpolation ? PlayStyle.Smooth : PlayStyle.Standard;

    private static string StyleName(PlayStyle s) => s switch
    {
        PlayStyle.LowLatency => L.T("低遅延"),
        PlayStyle.Smooth => L.T("なめらか"),
        _ => L.T("標準"),
    };

    private bool CanSwitchStyle => _video.SupportsLowLatency && _video.SupportsInterpolation;

    private void UpdateStyleItems()
    {
        bool can = CanSwitchStyle;
        // What is shown, not what is stored: on the compatibility renderer neither setting has an effect.
        PlayStyle shown = can ? CurrentStyle : PlayStyle.Standard;
        string key = HotkeyMenuText((Keys)_settings.HotkeyStyle);
        foreach (ToolStripMenuItem root in _styleRoots)
        {
            root.Enabled = can;
            root.ToolTipText = can ? L.T("ホットキーでは なめらか ⇔ 低遅延 を切り替えます。") : L.T(D3dOnlyTip);
            root.ShortcutKeyDisplayString = key;
        }
        foreach (ToolStripMenuItem it in _styleItems)
            it.Checked = (PlayStyle)it.Tag! == shown;
    }

    /// <summary>The hotkey: なめらか ⇔ 低遅延 (標準 goes to 低遅延).</summary>
    private void ToggleStyle() => ChoosePlayStyle(CurrentStyle == PlayStyle.LowLatency ? PlayStyle.Smooth : PlayStyle.LowLatency);

    /// <summary>映像の出し方 from the menu or the hotkey. Serialised with the other engine switches (renderer, profile).</summary>
    private async void ChoosePlayStyle(PlayStyle target)
    {
        if (_testRunning || _startingUp) return;
        if (_engineSwapping || _engineSwitchBusy) { Log.Info("ChoosePlayStyle: another switch is running"); UpdateChecks(); return; }
        _engineSwitchBusy = true;
        _devOpBusy++;   // may show a dialog that pumps messages: no automatic recovery meanwhile
        try { await SwitchStyleAsync(target); }
        catch (Exception ex) { Log.Info("ChoosePlayStyle: " + ex); }
        finally { _devOpBusy--; _engineSwitchBusy = false; }
    }

    /// <summary>Switch to <paramref name="target"/>. True when it is done (the OSD has been shown); false when it was not possible
    /// (nothing changed, or the change was undone).</summary>
    private async Task<bool> SwitchStyleAsync(PlayStyle target)
    {
        if (!CanSwitchStyle)
        {
            ShowOsd(L.T("映像の出し方は Direct3D 描画のときに選べます（映像 → 描画方式）"), OsdLongMilliseconds);
            return false;
        }
        if (target == CurrentStyle) { ShowOsd(L.F("映像の出し方: {0}", StyleName(target))); return true; }
        NoteUserChange();
        bool toLow = target == PlayStyle.LowLatency;
        bool interpBefore = _settings.Interpolation;
        long t0 = Stopwatch.GetTimestamp();
        // 低遅延 keeps the interpolation setting as it is; the other two set it.
        if (!toLow) _settings.Interpolation = target == PlayStyle.Smooth;

        if (toLow != _settings.LowLatency)
        {
            if (!await SetLowLatencyAsync(toLow))
            {
                _settings.Interpolation = interpBefore;
                ApplyRenderOptions();
                UpdateChecks();
                return false;
            }
        }
        else
        {
            ApplyRenderOptions();   // interpolation only: no restart
        }
        MarkSettingsDirty();
        UpdateChecks();
        Log.Info($"style: {target} in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");

        string text = target == PlayStyle.Standard ? L.F("映像の出し方: {0}", StyleName(target)) : StyleOsdText();
        ShowOsd(text);
        if (target != PlayStyle.Standard) _ = StyleOsdFollowUpAsync(text);
        return true;
    }

    /// <summary>"なめらか（補間 39→144 fps・遅れ 約 50 ms）" / "低遅延（遅れ 約 15 ms）" with the numbers measured so far.</summary>
    private string StyleOsdText()
    {
        int late = (int)Math.Round(_video.VideoLatencyMs / 5.0) * 5;   // a measured delay is not worth more digits than 5 ms
        if (_settings.LowLatency)
            return late > 0 ? L.F("低遅延（遅れ 約 {0} ms）", late) : L.T("低遅延");

        InterpolationStatus? st = _video.InterpolationState;
        if (InterpolationRunning(st) && st != null)
        {
            double ps = _video.Stats?.PresentedFps ?? 0;
            double shown = ps > 0 ? ps : st.RefreshHz;
            double src = _video.GameFpsReliable && _video.GameFps > 0 ? _video.GameFps : st.SourceFps;
            return late > 0 ? L.F("なめらか（補間 {0:0}→{1:0} fps・遅れ 約 {2} ms）", src, shown, late)
                            : L.F("なめらか（補間 {0:0}→{1:0} fps）", src, shown);
        }
        // Say why it idles when the reason is one the user can act on (or should know about).
        if (st?.Idle == InterpolationIdle.RefreshTooLow)
            return L.F("なめらか（画面 {0:0}Hz が映像 {1:0}fps より十分高くないため、補間は待機中）", st.RefreshHz, st.SourceFps);
        if (st?.Idle == InterpolationIdle.Unavailable) return L.T("なめらか（この環境では補間を使えません）");
        return late > 0 ? L.F("なめらか（補間は待機中・遅れ 約 {0} ms）", late) : L.T("なめらか（補間は待機中）");
    }

    /// <summary>The numbers are not there yet right after the restart: look again once they are, and update the notice if it
    /// is still the one on screen.</summary>
    private async Task StyleOsdFollowUpAsync(string shown)
    {
        try
        {
            await Task.Delay(1500);
            if (IsDisposed || _testRunning) return;
            string now = StyleOsdText();
            if (now != shown && _osd.Showing && _osdCurrent is { } cur && cur.Text == shown) ShowOsd(now);
        }
        catch (Exception ex) { Log.Info("style follow-up: " + ex.Message); }
    }

    // =====================================================================
    //  The low-latency restart behind 映像の出し方
    // =====================================================================

    /// <summary>Set low latency to <paramref name="on"/> (restarting the engine when a device is open). On failure the old
    /// state is restored, the reason is shown and false is returned. No notice is shown on success: that is the caller's.</summary>
    private async Task<bool> SetLowLatencyAsync(bool on)
    {
        if (_frozen) ToggleFreeze();
        _settings.LowLatency = on;

        if (_currentVideoInfo != null)
        {
            Exception? failure = await ReplaceEngineAsync(_settings.Renderer);
            if (failure != null)
            {
                string reason = Errors.Describe(failure);
                _settings.LowLatency = !on;
                Exception? back = await ReplaceEngineAsync(_settings.Renderer);
                _videoError = back != null ? Errors.Describe(back) : null;
                LayoutCanvas();
                UpdateChecks();
                UpdateStatus();
                Ui.Message(this, reason, MsgKind.Warning);
                ShowOsd(L.T("映像の出し方を切り替えられなかったため、元に戻しました"), OsdLongMilliseconds);
                return false;
            }
            _videoError = null;
        }
        else
        {
            ApplyRenderOptions();
        }

        LayoutCanvas();
        UpdateChecks();
        UpdateStatus();
        SaveSettings();
        return true;
    }
}
