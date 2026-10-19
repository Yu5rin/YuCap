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

    private void SaveSnapshot()
    {
        bool fellBackBefore = _snapshotDirFellBack;
        bool wantCopy = _settings.SnapshotAlsoCopy;
        if (!SaveSnapshotCore(out string file, out string? error, alsoCopy: wantCopy))
        {
            ShowOsd(L.T("保存に失敗しました"));
            MessageBox.Show(this, L.F("保存に失敗しました。\n\n{0}", error),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        try
        {
            bool jpg = _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
            // Milliseconds in the name so rapid consecutive shots never overwrite (and a clash gets a _2 suffix).
            string path = UniqueSnapshotPath(dir, jpg ? "jpg" : "png", DateTime.Now);
            fileName = Path.GetFileName(path);
            _lastSaveDir = dir;   // may be the fallback, not SnapshotDirectory
            _lastSavedPath = path;
            if (jpg)
            {
                ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                frame.Save(path, codec, ep);
            }
            else
            {
                frame.Save(path, ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            Log.Info("snapshot save failed: " + ex);
            error = Errors.Describe(ex);
            return false;
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
            MessageBox.Show(this, Errors.Describe(ex), "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowSnapshotSettings()
    {
        using var dlg = Ui.NewDialog(L.T("スナップショット設定"), 460, 330);
        var stack = Ui.Stack();

        // 保存先 / 形式: a two-row grid so the labels line up whatever the scaling.
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, Ui.Gap),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lblDir = Ui.Label(L.T("保存先:"), 0, 0);
        lblDir.Anchor = AnchorStyles.Left;
        lblDir.Margin = new Padding(0, 0, Ui.Gap, 0);
        var txtDir = new TextBox
        {
            Text = SnapshotDirectory,
            ReadOnly = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 3, Ui.Gap, 3),
        };
        var browse = Ui.Button(L.T("参照..."), 0, 0, 88);
        browse.AutoSize = true;
        browse.AutoSizeMode = AutoSizeMode.GrowOnly;
        browse.MinimumSize = new Size(Ui.ButtonWidth, Ui.ButtonHeight);
        browse.Margin = Padding.Empty;
        browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog
            {
                Description = L.T("スナップショットの保存先フォルダ"),
                SelectedPath = SnapshotDirectory,
                ShowNewFolderButton = true,
            };
            if (fb.ShowDialog(dlg) == DialogResult.OK) txtDir.Text = fb.SelectedPath;
        };

        var lblFmt = Ui.Label(L.T("形式:"), 0, 0);
        lblFmt.Anchor = AnchorStyles.Left;
        lblFmt.Margin = new Padding(0, 0, Ui.Gap, 0);
        var cmbFmt = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left,
            Width = 140,
            Margin = new Padding(0, 3, 0, 3),
        };
        cmbFmt.Items.AddRange(new object[] { L.T("PNG (無劣化)"), L.T("JPEG (高画質)") });
        cmbFmt.SelectedIndex =
            _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        grid.Controls.Add(lblDir, 0, 0);
        grid.Controls.Add(txtDir, 1, 0);
        grid.Controls.Add(browse, 2, 0);
        grid.Controls.Add(lblFmt, 0, 1);
        grid.Controls.Add(cmbFmt, 1, 1);
        stack.Controls.Add(grid);

        var hint = Ui.FlowHint(L.T("ウィンドウの大きさに関係なく、入力された映像の解像度で保存します。"));
        hint.Margin = new Padding(0, 0, 0, Ui.Gap * 2);
        stack.Controls.Add(hint);

        // 表示どおりに保存: only the Direct3D renderer has a composed frame to save.
        bool canDisplaySize = _video.SupportsScalingQuality;
        var chkDisplay = new WrapCheckBox
        {
            Text = L.T("表示どおりの大きさで保存する（拡大の画質を反映。Direct3D 描画のとき）"),
            Checked = _settings.SaveDisplaySize,
            Enabled = canDisplaySize,
            Margin = new Padding(0, 0, 0, canDisplaySize ? Ui.Gap : 0),
        };
        stack.Controls.Add(chkDisplay);
        if (!canDisplaySize)
        {
            var displayHint = Ui.FlowHint(L.T("Direct3D 描画のときに使えます（映像 → 描画方式）"));
            displayHint.Margin = new Padding(Ui.Gap * 2 + 8, 0, 0, Ui.Gap);
            stack.Controls.Add(displayHint);
        }

        var chkCopy = new WrapCheckBox
        {
            Text = L.T("保存したらクリップボードにもコピーする"),
            Checked = _settings.SnapshotAlsoCopy,
            Margin = new Padding(0, 0, 0, Ui.Gap),
        };
        stack.Controls.Add(chkCopy);

        var chkProfile = new WrapCheckBox
        {
            Text = L.T("プロファイルごとにフォルダを分ける"),
            Checked = _settings.SnapshotByProfile,
            Margin = new Padding(0, 0, 0, 2),
        };
        stack.Controls.Add(chkProfile);
        var profileHint = Ui.FlowHint(L.T("使っているプロファイルの名前のフォルダに保存します。プロファイルを使っていないときは保存先にそのまま保存します。"));
        profileHint.Margin = new Padding(Ui.Gap * 2 + 8, 0, 0, 0);
        stack.Controls.Add(profileHint);

        var reset = new Button { Text = L.T("既定に戻す") };
        reset.Click += (_, _) =>
        {
            txtDir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
            cmbFmt.SelectedIndex = 0;
            chkDisplay.Checked = false;
            chkCopy.Checked = false;
            chkProfile.Checked = false;
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel };
        Ui.Frame(dlg, stack, false, reset, ok, cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

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

        using var dlg = Ui.NewDialog(L.T("バージョン情報"), 380, 240);

        var pic = new PictureBox
        {
            Location = new Point(20, 20),
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.StretchImage,
        };
        try { pic.Image = Icon?.ToBitmap(); } catch { /* no icon */ }

        var title = new Label
        {
            Text = L.T("YuCap - キャプチャビューア"),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(80, 22),
        };
        var version = new Label
        {
            Text = L.F("バージョン {0}", ver) + "\n© 2026 YUGO",
            AutoSize = true,
            Location = new Point(82, 50),
        };
        var libs = new Label
        {
            Text = L.T("使用ライブラリ:") + "\n" +
                   "  ・Windows Media Foundation (Capture Engine)\n" +
                   "  ・NAudio — MIT License\n" +
                   "  ・Vortice.MediaFoundation — MIT License",
            AutoSize = true,
            Location = new Point(20, 96),
        };
        var ok = Ui.Button("OK", 380 - Ui.Pad - Ui.ButtonWidth, 240 - Ui.Pad - Ui.ButtonHeight, Ui.ButtonWidth, true);
        ok.DialogResult = DialogResult.OK;

        dlg.Controls.AddRange(new Control[] { pic, title, version, libs, ok });
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        dlg.ShowDialog(this);
        pic.Image?.Dispose();
        title.Font.Dispose();
    }

}

/// <summary>A check box whose text wraps to the width its dialog really has (a plain auto-sized check box
/// measures the text on one line, so a long caption runs off the dialog at larger scalings or in English).</summary>
internal sealed class WrapCheckBox : CheckBox
{
    public WrapCheckBox() { AutoSize = true; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Form? f = FindForm();
        int w = 0;
        if (f != null && f.ClientSize.Width > 0)
            w = f.ClientSize.Width - (f.Controls.Count > 0 ? f.Controls[0].Padding.Horizontal : 0) - Margin.Horizontal;
        if (w <= 0) w = proposedSize.Width > 1 && proposedSize.Width < 100000 ? proposedSize.Width : 300;
        int box = (int)Math.Ceiling(22 * DeviceDpi / 96.0);
        Size ts = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(20, w - box), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int h = Math.Max(ts.Height + 6, (int)Math.Ceiling(20 * DeviceDpi / 96.0));
        return new Size(Math.Min(w, ts.Width + box), h);
    }
}
