using System;
using System.IO;
using System.Windows.Forms;

namespace YuCap;

/// <summary>The snapshot list (スナップショット一覧): opened from the file menu, Ctrl+G, and the OSD after a save.</summary>
public sealed partial class MainForm
{
    private SnapshotGalleryForm? _gallery;

    /// <summary>The folder the list shows: the save folder, or the default folder when a save had to fall back to it.</summary>
    private string GalleryRoot()
    {
        string root = SnapshotDirectory;
        if (_snapshotDirFellBack && !Directory.Exists(root))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureViewer");
        return root;
    }

    /// <summary>Opens the list (or brings the open one forward), optionally at one picture.</summary>
    private void ShowSnapshotGallery(string? selectPath = null)
    {
        if (Ui.DialogAudit != null)
        {
            // Layout check of the dialog audit (test mode): the window is inspected instead of shown.
            using var probe = new SnapshotGalleryForm(GalleryRoot(), audit: true);
            Ui.DialogAudit(probe);
            return;
        }
        if (_gallery == null || _gallery.IsDisposed)
        {
            var g = new SnapshotGalleryForm(GalleryRoot()) { Icon = Icon };
            g.FormClosed += (_, _) => { if (ReferenceEquals(_gallery, g)) _gallery = null; };
            _gallery = g;
            g.Show(this);
        }
        else
        {
            if (_gallery.WindowState == FormWindowState.Minimized) _gallery.WindowState = FormWindowState.Normal;
            _gallery.Activate();
        }
        if (selectPath != null) _gallery.SelectFile(selectPath);
    }
}
