using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// 自分専用モデル: the menu entries (オプション → 自分専用モデル, 拡大の画質 → 自分専用モデル（name）), the picture collector
/// (<see cref="TrainCapture"/>) and its status-bar count. Training itself is tools/train_upscaler.py (README: 自分専用モデルを作る).
/// </summary>
public sealed partial class MainForm
{
    private readonly ToolStripStatusLabel _lblTrain = new();
    private TrainCapture? _trainCap;
    private volatile bool _trainLive;
    private long _modelCheckTick;
    private readonly List<ToolStripMenuItem> _trainCapItems = new();

    private const string UserModelTagPrefix = "user:";

    /// <summary>オプション → 自分専用モデル.</summary>
    private ToolStripMenuItem BuildTrainSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("自分専用モデル"));
        var collect = new ToolStripMenuItem(L.T("学習用の絵を集める"), null, (_, _) => ToggleTrainCapture())
        {
            ToolTipText = L.T("遊んでいる間、動いているゲームの絵を数秒おきにそのままの大きさで保存します（オフが標準）。保存した絵は、自分専用の拡大モデルを作るための学習に使います。作り方は README の「自分専用モデルを作る」。"),
        };
        _trainCapItems.Add(collect);
        m.DropDownItems.Add(collect);
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("学習用の絵のフォルダーを開く"), null, (_, _) => OpenFolder(UserModels.DataDirFor(_settings.ActiveProfile), null)));
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("自分専用モデルのフォルダーを開く"), null, (_, _) => OpenFolder(UserModels.ModelsDir, null)));
        m.DropDownOpening += (_, _) => UpdateTrainChecks();
        return m;
    }

    private void UpdateTrainChecks()
    {
        foreach (var i in _trainCapItems) i.Checked = _settings.TrainCapture;
    }

    private void ToggleTrainCapture()
    {
        _settings.TrainCapture = !_settings.TrainCapture;
        MarkSettingsDirty();
        ApplyTrainFeatures();
        UpdateTrainChecks();
        ShowOsd(_settings.TrainCapture ? L.T("学習用の絵を集めています") : L.T("学習用の絵を集めるのをやめました"));
    }

    /// <summary>Start / stop the collector and tell the renderer which user model くっきり uses. Cheap and idempotent; called from ApplyRenderOptions.</summary>
    private void ApplyTrainFeatures()
    {
        try
        {
            if (!_testRunning)
                D3DRenderer.SetA4kCustom(_settings.ScalingQuality == "anime4k" ? UserModels.Resolve(_settings.A4kModel) : null);
            bool want = _settings.TrainCapture && !_testRunning;
            if (want && _trainCap == null)
            {
                _trainCap = new TrainCapture(() => _video.GrabOrientedFrame(), () => _trainLive,
                    () => UserModels.DataDirFor(_settings.ActiveProfile), () => _settings.TrainCapGb * (1L << 30));
                Log.Info("train capture: on, folder " + UserModels.DataDirFor(_settings.ActiveProfile));
            }
            else if (!want && _trainCap != null)
            {
                _trainCap.Dispose(); _trainCap = null;
                Log.Info("train capture: off");
            }
        }
        catch (Exception ex) { Log.Info("train features: " + ex.Message.Split('\n')[0]); }
    }

    /// <summary>Status bar: 「学習用の絵: 1234 枚」 while collecting (hidden otherwise). Runs on the UI timer; also tells the collector
    /// whether a game picture is live (video running, signal present, not minimised) without it touching UI state from its thread.</summary>
    private void UpdateTrainLabel()
    {
        // a model retrained under the same name (the file changed) is picked up here, every couple of seconds: SetA4kCustom changes the cache key only when the file did
        long now = Environment.TickCount64;
        if (!_testRunning && _settings.ScalingQuality == "anime4k" && _settings.A4kModel.Length > 0 && now - _modelCheckTick > 2000)
        {
            _modelCheckTick = now;
            D3DRenderer.SetA4kCustom(UserModels.Resolve(_settings.A4kModel));
        }
        TrainCapture? tc = _trainCap;
        _trainLive = tc != null && _video.IsRunning && !NoSignalNow && !_testRunning && !_frozen && WindowState != FormWindowState.Minimized;
        bool show = tc != null;
        if (_lblTrain.Visible != show) _lblTrain.Visible = show;
        if (!show) return;
        string text = L.F("学習用の絵: {0} 枚", tc!.Count);
        if (_lblTrain.Text != text) _lblTrain.Text = text;
        string state = tc.State;
        _lblTrain.ToolTipText = L.F("学習用の絵を集めています。保存先: {0}\n{1:0.0} GB（上限 {2} GB）", tc.Folder, tc.Bytes / 1073741824.0, _settings.TrainCapGb)
            + (state.Length > 0 ? "\n" + L.F("今は待っています: {0}", StateText(state)) : string.Empty);
    }

    private static string StateText(string state) => state switch
    {
        "no signal" => L.T("信号がありません"),
        "no game picture" => L.T("ゲームの絵ではありません"),
        "picture not moving" => L.T("絵が動いていません"),
        "same view" => L.T("似た絵を保存済み"),
        "looking" => L.T("様子を見ています"),
        "disk almost full" => L.T("ディスクの空きが少ない"),
        _ => state,
    };

    // ---- 拡大の画質 → 自分専用モデル（name）: rebuilt each time the submenu opens (a model may have been trained meanwhile) ----

    /// <summary>Insert one item per trained model after the くっきり item of the quality submenu <paramref name="m"/>, now and on every opening.</summary>
    private void AttachUserModelItems(ToolStripMenuItem m, ToolStripMenuItem after)
    {
        void Refresh()
        {
            for (int i = m.DropDownItems.Count - 1; i >= 0; i--)
                if (m.DropDownItems[i].Tag is string t && t.StartsWith(UserModelTagPrefix, StringComparison.Ordinal)) m.DropDownItems.RemoveAt(i);
            int at = m.DropDownItems.IndexOf(after) + 1;
            foreach (string name in UserModels.List().Take(5))
            {
                string n = name;
                var item = new ToolStripMenuItem(L.F("自分専用モデル（{0}）", n), null, (_, _) => SetUserModel(n))
                {
                    Tag = UserModelTagPrefix + n,
                    ToolTipText = L.T("自分のゲーム画面で学習させた拡大モデルを使います。くっきりと同じ仕組みで、GPU の使い方も同じです。"),
                    Checked = _settings.ScalingQuality == "anime4k" && string.Equals(_settings.A4kModel, n, StringComparison.OrdinalIgnoreCase),
                    Enabled = _video.SupportsScalingQuality,
                };
                m.DropDownItems.Insert(at++, item);
            }
        }
        Refresh();
        m.DropDownOpening += (_, _) => Refresh();
    }

    private void SetUserModel(string name)
    {
        _settings.A4kModel = name;
        SetScalingQuality("anime4k");
    }

    /// <summary>True while くっきり runs a user-trained model (the setting names one and its file exists).</summary>
    private bool UserModelActive => _settings.ScalingQuality == "anime4k" && UserModels.Resolve(_settings.A4kModel) != null;

    private void UpdateUserModelChecks()
    {
        foreach (var root in _qualityRoots)
            foreach (ToolStripItem it in root.DropDownItems)
                if (it is ToolStripMenuItem mi && mi.Tag is string t && t.StartsWith(UserModelTagPrefix, StringComparison.Ordinal))
                    mi.Checked = UserModelActive && string.Equals(_settings.A4kModel, t.Substring(UserModelTagPrefix.Length), StringComparison.OrdinalIgnoreCase);
    }
}
