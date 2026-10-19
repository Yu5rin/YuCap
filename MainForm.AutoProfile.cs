using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// プロファイルの自動切り替え. Every profile remembers a small <see cref="InputSignature"/> of the input it was used with
/// (capture device, negotiated mode, the picture's real detail, the game's frame rate band). When the input changes - a device
/// is connected, the mode or resolution changes, or the app has just started - and the signal has settled, the live signature
/// is compared with the stored ones (<see cref="ProfileMatcher"/>). If another profile fits clearly better than the active one,
/// it is offered on the OSD (click to switch) or switched to, depending on <c>AutoProfileMode</c>. When two profiles fit alike
/// (two consoles that are both 1080p60), nothing happens. Everything is cheap property reads on the UI timer; nothing blocks.
/// </summary>
public sealed partial class MainForm
{
    private const int AutoProfileSettleMs = 10_000;      // after an input change, before judging
    private const int AutoProfileMaxWaitMs = 30_000;     // ... and the longest wait for the estimates to settle
    private const int AutoProfileQuietMs = 30_000;       // no settings changed by the user for this long
    private const int AutoProfileLearnMs = 60_000;       // stable use of a profile before its signature is refreshed
    private const int AutoProfileOfferMs = 8_000;

    private long _lastUserChangeTick = Environment.TickCount64;
    private string? _apKey;                  // the input as of the last tick: device|WxH@fps
    private long _apKeySince;
    private bool _apDecided;                 // this input change has been judged (once per change)
    private string? _apOfferedKey;           // a better profile existed for this input and the user stayed: do not learn
    private InputSignature? _apLive;         // the signature seen at the last tick, to tell when it has been stable
    private long _apLiveSince;
    private bool _apLearnDone;
    private long _apNoSignalSince;
    private string _apLastDecision = string.Empty;

    private void NoteUserChange() => _lastUserChangeTick = Environment.TickCount64;

    // =====================================================================
    //  The live signature
    // =====================================================================

    /// <summary>The input as it is now, or null when no picture is running. <paramref name="settled"/> is false while the
    /// estimates it rests on (the picture's real detail) are still being judged.</summary>
    internal InputSignature? LiveSignature(out bool settled)
    {
        settled = true;
        if (!_video.IsRunning || _currentVideoInfo == null) return null;
        Size cap = _video.CurrentResolution;
        if (cap.Width <= 0 || cap.Height <= 0) return null;
        var s = new InputSignature
        {
            DeviceId = _currentVideoInfo.Id,
            Width = cap.Width,
            Height = cap.Height,
            ModeFps = _video.NegotiatedFps > 0 ? _video.NegotiatedFps : (_video.CurrentMode?.Fps ?? 0),
        };
        if (_video.SupportsNativeRes && _settings.NativeResScale != "off" && _video.NativeResState is { } nr)
        {
            if (nr.Judging) settled = false;
            else s.DetailHeight = nr.Active > 0 ? nr.Active : nr.Target > 0 ? nr.Target : nr.SourceHeight;
        }
        if (_video.GameFpsReliable && _video.GameFps > 0) s.GameFpsBand = InputSignature.BandOf(_video.GameFps);
        return s.Normalized();
    }

    // =====================================================================
    //  The timer
    // =====================================================================

    private void AutoProfileTick()
    {
        if (_testRunning || _startingUp || IsDisposed) return;
        if (_engineSwapping || _devOpBusy > 0) return;
        InputSignature? live = LiveSignature(out bool settled);
        long now = Environment.TickCount64;
        if (live == null)
        {
            // A signal that is gone for a few seconds (the console was switched, the cable was moved) ends the input; a blink does not.
            if (_apNoSignalSince == 0) _apNoSignalSince = now;
            else if (now - _apNoSignalSince > 3000) { _apKey = null; _apLive = null; }
            return;
        }
        _apNoSignalSince = 0;

        string key = $"{live.DeviceId}|{live.Width}x{live.Height}@{live.ModeFps}";
        if (key != _apKey)
        {
            _apKey = key; _apKeySince = now; _apDecided = false; _apLive = null; _apOfferedKey = null;
            Log.Info("auto-profile: input " + live);
            return;
        }

        // How long the signature has been the same (for learning).
        if (!settled || _apLive == null || !_apLive.SameAs(live)) { _apLive = settled ? live : null; _apLiveSince = now; _apLearnDone = false; }

        if (!_apDecided) JudgeInput(live, settled, now);
        else if (!_apLearnDone && settled && now - _apLiveSince >= AutoProfileLearnMs) LearnSignature(live);
    }

    // =====================================================================
    //  Judging a new input
    // =====================================================================

    private void JudgeInput(InputSignature live, bool settled, long now)
    {
        long since = now - _apKeySince;
        if (since < AutoProfileSettleMs) return;
        if (!settled && since < AutoProfileMaxWaitMs) return;
        if (Profiles.Count < 2) { _apDecided = true; return; }
        // The user is busy with the settings: ask again later (this does not use up the one judgement per change).
        if (now - _lastUserChangeTick < AutoProfileQuietMs) return;

        _apDecided = true;
        VideoProfile? chosen = ChooseProfileFor(live, out string why);
        _apLastDecision = why;
        Log.Info("auto-profile: " + why + (chosen != null ? " -> " + chosen.Name : " -> stay"));
        if (chosen == null) return;

        _apOfferedKey = _apKey;   // a better profile exists: whatever happens, the active one must not learn this input
        string mode = _settings.AutoProfileMode;
        if (mode == "off") return;
        string name = chosen.Name;
        if (mode == "auto")
        {
            Log.Info("auto-profile: switching to " + name);
            ApplyProfile(chosen, auto: true);
            return;
        }
        ShowOsd(L.F("{0} のプロファイルに切り替えますか？（クリックで切り替え）", name), AutoProfileOfferMs, () =>
        {
            VideoProfile? p = Profiles.Find(x => x.Name == name);   // may have been renamed or deleted meanwhile
            if (p == null || _testRunning) return;
            Log.Info("auto-profile: accepted " + name);
            ApplyProfile(p);
        });
    }

    /// <summary>The profile that fits <paramref name="live"/> clearly better than the active one, or null (with the reason).</summary>
    internal VideoProfile? ChooseProfileFor(InputSignature live, out string why)
    {
        var list = Profiles;
        VideoProfile? active = FindActiveProfile();
        int? i = ProfileMatcher.Choose(live, list.Select(p => p.Signature).ToList(), active == null ? -1 : list.IndexOf(active), out why);
        return i is int idx ? list[idx] : null;
    }

    // =====================================================================
    //  Learning
    // =====================================================================

    /// <summary>The signature to store for a profile saved now: what is live, over what the profile already knew.</summary>
    private InputSignature? SignatureForSave(InputSignature? old)
    {
        InputSignature? live = LiveSignature(out _);
        if (live == null) return old;
        return old == null ? live : old.MergedWith(live);
    }

    /// <summary>The active profile has been used with this input, steadily, for a while: remember it.</summary>
    private void LearnSignature(InputSignature live)
    {
        _apLearnDone = true;
        if (_apOfferedKey == _apKey) return;   // a better-fitting profile was there and the user stayed: not this profile's input
        VideoProfile? active = FindActiveProfile();
        if (active == null) return;
        InputSignature merged = (active.Signature ?? new InputSignature()).MergedWith(live);
        if (active.Signature != null && active.Signature.SameAs(merged)) return;
        active.Signature = merged;
        Log.Info($"auto-profile: learned for {active.Name}: {merged}");
        MarkSettingsDirty(userChange: false);
    }

    // =====================================================================
    //  Menu
    // =====================================================================

    private ToolStripMenuItem BuildAutoProfileMenu()
    {
        var m = new ToolStripMenuItem(L.T("プロファイルの自動切り替え"))
        {
            ToolTipText = L.T("接続した機器や映像モードが変わったとき、それに合うプロファイルがあれば切り替えを提案します。合うものが複数あるときは何もしません。"),
        };
        void Add(string key, string label)
        {
            m.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetAutoProfileMode(key))
            { Checked = _settings.AutoProfileMode == key });
        }
        Add("off", L.T("オフ"));
        Add("ask", L.T("確認する"));
        Add("auto", L.T("自動で切り替える"));
        return m;
    }

    private void SetAutoProfileMode(string mode)
    {
        _settings.AutoProfileMode = mode;
        if (mode != "off") _apDecided = false;   // judge the input that is there now
        SaveSettings();
        ShowOsd(L.F("プロファイルの自動切り替え: {0}", mode switch
        {
            "off" => L.T("オフ"),
            "auto" => L.T("自動で切り替える"),
            _ => L.T("確認する"),
        }));
    }

    // =====================================================================
    //  For the test mode
    // =====================================================================

    /// <summary>The current signature and the profile that would be chosen for it (test mode / diagnostics).</summary>
    internal string DescribeInputMatch(out InputSignature? live, out string chosen)
    {
        live = LiveSignature(out bool settled);
        chosen = "—";
        if (live == null) return "映像が動いていません";
        VideoProfile? p = ChooseProfileFor(live, out string why);
        chosen = p?.Name ?? "（切り替えなし）";
        return $"{live}{(settled ? "" : "（細かさを判定中）")} / {why}";
    }
}
