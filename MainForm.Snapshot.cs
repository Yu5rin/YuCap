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

/// <summary>Snapshot capture, burst mode and the About dialog.</summary>
public sealed partial class MainForm
{
    // ---- Snapshot --------------------------------------------------------

    private string SnapshotDirectory =>
        string.IsNullOrWhiteSpace(_settings.SnapshotDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer")
            : _settings.SnapshotDir!;

    /// <summary>
    /// Grab the current frame for saving. Prefers the capture engine's photo
    /// sink — source resolution, unaffected by window size or anything covering
    /// it — and falls back to the compositor copy if that path is unavailable.
    /// The OSD is hidden first because the fallback would otherwise burn a
    /// lingering bubble into the image.
    /// </summary>
    private Bitmap? GrabFrame()
    {
        if (_busyText != null && !_frozen) return null;   // the video is being (re)started: there is no picture to take, and a screen copy would catch the waiting message

        if (_osd.Showing)
        {
            _osdTimer.Stop();
            _osd.Showing = false;
            _osd.Update();
        }

        // While paused on a photo-sink still, that still is already the source
        // resolution frame in memory — reuse it instead of falling through to
        // the (window-sized) screen copy. Clone it: MainForm owns _freezeImage
        // and disposes it on resume/exit.
        if (_frozen && _freezeIsPhoto && _freezeImage != null)
        {
            return new Bitmap(_freezeImage);
        }

        if (!_frozen)
        {
            // 表示どおりに保存: the composed frame at the size shown (FSR included) instead of the
            // source picture. While paused the source-size still above stays in charge.
            if (_settings.SaveDisplaySize && _video.SupportsScalingQuality)
            {
                Bitmap? shown = _video.Snapshot(cropToVideo: true);
                if (shown != null) return shown;
                Log.Info("snapshot: displayed-size frame unavailable, using the source picture");
            }
            Bitmap? photo = _video.PhotoSnapshot();
            if (photo != null) return photo;
            Log.Info("snapshot: photo sink unavailable, using screen copy");
        }
        return _video.Snapshot();
    }

    /// <summary>Set once the configured snapshot folder has proven unusable
    /// and a save fell back to the default Pictures folder, so the caller can
    /// tell the user about it a single time rather than on every burst shot.</summary>
    private bool _snapshotDirFellBack;

    /// <summary>Full path of the file the last <see cref="SaveSnapshotCore"/> wrote, and whether the 保存と同時にコピー
    /// copy of it reached the clipboard.</summary>
    private string _lastSavedPath = string.Empty;
    private bool _lastSaveCopied;

    private bool _snapshotErrorShowing;

    private void SaveSnapshot()
    {
        bool fellBackBefore = _snapshotDirFellBack;
        bool wantCopy = _settings.SnapshotAlsoCopy;
        if (!SaveSnapshotCore(out string file, out string? error, alsoCopy: wantCopy))
        {
            ShowOsd(L.T("保存に失敗しました"));
            // The global hotkey still works while this box is open: further presses must not stack more boxes.
            if (_snapshotErrorShowing) return;
            _snapshotErrorShowing = true;
            try
            {
                Ui.Message(this, L.F("保存に失敗しました。\n\n{0}", error),
                    MsgKind.Error);
            }
            finally { _snapshotErrorShowing = false; }
            return;
        }

        // Capture in locals: _lastSaveDir may be overwritten by a later save
        // (burst mode) before this OSD bubble is clicked.
        string dir = _lastSaveDir ?? SnapshotDirectory;
        string savedFile = file;
        string savedPath = _lastSavedPath;
        bool copied = _lastSaveCopied;

        if (_snapshotDirFellBack && !fellBackBefore)
        {
            // The configured folder just proved unusable for the first time
            // this session — say so once instead of silently redirecting saves.
            ShowOsd(L.T("保存先フォルダを使えないため、既定のフォルダに保存しました。"), OsdLongMilliseconds,
                () => OpenFolder(dir, savedFile));
        }
        else
        {
            // A manual action: one short line. The OSD is itself the shortcut to the picture in the list until it fades.
            string text = copied ? L.F("保存してコピーしました: {0}（クリックで一覧を開く）", file)
                : wantCopy ? L.F("保存しました（コピーは失敗）: {0}（クリックで一覧を開く）", file)
                : L.F("保存しました: {0}（クリックで一覧を開く）", file);
            ShowOsd(text, OsdSnapshotMilliseconds, () => ShowSnapshotGallery(savedPath));
        }
    }

    private const int OsdSnapshotMilliseconds = 2500;

    /// <summary>A folder name made from a profile name: characters Windows does not allow in a name become "_",
    /// trailing dots/spaces (dropped by Windows) are cut, reserved device names are prefixed, and the length is
    /// limited. Empty for no name.</summary>
    internal static string SafeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (char c in name.Trim()) sb.Append(Array.IndexOf(bad, c) >= 0 || char.IsControl(c) ? '_' : c);
        string s = sb.ToString().TrimEnd(' ', '.');
        if (s.Length > 48) s = s[..48].TrimEnd(' ', '.');
        if (s.Length == 0) return "_";
        string stem = s.Contains('.') ? s[..s.IndexOf('.')] : s;
        stem = stem.TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
            s = "_" + s;
        return s;
    }

    /// <summary>A file name that sorts by time (date, time and milliseconds) and is not taken yet in
    /// <paramref name="dir"/>: a second shot in the same millisecond gets "_2", "_3", ... instead of overwriting.</summary>
    internal static string UniqueSnapshotPath(string dir, string ext, DateTime now)
    {
        string stem = $"Capture_{now:yyyyMMdd_HHmmss_fff}";
        string path = Path.Combine(dir, stem + "." + ext);
        for (int n = 2; File.Exists(path) && n < 10000; n++)
            path = Path.Combine(dir, $"{stem}_{n}.{ext}");
        return path;
    }

    /// <summary>Save one snapshot. Never shows a MessageBox or OSD itself —
    /// burst mode calls this on a timer, and a MessageBox per failed shot
    /// would pile up into an unstoppable storm of modal dialogs (and, since a
    /// failed shot didn't used to advance the burst counter, never stop).
    /// Callers (Ctrl+S, the global hotkey, burst mode) decide how to surface
    /// <paramref name="error"/> themselves.</summary>
    private bool SaveSnapshotCore(out string fileName, out string? error, bool alsoCopy = false)
    {
        fileName = string.Empty;
        error = null;
        using Bitmap? frame = GrabFrame();
        if (frame == null)
        {
            // The screen-copy fallback now refuses (and explains why) when
            // YuCap is minimized or covered by another window — surface that
            // specific reason instead of a generic "no video" message.
            error = _video.LastSnapshotError ?? NoVideoText;
            return false;
        }
        return SaveBitmapAsSnapshot(frame, out fileName, out error, alsoCopy);
    }

    /// <summary>Write <paramref name="frame"/> into the snapshot folder (with the per-profile subfolder when that
    /// option is on) under a unique time-sortable name, and copy it to the clipboard when asked.</summary>
    private bool SaveBitmapAsSnapshot(Bitmap frame, out string fileName, out string? error, bool alsoCopy)
    {
        fileName = string.Empty;
        error = null;
        _lastSaveCopied = false;
        string dir = SnapshotDirectory;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            // The configured folder may be on a removed USB drive or a
            // disconnected network share. Fall back to the default Pictures
            // folder rather than failing outright, but leave the user's
            // setting alone — the drive may come back on a later save.
            Log.Info("snapshot: configured folder unusable, falling back to default — " + ex);
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
            try
            {
                Directory.CreateDirectory(fallback);
                dir = fallback;
                _snapshotDirFellBack = true;
            }
            catch (Exception ex2)
            {
                Log.Info("snapshot: fallback folder also unusable — " + ex2);
                error = Errors.Describe(ex2);
                return false;
            }
        }

        // プロファイルごとにフォルダを分ける: no profile in use => straight into the folder.
        if (_settings.SnapshotByProfile)
        {
            string sub = SafeFolderName(_settings.ActiveProfile);
            if (sub.Length > 0)
            {
                try
                {
                    dir = Path.Combine(dir, sub);
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    Log.Info("snapshot: profile folder unusable — " + ex);
                    error = Errors.Describe(ex);
                    return false;
                }
            }
        }

        string? tmp = null;
        try
        {
            bool jpg = _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
            string ext = jpg ? "jpg" : "png";
            // Encode in memory, write a temporary file, then rename it: GDI+ writing straight to the path reports every
            // file-system failure (no permission, disk full, a path over 260 characters) as the same generic error, and
            // leaves a 0-byte or truncated picture behind. FileStream gives typed errors (Errors.Describe maps them),
            // handles long paths, and the picture appears under its real name only when it is complete.
            using var encoded = new MemoryStream();
            if (jpg)
            {
                ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                frame.Save(encoded, codec, ep);
            }
            else
            {
                frame.Save(encoded, ImageFormat.Png);
            }

            // Milliseconds in the name so rapid consecutive shots never overwrite (and a clash gets a _2 suffix).
            string path = UniqueSnapshotPath(dir, ext, DateTime.Now);
            tmp = path + ".tmp";   // not .png/.jpg, so the gallery and Explorer previews ignore it
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoded.Position = 0;
                encoded.CopyTo(fs);
            }
            try { File.Move(tmp, path); }
            catch (IOException) when (File.Exists(path))
            {
                // Taken in the meantime (another YuCap shot in the same millisecond): pick the next free name.
                path = UniqueSnapshotPath(dir, ext, DateTime.Now);
                File.Move(tmp, path);
            }
            tmp = null;
            fileName = Path.GetFileName(path);
            _lastSaveDir = dir;   // may be the fallback, not SnapshotDirectory
            _lastSavedPath = path;
        }
        catch (Exception ex)
        {
            Log.Info("snapshot save failed: " + ex);
            fileName = string.Empty;
            error = Errors.Describe(ex);
            return false;
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { /* best effort */ } }
        }

        if (alsoCopy) _lastSaveCopied = TryCopyImage(frame);
        return true;
    }

    /// <summary>Put the picture on the clipboard; false (logged) when another process holds the clipboard.</summary>
    private static bool TryCopyImage(Bitmap frame)
    {
        try
        {
            Clipboard.SetImage(frame);
            return true;
        }
        catch (Exception ex)
        {
            // Clipboard.SetImage fails when another process holds the clipboard
            // open. Silently swallowing it left nothing to diagnose from.
            Log.Info("clipboard copy failed: " + ex);
            return false;
        }
    }

    private void CopySnapshotToClipboard()
    {
        using Bitmap? frame = GrabFrame();
        if (frame == null)
        {
            ShowOsd(NoVideoText);
            return;
        }
        ShowOsd(TryCopyImage(frame) ? L.T("クリップボードにコピーしました") : L.T("コピーに失敗しました"));
    }

    private void OpenSnapshotFolder() => OpenFolder(SnapshotDirectory, null);

    /// <summary>
    /// Open a folder in Explorer, optionally selecting a file. The path is
    /// passed as a quoted argument: handing Explorer a bare path containing
    /// spaces makes it open the wrong (or no) folder.
    /// </summary>
    private void OpenFolder(string dir, string? selectFile)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string args = selectFile != null && File.Exists(Path.Combine(dir, selectFile))
                ? $"/select,\"{Path.Combine(dir, selectFile)}\""
                : $"\"{dir}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", args)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Info("open folder failed: " + ex);
            Ui.Message(this, Errors.Describe(ex), MsgKind.Warning);
        }
    }

    private void ShowSnapshotSettings()
    {
        using var dlg = Ui.NewDialog(L.T("スナップショット設定"), 460);

        var txtDir = new TextBox { Text = SnapshotDirectory, ReadOnly = true };
        var browse = Ui.Btn(L.T("参照..."));
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog
            {
                Description = L.T("スナップショットの保存先フォルダー"),
                SelectedPath = SnapshotDirectory,
                ShowNewFolderButton = true,
            };
            if (fb.ShowDialog(dlg) == DialogResult.OK) txtDir.Text = fb.SelectedPath;
        };
        var cmbFmt = Ui.Combo(160, L.T("PNG (無劣化)"), L.T("JPEG (高画質)"));
        cmbFmt.SelectedIndex =
            _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        // 保存先 / 形式 line up in a grid; the check boxes below wrap to the dialog's width.
        var grid = Ui.Grid()
            .AddRow(Ui.Caption(L.T("保存先:")), txtDir, browse)
            .AddRow(Ui.Caption(L.T("形式:")), cmbFmt)
            .AddSpan(Ui.Hint(L.T("ウィンドウの大きさに関係なく、入力された映像の解像度で保存します。")), before: 4);
        var stack = Ui.Stack();
        stack.Add(grid);

        // 表示どおりに保存: only the Direct3D renderer has a composed frame to save.
        bool canDisplaySize = _video.SupportsScalingQuality;
        var chkDisplay = Ui.Check(L.T("表示どおりの大きさで保存する（拡大の画質を反映。Direct3D 描画のとき）"), _settings.SaveDisplaySize);
        chkDisplay.Enabled = canDisplaySize;
        stack.Add(chkDisplay, before: Ui.Pad + 4);
        if (!canDisplaySize)
            stack.Add(Ui.Hint(L.T("Direct3D 描画のときに使えます（映像 → 描画方式）")), before: 2, indent: 24);

        var chkCopy = stack.Add(Ui.Check(L.T("保存したらクリップボードにもコピーする"), _settings.SnapshotAlsoCopy), before: Ui.Gap + 4);
        var chkProfile = stack.Add(Ui.Check(L.T("プロファイルごとにフォルダを分ける"), _settings.SnapshotByProfile), before: Ui.Gap + 4);
        stack.Add(Ui.Hint(L.T("使っているプロファイルの名前のフォルダに保存します。プロファイルを使っていないときは保存先にそのまま保存します。")), before: 2, indent: 24);

        var reset = Ui.Btn(L.T("既定に戻す"));
        reset.Click += (_, _) =>
        {
            txtDir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
            cmbFmt.SelectedIndex = 0;
            chkDisplay.Checked = false;
            chkCopy.Checked = false;
            chkProfile.Checked = false;
        };
        UiButton ok = Ui.FrameOkCancel(dlg, stack, left: new Button[] { reset });
        dlg.InitialFocus = ok;   // not the read-only path (it would open selected)

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string defDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
        _settings.SnapshotDir = string.Equals(txtDir.Text, defDir, StringComparison.OrdinalIgnoreCase)
            ? null : txtDir.Text;
        _settings.SnapshotFormat = cmbFmt.SelectedIndex == 1 ? "jpg" : "png";
        if (canDisplaySize) _settings.SaveDisplaySize = chkDisplay.Checked;
        _settings.SnapshotAlsoCopy = chkCopy.Checked;
        _settings.SnapshotByProfile = chkProfile.Checked;
        SaveSettings();
        _gallery?.SetRoot(GalleryRoot());
        ShowOsd(L.F("スナップショット: {0}", cmbFmt.SelectedIndex == 1 ? "JPEG" : "PNG"));
    }

    // ---- About -----------------------------------------------------------

    private void ShowAbout()
    {
        string ver = Application.ProductVersion;
        int plus = ver.IndexOf('+'); // strip build metadata if present
        if (plus > 0) ver = ver[..plus];

        using var dlg = Ui.NewDialog(L.T("バージョン情報"), 420);

        // The 256 px frame of the exe's icon, drawn at the size the layout gives it (crisp at 200%, unlike the 32 px
        // frame of Form.Icon stretched).
        var icon = new UiIcon(Ui.AppIcon, 48);
        var title = Ui.Styled(L.T("YuCap - キャプチャビューア"), Ui.TitlePt, FontStyle.Bold);
        var version = Ui.Text(L.F("バージョン {0}", ver) + "\n© 2026 YUGO");
        var copy = Ui.Link(L.T("バージョンをコピー"), () =>
        {
            try { Clipboard.SetText("YuCap " + ver); ShowOsd(L.T("バージョンをコピーしました")); }
            catch (Exception ex) { Log.Info("about: copy failed - " + ex.Message); }
        });
        var site = Ui.Link("github.com/Yu5rin/YuCap", () => OpenUrl("https://github.com/Yu5rin/YuCap"));
        var head = Ui.Stack();
        head.Add(title);
        head.Add(version, before: 4);
        head.Add(Ui.Row(copy, site), before: 4);

        // Every third-party component in the exe (the full texts are behind ライセンス...: THIRD-PARTY-NOTICES.txt, embedded).
        var libs = Ui.Grid();
        void Lib(string name, string license) => libs.AddRow(null, Ui.Text("•  " + name), Ui.Caption(license), before: 2);
        Lib("Windows Media Foundation / Direct3D (Windows)", string.Empty);
        Lib("NAudio", "MIT License");
        Lib("Vortice.Windows (Direct3D11, DXGI, D3DCompiler, DirectComposition), Vortice.Mathematics", "MIT License");
        Lib("SharpGen.Runtime", "MIT License");
        Lib("AMD FidelityFX Super Resolution 1", "MIT License");
        Lib("Anime4K", "MIT License");
        Lib("NVIDIA Optical Flow SDK (headers)", "MIT License");

        var body = Ui.Stack();
        body.Add(Ui.Row(icon, Ui.Before(Ui.Stretch(head), Ui.Pad)));
        body.Add(Ui.Caption(L.T("使用ライブラリ:")), before: Ui.Pad + 4);
        body.Add(libs, before: 4);

        var licenses = Ui.Btn(L.T("ライセンス..."));
        licenses.Click += (_, _) => ShowThirdPartyNotices(dlg);
        Ui.FrameClose(dlg, body, "OK", left: new Button[] { licenses });
        dlg.ShowDialog(this);
    }

    /// <summary>The licence texts of the third-party components (THIRD-PARTY-NOTICES.txt, embedded so that the release stays one exe).</summary>
    private static void ShowThirdPartyNotices(IWin32Window owner)
    {
        string text;
        try
        {
            using Stream? s = typeof(MainForm).Assembly.GetManifestResourceStream("ThirdPartyNotices.txt");
            if (s == null) throw new FileNotFoundException("ThirdPartyNotices.txt");
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            text = r.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Info("about: third-party notices unavailable - " + ex.Message);
            text = "https://github.com/Yu5rin/YuCap/blob/main/THIRD-PARTY-NOTICES.txt";
        }

        using var dlg = Ui.NewDialog(L.T("ライセンス"), 680, 480);
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = text,
            BorderStyle = BorderStyle.None,
        };
        Ui.Opt(box).FontPt = Ui.BodyPt;   // the UI font (Consolas has no Japanese; a monospace look is not needed)
        Ui.FrameClose(dlg, box, fill: true);
        dlg.Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; };
        dlg.ShowDialog(owner);
    }

}
