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
        if (_isFullscreen) ExitFullscreen(userAction: false);

        _prePipWindowState = WindowState;
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
        SyncPipHotkey();
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
        if (_prePipBounds.Width > 0)
        {
            _prePipBounds = EnsureOnScreen(_prePipBounds);   // the monitor it came from may be gone
            Bounds = _prePipBounds;
        }
        // Return to the mode the user was in before PiP (e.g. fullscreen).
        if (_prePipFullscreen)
        {
            _prePipFullscreen = false;
            EnterFullscreen(userAction: false);
            // Leaving that fullscreen later must lead back to where the window was (maximized included).
            _restoreWindowState = _prePipWindowState;
            if (_prePipBounds.Width > 0) _restoreBounds = _prePipBounds;
        }
        else if (_prePipWindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Maximized;
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
                ShowOsd(L.T("クリックを通すには PiP の切り替えのホットキーが必要です。\nオプション → ホットキーの設定 で割り当ててください。"), OsdLongMilliseconds);
                return;
            }
            _settings.PipClickThrough = true;
        }
        else
        {
            _settings.PipClickThrough = false;
        }
        if (_isPip) ApplyClickThrough(_settings.PipClickThrough);
        SyncPipHotkey();   // registered only while it is needed (global hotkeys on, or an active click-through PiP)
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
            SyncPipHotkey();
        }
    }

    /// <summary>The PiP hotkey is the way back out of a click-through PiP, so it must be registered then even when the
    /// global hotkeys are off - and it must not stay registered once that is no longer true.</summary>
    private void SyncPipHotkey()
    {
        if (!IsHandleCreated || _settings.GlobalHotkeys) return;   // with global hotkeys on, RegisterGlobalHotkeys owns it
        if (_isPip && _settings.PipClickThrough)
        {
            if (!EnsurePipHotkeyForClickThrough()) UnregisterHotKey(Handle, HkPip);
        }
        else UnregisterHotKey(Handle, HkPip);
    }

    // ---- Global hotkeys --------------------------------------------------

    private void RegisterGlobalHotkeys(bool announce = true)
    {
        var failed = new List<string>();
        TryRegisterHotkey(HkSnapshot, (Keys)_settings.HotkeySnapshot, failed);
        TryRegisterHotkey(HkMute, (Keys)_settings.HotkeyMute, failed);
        TryRegisterHotkey(HkPip, (Keys)_settings.HotkeyPip, failed);
        // The volume keys auto-repeat while held (no MOD_NOREPEAT), like the arrow keys in the window.
        TryRegisterHotkey(HkVolUp, (Keys)_settings.HotkeyVolumeUp, failed, repeat: true);
        TryRegisterHotkey(HkVolDown, (Keys)_settings.HotkeyVolumeDown, failed, repeat: true);
        TryRegisterHotkey(HkStyle, (Keys)_settings.HotkeyStyle, failed);
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

    private void TryRegisterHotkey(int id, Keys combo, List<string> failed, bool repeat = false)
    {
        // Defensive: an id left over from a previous register (toggle, click-
        // through safety hatch) makes a re-register fail — clear it first.
        UnregisterHotKey(Handle, id);
        if ((combo & Keys.KeyCode) == Keys.None) return; // disabled
        uint mods = repeat ? 0u : ModNoRepeat;
        if (combo.HasFlag(Keys.Control)) mods |= ModControl;
        if (combo.HasFlag(Keys.Alt)) mods |= ModAlt;
        if (combo.HasFlag(Keys.Shift)) mods |= ModShift;
        if (!RegisterHotKey(Handle, id, mods, (uint)(combo & Keys.KeyCode)))
            failed.Add(FormatHotkey(combo));
    }

    /// <summary>Friendly display name for a hotkey combo (Ctrl+Alt+1, not
    /// Ctrl+Alt+D1; Num 0 not NumPad0; etc.) — used in dialogs and lists where
    /// showing "unassigned" explicitly is useful.</summary>
    private static string FormatHotkey(Keys combo)
    {
        if ((combo & Keys.KeyCode) == Keys.None) return L.T("なし");
        var parts = new List<string>();
        if (combo.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (combo.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (combo.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(FriendlyKeyName(combo & Keys.KeyCode));
        return string.Join("+", parts);
    }

    /// <summary>Same as <see cref="FormatHotkey"/> but "" when unassigned, for
    /// ShortcutKeyDisplayString — a menu item showing "なし" as its shortcut
    /// text would just be noise.</summary>
    private static string HotkeyMenuText(Keys combo) =>
        (combo & Keys.KeyCode) == Keys.None ? "" : FormatHotkey(combo);

    private static string FriendlyKeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)(key - Keys.D0)).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + (int)(key - Keys.NumPad0),
        Keys.Oemplus => "+",
        Keys.OemMinus => "-",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.Oemtilde => "`",
        Keys.OemOpenBrackets => "[",
        Keys.Oem6 => "]",
        Keys.Oem5 => "\\",
        Keys.Oem1 => ";",
        Keys.Oem7 => "'",
        Keys.OemBackslash => "\\",
        Keys.Return => "Enter",
        Keys.Back => "Backspace",
        Keys.Prior => "PageUp",
        Keys.Next => "PageDown",
        Keys.Capital => "CapsLock",
        Keys.Escape => "Esc",
        Keys.Space => "Space",
        Keys.Multiply => "Num *",
        Keys.Add => "Num +",
        Keys.Subtract => "Num -",
        Keys.Divide => "Num /",
        Keys.Decimal => "Num .",
        _ => key.ToString(),
    };

    /// <summary>Combos that would steal a Windows-wide or universal-app basic
    /// (copy/paste/undo/etc, Alt+Tab/F4/...) from every application while YuCap
    /// is running, for the whole time its hotkeys are registered. Rejected on
    /// OK in the hotkey dialog regardless of whether anything currently has
    /// them registered — RegisterHotKey itself would happily succeed since
    /// these aren't OS-reserved, which is exactly the problem.</summary>
    private static bool IsReservedCombo(Keys combo)
    {
        Keys mods = combo & Keys.Modifiers;
        Keys key = combo & Keys.KeyCode;
        if (mods == Keys.Control || mods == (Keys.Control | Keys.Shift))
        {
            if (key is Keys.C or Keys.V or Keys.X or Keys.Z or Keys.Y or Keys.A) return true;
        }
        if (mods == Keys.Alt && key is Keys.F4 or Keys.Tab or Keys.Escape or Keys.Space) return true;
        if (mods == Keys.Control && key is Keys.Escape) return true;                       // Start menu
        if (mods == (Keys.Control | Keys.Shift) && key is Keys.Escape) return true;       // Task Manager
        if (mods == (Keys.Control | Keys.Alt) && key is Keys.Delete) return true;         // secure attention
        return false;
    }

    /// <summary>A combo the hotkey dialog cannot take at all: no modifier (the key would stop typing / working
    /// everywhere), or Shift alone with anything but a function key (it would stop capital letters and symbols).
    /// A modifier on its own is not a combo either.</summary>
    private static bool NeedsCtrlOrAlt(Keys combo)
    {
        Keys mods = combo & Keys.Modifiers;
        Keys key = combo & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true;
        if ((mods & (Keys.Control | Keys.Alt)) != Keys.None) return false;
        return !(mods == Keys.Shift && key is >= Keys.F1 and <= Keys.F24);
    }

    /// <summary>The name of YuCap's own fixed shortcut a combo would take over (configured hotkeys are matched first
    /// in ProcessCmdKey), or null. Keys without Ctrl/Alt are never accepted, so only modified shortcuts are listed.</summary>
    private static string? BuiltInShortcutName(Keys combo)
    {
        if (ProfileIndexForKey(combo) != null) return L.T("プロファイルを切り替える");
        return combo switch
        {
            Keys.Control | Keys.S => L.T("スナップショットを保存"),
            Keys.Control | Keys.C => L.T("スナップショットをコピー"),
            Keys.Control | Keys.G => L.T("スナップショット一覧"),
            Keys.Control | Keys.T => L.T("常に手前に表示"),
            Keys.Control | Keys.B => L.T("ウィンドウ枠を隠す"),
            Keys.Control | Keys.D0 or Keys.Control | Keys.NumPad0 => L.T("ズームをリセット"),
            Keys.Control | Keys.Oemplus or Keys.Control | Keys.Add => L.T("ズームイン"),
            Keys.Control | Keys.OemMinus or Keys.Control | Keys.Subtract => L.T("ズームアウト"),
            Keys.Control | Keys.Shift | Keys.I => L.T("情報表示の切り替え（オフ → FPS だけ → 詳しく）"),
            Keys.Alt | Keys.Enter => L.T("全画面表示の切り替え"),
            _ => null,
        };
    }

    /// <summary>Ctrl (+ Shift) with a letter or digit: other apps commonly use these (Ctrl+F, Ctrl+W, Ctrl+Shift+N ...),
    /// and a global hotkey takes the key away from all of them while YuCap runs.</summary>
    private static bool IsCommonAppCombo(Keys combo)
    {
        Keys mods = combo & Keys.Modifiers;
        Keys key = combo & Keys.KeyCode;
        if (mods != Keys.Control && mods != (Keys.Control | Keys.Shift)) return false;
        return key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9;
    }

    private void ShowHotkeySettings()
    {
        var dlg = Ui.NewDialog(L.T("ホットキーの設定"), 540);
        var grid = Ui.Grid();

        Keys snapCombo = (Keys)_settings.HotkeySnapshot;
        Keys muteCombo = (Keys)_settings.HotkeyMute;
        Keys pipCombo = (Keys)_settings.HotkeyPip;
        Keys volUpCombo = (Keys)_settings.HotkeyVolumeUp;
        Keys volDownCombo = (Keys)_settings.HotkeyVolumeDown;
        Keys styleCombo = (Keys)_settings.HotkeyStyle;

        string hintText = L.T("欄を選んでキーの組み合わせを押してください。［解除］または Backspace で割り当てを外せます。");
        Label? hint = null;

        (TextBox tb, Button clear) MakeRow(string label, Keys initial, Action<Keys> set)
        {
            var lbl = Ui.Caption(L.T(label));
            var tb = new TextBox
            {
                Text = FormatHotkey(initial),
                ReadOnly = true,
                TabStop = true,
            };
            tb.GotFocus += (_, _) => tb.BeginInvoke(new Action(() => tb.Select(tb.TextLength, 0)));
            var clearBtn = Ui.Btn(L.T("解除"));
            void Clear()
            {
                set(Keys.None);
                tb.Text = FormatHotkey(Keys.None);
            }
            tb.KeyDown += (_, e) =>
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                // Esc is intentionally NOT handled here: it is a dialog key
                // that CancelButton intercepts in ProcessDialogKey before the
                // TextBox ever sees KeyDown, so Esc always just cancels the
                // whole dialog. Backspace/Delete DO reach KeyDown normally and
                // serve as the keyboard-only way to unassign a combo.
                if (e.KeyCode is Keys.Back or Keys.Delete) { Clear(); return; }
                // A modifier on its own is the start of a combo: wait for the rest.
                if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
                // A key without Ctrl/Alt would be taken from typing everywhere: say why nothing was assigned
                // instead of ignoring the press.
                if (NeedsCtrlOrAlt(e.KeyData))
                {
                    if (hint != null) hint.Text = L.F("{0} は使えません。Ctrl か Alt を押しながら、ほかのキーを押してください。", FormatHotkey(e.KeyData));
                    return;
                }
                if (hint != null) hint.Text = hintText;
                set(e.KeyData);
                tb.Text = FormatHotkey(e.KeyData);
            };
            clearBtn.Click += (_, _) => Clear();
            grid.AddRow(lbl, tb, clearBtn);
            return (tb, clearBtn);
        }

        var (tbSnap, _) = MakeRow("スナップショット:", snapCombo, k => snapCombo = k);
        var (tbMute, _) = MakeRow("ミュート:", muteCombo, k => muteCombo = k);
        var (tbPip, _) = MakeRow("PiP の切り替え:", pipCombo, k => pipCombo = k);

        var (tbVolUp, _) = MakeRow("音量を上げる:", volUpCombo, k => volUpCombo = k);
        var (tbVolDown, _) = MakeRow("音量を下げる:", volDownCombo, k => volDownCombo = k);

        var (tbStyle, _) = MakeRow("映像の出し方（なめらか ⇔ 低遅延）:", styleCombo, k => styleCombo = k);

        hint = Ui.Hint(hintText);
        grid.AddSpan(hint, underField: false, before: Ui.Pad);

        var reset = Ui.Btn(L.T("既定に戻す"));
        reset.Click += (_, _) =>
        {
            var defaults = new AppSettings();   // the one place the defaults are defined
            snapCombo = (Keys)defaults.HotkeySnapshot;
            muteCombo = (Keys)defaults.HotkeyMute;
            pipCombo = (Keys)defaults.HotkeyPip;
            volUpCombo = (Keys)defaults.HotkeyVolumeUp;
            volDownCombo = (Keys)defaults.HotkeyVolumeDown;
            styleCombo = (Keys)defaults.HotkeyStyle;
            if (hint != null) hint.Text = hintText;
            tbStyle.Text = FormatHotkey(styleCombo);
            tbVolUp.Text = FormatHotkey(volUpCombo);
            tbVolDown.Text = FormatHotkey(volDownCombo);
            tbSnap.Text = FormatHotkey(snapCombo);
            tbMute.Text = FormatHotkey(muteCombo);
            tbPip.Text = FormatHotkey(pipCombo);
        };
        Ui.FrameOkCancel(dlg, grid, left: new Button[] { reset });
        dlg.InitialFocus = tbSnap;

        // Loop instead of a single ShowDialog: a duplicate/reserved assignment
        // must send the user back into the same dialog with their typed values
        // intact, not close it — ShowDialog can be called again on the same
        // (undisposed) Form, and none of the controls' state is reset in between.
        // While the dialog is open the registered combos must reach the text boxes: a registered global hotkey is
        // delivered as WM_HOTKEY instead of a key press, which would fire the action under the dialog (mute, a
        // snapshot, PiP) and leave the box unchanged - an assigned combo could never be moved to another action.
        UnregisterGlobalHotkeys();
        while (true)
        {
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                dlg.Dispose();
                if (_settings.GlobalHotkeys) RegisterGlobalHotkeys(announce: false);   // put back what was there
                else SyncPipHotkey();
                return;
            }

            // Reject the same combo assigned to two actions: RegisterHotKey
            // would only fail on the second one, and telling the user "another
            // application is using this key" would be false and unactionable.
            Keys[] all = { snapCombo, muteCombo, pipCombo, volUpCombo, volDownCombo, styleCombo };
            bool duplicate = false;
            for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                    if ((all[i] & Keys.KeyCode) != Keys.None && all[i] == all[j]) duplicate = true;
            if (duplicate)
            {
                Ui.Message(this, L.T("同じキーが複数の操作に割り当てられています。"), MsgKind.Warning);
                continue;
            }

            Keys reserved = Keys.None;
            foreach (Keys c in all) if (reserved == Keys.None && IsReservedCombo(c)) reserved = c;
            if ((reserved & Keys.KeyCode) != Keys.None)
            {
                Ui.Message(this, L.F("{0} は Windows の基本操作と重なるため使えません。", FormatHotkey(reserved)),
                    MsgKind.Warning);
                continue;
            }

            // Allowed, but it takes a key away from something: say what, and let the user decide.
            var clashes = new List<string>();
            foreach (Keys c in all)
            {
                if ((c & Keys.KeyCode) == Keys.None) continue;
                if (BuiltInShortcutName(c) is string builtIn)
                    clashes.Add(L.F("{0}: YuCap の「{1}」が使えなくなります", FormatHotkey(c), builtIn));
                else if (IsCommonAppCombo(c))
                    clashes.Add(L.F("{0}: ほかのアプリでもよく使うキーです。どこからでも効くようにすると、そのアプリでは使えなくなります", FormatHotkey(c)));
            }
            if (clashes.Count > 0 && Ui.Message(this,
                    L.F("次のキーは、ほかの操作と重なります。\n\n{0}\n\nこのまま割り当てますか？", string.Join("\n", clashes)),
                    MsgKind.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                continue;
            break;
        }
        dlg.Dispose();

        _settings.HotkeySnapshot = (int)snapCombo;
        _settings.HotkeyMute = (int)muteCombo;
        _settings.HotkeyPip = (int)pipCombo;
        _settings.HotkeyVolumeUp = (int)volUpCombo;
        _settings.HotkeyVolumeDown = (int)volDownCombo;
        _settings.HotkeyStyle = (int)styleCombo;

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
        else SyncPipHotkey();                                  // a click-through PiP needs its (possibly new) combo
        UpdateChecks();
    }

    private void UnregisterGlobalHotkeys()
    {
        UnregisterHotKey(Handle, HkSnapshot);
        UnregisterHotKey(Handle, HkMute);
        UnregisterHotKey(Handle, HkPip);
        UnregisterHotKey(Handle, HkVolUp);
        UnregisterHotKey(Handle, HkVolDown);
        UnregisterHotKey(Handle, HkStyle);
    }

    private void ToggleGlobalHotkeys()
    {
        _settings.GlobalHotkeys = !_settings.GlobalHotkeys;
        if (_settings.GlobalHotkeys) RegisterGlobalHotkeys();
        else { UnregisterGlobalHotkeys(); SyncPipHotkey(); }
        UpdateChecks();
    }
}
