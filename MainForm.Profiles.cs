using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YuCap;

/// <summary>Per-source profiles (プロファイル) and the black-border crop.</summary>
public sealed partial class MainForm
{
    private ToolStripMenuItem _miProfiles = null!;
    private ToolStripMenuItem _miCrop = null!;

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
            NativeResScale = _settings.NativeResScale,
            DisplayMode = (int)_displayMode,
            AdjBrightness = _settings.AdjBrightness,
            AdjContrast = _settings.AdjContrast,
            AdjSaturation = _settings.AdjSaturation,
            AdjGamma = _settings.AdjGamma,
            AvOffsetMs = _settings.AvOffsetMs,
            AvAuto = _settings.AvAuto,
            ShadowBoost = _settings.ShadowBoost,
            CropLeft = _settings.CropLeft,
            CropTop = _settings.CropTop,
            CropRight = _settings.CropRight,
            CropBottom = _settings.CropBottom,
            LowLatency = _settings.LowLatency,
            Interpolation = _settings.Interpolation,
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
        _miProfiles = Refilled(TopMenu(IdProfiles, L.T("プロファイル(&P)")));
        // Rebuilt every time it opens (the state it shows - which profile is active, whether it was
        // changed - is worked out then), so nothing else has to keep it up to date.
        _miProfiles.DropDownOpening += (_, _) => UpdateProfileMenu();
        UpdateProfileMenu();
        return _miProfiles;
    }

    /// <summary>Rebuild the dropdown: the profiles as radio items, then the management commands.</summary>
    private void UpdateProfileMenu()
    {
        FillProfileMenu(_miProfiles);
        FillProfileMenu(_cmiProfiles);
    }

    private void FillProfileMenu(ToolStripMenuItem? root)
    {
        if (root == null) return;
        var items = root.DropDownItems;
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
            items.Add(Radio(new ToolStripMenuItem(text, null, (_, _) => ApplyProfileAt(index))
            {
                Checked = isActive,
                ShortcutKeyDisplayString = i < 9 ? "Ctrl+" + (i + 1) : string.Empty,
            }));
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
        // The commands below act on the profile in use: they name it, and say why they are grey when there is none.
        string noneTip = L.T("使用中のプロファイルがないため使えません。上の一覧から選ぶと使えます。");
        if (active != null)
        {
            string name = active.Name.Replace("&", "&&");
            items.Add(new ToolStripMenuItem(L.F("「{0}」を現在の設定で上書き...", name), null, (_, _) => OverwriteActiveProfile()));
            items.Add(new ToolStripMenuItem(L.F("「{0}」の名前を変更...", name), null, (_, _) => RenameActiveProfile()));
            items.Add(new ToolStripMenuItem(L.F("「{0}」を削除...", name), null, (_, _) => DeleteActiveProfile()));
        }
        else
        {
            items.Add(new ToolStripMenuItem(L.T("名前を変更..."), null, (_, _) => RenameActiveProfile()) { Enabled = false, ToolTipText = noneTip });
            items.Add(new ToolStripMenuItem(L.T("削除..."), null, (_, _) => DeleteActiveProfile()) { Enabled = false, ToolTipText = noneTip });
        }
        var manage = new ToolStripMenuItem(L.T("プロファイルの管理..."), null, (_, _) => ShowProfileManager())
        {
            ToolTipText = L.T("プロファイルの並び順（Ctrl+1〜9 の順番）を変えたり、名前を変更・削除したりします。"),
        };
        if (Profiles.Count == 0)
        {
            manage.Enabled = false;
            manage.ToolTipText = L.T("プロファイルがまだありません");
        }
        items.Add(manage);
        items.Add(new ToolStripSeparator());
        items.Add(BuildAutoProfileMenu());
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
        _settings.NativeResScale = p.NativeResScale;
        _displayMode = (VideoDisplayMode)Math.Clamp(p.DisplayMode, 0, 3);
        _settings.DisplayMode = (int)_displayMode;
        _settings.AdjBrightness = p.AdjBrightness;
        _settings.AdjContrast = p.AdjContrast;
        _settings.AdjSaturation = p.AdjSaturation;
        _settings.AdjGamma = p.AdjGamma;
        _settings.AvOffsetMs = p.AvOffsetMs;
        _settings.AvAuto = p.AvAuto;
        _settings.ShadowBoost = p.ShadowBoost;
        _settings.CropLeft = p.CropLeft;
        _settings.CropTop = p.CropTop;
        _settings.CropRight = p.CropRight;
        _settings.CropBottom = p.CropBottom;
        _settings.LowLatency = p.LowLatency;
        _settings.Interpolation = p.Interpolation;
        _settings.SaveDisplaySize = p.SaveDisplaySize;
        _savedMode = p.ToMode();
        _cliMode = null;   // an explicit choice overrides the --mode switch, as for a menu pick
    }

    private async void ApplyProfile(VideoProfile profile, bool auto = false)
    {
        if (_engineSwapping || _engineSwitchBusy) { Log.Info("ApplyProfile: another switch is running"); return; }
        _engineSwitchBusy = true;
        _devOpBusy++;   // may show a dialog that pumps messages: no automatic recovery meanwhile
        try { await ApplyProfileCoreAsync(profile, auto); }
        catch (Exception ex) { Log.Info("ApplyProfile: " + ex); }
        finally { _devOpBusy--; _engineSwitchBusy = false; }
    }

    private async Task ApplyProfileCoreAsync(VideoProfile profile, bool auto = false)
    {
        NoteUserChange();   // the automatic switch waits for things to be quiet again
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
            Exception? failure = await ReplaceEngineAsync(profile.Renderer);
            if (failure != null)
            {
                failed = true;
                string reason = Errors.Describe(failure);
                LoadProfileValues(before);
                Exception? back = await ReplaceEngineAsync(before.Renderer);
                _videoError = back != null ? Errors.Describe(back) : null;
                Ui.Message(this, reason, MsgKind.Warning);
            }
        }
        else if (hasDevice && modeDiffers)
        {
            try
            {
                await StartVideoAsync(_currentVideoInfo!, _savedMode);
                _videoError = null;
            }
            catch (OperationCanceledException) { /* stopped on purpose meanwhile */ }
            catch (Exception ex)
            {
                failed = true;
                Ui.Message(this, L.F("映像モードの変更に失敗しました。\n\n{0}", Errors.Describe(ex)),
                    MsgKind.Error);
                LoadProfileValues(before);
                await RecoverVideoAsync();
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
            : auto ? L.F("入力に合わせてプロファイルを切り替えました: {0}", profile.Name)
            : L.F("プロファイル: {0}", profile.Name), failed ? OsdLongMilliseconds : null);
    }

    private void SaveProfileAsNew()
    {
        if (Profiles.Count >= AppSettings.MaxProfiles) return;
        string? name = AskProfileName(L.T("新しいプロファイル"), string.Empty, null);
        if (name == null) return;
        VideoProfile created = CaptureProfile(name);
        created.Signature = SignatureForSave(null);
        Profiles.Add(created);
        _settings.ActiveProfile = name;
        FinishProfileEdit(L.F("プロファイルを保存しました: {0}", name));
    }

    private void OverwriteActiveProfile()
    {
        VideoProfile? active = FindActiveProfile();
        if (active == null) return;
        // Nothing to undo with: ask once before the profile's values are replaced.
        if (Ui.Message(this, L.F("プロファイル「{0}」を現在の設定で上書きしますか？", active.Name), MsgKind.Question,
                MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        int i = Profiles.IndexOf(active);
        VideoProfile updated = CaptureProfile(active.Name);
        updated.Signature = SignatureForSave(active.Signature);
        Profiles[i] = updated;
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
        if (Ui.Message(this, L.F("プロファイル「{0}」を削除しますか？", active.Name), MsgKind.Question,
                MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Profiles.Remove(active);
        _settings.ActiveProfile = null;
        FinishProfileEdit(L.F("プロファイルを削除しました: {0}", active.Name));
    }

    private void FinishProfileEdit(string osd)
    {
        NoteUserChange();
        UpdateChecks();
        UpdateStatus();
        SaveSettings();
        ShowOsd(osd);
    }

    /// <summary>
    /// プロファイルの管理: reorder (Ctrl+1..9 follow the list order), rename and delete. Everything is done on a working
    /// copy that replaces the list on OK; キャンセル leaves the profiles as they were. The automatic switch scores every
    /// profile on its own and does nothing when two fit alike, so the order never changes which one it picks.
    /// </summary>
    private void ShowProfileManager()
    {
        VideoProfile? active = FindActiveProfile();
        var rows = Profiles.Select(p => new ProfileOrderList.Row(p, p.Name)).ToList();

        using var dlg = Ui.NewDialog(L.T("プロファイルの管理"), 460);
        var list = Ui.Stretch(new ProfileOrderList(rows));
        UiButton up = Ui.Stretch(Ui.Btn(L.T("上へ")));
        UiButton down = Ui.Stretch(Ui.Btn(L.T("下へ")));
        UiButton rename = Ui.Stretch(Ui.Btn(L.T("名前を変更...")));
        UiButton delete = Ui.Stretch(Ui.Btn(L.T("削除...")));
        var buttons = Ui.Stack();
        buttons.Add(up);
        buttons.Add(down);
        buttons.Add(rename, before: Ui.Pad);
        buttons.Add(delete);
        Ui.Opt(buttons).Top = true;
        var stack = Ui.Stack();
        stack.Add(Ui.Row(list, buttons));
        stack.Add(Ui.Hint(L.T("Ctrl+1〜9 はこの並び順で割り当てられます。ドラッグや Alt+↑↓ でも並べ替えられます。")), before: Ui.Gap);
        Ui.FrameOkCancel(dlg, stack);
        dlg.InitialFocus = list;
        if (rows.Count > 0) list.Selected = Math.Max(0, rows.FindIndex(r => ReferenceEquals(r.Profile, active)));

        void UpdateButtons()
        {
            int s = list.Selected;
            up.Enabled = s > 0;
            down.Enabled = s >= 0 && s < rows.Count - 1;
            rename.Enabled = delete.Enabled = s >= 0;
            // A button that has just greyed itself out would pass the focus on to whatever is next: keep it on the list.
            if (dlg.ActiveControl is Button { Enabled: false }) list.Focus();
        }

        void Rename()
        {
            int s = list.Selected;
            if (s < 0) return;
            ProfileOrderList.Row row = rows[s];
            string? name = AskProfileName(L.T("名前を変更"), row.Name, row.Name, rows.Select(r => r.Name).ToList(), dlg);
            if (name == null) return;
            row.Name = name;
            list.Invalidate();
        }

        void Delete()
        {
            int s = list.Selected;
            if (s < 0) return;
            if (Ui.Message(dlg, L.F("プロファイル「{0}」を削除しますか？", rows[s].Name), MsgKind.Question,
                    MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            rows.RemoveAt(s);
            list.Selected = Math.Min(s, rows.Count - 1);
            list.Invalidate();
            UpdateButtons();
        }

        list.SelectionChanged += (_, _) => UpdateButtons();
        list.RenameRequested += (_, _) => Rename();
        list.DeleteRequested += (_, _) => Delete();
        up.Click += (_, _) => list.MoveSelected(-1);
        down.Click += (_, _) => list.MoveSelected(1);
        rename.Click += (_, _) => Rename();
        delete.Click += (_, _) => Delete();
        UpdateButtons();

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        bool changed = rows.Count != Profiles.Count
                       || rows.Where((r, i) => !ReferenceEquals(r.Profile, Profiles[i]) || r.Name != r.Profile.Name).Any();
        if (!changed) return;
        VideoProfile? activeNow = FindActiveProfile();   // looked up again: the dialog pumped messages meanwhile
        foreach (ProfileOrderList.Row r in rows) r.Profile.Name = r.Name;
        Profiles.Clear();
        Profiles.AddRange(rows.Select(r => r.Profile));
        _settings.ActiveProfile = activeNow != null && Profiles.Contains(activeNow) ? activeNow.Name : null;
        FinishProfileEdit(L.T("プロファイルを更新しました"));
    }

    /// <summary>One text box, 32 characters at most, trimmed, non-empty and unique (ignoring case).
    /// The reason it cannot be accepted is shown under the box and OK stays disabled meanwhile.
    /// <paramref name="ownName"/> is the name being renamed, which may of course be kept. <paramref name="names"/>: the
    /// names it must differ from (default: the saved profiles); <paramref name="owner"/>: the window it opens over.</summary>
    private string? AskProfileName(string title, string initial, string? ownName,
        IReadOnlyCollection<string>? names = null, IWin32Window? owner = null)
    {
        using var dlg = Ui.NewDialog(title, 380);

        var box = new TextBox { Text = initial, MaxLength = VideoProfile.MaxNameLength };
        var reason = Ui.Hint(string.Empty);   // one line is kept for it, so the dialog does not jump while typing
        var stack = Ui.Stack();
        stack.Add(Ui.Caption(L.T("プロファイル名:")));
        stack.Add(box, before: 4);
        stack.Add(reason, before: 4);
        Button ok = Ui.FrameOkCancel(dlg, stack);

        string? Problem(string text)
        {
            if (text.Length == 0) return L.T("名前を入力してください");
            if ((names ?? Profiles.Select(p => p.Name)).Any(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(n, ownName, StringComparison.OrdinalIgnoreCase)))
                return L.T("同じ名前のプロファイルがあります");
            return null;
        }

        void Check()
        {
            string? problem = Problem(box.Text.Trim());
            ok.Enabled = problem == null;
            reason.Text = problem ?? string.Empty;
            reason.ForeColor = box.Text.Trim().Length == 0 ? Ui.Colors.SubText : Ui.Colors.Danger;
        }
        box.TextChanged += (_, _) => Check();
        Check();
        dlg.InitialFocus = box;
        dlg.Shown += (_, _) => box.SelectAll();

        return dlg.ShowDialog(owner ?? this) == DialogResult.OK ? box.Text.Trim() : null;
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

        using var dlg = Ui.NewDialog(L.T("黒い縁の切り取り"), 400);

        UiGrid sides = Ui.Grid(), sides2 = Ui.Grid();
        NumericUpDown Spin(UiGrid grid, string name, int value, int sourceLen)
        {
            int max = sourceLen > CropMath.MinSide ? Math.Min(AppSettings.MaxCrop, sourceLen - CropMath.MinSide) : AppSettings.MaxCrop;
            var nud = Ui.Number(0, max, value, 84);
            grid.AddRow(Ui.Caption(name), nud, Ui.Caption("px"));
            return nud;
        }

        // 上・下 in one column and 左・右 in the next (tab order follows the same reading order).
        NumericUpDown nt = Spin(sides, L.T("上"), t0, src.Height);
        NumericUpDown nb = Spin(sides, L.T("下"), b0, src.Height);
        NumericUpDown nl = Spin(sides2, L.T("左"), l0, src.Width);
        NumericUpDown nr = Spin(sides2, L.T("右"), r0, src.Width);

        var result = Ui.Text(string.Empty);
        var detect = Ui.Btn(L.T("自動で検出"));
        var reset = Ui.Btn(L.T("既定に戻す"));
        var note = Ui.Hint(string.Empty);
        var stack = Ui.Stack();
        stack.Add(Ui.Row(Ui.Stretch(sides, false), Ui.Before(Ui.Stretch(sides2, false), 32)));
        stack.Add(result, before: Ui.Pad);
        stack.Add(detect, before: Ui.Gap);
        stack.Add(note, before: 4);
        Ui.FrameOkCancel(dlg, stack, left: new Button[] { reset });
        dlg.InitialFocus = nt;

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
}

/// <summary>The list of the profile manager: one row per profile with the key it has (Ctrl+1..9) and its name, in the dark
/// palette. Reordered with the buttons beside it, Alt+Up / Alt+Down, or by dragging a row.</summary>
internal sealed class ProfileOrderList : Control
{
    /// <summary>A working copy of one profile in the manager: the profile itself and the name it will have.</summary>
    internal sealed class Row
    {
        public VideoProfile Profile;
        public string Name;
        public Row(VideoProfile profile, string name) { Profile = profile; Name = name; }
    }

    private readonly List<Row> _rows;
    private readonly int _visibleRows;
    private int _selected = -1;
    private bool _dragging;

    public event EventHandler? SelectionChanged;
    public event EventHandler? RenameRequested;
    public event EventHandler? DeleteRequested;

    public ProfileOrderList(List<Row> rows)
    {
        _rows = rows;
        _visibleRows = Math.Clamp(rows.Count, 5, AppSettings.MaxProfiles);   // fixed: the dialog does not shrink after a delete
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        BackColor = Ui.Colors.Field;
        ForeColor = Ui.Colors.Text;
        AccessibleRole = AccessibleRole.List;
    }

    public int Selected
    {
        get => _selected;
        set
        {
            value = _rows.Count == 0 ? -1 : Math.Clamp(value, -1, _rows.Count - 1);
            if (value == _selected) { Invalidate(); return; }
            _selected = value;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Move the selected row up (-1) or down (+1) in the list; the selection goes with it.</summary>
    public void MoveSelected(int delta)
    {
        int from = _selected, to = from + delta;
        if (from < 0 || to < 0 || to >= _rows.Count) return;
        (_rows[from], _rows[to]) = (_rows[to], _rows[from]);
        _selected = to;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private int RowHeight => Font.Height + 2 * Ui.Px(this, 6);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int w = proposedSize.Width > 1 && proposedSize.Width < 100000 ? proposedSize.Width : Ui.Px(this, 280);
        return new Size(w, RowHeight * _visibleRows + 2);
    }

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                                              | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        int rh = RowHeight, padX = Ui.Px(this, 12);
        int keyW = TextRenderer.MeasureText("Ctrl+9", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        bool focused = Focused;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = new Rectangle(1, 1 + i * rh, Width - 2, rh);
            if (!row.IntersectsWith(e.ClipRectangle)) continue;
            bool sel = i == _selected;
            Color back = sel ? Ui.Colors.Hover : i % 2 == 1 ? Ui.Colors.FieldAlt : BackColor;
            if (back != BackColor) using (var bb = new SolidBrush(back)) g.FillRectangle(bb, row);
            if (sel && focused) using (var bar = new SolidBrush(Ui.Colors.Accent)) g.FillRectangle(bar, row.X, row.Y, Ui.Px(this, 3), row.Height);
            var key = new Rectangle(row.X + padX, row.Y, keyW, rh);
            if (i < 9) TextRenderer.DrawText(g, "Ctrl+" + (i + 1), Font, key, Ui.Colors.SubText, back, TextFlags);
            int nameX = key.Right + padX;
            var name = new Rectangle(nameX, row.Y, Math.Max(1, row.Right - padX - nameX), rh);
            TextRenderer.DrawText(g, _rows[i].Name, Font, name, ForeColor, back, TextFlags);
        }
        using var pen = new Pen(Ui.FieldEdge(this));
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private int RowAt(int y) => _rows.Count == 0 ? -1 : Math.Clamp((y - 1) / RowHeight, 0, _rows.Count - 1);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || _rows.Count == 0) return;
        Selected = RowAt(e.Y);
        _dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _selected < 0) return;
        // Dragging a row: it swaps with whichever row the pointer is over, one step at a time.
        int target = RowAt(e.Y);
        while (target != _selected) MoveSelected(target < _selected ? -1 : 1);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { _dragging = false; base.OnMouseCaptureChanged(e); }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _selected >= 0) RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool alt = e.Alt && !e.Control && !e.Shift;
        switch (e.KeyCode)
        {
            case Keys.Up when alt: MoveSelected(-1); break;
            case Keys.Down when alt: MoveSelected(1); break;
            case Keys.Up when !e.Alt: Selected = Math.Max(0, _selected - 1); break;
            case Keys.Down when !e.Alt: Selected = _selected + 1; break;
            case Keys.Home: Selected = 0; break;
            case Keys.End: Selected = _rows.Count - 1; break;
            case Keys.F2: if (_selected >= 0) RenameRequested?.Invoke(this, EventArgs.Empty); break;
            case Keys.Delete: if (_selected >= 0) DeleteRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); }
}
