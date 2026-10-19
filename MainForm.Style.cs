using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// 「なめらか ⇔ 低遅延」: the two ways of showing the picture that people actually choose between, as one switch.
/// なめらか = frame interpolation on and low latency off (every refresh shows a new picture, about one source frame later);
/// 低遅延 = low latency on (each frame is shown at once; interpolation then has nothing to fill and idles).
/// Switching restarts the engine on a fresh window (the swap chain kind is fixed at start) through the same path as the
/// 低遅延モード item, so it never blocks the UI thread and keeps fullscreen. Going back to なめらか turns interpolation on.
/// </summary>
public sealed partial class MainForm
{
    private ToolStripMenuItem _miStyle = null!;
    private ToolStripMenuItem _cmiStyle = null!;

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
        if (_miStyle == null || _cmiStyle == null) return;
        bool can = CanSwitchStyle;
        string text = L.T("なめらか ⇔ 低遅延") + (can ? L.Par(L.F("今: {0}", StyleName(CurrentStyle))) : string.Empty);
        string tip = can
            ? L.T("なめらか（フレーム補間オン）と低遅延（低遅延モードオン）を切り替えます。切り替えると映像が一度つながり直ります。")
            : L.T("Direct3D 描画のときに使えます（映像 → 描画方式）");
        string key = HotkeyMenuText((Keys)_settings.HotkeyStyle);
        foreach (ToolStripMenuItem it in new[] { _miStyle, _cmiStyle })
        {
            it.Text = text;
            it.Enabled = can;
            it.ToolTipText = tip;
            it.ShortcutKeyDisplayString = key;
        }
    }

    private async void ToggleStyle()
    {
        if (_engineSwapping || _testRunning || _startingUp) return;
        _devOpBusy++;   // may show a dialog that pumps messages: no automatic recovery meanwhile
        try { await SwitchStyleAsync(); }
        catch (Exception ex) { Log.Info("ToggleStyle: " + ex); }
        finally { _devOpBusy--; }
    }

    /// <summary>Switch to the other style. True when it is done (the OSD has been shown); false when it was not possible
    /// (nothing changed, or the change was undone).</summary>
    private async Task<bool> SwitchStyleAsync()
    {
        if (!CanSwitchStyle)
        {
            ShowOsd(L.T("なめらか ⇔ 低遅延は Direct3D 描画のときに使えます（映像 → 描画方式）"), OsdLongMilliseconds);
            return false;
        }
        NoteUserChange();
        bool toLow = !_settings.LowLatency;
        bool interpBefore = _settings.Interpolation;
        long t0 = Stopwatch.GetTimestamp();
        if (!toLow) _settings.Interpolation = true;   // なめらか: interpolation on, low latency off

        if (!await SetLowLatencyAsync(toLow))
        {
            _settings.Interpolation = interpBefore;
            ApplyRenderOptions();
            UpdateChecks();
            return false;
        }
        MarkSettingsDirty();
        UpdateChecks();
        Log.Info($"style: {(toLow ? "low latency" : "smooth")} in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");

        string text = StyleOsdText();
        ShowOsd(text);
        _ = StyleOsdFollowUpAsync(text);
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
    //  The low-latency restart shared by the 低遅延モード item and the style switch
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
                MessageBox.Show(this, reason, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowOsd(L.T("低遅延モードを切り替えられなかったため、元に戻しました"), OsdLongMilliseconds);
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
