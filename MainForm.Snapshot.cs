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
        if (_osd.Visible)
        {
            _osdTimer.Stop();
            _osd.Visible = false;
            _osd.Update();
        }

        if (!_frozen)
        {
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

    private void SaveSnapshot()
    {
        bool fellBackBefore = _snapshotDirFellBack;
        if (!SaveSnapshotCore(out string file, out string? error))
        {
            ShowOsd(L.T("保存に失敗しました"));
            MessageBox.Show(this, L.F("保存に失敗しました。\n\n{0}", error),
                "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_snapshotDirFellBack && !fellBackBefore)
        {
            // The configured folder just proved unusable for the first time
            // this session — say so once instead of silently redirecting saves.
            ShowOsd(L.T("保存先フォルダを使えないため、既定のフォルダに保存しました。"), OsdLongMilliseconds);
        }
        else
        {
            // The OSD becomes a shortcut to the file until it fades. Set the target
            // after ShowOsd, which clears it for any other kind of message.
            ShowOsd(L.F("保存しました: {0}（クリックで開く）", file), OsdLongMilliseconds);
        }
        _lastSavedSnapshot = file;
    }

    /// <summary>Save one snapshot. Never shows a MessageBox or OSD itself —
    /// burst mode calls this on a timer, and a MessageBox per failed shot
    /// would pile up into an unstoppable storm of modal dialogs (and, since a
    /// failed shot didn't used to advance the burst counter, never stop).
    /// Callers (Ctrl+S, the global hotkey, burst mode) decide how to surface
    /// <paramref name="error"/> themselves.</summary>
    private bool SaveSnapshotCore(out string fileName, out string? error)
    {
        fileName = string.Empty;
        error = null;
        using Bitmap? frame = GrabFrame();
        if (frame == null)
        {
            error = L.T("映像がありません");
            return false;
        }

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

        try
        {
            bool jpg = _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
            // Milliseconds in the name so rapid consecutive shots never overwrite.
            fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{(jpg ? "jpg" : "png")}";
            string path = Path.Combine(dir, fileName);
            _lastSaveDir = dir;   // may be the fallback, not SnapshotDirectory
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
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("snapshot save failed: " + ex);
            error = Errors.Describe(ex);
            return false;
        }
    }

    private void CopySnapshotToClipboard()
    {
        using Bitmap? frame = GrabFrame();
        if (frame == null)
        {
            ShowOsd(L.T("映像がありません"));
            return;
        }
        try
        {
            Clipboard.SetImage(frame);
            ShowOsd(L.T("クリップボードにコピーしました"));
        }
        catch (Exception ex)
        {
            // Clipboard.SetImage fails when another process holds the clipboard
            // open. Silently swallowing it left nothing to diagnose from.
            Log.Info("clipboard copy failed: " + ex);
            ShowOsd(L.T("コピーに失敗しました"));
        }
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
        using var dlg = new Form
        {
            Text = L.T("スナップショット設定"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 150),
            // (7, 15) is the metric of the default Segoe UI 9pt these layouts
            // were drawn against at 100% — WinForms then scales every
            // Location/Size by the same factor as the font, so the layout
            // still holds together at 125-200% display scaling.
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };

        var lblDir = new Label { Text = L.T("保存先:"), AutoSize = true, Location = new Point(16, 20) };
        var txtDir = new TextBox
        {
            Text = SnapshotDirectory,
            ReadOnly = true,
            Location = new Point(80, 16),
            Width = 240,
        };
        var browse = new Button { Text = L.T("参照..."), Location = new Point(328, 14), Width = 76 };
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

        var lblFmt = new Label { Text = L.T("形式:"), AutoSize = true, Location = new Point(16, 62) };
        var cmbFmt = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(80, 58),
            Width = 140,
        };
        cmbFmt.Items.AddRange(new object[] { L.T("PNG (無劣化)"), L.T("JPEG (高画質)") });
        cmbFmt.SelectedIndex =
            _settings.SnapshotFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        var reset = new Button { Text = L.T("既定に戻す"), Location = new Point(16, 108), Width = 100 };
        reset.Click += (_, _) =>
        {
            txtDir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
            cmbFmt.SelectedIndex = 0;
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(224, 108), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(315, 108), Width = 90 };

        dlg.Controls.AddRange(new Control[] { lblDir, txtDir, browse, lblFmt, cmbFmt, reset, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string defDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
        _settings.SnapshotDir = string.Equals(txtDir.Text, defDir, StringComparison.OrdinalIgnoreCase)
            ? null : txtDir.Text;
        _settings.SnapshotFormat = cmbFmt.SelectedIndex == 1 ? "jpg" : "png";
        SaveSettings();
        ShowOsd(L.F("スナップショット: {0}", cmbFmt.SelectedIndex == 1 ? "JPEG" : "PNG"));
    }

    // ---- About -----------------------------------------------------------

    private void ShowAbout()
    {
        string ver = Application.ProductVersion;
        int plus = ver.IndexOf('+'); // strip build metadata if present
        if (plus > 0) ver = ver[..plus];

        using var dlg = new Form
        {
            Text = L.T("バージョン情報"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 240),
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };

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
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(280, 198), Width = 85 };

        dlg.Controls.AddRange(new Control[] { pic, title, version, libs, ok });
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        dlg.ShowDialog(this);
        pic.Image?.Dispose();
        title.Font.Dispose();
    }

}
