using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// Starting the video without freezing the window. A capture card can need seconds to come up after a replug, so the
/// Direct3D engine opens it on a worker thread (<see cref="IVideoEngine.StartAsync"/>) while the UI keeps running and the
/// canvas says the picture is being prepared. Every production start path goes through <see cref="StartVideoAsync"/>:
/// one start is in flight at a time, identical requests that arrive meanwhile (the recovery timer and WM_DEVICECHANGE
/// usually fire together) share it, and a different request replaces whatever has not begun yet - the later one wins.
/// The Media Foundation engine cannot leave the UI thread (its COM objects are apartment-bound), so there the same code
/// runs the start synchronously, as it always did.
/// </summary>
public sealed partial class MainForm
{
    private sealed class VideoStartReq
    {
        public VideoStartReq(VideoDeviceInfo info, CaptureMode? mode) { Info = info; Mode = mode; }
        public VideoDeviceInfo Info { get; }
        public CaptureMode? Mode { get; }
        public readonly List<TaskCompletionSource> Waiters = new();
        public bool LateJoin;   // somebody asked for the same start while it was already running
        public bool Retried;
    }

    private VideoStartReq? _vsRunning, _vsPending;
    private bool _engineSwapping;   // the engine object is being replaced: no start may touch it

    private static bool SameStart(VideoStartReq r, VideoDeviceInfo info, CaptureMode? mode) =>
        r.Info.Id == info.Id && Equals(r.Mode, mode);

    /// <summary>Message shown over the empty canvas while a start is in flight.</summary>
    private static string PreparingText => L.T("映像を準備しています…");

    /// <summary>What to tell the user when something needs a picture that is not there (yet).</summary>
    private string NoVideoText => _busyText != null ? L.T("映像を準備中です") : L.T("映像がありません");

    /// <summary>Open <paramref name="info"/> and start showing it. Completes when it runs; faults with the start error;
    /// is cancelled (<see cref="OperationCanceledException"/>) when the video was stopped on purpose meanwhile.</summary>
    private Task StartVideoAsync(VideoDeviceInfo info, CaptureMode? mode)
    {
        if (_engineSwapping || IsDisposed) return Task.FromCanceled(new System.Threading.CancellationToken(true));
        var w = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_vsRunning is { } run && SameStart(run, info, mode))
        {
            run.LateJoin = true;
            run.Waiters.Add(w);
            Log.Info("video start: joined the start already in progress");
            return w.Task;
        }
        VideoStartReq? pend = _vsPending;
        if (pend != null && SameStart(pend, info, mode)) { pend.Waiters.Add(w); return w.Task; }
        var req = new VideoStartReq(info, mode);
        if (pend != null) req.Waiters.AddRange(pend.Waiters);   // the later request wins; the earlier callers hear its result
        req.Waiters.Add(w);
        _vsPending = req;
        if (_vsRunning == null) _ = RunVideoStartLoopAsync();
        return w.Task;
    }

    /// <summary>Stop the video on purpose: a start that has not begun is dropped, one in flight is cancelled by the engine.
    /// Never blocks (the Direct3D engine shuts its session down in the background).</summary>
    private void StopVideo()
    {
        VideoStartReq? pend = _vsPending;
        _vsPending = null;
        if (pend != null) foreach (var w in pend.Waiters) w.TrySetCanceled();
        try { _video.Stop(); } catch { /* already dead */ }
    }

    private async Task RunVideoStartLoopAsync()
    {
        while (_vsPending is { } req)
        {
            _vsPending = null;
            _vsRunning = req;
            Exception? err = null;
            bool offUi = _video.StartsOffUiThread;
            long t0 = Environment.TickCount64;

            _busyText = PreparingText;
            NoteVideoStopped();   // a deliberate stop: the health check must not "recover" it behind our back
            _devOpBusy++;
            try { _video.Stop(); } catch { /* best-effort: about to (re)start anyway */ }
            _canvas.Invalidate();
            if (offUi) Cursor = Cursors.AppStarting;
            else { _canvas.Update(); Cursor = Cursors.WaitCursor; }   // about to block: force the message out first
            try
            {
                // Before the start: the low-latency choice decides the swap chain kind when the engine starts.
                ApplyRenderOptions();
                try { await _video.StartAsync(req.Info, req.Mode); }
                catch (Exception ex) when (IsWindowAlreadyComposed(ex) && !IsDisposed)
                {
                    // The window still carries the DirectComposition target of a previous session
                    // (DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED). A fresh window has none: same cure as RestartPreview.
                    Log.Info("start: window already composed - recreating the render window and trying once more");
                    await _video.StopAsync();
                    _canvas.ResetHandle();
                    _video.Attach(_canvas.Handle);
                    ApplyRenderOptions();
                    await _video.StartAsync(req.Info, req.Mode);
                }
                if (!IsDisposed) ApplyRenderOptions();   // every start path goes through here
            }
            catch (Exception ex) { err = ex; }
            finally
            {
                _devOpBusy--;
                _vsRunning = null;
                _busyText = null;
                if (!IsDisposed) { Cursor = Cursors.Default; _canvas.Invalidate(); }
            }
            if (err == null) _currentVideoInfo = req.Info;
            if (offUi) Log.Info($"video start: {(err == null ? "done" : "failed")} in {Environment.TickCount64 - t0}ms (UI kept running)");

            if (IsDisposed) err ??= new OperationCanceledException();
            if (_vsPending != null)
            {
                // A different request arrived meanwhile: its result answers this one's callers too.
                _vsPending.Waiters.AddRange(req.Waiters);
                continue;
            }
            if (err != null && err is not OperationCanceledException && req.LateJoin && !req.Retried && !IsDisposed)
            {
                // Somebody asked again while this one was running (the card was still settling): one more try.
                req.Retried = true;
                req.LateJoin = false;
                Log.Info("video start: failed but was asked for again meanwhile - trying once more");
                _vsPending = req;
                continue;
            }
            foreach (var w in req.Waiters)
            {
                if (err == null) w.TrySetResult();
                else if (err is OperationCanceledException) w.TrySetCanceled();
                else w.TrySetException(err);
            }
        }
    }

    // ---- replacing the engine (renderer / low-latency switch) -------------------------------------

    /// <summary>Replace the engine with the one for <paramref name="renderer"/> and restart the current device on it, without
    /// holding the UI thread. Returns null on success, else the failure (the engine is then left stopped and
    /// <c>_settings.Renderer</c> unchanged). The canvas window MUST be recreated first: both engines bind a DirectComposition
    /// target to the HWND and a window that already has one cannot be given another (same reason as RestartPreview).</summary>
    private async Task<Exception?> ReplaceEngineAsync(string renderer, CaptureMode? modeOverride = null)
    {
        try
        {
            NoteVideoStopped();
            _engineSwapping = true;
            StopVideo();
            while (_vsRunning != null) await Task.Delay(20);
            IVideoEngine old = _video;
            _busyText = PreparingText;
            _canvas.Invalidate();
            if (old.StartsOffUiThread)
                await Task.Run(() => { try { old.Dispose(); } catch { /* going away anyway */ } });
            else
                try { old.Dispose(); } catch { /* going away anyway */ }
            _canvas.ResetHandle();
            _video = VideoEngines.Create(renderer);
            UpdateOverlayMode();
            _video.Attach(_canvas.Handle);
            _video.SetRotation(_settings.Rotation);
            _video.SetMirror(_settings.Mirror);
            _engineSwapping = false;
            if (_currentVideoInfo != null)
                await StartVideoAsync(_currentVideoInfo, modeOverride ?? _cliMode ?? _savedMode);
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
        finally
        {
            _engineSwapping = false;
            if (_vsRunning == null && _vsPending == null) { _busyText = null; if (!IsDisposed) _canvas.Invalidate(); }
        }
    }

    // ---- hot-plug / recovery ---------------------------------------------------------------------

    private bool _devChangeRunning, _devChangeAgain;
    private TaskCompletionSource? _devChangeDone;

    /// <summary>Debounced WM_DEVICECHANGE work. One pass at a time; a request that arrives during a pass makes it run again,
    /// and every caller can wait for the whole thing.</summary>
    private Task OnDeviceChangeAsync()
    {
        if (IsDisposed || _testRunning) return Task.CompletedTask;
        if (_devChangeRunning) { _devChangeAgain = true; return _devChangeDone!.Task; }
        _devChangeRunning = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _devChangeDone = done;
        _ = DeviceChangeLoopAsync(done);
        return done.Task;
    }

    private async Task DeviceChangeLoopAsync(TaskCompletionSource done)
    {
        try
        {
            do
            {
                _devChangeAgain = false;
                await OnDeviceChangeOnceAsync();
            } while (_devChangeAgain && !IsDisposed && !_testRunning);
        }
        catch (Exception ex) { Log.Info("OnDeviceChange failed: " + ex.Message); }
        finally
        {
            _devChangeRunning = false;
            done.TrySetResult();
        }
    }

    /// <summary>One reconnection attempt for the video (the automatic recovery). False when the device is not there, does not
    /// start, or the video was stopped on purpose meanwhile.</summary>
    private async Task<bool> TryReconnectVideoAsync()
    {
        _devOpBusy++;
        try
        {
            var devices = VideoEngine.EnumerateDevices();
            VideoDeviceInfo? cur = _currentVideoInfo;
            VideoDeviceInfo? pick = (cur != null ? devices.Find(d => d.Id == cur.Id) : null)
                ?? ChooseVideoDevice(devices, _settings.VideoDeviceId, _settings.DeviceKeyword);
            if (pick == null) return false;
            await StartVideoAsync(pick, _cliMode ?? _savedMode);
            ClearNoCaptureDeviceState();
            LayoutCanvas();
            return _video.IsRunning;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Info("video reconnect failed: " + ex.Message);
            return false;
        }
        finally { _devOpBusy--; }
    }

    private async Task VideoReconnectAttemptAsync()
    {
        bool ok = await TryReconnectVideoAsync();
        if (IsDisposed || _suspended) return;
        long now = Environment.TickCount64;
        if (ok)
        {
            _videoWasRunning = true;
            _vOkSince = now;
            ShowOsd(L.T("映像をつなぎ直しました"));
            Log.Info("video reconnected automatically");
        }
        else if (_vRecovering)   // false when a plug event reset the recovery meanwhile: it starts over by itself
        {
            if (_vRetry >= RecoveryBackoffMs.Length) GiveUpVideo();
            else _vNextMs = now + RecoveryBackoffMs[Math.Min(_vRetry, RecoveryBackoffMs.Length - 1)];
        }
        UpdateStatus();
    }
}
