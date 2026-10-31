using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>オプション → 自分専用モデル（右クリックのメニューにも同じものが入る）.</summary>
    private ToolStripMenuItem BuildTrainSubmenu()
    {
        var m = new ToolStripMenuItem(L.T("自分専用モデル"));
        var collect = new ToolStripMenuItem(L.T("学習用の絵を集める"), null, (_, _) => ToggleTrainCapture())
        {
            ToolTipText = L.T("遊んでいる間、動いているゲームの絵を数秒おきにそのままの大きさで保存します（オフが標準）。保存した絵は、自分専用の拡大モデルを作るための学習に使います。作り方は README の「自分専用モデルを作る」。"),
        };
        _trainCapItems.Add(collect);
        m.DropDownItems.Add(collect);
        var count = new ToolStripMenuItem(TrainCountText(), null, (_, _) => OpenFolder(UserModels.DataDirFor(_settings.ActiveProfile), null)) { Name = "traincount" };
        _trainCountItems.Add(count);
        m.DropDownItems.Add(count);
        m.DropDownItems.Add(new ToolStripSeparator());

        var start = new ToolStripMenuItem(L.T("学習を始める..."), null, (_, _) => ShowTrainDialog())
        {
            Name = "trainstart",
            ToolTipText = L.T("集めた絵で、自分専用の拡大モデルを学習します。学習は別のプロセスで動くので、YuCap はそのまま使えます。"),
        };
        _trainStartItems.Add(start);
        m.DropDownItems.Add(start);
        var setup = new ToolStripMenuItem(L.T("学習の準備をする（約 3 GB ダウンロード）..."), null, (_, _) => OfferTrainSetup()) { Name = "trainsetup" };
        _trainSetupItems.Add(setup);
        m.DropDownItems.Add(setup);
        var pause = new ToolStripMenuItem(L.T("学習中は映像の重い処理を止める"), null, (_, _) => ToggleTrainPause())
        {
            ToolTipText = L.T("学習のあいだ、拡大を標準に、フレーム補間とぼやけ取りをオフにして GPU を空けます。終わると元に戻ります（設定は書き換えません）。"),
        };
        _trainPauseItems.Add(pause);
        m.DropDownItems.Add(pause);
        m.DropDownItems.Add(new ToolStripSeparator());

        var choose = new ToolStripMenuItem(L.T("自分専用モデルを選ぶ"));
        choose.DropDownItems.Add(new ToolStripMenuItem(L.T("（開くと更新）")) { Enabled = false });
        choose.DropDownOpening += (_, _) => FillTrainChoose(choose);
        _trainChooseRoots.Add(choose);
        m.DropDownItems.Add(choose);
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("自分専用モデルのフォルダーを開く"), null, (_, _) => OpenFolder(UserModels.ModelsDir, null)));
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("学習用の絵のフォルダーを開く"), null, (_, _) => OpenFolder(UserModels.DataDirFor(_settings.ActiveProfile), null)));
        m.DropDownItems.Add(new ToolStripMenuItem(L.T("学習のフォルダーを開く"), null, (_, _) => OpenFolder(UserModels.Root, null)));
        m.DropDownOpening += (_, _) => { UpdateTrainChecks(); RefreshTrainCount(); };
        return m;
    }

    private string TrainCountText() =>
        _trainCountCache >= 0 ? L.F("集めた絵: {0} 枚（フォルダーを開く）", _trainCountCache) : L.T("集めた絵（フォルダーを開く）");

    /// <summary>Count the collected pictures of the active profile off the UI thread, then show the number in the menu.</summary>
    private void RefreshTrainCount()
    {
        string dir = UserModels.DataDirFor(_settings.ActiveProfile);
        _ = Task.Run(() =>
        {
            int n = TrainEnv.CountPictures(dir);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _trainCountCache = n;
                    foreach (var i in _trainCountItems) i.Text = TrainCountText();
                }));
            }
            catch { /* closing */ }
        });
    }

    /// <summary>自分専用モデルを選ぶ: the trained models, newest first (each with its layout), plus the way back to the built-in くっきり.</summary>
    private void FillTrainChoose(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        bool crisp = _settings.ScalingQuality == "anime4k";
        root.DropDownItems.Add(new ToolStripMenuItem(L.T("内蔵のくっきり"), null, (_, _) => { _settings.A4kModel = ""; SetScalingQuality("anime4k"); })
        {
            Checked = crisp && !UserModelActive,
            Enabled = _video.SupportsScalingQuality,
        });
        List<string> names = UserModels.List();
        if (names.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem(L.T("（まだありません。学習を始めると作れます）")) { Enabled = false });
            return;
        }
        root.DropDownItems.Add(new ToolStripSeparator());
        foreach (string name in names)
        {
            string n = name;
            char arch = UserModels.ArchOf(UserModels.PathOf(n));
            root.DropDownItems.Add(new ToolStripMenuItem($"{n}（{arch}）", null, (_, _) => SetUserModel(n))
            {
                Checked = crisp && string.Equals(_settings.A4kModel, n, StringComparison.OrdinalIgnoreCase),
                Enabled = _video.SupportsScalingQuality,
                ToolTipText = L.F("{0} 形のモデル（{1}）", arch, File.GetLastWriteTime(UserModels.PathOf(n)).ToString("yyyy-MM-dd HH:mm")),
            });
        }
    }

    private void ToggleTrainPause()
    {
        _settings.TrainPauseHeavy = !_settings.TrainPauseHeavy;
        MarkSettingsDirty();
        if (TrainRunning) SetTrainHold(_settings.TrainPauseHeavy);
        UpdateTrainChecks();
    }

    private void UpdateTrainChecks()
    {
        foreach (var i in _trainCapItems) i.Checked = _settings.TrainCapture;
        foreach (var i in _trainPauseItems) i.Checked = _settings.TrainPauseHeavy;
        bool running = TrainRunning;
        foreach (var i in _trainStartItems)
            i.Text = running ? L.T("学習の経過を見る...") : L.T("学習を始める...");
        bool ready = TrainEnv.Ready;
        foreach (var i in _trainSetupItems)
        {
            i.Enabled = !ready && !_trainSetupRunning;
            i.ToolTipText = ready ? L.T("学習の準備はできています。") : L.F("PyTorch などを {0} に入れます。押すと、入れるものを確かめてから始めます。", Path.Combine(UserModels.Root, "venv"));
        }
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
        if (_trainJob is { IsRunning: true } tj)
        {
            // a training run is going: its progress takes the place of the picture count
            TrainStatus st = tj.Snapshot();
            double total = tj.Options.Minutes;
            double el = st.Phase == TrainStatus.PhaseKind.Training ? Math.Clamp(st.Minutes + (DateTime.UtcNow - st.MinutesAt).TotalMinutes, 0, total) : 0;
            string t = st.Phase == TrainStatus.PhaseKind.Training ? L.F("学習中: {0:0} / {1:0.#} 分", el, total)
                : st.Phase == TrainStatus.PhaseKind.Finishing ? L.T("学習: 仕上げ中") : L.T("学習: 準備中");
            if (!_lblTrain.Visible) _lblTrain.Visible = true;
            if (_lblTrain.Text != t) _lblTrain.Text = t;
            _lblTrain.ToolTipText = L.T("自分専用モデルを学習しています。クリックで経過の窓を開きます。");
            return;
        }
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
