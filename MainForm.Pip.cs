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

/// <summary>Picture-in-picture mode and global hotkeys.</summary>
public sealed partial class MainForm
{
    // ---- Picture-in-picture ---------------------------------------------

    private void TogglePip()
    {
        if (_isPip) ExitPip();
        else EnterPip();
    }

    private void EnterPip()
    {
        // Remember whether we were fullscreen so exiting PiP returns there.
        _prePipFullscreen = _isFullscreen;
        if (_isFullscreen) ExitFullscreen();

        _prePipBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _prePipBorderless = _isBorderless;
        _prePipTopmost = _alwaysOnTop;
        _prePipMenu = _menu.Visible;
        _prePipStatus = _status.Visible;
        _isPip = true;

        WindowState = FormWindowState.Normal;
        _menu.Visible = false;
        _status.Visible = false;
        if (!_isBorderless)
        {
            _isBorderless = true;
            _canvas.EnableEdgeResize = true;
            RefreshFrame();
        }
        TopMost = true;

        // The normal-mode minimum (320x240) would clamp the smaller PiP size
        // presets (10-20%) up to a 4:3 window with black bars instead of a
        // small 16:9 one — lower it before sizing the PiP window.
        MinimumSize = new Size(120, 68);

        // Preset-sized window docked to the configured work-area corner.
        ApplyPipSize(anchorCorner: false);
        Location = PipCornerLocation();

        _pipHovered = Bounds.Contains(Cursor.Position);
        ApplyPipOpacity();
        _pipHoverTimer.Start();
        if (_settings.PipClickThrough) ApplyClickThrough(true);

        LayoutCanvas(ModeChangeSettleMs);
        UpdateChecks();
        ShowOsd(L.T("PiP: オン"));
    }

    private void ExitPip()
    {
        _isPip = false;
        _pipHoverTimer.Stop();
        ApplyClickThrough(false);
        try { Opacity = 1.0; } catch { }
        TopMost = _alwaysOnTop = _prePipTopmost;
        if (_isBorderless != _prePipBorderless)
        {
            _isBorderless = _prePipBorderless;
            _canvas.EnableEdgeResize = _isBorderless;
            RefreshFrame();
        }
        _menu.Visible = _prePipMenu;
        _status.Visible = _prePipStatus;
        // Restore the normal minimum BEFORE restoring Bounds below: otherwise
        // the PiP-era 120x68 floor is still in effect while _prePipBounds is
        // applied, and raising MinimumSize afterwards would immediately clamp
        // the just-restored window back up rather than leaving it untouched.
        MinimumSize = NormalMinimumSize;
        if (_prePipBounds.Width > 0) Bounds = _prePipBounds;
        // Return to the mode the user was in before PiP (e.g. fullscreen).
        if (_prePipFullscreen)
        {
            _prePipFullscreen = false;
            EnterFullscreen();
        }
        LayoutCanvas(ModeChangeSettleMs);
        UpdateChecks();
        ShowOsd(L.T("PiP: オフ"));
    }

    private void SetPipSize(int pct)
    {
        _settings.PipSizePct = Math.Clamp(pct, 5, 100);
        if (_isPip) ApplyPipSize(anchorCorner: true);
        UpdateChecks();
    }

    private void SetPipCorner(int corner)
    {
        _settings.PipCorner = Math.Clamp(corner, 0, 3);
        if (_isPip) Location = PipCornerLocation();
        UpdateChecks();
    }

    /// <summary>Work-area location docking the current window to the configured
    /// corner (0=BR, 1=BL, 2=TR, 3=TL) with a 16px margin.</summary>
    private Point PipCornerLocation()
    {
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        const int m = 16;
        return _settings.PipCorner switch
        {
            1 => new Point(wa.Left + m, wa.Bottom - Height - m),
            2 => new Point(wa.Right - Width - m, wa.Top + m),
            3 => new Point(wa.Left + m, wa.Top + m),
            _ => new Point(wa.Right - Width - m, wa.Bottom - Height - m),
        };
    }

    /// <summary>Resize the PiP window to the preset % of the source resolution.
    /// With click-through on, drag-resizing is impossible, so this preset is the
    /// only sizing control. When resizing mid-session the window keeps the
    /// corner matching the configured docking corner, so a docked PiP (even one
    /// the user has dragged elsewhere) grows away from its anchor.</summary>
    private void ApplyPipSize(bool anchorCorner)
    {
        Size res = _video.DisplayResolution;
        int pct = Math.Clamp(_settings.PipSizePct, 5, 100);
        int w = res.Width > 0 ? Math.Max(160, res.Width * pct / 100) : 480;
        int h = res.Width > 0 ? (int)Math.Round((double)w * res.Height / res.Width) : 270;
        Rectangle old = Bounds;
        SetOuterForClient(new Size(w, h));
        if (anchorCorner)
        {
            Location = _settings.PipCorner switch
            {
                1 => new Point(old.Left, old.Bottom - Height),          // BL
                2 => new Point(old.Right - Width, old.Top),             // TR
                3 => new Point(old.Left, old.Top),                      // TL
                _ => new Point(old.Right - Width, old.Bottom - Height), // BR
            };
        }
    }

    private void SetPipOpacity(int pct, bool hover)
    {
        // Hover opacity has a floor of 10%: it is the one guarantee that a PiP
        // window can always be found again with the mouse. Idle opacity has no
        // such floor — fully hiding an idle PiP is a legitimate thing to want,
        // since hovering (or the recovery invariant below) still reveals it.
        pct = hover ? Math.Clamp(pct, 10, 100) : Math.Clamp(pct, 0, 100);
        if (hover) _settings.PipOpacityHover = pct;
        else _settings.PipOpacity = pct;
        if (_isPip) ApplyPipOpacity();
        UpdateChecks();
    }

    /// <summary>Apply the idle/hover opacity matching the current cursor state.
    /// Idle opacity may be 0% (fully invisible): hovering — or Ctrl+Alt+P —
    /// brings it back. Hover opacity is floored at 10% defensively here too,
    /// in case a settings.json written by an older build still holds 0.
    /// While click-through is on, the layered alpha is driven DIRECTLY: the
    /// Form.Opacity setter rewrites the ex-style from WinForms' own cache, which
    /// strips WS_EX_TRANSPARENT and silently disables click-through.</summary>
    private void ApplyPipOpacity()
    {
        int pct = _pipHovered
            ? Math.Clamp(_settings.PipOpacityHover, 10, 100)
            : Math.Clamp(_settings.PipOpacity, 0, 100);
        if (_isPip && _settings.PipClickThrough && IsHandleCreated)
        {
            int ex = GetWindowLong(Handle, GwlExStyle);
            SetWindowLong(Handle, GwlExStyle, ex | WsExTransparent | WsExLayered);
            SetLayeredWindowAttributes(Handle, 0, (byte)(pct * 255 / 100), LwaAlpha);
        }
        else
        {
            try { Opacity = pct / 100.0; } catch { }
        }
    }

    private void UpdatePipHoverOpacity()
    {
        if (!_isPip) { _pipHoverTimer.Stop(); return; }
        bool hovered = Bounds.Contains(Cursor.Position);
        if (hovered == _pipHovered) return;
        _pipHovered = hovered;
        ApplyPipOpacity();
    }

    /// <summary>Click-through must never be enabled without an escape route: a
    /// working PiP-toggle hotkey is the only way to reach an unreachable window
    /// once the mouse passes through it. Returns false (and registers nothing)
    /// when the hotkey is unset, or true only if registering it succeeded.
    /// TryRegisterHotkey already unregisters the id first, so calling this while
    /// global hotkeys are already on (and the id already registered) is safe.</summary>
    private bool EnsurePipHotkeyForClickThrough()
    {
        Keys combo = (Keys)_settings.HotkeyPip;
        if ((combo & Keys.KeyCode) == Keys.None) return false;
        var failed = new List<string>();
        TryRegisterHotkey(HkPip, combo, failed);
        return failed.Count == 0;
    }

    private void TogglePipClickThrough()
    {
        if (!_settings.PipClickThrough)
        {
            if (!EnsurePipHotkeyForClickThrough())
            {
                ShowOsd(L.T("クリックスルーには PiP切替 のホットキーが必要です。\nオプション → ホットキー設定 で割り当ててください。"), OsdLongMilliseconds);
                return;
            }
            _settings.PipClickThrough = true;
        }
        else
        {
            _settings.PipClickThrough = false;
        }
        if (_isPip) ApplyClickThrough(_settings.PipClickThrough);
        UpdateChecks();
        // With the mouse passing through, the menus are unreachable — name the
        // key that gets the window back rather than leaving the user stuck.
        ShowOsd(_settings.PipClickThrough
            ? L.F("クリックスルー: オン（{0} で解除）", FormatHotkey((Keys)_settings.HotkeyPip))
            : L.T("クリックスルー: オフ"), _settings.PipClickThrough ? OsdLongMilliseconds : (int?)null);
    }

    private void ApplyClickThrough(bool on)
    {
        if (!IsHandleCreated) return;
        if (on)
        {
            // Also guards the EnterPip path, where PipClickThrough may already
            // be true from a saved settings.json whose PiP hotkey was since
            // cleared — never trust the stored flag alone.
            if (!EnsurePipHotkeyForClickThrough())
            {
                _settings.PipClickThrough = false;
                ShowOsd(L.T("PiP のホットキーが未設定のため、クリックスルーを解除しました"), OsdLongMilliseconds);
                return;
            }
            // Safety hatch: with the mouse passing through, the PiP hotkey must
            // work even if the user disabled global hotkeys — the check above
            // already registered it unconditionally (TryRegisterHotkey is a
            // harmless no-op re-register when it was already active).
            ApplyPipOpacity(); // sets WS_EX_TRANSPARENT|LAYERED + the layered alpha
        }
        else
        {
            int ex = GetWindowLong(Handle, GwlExStyle);
            SetWindowLong(Handle, GwlExStyle, ex & ~(WsExTransparent | WsExLayered));
            // Hand opacity back to WinForms. Its cached Opacity may equal the
            // target (making the setter a no-op), so pass through 1.0 first to
            // force a real style re-apply, then restore the PiP opacity if any.
            try
            {
                Opacity = 1.0;
                if (_isPip) ApplyPipOpacity();
            }
            catch { }
            if (!_settings.GlobalHotkeys) UnregisterHotKey(Handle, HkPip);
        }
    }

    // ---- Global hotkeys --------------------------------------------------

    private void RegisterGlobalHotkeys(bool announce = true)
    {
        var failed = new List<string>();
        TryRegisterHotkey(HkSnapshot, (Keys)_settings.HotkeySnapshot, failed);
        TryRegisterHotkey(HkMute, (Keys)_settings.HotkeyMute, failed);
        TryRegisterHotkey(HkPip, (Keys)_settings.HotkeyPip, failed);
        if (failed.Count == 0)
        {
            _settings.HotkeyConflictNotified = null;   // resolved; warn again if it returns
            return;
        }

        // Partial failure just means one combo is owned by another app — name it
        // rather than giving a blanket error; the others still work. Announce a
        // given set of conflicts once: the startup check runs on every launch and
        // a warning repeated forever is a warning nobody reads.
        string signature = string.Join(", ", failed);
        Log.Info("hotkey conflict: " + signature);
        if (!announce && _settings.HotkeyConflictNotified == signature) return;
        _settings.HotkeyConflictNotified = signature;
        ShowOsd(L.F("ホットキー使用中のため無効: {0}（オプションで変更できます）", signature), OsdLongMilliseconds);
    }

    private void TryRegisterHotkey(int id, Keys combo, List<string> failed)
    {
        // Defensive: an id left over from a previous register (toggle, click-
        // through safety hatch) makes a re-register fail — clear it first.
        UnregisterHotKey(Handle, id);
        if ((combo & Keys.KeyCode) == Keys.None) return; // disabled
        uint mods = ModNoRepeat;
        if (combo.HasFlag(Keys.Control)) mods |= ModControl;
        if (combo.HasFlag(Keys.Alt)) mods |= ModAlt;
        if (combo.HasFlag(Keys.Shift)) mods |= ModShift;
        if (!RegisterHotKey(Handle, id, mods, (uint)(combo & Keys.KeyCode)))
            failed.Add(FormatHotkey(combo));
    }

    private static string FormatHotkey(Keys combo)
    {
        if ((combo & Keys.KeyCode) == Keys.None) return L.T("なし");
        var parts = new List<string>();
        if (combo.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (combo.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (combo.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add((combo & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }

    private void ShowHotkeySettings()
    {
        using var dlg = new Form
        {
            Text = L.T("ホットキー設定"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 210),
            KeyPreview = true,
            // The controls below are laid out with fixed pixel coordinates
            // drawn against the default Segoe UI 9pt metric (7, 15) at 100%
            // scaling. Without this, the font grows with display scaling but
            // the layout doesn't, and controls overlap at 125-200%.
            AutoScaleMode = AutoScaleMode.Font,
            AutoScaleDimensions = new SizeF(7F, 15F),
        };

        Keys snapCombo = (Keys)_settings.HotkeySnapshot;
        Keys muteCombo = (Keys)_settings.HotkeyMute;
        Keys pipCombo = (Keys)_settings.HotkeyPip;

        TextBox MakeRow(string label, int y, Keys initial, Action<Keys> set)
        {
            var lbl = new Label { Text = L.T(label), AutoSize = true, Location = new Point(16, y + 4) };
            var tb = new TextBox
            {
                Text = FormatHotkey(initial),
                ReadOnly = true,
                Location = new Point(150, y),
                Width = 190,
                TabStop = true,
            };
            tb.KeyDown += (_, e) =>
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (e.KeyCode == Keys.Escape)
                {
                    set(Keys.None);
                    tb.Text = FormatHotkey(Keys.None);
                    return;
                }
                // Ignore presses of a modifier alone; require Ctrl/Alt/Shift.
                if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return;
                if (e.Modifiers == Keys.None) return;
                set(e.KeyData);
                tb.Text = FormatHotkey(e.KeyData);
            };
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(tb);
            return tb;
        }

        var tbSnap = MakeRow("スナップショット:", 16, snapCombo, k => snapCombo = k);
        var tbMute = MakeRow("ミュート:", 52, muteCombo, k => muteCombo = k);
        var tbPip = MakeRow("PiP切替:", 88, pipCombo, k => pipCombo = k);

        var hint = new Label
        {
            Text = L.T("欄をクリックしてキーを押してください。Esc で無効化できます。"),
            AutoSize = true,
            // Color.Gray is ~2.9:1 against the dialog background, below the
            // 4.5:1 readability threshold; SystemColors.GrayText also respects
            // the user's theme/contrast settings.
            ForeColor = SystemColors.GrayText,
            Location = new Point(16, 126),
        };
        var reset = new Button { Text = L.T("既定に戻す"), Location = new Point(16, 168), Width = 100 };
        reset.Click += (_, _) =>
        {
            snapCombo = Keys.Control | Keys.Alt | Keys.S;
            muteCombo = Keys.Control | Keys.Alt | Keys.M;
            pipCombo = Keys.Control | Keys.Alt | Keys.P;
            tbSnap.Text = FormatHotkey(snapCombo);
            tbMute.Text = FormatHotkey(muteCombo);
            tbPip.Text = FormatHotkey(pipCombo);
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(164, 168), Width = 85 };
        var cancel = new Button { Text = L.T("キャンセル"), DialogResult = DialogResult.Cancel, Location = new Point(255, 168), Width = 90 };
        dlg.Controls.AddRange(new Control[] { hint, reset, ok, cancel });
        dlg.CancelButton = cancel;
        // The textboxes are read-only and their KeyDown handler marks every
        // key (including Enter) as handled, so without an AcceptButton, Enter
        // did nothing. This makes Enter work when focus is elsewhere (e.g. on
        // Reset); it still has no effect while a hotkey field has focus, which
        // is fine — that field is reachable by Tab or click regardless.
        dlg.AcceptButton = ok;

        // Loop instead of a single ShowDialog: a duplicate assignment must send
        // the user back into the same dialog with their typed values intact,
        // not close it — ShowDialog can be called again on the same (undisposed)
        // Form, and none of the controls' state is reset in between.
        while (true)
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Reject the same combo assigned to two actions: RegisterHotKey
            // would only fail on the second one, and telling the user "another
            // application is using this key" would be false and unactionable.
            bool duplicate =
                ((snapCombo & Keys.KeyCode) != Keys.None && snapCombo == muteCombo) ||
                ((snapCombo & Keys.KeyCode) != Keys.None && snapCombo == pipCombo) ||
                ((muteCombo & Keys.KeyCode) != Keys.None && muteCombo == pipCombo);
            if (duplicate)
            {
                MessageBox.Show(this, L.T("同じキーが複数の操作に割り当てられています。"), "YuCap",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            break;
        }

        _settings.HotkeySnapshot = (int)snapCombo;
        _settings.HotkeyMute = (int)muteCombo;
        _settings.HotkeyPip = (int)pipCombo;

        // Don't let this dialog create the same unreachable-PiP state that
        // EnsurePipHotkeyForClickThrough guards against elsewhere: clearing the
        // PiP hotkey while click-through is on would otherwise leave no way to
        // get the window back once the mouse passes through it.
        if ((pipCombo & Keys.KeyCode) == Keys.None && _settings.PipClickThrough)
        {
            _settings.PipClickThrough = false;
            if (_isPip) ApplyClickThrough(false);
            ShowOsd(L.T("PiP のホットキーが未設定のため、クリックスルーを解除しました"), OsdLongMilliseconds);
        }

        SaveSettings();
        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys(); // re-register + report conflicts now
        UpdateChecks();
    }

    private void UnregisterGlobalHotkeys()
    {
        UnregisterHotKey(Handle, HkSnapshot);
        UnregisterHotKey(Handle, HkMute);
        UnregisterHotKey(Handle, HkPip);
    }

    private void ToggleGlobalHotkeys()
    {
        _settings.GlobalHotkeys = !_settings.GlobalHotkeys;
        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys();
        else UnregisterGlobalHotkeys();
        UpdateChecks();
    }
}
