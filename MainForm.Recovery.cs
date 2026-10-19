using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// Choosing the capture devices, and the automatic reconnection when the video or the audio stops by itself
/// (cable pulled and re-plugged, the card reset, the output device removed, the Windows default output changed).
/// Polled from the 500 ms UI timer; deliberate stops (device switch, renderer/mode switch, sleep, test mode,
/// unplugging that is already handled by WM_DEVICECHANGE) never count as a failure.
/// </summary>
public sealed partial class MainForm
{
    // ---- choosing the device ------------------------------------------------

    /// <summary>The device to open: the one the user picked last (by id), else the first whose name contains the
    /// keyword, else null. Never "whatever is there": a microphone or a webcam is not a capture card.</summary>
    internal static VideoDeviceInfo? ChooseVideoDevice(IReadOnlyList<VideoDeviceInfo> devices, string? savedId, string? keyword)
    {
        if (!string.IsNullOrEmpty(savedId))
        {
            var byId = devices.FirstOrDefault(d => d.Id == savedId);
            if (byId != null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(keyword))
            return devices.FirstOrDefault(d => d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    internal static AudioDeviceInfo? ChooseAudioDevice(IReadOnlyList<AudioDeviceInfo> devices, string? savedId, string? keyword)
    {
        if (!string.IsNullOrEmpty(savedId))
        {
            var byId = devices.FirstOrDefault(d => d.Id == savedId);
            if (byId != null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(keyword))
            return devices.FirstOrDefault(d => d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Canvas text when no capture device can be chosen (nothing plugged in, or nothing matches).</summary>
    private void SetNoCaptureDeviceState(bool otherDevicesExist)
    {
        _recoveryMain = L.T("キャプチャーボードが見つかりません");
        _videoError = otherDevicesExist
            ? L.T("使う機器が決まっていません。「デバイス」メニューの「映像デバイス」から選んでください（接続すると自動的に表示されます）")
            : L.T("キャプチャーボードを接続すると自動的に表示されます");
        _canvas.Invalidate();
    }

    private void ClearNoCaptureDeviceState()
    {
        _recoveryMain = null;
        _videoError = null;
    }

    /// <summary>DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED (0x88980800) anywhere in the exception chain.</summary>
    private static bool IsWindowAlreadyComposed(Exception? ex)
    {
        for (int depth = 0; ex != null && depth < 8; depth++, ex = ex.InnerException)
        {
            if (ex.HResult == unchecked((int)0x88980800)) return true;
            if (ex is System.Runtime.InteropServices.COMException ce && ce.ErrorCode == unchecked((int)0x88980800)) return true;
            if (ex.Message.Contains("88980800", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- automatic reconnection ---------------------------------------------

    private static readonly int[] RecoveryBackoffMs = { 1000, 2000, 5000 };
    private const long RecoveryStableMs = 60_000;

    private bool _videoWasRunning, _audioWasRunning;       // was running at the last look; cleared by every deliberate stop
    private int _devOpBusy;                                // >0 while a user-initiated device operation is in progress (it may pump messages)
    private bool _suspended;                               // between sleep and the end of the post-resume restarts
    private string? _recoveryMain;                         // canvas headline replacing 「映像が入力されていません」

    private bool _vRecovering, _vGaveUp;
    private int _vRetry;
    private long _vNextMs, _vOkSince;

    private bool _aRecovering, _aGaveUp;
    private int _aRetry;
    private long _aNextMs, _aOkSince;
    private int _audioPollTick;

    private bool VideoGaveUp => _vGaveUp;
    private bool AudioGaveUp => _aGaveUp;

    private void NoteVideoStopped() => _videoWasRunning = false;
    private void NoteAudioStopped() => _audioWasRunning = false;

    private void ResetRecovery()
    {
        _vRecovering = _vGaveUp = false; _vRetry = 0; _vNextMs = _vOkSince = 0;
        _aRecovering = _aGaveUp = false; _aRetry = 0; _aNextMs = _aOkSince = 0;
    }

    private bool RecoveryAllowed =>
        !IsDisposed && IsHandleCreated && !_testRunning && !_startingUp && !_suspended && _busyText == null
        && _devOpBusy == 0 && !_frozen && !_skipSaveOnClose && !_resumeTimer.Enabled;

    /// <summary>Called by the UI timer every tick (about twice a second).</summary>
    private void CheckEngineHealth()
    {
        long now = Environment.TickCount64;
        try { CheckVideoHealth(now); } catch (Exception ex) { Log.Info("video health check failed: " + ex.Message); }
        try { CheckAudioHealth(now); } catch (Exception ex) { Log.Info("audio health check failed: " + ex.Message); }
    }

    private void CheckVideoHealth(long now)
    {
        bool ended = _video.EndedUnexpectedly;
        if (_video.IsRunning && !ended)
        {
            _videoWasRunning = true;
            _vGaveUp = false;
            if (_vRetry > 0 || _vRecovering)
            {
                // Healthy again; forget the failures only after it has stayed up for a minute.
                if (_vOkSince == 0) _vOkSince = now;
                else if (now - _vOkSince >= RecoveryStableMs) { _vRetry = 0; _vRecovering = false; _vOkSince = 0; }
            }
            return;
        }
        _vOkSince = 0;
        if (!(ended || _videoWasRunning) || _currentVideoInfo == null) return;
        if (!RecoveryAllowed || _vGaveUp) return;

        if (_vRetry >= RecoveryBackoffMs.Length) { GiveUpVideo(); return; }
        if (!_vRecovering)
        {
            _vRecovering = true;
            _vNextMs = now + RecoveryBackoffMs[Math.Min(_vRetry, RecoveryBackoffMs.Length - 1)];
            Log.Info($"video stopped by itself (ended={ended}) - reconnecting (try {_vRetry + 1})");
            ShowOsd(L.T("映像が止まったため、つなぎ直しています"), OsdLongMilliseconds);
            UpdateStatus();
            return;
        }
        if (now < _vNextMs) return;

        _vRetry++;
        bool ok = TryReconnectVideo();
        if (ok)
        {
            _videoWasRunning = true;
            _vOkSince = now;
            ShowOsd(L.T("映像をつなぎ直しました"));
            Log.Info("video reconnected automatically");
        }
        else if (_vRetry >= RecoveryBackoffMs.Length) GiveUpVideo();
        else _vNextMs = now + RecoveryBackoffMs[Math.Min(_vRetry, RecoveryBackoffMs.Length - 1)];
        UpdateStatus();
    }

    private void GiveUpVideo()
    {
        _vGaveUp = true;
        _vRecovering = false;
        _videoWasRunning = false;
        try { _video.Stop(); } catch { /* already dead */ }
        _recoveryMain = L.T("映像が止まり、つなぎ直せませんでした");
        _videoError = L.T("ケーブルとキャプチャーボードを確かめてください。抜き差しすると自動で再開します。「デバイス」メニューから選び直すこともできます");
        Log.Info("video: gave up reconnecting");
        ShowOsd(L.T("映像をつなぎ直せませんでした"), OsdLongMilliseconds);
        _canvas.Invalidate();
        UpdateStatus();
    }

    /// <summary>One reconnection attempt for the video. False when the device is not there or does not start.</summary>
    private bool TryReconnectVideo()
    {
        _devOpBusy++;
        try
        {
            var devices = VideoEngine.EnumerateDevices();
            VideoDeviceInfo? cur = _currentVideoInfo;
            VideoDeviceInfo? pick = (cur != null ? devices.FirstOrDefault(d => d.Id == cur.Id) : null)
                ?? ChooseVideoDevice(devices, _settings.VideoDeviceId, _settings.DeviceKeyword);
            if (pick == null) return false;
            try { _video.Stop(); } catch { /* ignore */ }
            RunDeviceStart(pick.Name, () => _video.Start(pick, _cliMode ?? _savedMode));
            _currentVideoInfo = pick;
            ClearNoCaptureDeviceState();
            LayoutCanvas();
            return _video.IsRunning;
        }
        catch (Exception ex)
        {
            Log.Info("video reconnect failed: " + ex.Message);
            return false;
        }
        finally { _devOpBusy--; }
    }

    private void CheckAudioHealth(long now)
    {
        bool bad = false;
        if (_audio.IsRunning)
        {
            _audioWasRunning = true;
            bad = _audio.IsFaulted;
            // The Windows default output changed: COM enumeration, so only every 2 s.
            if (!bad && (++_audioPollTick & 3) == 0 && RecoveryAllowed) bad = _audio.DefaultRenderChanged();
            if (!bad)
            {
                _aGaveUp = false;
                if (_aRetry > 0 || _aRecovering)
                {
                    if (_aOkSince == 0) _aOkSince = now;
                    else if (now - _aOkSince >= RecoveryStableMs) { _aRetry = 0; _aRecovering = false; _aOkSince = 0; }
                }
                return;
            }
        }
        _aOkSince = 0;
        if (!(bad || _audioWasRunning) || (_currentAudioInfo == null && !bad)) return;
        if (!RecoveryAllowed || _aGaveUp) return;

        if (_aRetry >= RecoveryBackoffMs.Length) { GiveUpAudio(); return; }
        if (!_aRecovering)
        {
            _aRecovering = true;
            _aNextMs = now + (bad && _aRetry == 0 ? 300 : RecoveryBackoffMs[Math.Min(_aRetry, RecoveryBackoffMs.Length - 1)]);
            Log.Info($"audio stopped / output changed (faulted={_audio.IsFaulted}) - reconnecting (try {_aRetry + 1})");
            // A change of the default output is routine: restart quietly. A real fault is announced.
            if (_audio.IsFaulted || !_audio.IsRunning)
                ShowOsd(L.T("音声が止まったため、つなぎ直しています"), OsdLongMilliseconds);
            UpdateStatus();
            return;
        }
        if (now < _aNextMs) return;

        _aRetry++;
        bool ok = TryReconnectAudio();
        if (ok)
        {
            _audioWasRunning = true;
            _aOkSince = now;
            Log.Info("audio reconnected automatically");
        }
        else if (_aRetry >= RecoveryBackoffMs.Length) GiveUpAudio();
        else _aNextMs = now + RecoveryBackoffMs[Math.Min(_aRetry, RecoveryBackoffMs.Length - 1)];
        UpdateStatus();
    }

    private void GiveUpAudio()
    {
        _aGaveUp = true;
        _aRecovering = false;
        _audioWasRunning = false;
        try { _audio.Stop(); } catch { /* already dead */ }
        Log.Info("audio: gave up reconnecting");
        ShowOsd(L.T("音声をつなぎ直せませんでした（抜き差しすると自動で再開します）"), OsdLongMilliseconds);
        UpdateStatus();
    }

    private bool TryReconnectAudio()
    {
        _devOpBusy++;
        try
        {
            var devices = AudioEngine.EnumerateDevices();
            AudioDeviceInfo? cur = _currentAudioInfo;
            AudioDeviceInfo? pick = (cur != null ? devices.FirstOrDefault(d => d.Id == cur.Id) : null)
                ?? ChooseAudioDevice(devices, _settings.AudioDeviceId, _settings.DeviceKeyword);
            if (pick == null) return false;
            try { _audio.Stop(); } catch { /* ignore */ }
            _audio.Start(pick, _settings.AudioOutputDeviceId);
            _currentAudioInfo = pick;
            return _audio.IsRunning && !_audio.IsFaulted;
        }
        catch (Exception ex)
        {
            Log.Info("audio reconnect failed: " + ex.Message);
            return false;
        }
        finally { _devOpBusy--; }
    }
}
