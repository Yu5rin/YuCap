using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// 配信用ウィンドウ: a second top-level window that shows only the video (see <see cref="StreamForm"/>), for OBS and the like to
/// capture. The window is plain WinForms; the Direct3D engine presents into it from its own swap chain
/// (<see cref="IVideoEngine.SetStreamWindow"/>). Open/closed and the size are remembered; the main window closing closes it.
/// </summary>
public sealed partial class MainForm
{
    private StreamForm? _stream;
    private IVideoEngine? _streamBoundTo;          // the engine that was given the window (an engine replaced by a renderer switch needs it again)
    private Size _streamBoundSize;
    private Size _streamLastWant;              // the size last asked of the window (an OS-clamped window must not be re-sized every tick)
    private bool _streamClosingWithMain;
    private ToolStripMenuItem? _miStream, _miStreamSize;
    private readonly List<ToolStripMenuItem> _streamSizeItems = new();

    private void AddStreamMenu(ToolStripMenuItem view)
    {
        _miStream = new ToolStripMenuItem(L.T("配信用ウィンドウを開く"), null, (_, _) => ToggleStreamWindow()) { Name = "stream" };
        _miStreamSize = new ToolStripMenuItem(L.T("配信用ウィンドウの大きさ"));
        foreach (string key in StreamSizes.Keys)
        {
            string k = key;
            string label = k == StreamSizes.Video ? L.T("映像と同じ") : k == "1920x1080" ? k + L.Par(L.T("既定")) : k;
            var it = new ToolStripMenuItem(label, null, (_, _) => SetStreamSize(k)) { Tag = k };
            _streamSizeItems.Add(it);
            _miStreamSize.DropDownItems.Add(it);
        }
        view.DropDownItems.Add(_miStream);
        view.DropDownItems.Add(_miStreamSize);
    }

    private void UpdateStreamChecks()
    {
        if (_miStream == null || _miStreamSize == null) return;
        bool ok = _video.SupportsStreamWindow;
        _miStream.Enabled = ok;
        _miStreamSize.Enabled = ok;
        _miStream.Text = _stream != null ? L.T("配信用ウィンドウを閉じる") : L.T("配信用ウィンドウを開く");
        _miStream.ToolTipText = ok
            ? L.T("OBS などで取り込むための、映像だけのウィンドウを別に開きます（OSD・情報表示・メニューは入りません）。")
            : L.T("Direct3D 描画のときに使えます（映像 → 描画方式）");
        _miStreamSize.ToolTipText = ok ? string.Empty : _miStream.ToolTipText;
        string cur = StreamSizes.Normalize(_settings.StreamWindowSize);
        foreach (var it in _streamSizeItems) it.Checked = (string)it.Tag! == cur;
    }

    private Size StreamWantedSize() => StreamSizes.Resolve(_settings.StreamWindowSize, _video.DisplayResolution);

    private void ToggleStreamWindow()
    {
        if (_testRunning) return;
        if (_stream != null) { CloseStreamWindow(forget: true); return; }
        if (!_video.SupportsStreamWindow) return;
        OpenStreamWindow();
        _settings.StreamWindowOpen = true;
        UpdateChecks();
    }

    private void OpenStreamWindow()
    {
        if (_stream != null) return;
        Size size = _streamLastWant = StreamWantedSize();
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        var f = new StreamForm(size, new Point(wa.X + 40, wa.Y + 40));
        f.FormClosing += (_, _) => { try { _streamBoundTo?.SetStreamWindow(IntPtr.Zero, Size.Empty); } catch { /* ignore */ } };   // let go of the window before it is destroyed
        f.FormClosed += (_, _) => StreamFormClosed(f);
        _stream = f;
        f.Show();                                   // does not take the focus (ShowWithoutActivation + WS_EX_NOACTIVATE)
        BindStreamWindow();
        Log.Info($"stream window opened: asked {size.Width}x{size.Height}, client {f.ClientSize.Width}x{f.ClientSize.Height}");
    }

    private void BindStreamWindow()
    {
        if (_stream == null || !_stream.IsHandleCreated) return;
        Size size = _stream.ClientSize;
        _streamBoundTo = _video;
        _streamBoundSize = size;
        _video.SetStreamWindow(_stream.Handle, size);
    }

    private void StreamFormClosed(StreamForm f)
    {
        if (!ReferenceEquals(f, _stream)) return;
        _stream = null;
        try { _video.SetStreamWindow(IntPtr.Zero, Size.Empty); } catch { /* the engine may be going away */ }
        _streamBoundTo = null;
        if (_streamClosingWithMain) return;          // closing with the main window or not usable now: "open" stays remembered
        _settings.StreamWindowOpen = false;
        Log.Info("stream window closed");
        UpdateChecks();
    }

    /// <summary>Close the stream window. <paramref name="forget"/>: also remember it as closed (the user's choice); false when it is only
    /// not usable now (the Media Foundation renderer), so it comes back with Direct3D.</summary>
    private void CloseStreamWindow(bool forget)
    {
        StreamForm? f = _stream;
        if (f == null) return;
        _streamClosingWithMain = !forget;
        try { f.Close(); } finally { _streamClosingWithMain = false; }
        if (forget) { _settings.StreamWindowOpen = false; UpdateChecks(); }
    }

    private void StreamOnMainClosing()
    {
        _streamClosingWithMain = true;
        try { _stream?.Close(); } catch { /* ignore */ }
    }

    private void SetStreamSize(string key)
    {
        _settings.StreamWindowSize = StreamSizes.Normalize(key);
        if (_stream != null) ApplyStreamSize();
        UpdateChecks();
    }

    private void ApplyStreamSize()
    {
        if (_stream == null) return;
        Size want = _streamLastWant = StreamWantedSize();
        if (_stream.ClientSize != want) _stream.SetClient(want);
        BindStreamWindow();
    }

    /// <summary>UI timer: restore the window at start, keep it bound to the running engine (a renderer switch makes a new one), follow
    /// "映像と同じ" when the picture size changes, close it when the renderer cannot serve it.</summary>
    private void StreamTick()
    {
        if (_streamClosingWithMain || _testRunning) return;
        bool can = _video.SupportsStreamWindow;
        if (_stream == null)
        {
            if (can && _settings.StreamWindowOpen && _video.IsRunning) OpenStreamWindow();
            return;
        }
        if (!can) { CloseStreamWindow(forget: false); return; }
        if (_settings.StreamWindowSize == StreamSizes.Video && _video.IsRunning)
        {
            Size want = StreamWantedSize();
            if (want != _streamLastWant) { _streamLastWant = want; _stream.SetClient(want); BindStreamWindow(); return; }
        }
        if (!ReferenceEquals(_streamBoundTo, _video) || _streamBoundSize != _stream.ClientSize) BindStreamWindow();
    }
}
