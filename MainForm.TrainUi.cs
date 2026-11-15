using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>
/// 自分専用モデルをアプリの中で学習する: 「学習を始める...」のダイアログ、経過の窓（<see cref="TrainWindow"/>）、環境づくり、中止、
/// 学習中に映像の重い処理を止める仕組み。学習そのものは別プロセス（<see cref="TrainJob"/>）で動くので、画面は止まらない。
/// </summary>
public sealed partial class MainForm
{
    private TrainJob? _trainJob;
    private TrainWindow? _trainWin;
    private bool _trainHold;                    // 学習中は映像の重い処理を止める: ApplyRenderOptions forces 標準 / 補間オフ / ぼやけ取りオフ while true (the settings are untouched)
    private bool _trainSetupRunning;
    private int _trainCountCache = -1;
    private readonly List<ToolStripMenuItem> _trainStartItems = new();
    private readonly List<ToolStripMenuItem> _trainSetupItems = new();
    private readonly List<ToolStripMenuItem> _trainPauseItems = new();
    private readonly List<ToolStripMenuItem> _trainCountItems = new();
    private readonly List<ToolStripMenuItem> _trainChooseRoots = new();

    internal bool TrainRunning => _trainJob is { IsRunning: true };

    // ---- entry points -------------------------------------------------------------------------------------------------

    /// <summary>オプション → 自分専用モデル → 学習を始める...（学習中は、経過の窓を出す）。</summary>
    private void ShowTrainDialog()
    {
        if (_testRunning && Ui.DialogAudit == null) return;
        if (TrainRunning && Ui.DialogAudit == null) { ShowTrainWindow(); return; }
        if (!TrainEnv.Ready && Ui.DialogAudit == null)
        {
            if (TrainEnv.Partial) CheckPartialTrainEnv(ok => { if (ok) ShowTrainDialog(); else OfferTrainSetup(true); });
            else OfferTrainSetup(true);
            return;
        }

        using var dlg = Ui.NewDialog(L.T("自分専用モデルの学習"), 480);
        var stack = Ui.Stack();
        // A caption 4 px above its control; 16 px between the groups.
        T Field<T>(string caption, T control, int width96 = 0) where T : Control
        {
            stack.Add(Ui.Caption(caption), before: stack.Controls.Count == 0 ? 0 : Ui.Pad);
            if (width96 > 0) Ui.Fixed(control, width96);
            return stack.Add(control, before: 4);
        }

        // 名前
        var name = Field(L.T("モデルの名前（半角の英数字と _ -）"), new TextBox { MaxLength = 32 });
        var nameNote = stack.Add(Ui.Hint(string.Empty), before: 4);

        // 大きさ
        var arch = Field(L.T("モデルの大きさ"), Ui.Combo(0, L.T("M（標準）"), L.T("L（高画質・GPU 約 1.7 倍）")));

        // 採点
        var loss = Field(L.T("採点（何を重く見て学習するか）"), Ui.Combo(0, L.T("くっきり（edge: 輪郭を強く合わせる）"), L.T("質感重視（texture: ざらつき・模様を保つ）")));

        // 時間
        var minutes = Field(L.T("学習する時間（分）"), Ui.Number(1, 600, Math.Clamp(_settings.TrainMinutes, 1, 600), 96));

        // 絵のフォルダー: one check box per folder, in a box that scrolls when there are many
        var folders = TrainEnv.DataFolders();
        var counts = folders.Select(TrainEnv.CountPictures).ToList();
        var folderStack = Ui.Stack();
        folderStack.DefaultGap = 4;
        var boxes = new List<UiCheck>();
        for (int i = 0; i < folders.Count; i++)
            boxes.Add(folderStack.Add(Ui.Check(L.F("{0}（{1} 枚）", Path.GetFileName(folders[i]), counts[i]), counts[i] > 0)));
        if (folders.Count == 0) folderStack.Add(Ui.Hint(L.T("（絵のフォルダーがありません）")));
        Field(L.T("学習に使う絵のフォルダー"), new UiScroll(folderStack) { Boxed = true, MaxHeight96 = 120 });
        var total = stack.Add(Ui.Hint(string.Empty), before: 4);

        // 重い処理を止める
        var pause = stack.Add(Ui.Check(L.T("学習中は映像の重い処理を止める"), _settings.TrainPauseHeavy), before: Ui.Pad);
        var heavy = HeavyNow();
        string gpu = L.T("学習中は GPU を多く使います（ゲーム中は避けてください）。");
        if (heavy.Length > 0) gpu += " " + L.F("今は {0} が働いているので、GPU を分け合います。上のチェックを入れると、学習のあいだ止めて終わったら元に戻します。", heavy);
        stack.Add(Ui.Hint(gpu), before: 2, indent: 24);

        // 初期値
        char a0 = _settings.TrainArch == "L" ? 'L' : 'M';
        arch.SelectedIndex = a0 == 'L' ? 1 : 0;
        loss.SelectedIndex = _settings.TrainLoss == "texture" ? 1 : 0;
        name.Text = TrainEnv.SuggestName(a0);
        bool nameEdited = false;
        name.TextChanged += (_, _) => { if (name.Focused) nameEdited = true; };
        void UpdateNote()
        {
            string n = name.Text.Trim();
            string? prob = TrainEnv.NameProblem(n);
            nameNote.Text = prob ?? (File.Exists(UserModels.PathOf(n)) ? L.T("同じ名前のモデルがあります。学習し直すと置き換わります。") : string.Empty);
            Ui.Show(nameNote, nameNote.Text.Length > 0);
        }
        name.TextChanged += (_, _) => UpdateNote();
        arch.SelectedIndexChanged += (_, _) => { if (!nameEdited) { name.Text = TrainEnv.SuggestName(arch.SelectedIndex == 1 ? 'L' : 'M'); nameEdited = false; } };
        void UpdateTotal()
        {
            int n = 0;
            for (int i = 0; i < folders.Count; i++) if (boxes[i].Checked) n += counts[i];
            total.Text = L.F("使う絵: 合計 {0} 枚", n) + (n > 0 && n < 40 ? "  " + L.T("（少なすぎます。数百枚以上が目安です）") : string.Empty);
        }
        foreach (UiCheck b in boxes) b.CheckedChanged += (_, _) => UpdateTotal();
        UpdateNote(); UpdateTotal();

        var ok = Ui.FrameOkCancel(dlg, stack, L.T("学習を始める"));
        dlg.InitialFocus = name;
        ok.Click += (_, _) =>
        {
            string n = name.Text.Trim();
            string? prob = TrainEnv.NameProblem(n);
            var dirs = new List<string>();
            for (int i = 0; i < folders.Count; i++) if (boxes[i].Checked && counts[i] > 0) dirs.Add(folders[i]);
            string? err = prob ?? (dirs.Count == 0 ? L.T("学習に使う絵がありません。映像 → 自分専用モデル → 学習用の絵を集める をオンにして遊んでから、もう一度どうぞ。") : null);
            if (err != null)
            {
                Ui.Message(dlg, err, MsgKind.Info);
                dlg.DialogResult = DialogResult.None;
                return;
            }
            if (File.Exists(UserModels.PathOf(n)) &&
                Ui.Message(dlg, L.F("「{0}」というモデルがあります。学習し直して置き換えますか？", n), MsgKind.Question, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                dlg.DialogResult = DialogResult.None;
                return;
            }
            int pics = dirs.Sum(TrainEnv.CountPictures);
            if (pics < 40 &&
                Ui.Message(dlg, L.F("絵が {0} 枚しかありません。少ないと効果が出ないか、逆に悪くなります。それでも始めますか？", pics), MsgKind.Warning, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                dlg.DialogResult = DialogResult.None;
                return;
            }
            _trainPending = new TrainOptions
            {
                Name = n, Arch = arch.SelectedIndex == 1 ? 'L' : 'M', Loss = loss.SelectedIndex == 1 ? "texture" : "edge",
                Minutes = (double)minutes.Value, DataDirs = dirs,
            };
        };
        _trainPending = null;
        if (dlg.ShowDialog(this) != DialogResult.OK || _trainPending == null) return;
        TrainOptions o = _trainPending;
        _trainPending = null;
        _settings.TrainMinutes = (int)o.Minutes;
        _settings.TrainArch = o.Arch.ToString();
        _settings.TrainLoss = o.Loss;
        _settings.TrainPauseHeavy = pause.Checked;
        MarkSettingsDirty();
        UpdateTrainChecks();
        StartTraining(o);
    }

    private TrainOptions? _trainPending;

    /// <summary>Test mode (section 9): the progress window is laid out with made-up numbers in the longest states and handed to the dialog audit.</summary>
    private void AuditTrainWindow(bool logOpen)
    {
        if (Ui.DialogAudit == null) return;
        var v = new TrainView
        {
            Mode = logOpen ? TrainView.ModeKind.Running : TrainView.ModeKind.Done,
            Title = "mine_l（L・" + L.T("質感重視") + "）",
            Status = logOpen ? L.F("{0:0.0} / {1:0.#} 分（{2}%）　残り約 {3:0} 分", 12.3, 30, 41, 18) : L.F("学習が終わりました（{0:0.#} 分）。", 30),
            Note = logOpen ? L.F("今のところ: {0}", L.F("元の Anime4K より PSNR {0} dB / SSIM {1}", "+0.17", "+0.0021")) + "\n" + L.T("学習のあいだは、映像の重い処理（くっきり・補間など）を止めています。終わると元に戻ります。")
                : TrainJob.Verdict(new TrainStatus { FinalYours = new TrainScore(33.2, 0.9512), FinalOriginal = new TrainScore(33.03, 0.9491) }, out _) + "\n" + L.T("PSNR・SSIM は、大きいほど元の絵に近いことを表します（検証用に取り分けた絵で測っています）。"),
            Percent = logOpen ? 41 : 100,
            Bicubic = new TrainScore(31.12, 0.9302), Original = new TrainScore(33.03, 0.9491), Yours = new TrainScore(33.2, 0.9512),
            CanUse = !logOpen,
            Log = "it   500  1.2min lr 1.00e-04 loss 0.02134 | val PSNR yours 33.150 orig 33.030 bicubic 31.120 | SSIM yours 0.9500 orig 0.9491",
        };
        using var w = new TrainWindow(() => v, () => { }, () => { }, () => { });
        w.Render(v);
        if (logOpen) w.SetLogVisible(true);
        Ui.DialogAudit(w);
    }

    /// <summary>Names of the heavy things that are on now (for the dialog's note), "" when none.</summary>
    private string HeavyNow()
    {
        var l = new List<string>();
        if (_video.IsRunning && _video.Quality is ScalingQuality.Anime4K or ScalingQuality.NvRtx) l.Add(L.T("拡大"));
        if (_video.IsRunning && _settings.Interpolation && _video.SupportsInterpolation) l.Add(L.T("フレーム補間"));
        return string.Join(L.Sep, l);
    }

    // ---- start / end -------------------------------------------------------------------------------------------------

    private void StartTraining(TrainOptions o)
    {
        if (TrainRunning) { ShowTrainWindow(); return; }
        try { BeginTraining(o, finalize: false); }
        catch (Exception ex)
        {
            Log.Info("train: start failed - " + ex);
            Ui.Message(this, L.F("学習を始められませんでした。\n\n{0}", Errors.Describe(ex)), MsgKind.Error);
            return;
        }
        ShowTrainWindow();
        ShowOsd(L.T("学習を始めました"));
    }

    /// <summary>Start the python process (train, or finalize for a stopped run), hold the heavy video processing, and keep the job. No windows - the test mode uses this too.</summary>
    private TrainJob BeginTraining(TrainOptions o, bool finalize)
    {
        var job = new TrainJob(o);
        job.Ended += code => { try { BeginInvoke(new Action(() => { if (finalize) job.RefreshResult(); OnTrainEnded(job, code); })); } catch { /* the window is closing */ } };
        job.Start(finalize);
        _trainJob?.Dispose();
        _trainJob = job;
        SetTrainHold(_settings.TrainPauseHeavy);
        UpdateTrainChecks();
        return job;
    }

    /// <summary>On the UI thread: the python process ended (finished, failed, or was stopped).</summary>
    private void OnTrainEnded(TrainJob job, int code)
    {
        if (!ReferenceEquals(job, _trainJob)) return;
        SetTrainHold(false);
        UpdateTrainChecks();
        TrainStatus s = job.Snapshot();
        if (_testRunning) return;
        if (s.Phase == TrainStatus.PhaseKind.Done)
        {
            ShowOsd(L.F("学習が終わりました（{0}）", job.Options.Name), OsdLongMilliseconds, ShowTrainWindow);
            if (!_isFullscreen) ShowTrainWindow();
        }
        else if (s.Phase == TrainStatus.PhaseKind.Failed)
        {
            ShowOsd(L.T("学習が途中で止まりました"), OsdLongMilliseconds, ShowTrainWindow);
            if (!_isFullscreen) ShowTrainWindow();
        }
        _trainWin?.RefreshNow();
    }

    /// <summary>Hold (or release) the heavy video processing for the length of a training run.</summary>
    private void SetTrainHold(bool hold)
    {
        if (_trainHold == hold) return;
        _trainHold = hold;
        Log.Info("train: heavy video processing " + (hold ? "held (standard scaling, interpolation off)" : "released"));
        ApplyRenderOptions();
        UpdateChecks();
        UpdateStatus();
    }

    private void CancelTraining()
    {
        TrainJob? job = _trainJob;
        if (job == null || !job.IsRunning) return;
        string msg = job.Finalizing ? L.T("仕上げを中止しますか？") : L.T("学習を中止しますか？");
        if (Ui.Message(_trainWin ?? (IWin32Window)this, msg, MsgKind.Question, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!job.IsRunning) return;
        bool wasTraining = !job.Finalizing;
        job.Cancel();
        _ = Task.Run(() =>
        {
            bool gone = job.WaitGone(8000);
            Log.Info("train: cancelled, process tree " + (gone ? "gone" : "STILL RUNNING"));
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(job, _trainJob)) return;
                    if (wasTraining && job.HasBestCheckpoint &&
                        Ui.Message(_trainWin ?? (IWin32Window)this, L.T("ここまでで一番よかった状態を、モデルとして残しますか？\n（検証に少し時間がかかります）"), MsgKind.Question,
                            MessageBoxButtons.YesNo) == DialogResult.Yes)
                        FinalizeTraining(job.Options);
                    _trainWin?.RefreshNow();
                }));
            }
            catch { /* closing */ }
        });
    }

    /// <summary>A stopped run: validate the best checkpoint it saved and write the model (the script's <c>finalize</c>).</summary>
    private void FinalizeTraining(TrainOptions o)
    {
        try
        {
            BeginTraining(o, finalize: true);
            _trainWin?.RefreshNow();
        }
        catch (Exception ex)
        {
            Log.Info("train: finalize failed - " + ex);
            Ui.Message(this, L.F("学習を始められませんでした。\n\n{0}", Errors.Describe(ex)), MsgKind.Error);
        }
    }

    private void UseTrainedModel()
    {
        TrainJob? job = _trainJob;
        if (job == null) return;
        string name = job.Options.Name;
        if (UserModels.Resolve(name) == null)
        {
            Ui.Message(this, L.T("モデルのファイルが見つかりません。"), MsgKind.Warning);
            return;
        }
        SetUserModel(name);
    }

    private void ShowTrainWindow()
    {
        if (IsDisposed) return;
        if (_trainWin == null || _trainWin.IsDisposed)
        {
            _trainWin = new TrainWindow(MakeTrainView, CancelTraining, UseTrainedModel, () => OpenFolder(UserModels.ModelsDir, null));
            _trainWin.FormClosed += (_, _) => _trainWin = null;
            _trainWin.Show(this);       // owned: stays above the main window, even a maximised or fullscreen one
        }
        if (_trainWin.WindowState == FormWindowState.Minimized) _trainWin.WindowState = FormWindowState.Normal;
        _trainWin.RefreshNow();
        _trainWin.Activate();
    }

    /// <summary>The numbers and words the progress window shows, worked out from the job.</summary>
    private TrainView MakeTrainView()
    {
        TrainJob? job = _trainJob;
        var v = new TrainView();
        if (job == null)
        {
            v.Mode = TrainView.ModeKind.Idle;
            v.Status = L.T("学習はしていません。");
            return v;
        }
        TrainStatus s = job.Snapshot();
        TrainOptions o = job.Options;
        v.Title = L.F("{0}（{1}・{2}）", o.Name, o.Arch, o.Loss == "texture" ? L.T("質感重視") : L.T("くっきり"));
        v.Log = job.LogText;
        v.Bicubic = s.Bicubic; v.Original = s.Original;
        v.Yours = s.Phase is TrainStatus.PhaseKind.Done && s.FinalYours != null ? s.FinalYours : s.Best;
        if (s.Phase is TrainStatus.PhaseKind.Done && s.FinalOriginal != null) v.Original = s.FinalOriginal;
        if (s.Phase is TrainStatus.PhaseKind.Done && s.FinalBicubic != null) v.Bicubic = s.FinalBicubic;
        double total = o.Minutes;
        switch (s.Phase)
        {
            case TrainStatus.PhaseKind.Starting:
                v.Mode = TrainView.ModeKind.Running;
                v.Percent = -1;
                v.Status = L.T("準備しています（絵を読み込んでいます）...");
                v.Note = HeldNote();
                break;
            case TrainStatus.PhaseKind.Training:
                {
                    double el = s.Minutes + (DateTime.UtcNow - s.MinutesAt).TotalMinutes;
                    el = Math.Clamp(el, 0, total);
                    v.Mode = TrainView.ModeKind.Running;
                    v.Percent = (int)Math.Clamp(Math.Round(100 * el / Math.Max(0.1, total)), 0, 100);
                    v.Status = L.F("{0:0.0} / {1:0.#} 分（{2}%）　残り約 {3:0} 分", el, total, v.Percent, Math.Max(0, Math.Ceiling(total - el)));
                    v.Note = (s.Best != null && s.Original != null ? L.F("今のところ: {0}", Compare(s.Best.Value, s.Original.Value)) + "\n" : string.Empty) + HeldNote();
                    break;
                }
            case TrainStatus.PhaseKind.Finishing:
                v.Mode = TrainView.ModeKind.Running;
                v.Percent = -1;
                v.Status = L.T("仕上げています（検証してモデルを書き出しています）...");
                v.Note = HeldNote();
                break;
            case TrainStatus.PhaseKind.Done:
                v.Mode = TrainView.ModeKind.Done;
                v.Percent = 100;
                v.Status = L.F("学習が終わりました（{0:0.#} 分）。", s.Minutes > 0 ? s.Minutes : total);
                v.Note = TrainJob.Verdict(s, out bool better) + "\n" + L.T("PSNR・SSIM は、大きいほど元の絵に近いことを表します（検証用に取り分けた絵で測っています）。");
                v.CanUse = UserModels.Resolve(o.Name) != null;
                _ = better;
                break;
            case TrainStatus.PhaseKind.Cancelled:
                v.Mode = TrainView.ModeKind.Cancelled;
                v.Percent = 0;
                v.Status = L.T("学習を中止しました。");
                break;
            default:
                v.Mode = TrainView.ModeKind.Failed;
                v.Percent = 0;
                v.Status = L.T("学習が途中で止まりました。");
                v.Note = L.F("最後の出力: {0}", string.IsNullOrWhiteSpace(s.LastLine) ? "-" : s.LastLine) + "\n" + L.T("「詳しい経過」を開くと、原因が分かることがあります。");
                break;
        }
        if (s.Phase is TrainStatus.PhaseKind.Cancelled && job.IsRunning) v.Status = L.T("止めています...");
        return v;
    }

    private string HeldNote() => _trainHold ? L.T("学習のあいだは、映像の重い処理（くっきり・補間など）を止めています。終わると元に戻ります。") : string.Empty;

    private static string Compare(TrainScore y, TrainScore o) =>
        L.F("元の Anime4K より PSNR {0} dB / SSIM {1}",
            (y.Psnr - o.Psnr).ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture),
            (y.Ssim - o.Ssim).ToString("+0.0000;-0.0000;0.0000", System.Globalization.CultureInfo.InvariantCulture));

    // ---- environment ---------------------------------------------------------------------------------------------------

    /// <summary>学習の準備をする: explains exactly what is downloaded and asks, then runs tools\setup_train_env.ps1 in a visible console.</summary>
    /// <param name="envChecked">True when a venv left without the ready marker was already checked (and found incomplete) just now.</param>
    private void OfferTrainSetup(bool envChecked = false)
    {
        if (_trainSetupRunning) { ShowOsd(L.T("学習の準備を実行中です（黒い窓を見てください）")); return; }
        if (TrainEnv.Ready)
        {
            Ui.Message(this, L.T("学習の準備はできています。"), MsgKind.Info);
            return;
        }
        if (TrainEnv.Partial && !envChecked)
        {
            CheckPartialTrainEnv(ok =>
            {
                if (ok) Ui.Message(this, L.T("学習の準備はできています。"), MsgKind.Info);
                else OfferTrainSetup(true);
            });
            return;
        }
        string venv = Path.Combine(UserModels.Root, "venv");
        string text = (TrainEnv.Partial ? L.T("前回の学習の準備が最後まで終わっていません（PyTorch などを読み込めません）。準備をやり直します。") + "\n\n" : string.Empty)
            + L.T("自分専用モデルを学習するための環境を作ります。次のものをダウンロードします。") + "\n\n"
            + L.T("・PyTorch（NVIDIA GPU 用の CUDA 版）、numpy、pillow") + "\n"
            + L.T("・取得元: download.pytorch.org（PyTorch）と PyPI（numpy・pillow）") + "\n"
            + L.T("・大きさ: 約 3 GB（数分から数十分）") + "\n"
            + L.F("・入れる場所: {0}", venv) + "\n"
            + L.T("・消すとき: そのフォルダーを削除すれば元に戻ります（システムの Python は変えません）") + "\n"
            + L.T("・必要なもの: Python 3.10〜3.12 と NVIDIA の GPU") + "\n\n"
            + L.T("黒い窓で進み具合が表示されます。続けますか？");
        if (Ui.Message(this, text, MsgKind.Question, MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            _trainSetupRunning = true;
            TrainEnv.RunSetup(code =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _trainSetupRunning = false;
                        UpdateTrainChecks();
                        if (code == 0 && TrainEnv.Ready) ShowOsd(L.T("学習の準備ができました"), OsdLongMilliseconds);
                        else Ui.Message(this, L.T("学習の準備が終わりませんでした。黒い窓の最後の表示を見てください。"), MsgKind.Error);
                    }));
                }
                catch { /* closing */ }
            });
        }
        catch (Exception ex)
        {
            _trainSetupRunning = false;
            Log.Info("train: setup start failed - " + ex);
            Ui.Message(this, Errors.Describe(ex), MsgKind.Warning);
        }
    }

    private bool _trainEnvChecking;

    /// <summary>A venv without the ready marker (an interrupted set-up, or one made by an older YuCap): import the packages on a
    /// worker (importing torch takes seconds) and continue on the UI thread with the result.</summary>
    private void CheckPartialTrainEnv(Action<bool> then)
    {
        ShowOsd(L.T("学習の環境を確かめています..."));
        if (_trainEnvChecking) return;
        _trainEnvChecking = true;
        Task.Run(() => TrainEnv.ConfirmByImport()).ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _trainEnvChecking = false;
                    UpdateTrainChecks();
                    if (!IsDisposed) then(ok);
                }));
            }
            catch { /* closing */ }
        }, TaskScheduler.Default);
    }

    // ---- closing -------------------------------------------------------------------------------------------------------

    /// <summary>Set while a close that the user already confirmed (the update path asks before swapping the exe) is under way,
    /// so <see cref="ConfirmCloseDuringTraining"/> does not ask a second time.</summary>
    private bool _trainCloseConfirmed;

    /// <summary>False when the user chose to keep YuCap open because a training run is going.</summary>
    private bool ConfirmCloseDuringTraining()
    {
        if (_trainCloseConfirmed) return true;    // already asked (before an update swapped the exe)
        if (!TrainRunning || _testRunning) return true;
        if (Ui.Message(this, L.T("学習が動いています。終了すると学習は中止されます。終了しますか？"), MsgKind.Warning,
                MessageBoxButtons.YesNo, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        return true;
    }

    private void StopTrainingOnClose()
    {
        // The picture collector grabs from the video engine on its own thread: stop it before the engine goes away.
        TrainCapture? cap = _trainCap;
        _trainCap = null;
        if (cap != null && !cap.StopAndWait(2000)) Log.Info("train capture: the collector did not stop within 2 s on close");
        try { _trainWin?.Close(); } catch { /* ignore */ }
        try { _trainJob?.Dispose(); } catch { /* ignore */ }     // kills the python process tree
        _trainJob = null;
    }
}

/// <summary>What the progress window shows (plain data, so the layout test can feed it made-up values).</summary>
internal sealed class TrainView
{
    public enum ModeKind { Idle, Running, Done, Failed, Cancelled }
    public ModeKind Mode;
    public string Title = string.Empty, Status = string.Empty, Note = string.Empty, Log = string.Empty;
    public int Percent;                       // 0..100, -1 = no estimate yet
    public TrainScore? Bicubic, Original, Yours;
    public bool CanUse;
}

/// <summary>
/// 学習の経過の窓。操作できるまま最小化でき、メインの窓（最大化・全画面でも）の上に出る。2 回/秒で <see cref="TrainView"/> を取り直して描く。
/// 部品は Ui の縦の並び（UiStack）で並べ、文字と部品の大きさから高さが決まる（スケール 100–200% で重ならない）。
/// </summary>
internal sealed class TrainWindow : UiWindow
{
    private readonly Func<TrainView> _view;
    private readonly Label _title, _status, _note;
    private readonly UiProgress _bar = new();
    private readonly Label[,] _cells = new Label[4, 3];
    private readonly LinkLabel _logBtn;
    private readonly UiButton _cancel, _use, _folder, _close;
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private int _logLen;

    public TrainWindow(Func<TrainView> view, Action onCancel, Action onUse, Action onOpenModels)
    {
        _view = view;
        Text = L.T("自分専用モデルの学習の経過");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        ContentWidth96 = 520;
        // The icon is a GDI object of this window: released when it is disposed (it is created anew each time).
        Icon? icon = null;
        try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = icon; } catch { /* default icon */ }
        Disposed += (_, _) => icon?.Dispose();

        var stack = Ui.Stack();
        _title = stack.Add(Ui.Heading(string.Empty));
        _status = stack.Add(Ui.Text(string.Empty), before: 2);
        stack.Add(_bar, before: Ui.Gap + 2);

        // 小さな表: 方法 / PSNR / SSIM
        var grid = Ui.Grid();
        grid.RowGap = 4;
        string[] heads = { L.T("検証用の絵での点数"), "PSNR", "SSIM" };
        string[] rows = { L.T("ふつうの拡大（bicubic）"), L.T("元の Anime4K"), L.T("あなたのモデル（今の最良）") };
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                Label l = c == 0 ? Ui.Caption(r == 0 ? heads[0] : rows[r - 1]) : Ui.Fixed(Ui.Text(r == 0 ? heads[c] : "-"), 72);
                if (c > 0) l.TextAlign = ContentAlignment.MiddleRight;
                if (r == 0) l.ForeColor = Ui.Colors.SubText;
                if (r == 3) { Ui.Opt(l).FontPt = Ui.BodyPt; Ui.Opt(l).FontStyle = FontStyle.Bold; l.Font = Ui.FontAt(0, Ui.BodyPt, FontStyle.Bold); }
                _cells[r, c] = l;
            }
            grid.AddRow(_cells[r, 0], _cells[r, 1], _cells[r, 2]);
        }
        stack.Add(grid, before: Ui.Pad);
        _note = stack.Add(Ui.Hint(string.Empty), before: Ui.Gap + 4);

        _logBtn = stack.Add(Ui.Link(L.T("詳しい経過 ▼"), ToggleLog), before: Ui.Pad);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = true;
        Ui.Fixed(_log, 0, 170);
        Ui.Opt(_log).FontPt = 8.5f;   // the UI font: Consolas has no Japanese (it fell back to bitmap MS Gothic)
        stack.Add(_log, before: 4);
        Ui.Show(_log, false);

        _cancel = Ui.Btn(L.T("中止"));
        _folder = Ui.Btn(L.T("モデルのフォルダーを開く"));
        _use = Ui.Btn(L.T("このモデルを使う"));
        _close = Ui.Btn(L.T("閉じる"));
        _close.Click += (_, _) => Close();
        _cancel.Click += (_, _) => onCancel();
        _use.Click += (_, _) => onUse();
        _folder.Click += (_, _) => onOpenModels();
        Ui.Frame(this, stack, new Button[] { _cancel, _folder, _use, _close });
        AcceptButton = null;
        _cancel.Primary = false;
        _use.Primary = true;      // the action the finished run is waiting for
        CancelButton = _close;    // Esc closes the window (the training goes on)
        InitialFocus = _close;

        _timer.Tick += (_, _) => RefreshNow();
        Shown += (_, _) => { RefreshNow(); _timer.Start(); };
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    public void SetLogVisible(bool visible)
    {
        if (Ui.IsShown(_log) != visible) ToggleLog();
    }

    private void ToggleLog()
    {
        bool show = !Ui.IsShown(_log);
        Ui.Show(_log, show);
        _logBtn.Text = show ? L.T("詳しい経過 ▲") : L.T("詳しい経過 ▼");
        if (show) { _logLen = -1; RefreshNow(); }
        Fit();
    }

    private static string Fmt(double v, string f) => double.IsNaN(v) ? "-" : v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);

    private void SetRow(int r, TrainScore? s)
    {
        _cells[r, 1].Text = s is { } x ? Fmt(x.Psnr, "0.00") : "-";
        _cells[r, 2].Text = s is { } y ? Fmt(y.Ssim, "0.0000") : "-";
    }

    public void RefreshNow()
    {
        if (IsDisposed) return;
        Render(_view());
    }

    /// <summary>Show <paramref name="v"/> (also used by the layout test with made-up values).</summary>
    public void Render(TrainView v)
    {
        _title.Text = v.Title.Length > 0 ? v.Title : L.T("自分専用モデルの学習");
        _status.Text = v.Status;
        _note.Text = v.Note;
        Ui.Show(_note, v.Note.Length > 0);
        if (v.Percent < 0) _bar.Style = ProgressBarStyle.Marquee;
        else
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = Math.Clamp(v.Percent, 0, 100);
        }
        SetRow(1, v.Bicubic); SetRow(2, v.Original); SetRow(3, v.Yours);
        bool running = v.Mode == TrainView.ModeKind.Running;
        Ui.Show(_cancel, running);
        Ui.Show(_use, v.Mode == TrainView.ModeKind.Done && v.CanUse);
        Ui.Show(_folder, v.Mode == TrainView.ModeKind.Done);
        if (Ui.IsShown(_log) && v.Log.Length != _logLen)
        {
            _log.Text = v.Log;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            _logLen = v.Log.Length;
        }
        if (IsHandleCreated) Fit();
    }
}
