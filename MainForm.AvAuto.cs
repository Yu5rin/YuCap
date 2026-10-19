using System;
using System.Windows.Forms;

namespace YuCap;

// 音ズレの自動補正: the glue between AvAutoController (decisions) and the two engines (audio: AudioEngine.SetExtraDelaySlewed,
// video: the Direct3D ring delay, stepped here). The manual dialog keeps working as before; with 「自動で合わせる」 on,
// AvOffsetMs is only the user's trim and the effective offset is  automatic part + trim.
public sealed partial class MainForm
{
    private readonly AvAutoController _avCtl = new();
    private readonly System.Windows.Forms.Timer _avTimer = new() { Interval = 100 };
    private int _avVideoCur, _avVideoTarget;     // video delay actually set / wanted (ms); stepped 2 ms per tick in auto mode
    private long _avLastSample, _avWarmUntil;
    private bool _avPrevVideoRun, _avPrevAudioRun;
    private int _avLimitSide;                    // -1 the picture cannot be delayed (MF), +1 range end, 0 none

    private const int MaxAvMs = 300;            // range of the total offset (same as the slider)
    private const int AvStepMs = 2;              // video delay change per 100 ms tick
    private const int AvWarmupMs = 4000;         // after a stream (re)started the latency numbers are not trusted

    private void InitAvAuto()
    {
        _avTimer.Tick += (_, _) => { try { AvAutoTick(); } catch (Exception ex) { Log.Info("AvAutoTick failed: " + ex.Message); } };
        _avTimer.Start();
    }

    /// <summary>Total offset in force (or being worked towards): the automatic part plus the trim in auto mode, the slider value otherwise.</summary>
    private int AvTotalTarget()
    {
        if (!_settings.AvAuto) return _settings.AvOffsetMs;
        return Math.Clamp((int)Math.Round(_avCtl.AutoMs) + _settings.AvOffsetMs, -MaxAvMs, MaxAvMs);
    }

    /// <summary>The automatic part may move as long as the TOTAL stays in range and, when the picture cannot be delayed
    /// (standard renderer), does not go below 0.</summary>
    private void AvAutoRange(out double min, out double max)
    {
        int trim = _settings.AvOffsetMs;
        min = (_video.SupportsVideoDelay ? -MaxAvMs : 0) - trim;
        max = MaxAvMs - trim;
    }

    private bool AvAutoActive => _settings.AvAuto && _video.IsRunning && _audio.IsRunning;

    /// <summary>The user switched auto on / off or moved the trim: start measuring afresh.</summary>
    private void AvAutoChanged(bool resetAuto)
    {
        if (resetAuto) _avCtl.SetAuto(0); else _avCtl.ResetWindow();
        _avLastSample = 0;
        ApplyAvSync();
    }

    private void AvAutoTick()
    {
        if (!_settings.AvAuto) return;
        // the video delay follows its target in small steps (a jump would drop or repeat several pictures)
        if (_avVideoCur != _avVideoTarget)
        {
            _avVideoCur += Math.Clamp(_avVideoTarget - _avVideoCur, -AvStepMs, AvStepMs);
            try { _video.VideoDelayMs = _avVideoCur; } catch { }
        }

        long now = Environment.TickCount64;
        bool vRun = _video.IsRunning, aRun = _audio.IsRunning;
        if ((vRun && !_avPrevVideoRun) || (aRun && !_avPrevAudioRun)) { _avWarmUntil = now + AvWarmupMs; _avCtl.ResetWindow(); }
        _avPrevVideoRun = vRun; _avPrevAudioRun = aRun;

        if (now - _avLastSample < 500) return;
        _avLastSample = now;

        double v = vRun ? _video.VideoLatencyMs : 0, a = aRun ? _audio.LatencyMs : 0;
        bool stable = vRun && aRun && v > 0 && a > 0 && !_frozen && !_vRecovering && !_aRecovering && !_vGaveUp && !_aGaveUp
            && !_engineSwapping && _devOpBusy == 0 && now >= _avWarmUntil && WindowState != FormWindowState.Minimized;
        bool settled = Math.Abs(_audio.SlewPendingMs) < 0.5 && _avVideoCur == _avVideoTarget;

        AvAutoRange(out double min, out double max);
        if (_avCtl.Tick(stable, settled, a - v, _settings.AvOffsetMs, min, max))
        {
            Log.Info($"AV auto: step #{_avCtl.Steps} -> auto {_avCtl.AutoMs:0} ms (median diff {_avCtl.LastMedian:0.0}, trim {_settings.AvOffsetMs})");
            ApplyAvSync();
        }
        _avLimitSide = !_avCtl.AtLimit ? 0 : (_video.SupportsVideoDelay ? 1 : -1);
    }

    private static string AvSigned(int ms) => ms == 0 ? "0" : ms.ToString("+0;-0");

    /// <summary>Sentence for what is being corrected right now, e.g. 「音を 68 ms 遅らせています」.</summary>
    private static string AvDirectionText(int ms) =>
        ms > 0 ? L.F("音を {0} ms 遅らせています", ms)
        : ms < 0 ? L.F("映像を {0} ms 遅らせています", -ms)
        : L.T("補正していません");

    /// <summary>Dialog line: 「いまの補正: 音を 68 ms 遅らせています（自動）＋微調整 +10 ms」.</summary>
    private string AvCorrectionText()
    {
        if (!_settings.AvAuto)
            return L.F("いまの補正: {0}", AvDirectionText(_settings.AvOffsetMs));
        int auto = (int)Math.Round(_avCtl.AutoMs);
        string head = L.F("いまの補正: {0}", AvDirectionText(auto)) + L.Par(L.T("自動"));
        return head + L.F("＋微調整 {0} ms", AvSigned(_settings.AvOffsetMs));
    }

    /// <summary>Status of the automatic part for the dialog / info overlay.</summary>
    private string AvAutoStatusText()
    {
        if (!_video.IsRunning || !_audio.IsRunning) return L.T("映像か音声が動いていないため、待機しています");
        if (!_video.SupportsVideoDelay) return L.T("標準の描画では映像の遅れを測れないため、自動では合わせられません（映像 → 描画方式で Direct3D にしてください）");
        if (_avLimitSide > 0) return L.T("補正できる範囲の端に達しています");
        if (_avCtl.Converged) return L.T("合っています（動かさずに見守っています）");
        if (double.IsNaN(_avCtl.LastMedian)) return L.T("測定中です（数秒かかります）");
        return L.T("合わせています");
    }

    /// <summary>One line for the info overlay.</summary>
    private string AvInfoText()
    {
        if (!_settings.AvAuto) return _settings.AvOffsetMs == 0 ? L.T("なし") : L.F("手動 {0} ms", AvSigned(_settings.AvOffsetMs));
        return L.F("自動 {0} ms ＋微調整 {1} ms", AvSigned((int)Math.Round(_avCtl.AutoMs)), AvSigned(_settings.AvOffsetMs));
    }
}
