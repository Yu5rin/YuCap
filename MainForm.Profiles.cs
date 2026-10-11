using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>Per-source profiles (プロファイル), the black-border crop and the low-latency toggle.</summary>
public sealed partial class MainForm
{
    private ToolStripMenuItem _miProfiles = null!;
    private ToolStripMenuItem _miCrop = null!;
    private ToolStripMenuItem _miLowLatency = null!;

    // =====================================================================
    //  プロファイル
    // =====================================================================

    private List<VideoProfile> Profiles => _settings.Profiles;

    private VideoProfile? FindActiveProfile() =>
        _settings.ActiveProfile == null ? null
        : Profiles.Find(p => string.Equals(p.Name, _settings.ActiveProfile, StringComparison.OrdinalIgnoreCase));

    /// <summary>The live values as a profile. Taken from the live state rather than from
    /// <c>_settings</c> where the two differ: the display mode and the capture mode preference are
    /// only copied into the settings when they are saved.</summary>
    private VideoProfile CaptureProfile(string name)
    {
        var p = new VideoProfile
        {
            Name = name,
            Renderer = _settings.Renderer,
            ScalingQuality = _settings.ScalingQuality,
            FsrSharpness = _settings.FsrSharpness,
            DisplayMode = (int)_displayMode,
            AdjBrightness = _settings.AdjBrightness,
            AdjContrast = _settings.AdjContrast,
            AdjSaturation = _settings.AdjSaturation,
            AdjGamma = _settings.AdjGamma,
            AvOffsetMs = _settings.AvOffsetMs,
            CropLeft = _settings.CropLeft,
            CropTop = _settings.CropTop,
            CropRight = _settings.CropRight,
            CropBottom = _settings.CropBottom,
            LowLatency = _settings.LowLatency,
            SaveDisplaySize = _settings.SaveDisplaySize,
        };
        p.SetMode(_savedMode);
        p.Normalize();
        return p;
    }

    /// <summary>True when the active profile exists and the live values have drifted from it.
    /// Worked out on demand, so nothing that changes a value has to remember to flag it.</summary>
    private bool ActiveProfileModified() =>
        FindActiveProfile() is { } active && !CaptureProfile(active.Name).SameValues(active);

    private ToolStripMenuItem BuildProfilesMenu()
    {
        _miProfiles = new ToolStripMenuItem(L.T("プロファイル(&P)"));
        // Rebuilt every time it opens (the state it shows - which profile is active, whether it was
        // changed - is worked out then), so nothing else has to keep it up to date.
        _miProfiles.DropDownOpening += (_, _) => UpdateProfileMenu();
        UpdateProfileMenu();
        return _miProfiles;
    }

    /// <summary>Rebuild the dropdown: the profiles as radio items, then the management commands.</summary>
    private void UpdateProfileMenu()
    {
        if (_miProfiles == null) return;
        var items = _miProfiles.DropDownItems;
        items.Clear();   // not disposed: a profile click can be what triggered this rebuild

        VideoProfile? active = FindActiveProfile();
        bool modified = active != null && ActiveProfileModified();

        if (Profiles.Count == 0)
        {
            items.Add(new ToolStripMenuItem(L.T("（まだありません。現在の設定を保存して作成します）")) { Enabled = false });
        }
        for (int i = 0; i < Profiles.Count; i++)
        {
            VideoProfile p = Profiles[i];
            bool isActive = ReferenceEquals(p, active);
            int index = i;
            // '&' would turn into a menu mnemonic.
            string text = p.Name.Replace("&", "&&") + (isActive && modified ? " *" : string.Empty);
            items.Add(new ToolStripMenuItem(text, null, (_, _) => ApplyProfileAt(index))
            {
                Checked = isActive,
                ShortcutKeyDisplayString = "Ctrl+" + (i + 1),
            });
        }

        items.Add(new ToolStripSeparator());
        var save = new ToolStripMenuItem(L.T("現在の設定を新しいプロファイルとして保存..."), null,
            (_, _) => SaveProfileAsNew());
        if (Profiles.Count >= AppSettings.MaxProfiles)
        {
            save.Enabled = false;
            save.ToolTipText = L.F("プロファイルは最大{0}個までです", AppSettings.MaxProfiles);
        }
        items.Add(save);
        if (active != null)
            items.Add(new ToolStripMenuItem(L.F("「{0}」を現在の設定で上書き", active.Name.Replace("&", "&&")), null,
                (_, _) => OverwriteActiveProfile()));
        items.Add(new ToolStripMenuItem(L.T("名前を変更..."), null, (_, _) => RenameActiveProfile()) { Enabled = active != null });
        items.Add(new ToolStripMenuItem(L.T("削除..."), null, (_, _) => DeleteActiveProfile()) { Enabled = active != null });
    }

    /// <summary>Ctrl+1 .. Ctrl+9 (also on the number pad) as a zero-based profile index.</summary>
    private static int? ProfileIndexForKey(Keys keyData)
    {
        if ((keyData & ~Keys.KeyCode) != Keys.Control) return null;
        Keys k = keyData & Keys.KeyCode;
        if (k >= Keys.D1 && k <= Keys.D9) return k - Keys.D1;
        if (k >= Keys.NumPad1 && k <= Keys.NumPad9) return k - Keys.NumPad1;
        return null;
    }

    private void ApplyProfileAt(int index)
    {
        if (_startingUp) return;
        if (index < 0 || index >= Profiles.Count)
        {
            // Ctrl+digit with no profile in that slot: say so rather than doing nothing.
            ShowOsd(L.F("プロファイル {0} はありません", index + 1));
            return;
        }
        ApplyProfile(Profiles[index]);
    }

    /// <summary>Write a profile's values into the live state (everything except the renderer, which
    /// only changes once the new engine has really started — see <see cref="ReplaceEngine"/>).</summary>
    private void LoadProfileValues(VideoProfile p)
    {
        _settings.ScalingQuality = p.ScalingQuality;
        _settings.FsrSharpness = p.FsrSharpness;
        _displayMode = (VideoDisplayMode)Math.Clamp(p.DisplayMode, 0, 3);
        _settings.DisplayMode = (int)_displayMode;
        _settings.AdjBrightness = p.AdjBrightness;
        _settings.AdjContrast = p.AdjContrast;
        _settings.AdjSaturation = p.AdjSaturation;
        _settings.AdjGamma = p.AdjGamma;
        _settings.AvOffsetMs = p.AvOffsetMs;
        _settings.CropLeft = p.CropLeft;
        _settings.CropTop = p.CropTop;
        _settings.CropRight = p.CropRight;
        _settings.CropBottom = p.CropBottom;
        _settings.LowLatency = p.LowLatency;
        _settings.SaveDisplaySize = p.SaveDisplaySize;
        _savedMode = p.ToMode();
        _cliMode = null;   // an explicit choice overrides the --mode switch, as for a menu pick
    }

    private void ApplyProfile(VideoProfile profile)
    {
        VideoProfile before = CaptureProfile(string.Empty);   // to go back to if the new one cannot start
        bool rendererDiffers = profile.Renderer != _settings.Renderer;
        bool modeDiffers = profile.ToMode() != _savedMode;
        // Low latency is the kind of swap chain, fixed when the engine starts: only matters on D3D.
        bool latencyDiffers = profile.Renderer == "d3d" && profile.LowLatency != _settings.LowLatency;
        bool hasDevice = _currentVideoInfo != null;

        if (_frozen) ToggleFreeze();
        LoadProfileValues(profile);

        bool failed = false;
        if (rendererDiffers || (hasDevice && latencyDiffers))
        {
            // A new engine on a fresh window: the renderer switch path, with its failure fallback.
            Exception? failure = ReplaceEngine(profile.Renderer);
            if (failure != null)
            {
                failed = true;
                string reason = Errors.Describe(failure);
                LoadProfileValues(before);
                Exception? back = ReplaceEngine(before.Renderer);
                _videoError = back != null ? Errors.Describe(back) : null;
                MessageBox.Show(this, reason, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else if (hasDevice && modeDiffers)
        {
            try
            {
                RunDeviceStart(_currentVideoInfo!.Name, () => _video.Start(_currentVideoInfo, _savedMode));
                _videoError = null;
            }
            catch (Exception ex)
            {
                failed = true;
                MessageBox.Show(this, L.F("映像モードの変更に失敗しました。\n\n{0}", Errors.Describe(ex)),
                    "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                LoadProfileValues(before);
                RecoverVideo();
            }
        }

        ApplyRenderOptions();   // quality, adjustments, crop, A/V sync - on whichever engine is now running
        if (!failed) _settings.ActiveProfile = profile.Name;
        if (_lockAspect) ApplyLockedAspect();
        LayoutCanvas();
        UpdateChecks();
        UpdateStatus();
        SaveSettings();
        ShowOsd(failed
            ? L.T("プロファイルを適用できなかったため、元の設定に戻しました")
            : L.F("プロファイル: {0}", profile.Name), failed ? OsdLongMilliseconds : null);
    }

    private void SaveProfileAsNew()
    {
        if (Profiles.Count >= AppSettings.MaxProfiles) return;
        string? name = AskProfileName(L.T("新しいプロファイル"), string.Empty, null);
        if (name == null) return;
        Profiles.Add(CaptureProfile(name));
        _settings.ActiveProfile = name;
        FinishProfileEdit(L.F("プロファイルを保存しました: {0}", name));
    }

    private void OverwriteActiveProfile()
    {
        VideoProfile? active = FindActiveProfile();
        if (active == null) return;
        int i = Profiles.IndexOf(active);
        Profiles[i] = CaptureProfile(active.Name);
        FinishProfileEdit(L.F("プロファイルを上書きしました: {0}", active.Name));
    }

    private void RenameActiveProfile()
    {
        VideoProfile? active = FindActiveProfile();
        if (active == null) return;
        string? name = AskProfileName(L.T("名前を変更"), active.Name, active.Name);
        if (name == null || name == active.Name) return;
        active.Name = name;
        _settings.ActiveProfile = name;
        FinishProfileEdit(L.F("プロファイル名を変更しました: {0}", name));
    }

    private void DeleteActiveProfile()
    {
        VideoProfile? active = FindActiveProfile();
        if (active == null) return;
        if (MessageBox.Show(this, L.F("プロファイル「{0}」を削除しますか？", active.Name), "YuCap",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Profiles.Remove(active);
        _settings.ActiveProfile = null;
        FinishProfileEdit(L.F("プロファイルを削除しました: {0}", active.Name));
    }

    private void FinishProfileEdit(string osd)
    {
        UpdateChecks();
        UpdateStatus();
        SaveSettings();
        ShowOsd(osd);
    }

    /// <summary>One text box, 32 characters at most, trimmed, non-empty and unique (ignoring case).
    /// The reason it cannot be accepted is shown under the box and OK stays disabled meanwhile.
    /// <paramref name="ownName"/> is the name being renamed, which may of course be kept.</summary>
    private string? AskProfileName(string title, string initial, string? ownName)
    {
        using var dlg = Ui.NewDialog(title, 380, 150);
        int inner = dlg.ClientSize.Width - 2 * Ui.Pad;

        var label = Ui.Label(L.T("プロファイル名:"), Ui.Pad, 16);
        var box = new TextBox
        {
            Text = initial, MaxLength = VideoProfile.MaxNameLength,
            Location = new Point(Ui.Pad, 38), Width = inner,
        };
        var reason = Ui.Hint(string.Empty, Ui.Pad, 68, inner);
        dlg.Controls.AddRange(new Control[] { label, box, reason });
        Button ok = Ui.AddOkCancel(dlg);

        string? Problem(string text)
        {
            if (text.Length == 0) return L.T("名前を入力してください");
            if (Profiles.Any(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(p.Name, ownName, StringComparison.OrdinalIgnoreCase)))
                return L.T("同じ名前のプロファイルがあります");
            return null;
        }

        void Check()
        {
            string? problem = Problem(box.Text.Trim());
            ok.Enabled = problem == null;
            reason.Text = problem ?? string.Empty;
            reason.ForeColor = box.Text.Trim().Length == 0 ? SystemColors.GrayText : Color.Firebrick;
        }
        box.TextChanged += (_, _) => Check();
        Check();
        dlg.Shown += (_, _) => { box.Focus(); box.SelectAll(); };

        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    // =====================================================================
    //  黒い縁の切り取り
    // =====================================================================

    private bool HasCrop() =>
        _settings.CropLeft > 0 || _settings.CropTop > 0 || _settings.CropRight > 0 || _settings.CropBottom > 0;

    private void ShowCropDialog()
    {
        if (!_video.SupportsCrop) return;
        (int l0, int t0, int r0, int b0) = (_settings.CropLeft, _settings.CropTop, _settings.CropRight, _settings.CropBottom);
        Size src = _video.CurrentResolution;   // the device's own size: the crop is in source pixels

        using var dlg = Ui.NewDialog(L.T("黒い縁の切り取り"), 400, 236);
        int inner = dlg.ClientSize.Width - 2 * Ui.Pad;

        // label column wide enough for the longest of the four side names in the current language
        int lw = 24;
        foreach (string s in new[] { L.T("左"), L.T("上"), L.T("右"), L.T("下") })
            lw = Math.Max(lw, TextRenderer.MeasureText(s, dlg.Font).Width + 8);

        NumericUpDown Spin(string name, int x, int y, int value, int sourceLen)
        {
            int max = sourceLen > CropMath.MinSide ? Math.Min(AppSettings.MaxCrop, sourceLen - CropMath.MinSide) : AppSettings.MaxCrop;
            var lbl = Ui.Label(name, x, y + 4);
            var nud = new NumericUpDown
            {
                Minimum = 0, Maximum = max, Value = Math.Clamp(value, 0, max),
                Location = new Point(x + lw, y), Width = 84,
            };
            var px = Ui.Label("px", x + lw + 90, y + 4);
            dlg.Controls.AddRange(new Control[] { lbl, nud, px });
            return nud;
        }

        NumericUpDown nl = Spin(L.T("左"), Ui.Pad, 16, l0, src.Width);
        NumericUpDown nt = Spin(L.T("上"), 200, 16, t0, src.Height);
        NumericUpDown nr = Spin(L.T("右"), Ui.Pad, 50, r0, src.Width);
        NumericUpDown nb = Spin(L.T("下"), 200, 50, b0, src.Height);

        var result = Ui.Hint(string.Empty, Ui.Pad, 86, inner);
        var detect = Ui.Button(L.T("自動で検出"), Ui.Pad, 112, 110);
        var reset = Ui.Button(L.T("既定に戻す"), detect.Right + Ui.Gap, 112, 110);
        var note = Ui.Hint(string.Empty, Ui.Pad, 148, inner);
        dlg.Controls.AddRange(new Control[] { result, detect, reset, note });
        Ui.AddOkCancel(dlg);

        bool loading = false;
        void Push()
        {
            if (loading) return;
            _settings.CropLeft = (int)nl.Value; _settings.CropTop = (int)nt.Value;
            _settings.CropRight = (int)nr.Value; _settings.CropBottom = (int)nb.Value;
            ApplyRenderOptions();   // live: the picture behind the dialog follows
            Size d = _video.DisplayResolution;
            result.Text = d.Width > 0 ? L.F("切り取り後の大きさ: {0}x{1}", d.Width, d.Height) : string.Empty;
        }
        foreach (NumericUpDown n in new[] { nl, nt, nr, nb }) n.ValueChanged += (_, _) => { note.Text = string.Empty; Push(); };
        Push();

        reset.Click += (_, _) =>
        {
            loading = true;
            nl.Value = nt.Value = nr.Value = nb.Value = 0;
            loading = false;
            note.Text = string.Empty;
            Push();
        };
        detect.Click += (_, _) =>
        {
            Padding? found = DetectBlackBorders();
            if (found is not { } f || (f.Left == 0 && f.Top == 0 && f.Right == 0 && f.Bottom == 0))
            {
                note.Text = L.T("黒い縁は見つかりませんでした");
                return;
            }
            loading = true;
            nl.Value = Math.Min(f.Left, nl.Maximum); nt.Value = Math.Min(f.Top, nt.Maximum);
            nr.Value = Math.Min(f.Right, nr.Maximum); nb.Value = Math.Min(f.Bottom, nb.Maximum);
            loading = false;
            note.Text = string.Empty;
            Push();
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            (_settings.CropLeft, _settings.CropTop, _settings.CropRight, _settings.CropBottom) = (l0, t0, r0, b0);
            ApplyRenderOptions();
            UpdateStatus();
            return;
        }
        MarkSettingsDirty();
        if (_lockAspect) ApplyLockedAspect();
        LayoutCanvas();
        UpdateStatus();
    }

    /// <summary>
    /// Scan the current frame for black borders: take the picture with the crop temporarily
    /// cleared, then walk rows and columns inward from each edge while the line is black (mean
    /// luminance under 16 AND brightest pixel under 40, on 0..255), stopping at the first line
    /// that is not. Each side is capped at 25% of that dimension. The result is returned in SOURCE
    /// pixels (what <c>Crop</c> takes), i.e. mapped back through the mirror and rotation that the
    /// snapshot already carries. Null when no picture could be taken.
    /// </summary>
    private Padding? DetectBlackBorders()
    {
        Padding keep = _video.Crop;
        Bitmap? bmp;
        try
        {
            _video.Crop = Padding.Empty;
            bmp = _video.PhotoSnapshot();
        }
        finally { _video.Crop = keep; }
        if (bmp == null) return null;

        using (bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rowSum = new long[h]; var rowMax = new int[h];
            var colSum = new long[w]; var colMax = new int[w];
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                    for (int x = 0; x < w; x++)
                    {
                        int o = x * 4;   // B, G, R, A
                        int luma = (54 * row[o + 2] + 183 * row[o + 1] + 19 * row[o]) >> 8;
                        rowSum[y] += luma; colSum[x] += luma;
                        if (luma > rowMax[y]) rowMax[y] = luma;
                        if (luma > colMax[x]) colMax[x] = luma;
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }

            int Walk(long[] sum, int[] max, int lineLen, bool fromStart)
            {
                int n = sum.Length, cap = n / 4, count = 0;
                while (count < cap)
                {
                    int i = fromStart ? count : n - 1 - count;
                    if (sum[i] / (double)lineLen >= 16 || max[i] >= 40) break;
                    count++;
                }
                return count;
            }

            // Margins of the picture as shown (oriented): left/top/right/bottom.
            int mL = Walk(colSum, colMax, h, true), mR = Walk(colSum, colMax, h, false);
            int mT = Walk(rowSum, rowMax, w, true), mB = Walk(rowSum, rowMax, w, false);

            // Back to source sides. The shader rotates clockwise first and mirrors after, so undo
            // the mirror (swap left/right), then the rotation.
            if (_video.Mirror) (mL, mR) = (mR, mL);
            Padding src = _video.Rotation switch
            {
                90 => new Padding(left: mT, top: mR, right: mB, bottom: mL),
                180 => new Padding(left: mR, top: mB, right: mL, bottom: mT),
                270 => new Padding(left: mB, top: mL, right: mT, bottom: mR),
                _ => new Padding(left: mL, top: mT, right: mR, bottom: mB),
            };
            return src;
        }
    }

    // =====================================================================
    //  低遅延モード（試験版）
    // =====================================================================

    /// <summary>
    /// The swap chain kind (DirectComposition vs a window swap chain that can tear) is fixed when
    /// the engine starts, so the switch is a restart on a fresh window - the same path as changing
    /// the renderer, including falling back when the new mode cannot start.
    /// </summary>
    private void ToggleLowLatency()
    {
        if (!_video.SupportsLowLatency) return;
        bool on = !_settings.LowLatency;
        if (_frozen) ToggleFreeze();
        _settings.LowLatency = on;

        if (_currentVideoInfo != null)
        {
            Exception? failure = ReplaceEngine(_settings.Renderer);
            if (failure != null)
            {
                string reason = Errors.Describe(failure);
                _settings.LowLatency = !on;
                Exception? back = ReplaceEngine(_settings.Renderer);
                _videoError = back != null ? Errors.Describe(back) : null;
                LayoutCanvas();
                UpdateChecks();
                UpdateStatus();
                MessageBox.Show(this, reason, "YuCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowOsd(L.T("低遅延モードを切り替えられなかったため、元に戻しました"), OsdLongMilliseconds);
                return;
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
        ShowOsd(on ? L.T("低遅延モード: オン") : L.T("低遅延モード: オフ"));
    }
}
