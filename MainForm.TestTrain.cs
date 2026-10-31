using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

// Test mode: 自分専用モデルの学習を、アプリの道筋（スクリプトの取り出し → python の起動 → 中止）で試す。
// ユーザーの絵・モデルのフォルダーは使わない: YUCAP_TRAIN_DIR を一時フォルダーに向け、python だけ本物の venv を借りる。
public sealed partial class MainForm
{
    private async Task FeatureTrain(TestSection sec)
    {
        var it = new TestItem { Name = "機能の確認: 自分専用モデルの学習（スクリプトの取り出し・開始・中止）" };
        var problems = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), "YuCapTrainTest_" + Guid.NewGuid().ToString("N")[..8]);
        string? root0 = Environment.GetEnvironmentVariable("YUCAP_TRAIN_DIR"), py0 = Environment.GetEnvironmentVariable("YUCAP_TRAIN_PYTHON");
        string realPy = TrainEnv.PythonExe;
        bool heavy0 = _settings.TrainPauseHeavy, interp0 = _settings.Interpolation;
        string quality0 = _settings.ScalingQuality;
        bool haveVenv = File.Exists(realPy);
        TrainJob? job = null;
        try
        {
            Directory.CreateDirectory(tmp);
            Environment.SetEnvironmentVariable("YUCAP_TRAIN_DIR", tmp);
            Environment.SetEnvironmentVariable("YUCAP_TRAIN_PYTHON", haveVenv ? realPy : null);

            // ---- 1. the scripts are written out of the exe, and left alone when they are already there ----
            string script = await Task.Run(TrainEnv.EnsureTools);
            string[] need =
            {
                Path.Combine(TrainEnv.ToolsDir, "train_upscaler.py"), Path.Combine(TrainEnv.ToolsDir, "a4k_torch.py"), Path.Combine(TrainEnv.ToolsDir, "a4k_gen.py"),
                Path.Combine(TrainEnv.ToolsDir, "setup_train_env.ps1"),
                Path.Combine(tmp, "third_party", "Anime4K", "Anime4K_Upscale_CNN_x2_S.glsl"), Path.Combine(tmp, "third_party", "Anime4K", "Anime4K_Upscale_CNN_x2_M.glsl"),
                Path.Combine(tmp, "third_party", "Anime4K", "Anime4K_Upscale_CNN_x2_L.glsl"),
            };
            var missing = need.Where(f => !File.Exists(f)).Select(Path.GetFileName).ToList();
            if (missing.Count > 0) problems.Add("取り出せなかったファイル: " + string.Join(", ", missing));
            DateTime t1 = File.GetLastWriteTimeUtc(script);
            await Wait(30);
            await Task.Run(TrainEnv.EnsureTools);
            bool untouched = File.GetLastWriteTimeUtc(script) == t1;
            File.WriteAllText(script, "# old copy");
            await Task.Run(TrainEnv.EnsureTools);
            bool replaced = new FileInfo(script).Length > 1000;
            it.Num("スクリプトの取り出し", $"{need.Length - missing.Count}/{need.Length} 個（同じ内容なら書き直さない: {(untouched ? "はい" : "いいえ")}・古い内容は上書き: {(replaced ? "はい" : "いいえ")}）");
            if (!untouched) problems.Add("同じ内容のスクリプトを毎回書き直しています");
            if (!replaced) problems.Add("古い内容のスクリプトが上書きされませんでした");

            // ---- 2. names ----
            bool namesOk = TrainEnv.NameProblem("mine") == null && TrainEnv.NameProblem("mine_l-2") == null
                && TrainEnv.NameProblem("") != null && TrainEnv.NameProblem("a b") != null && TrainEnv.NameProblem("日本語") != null && TrainEnv.NameProblem("Orig") != null && TrainEnv.NameProblem("M") != null;
            it.Num("モデルの名前の確認", namesOk ? "半角英数字と _ - だけ・内蔵の名前は不可" : "おかしい");
            if (!namesOk) problems.Add("モデルの名前の検査が期待どおりではありません");

            if (!haveVenv)
            {
                it.Verdict = problems.Count == 0 ? Verdict.Info : Verdict.Ng;
                it.Summary = problems.Count == 0 ? "学習の環境（venv）がないため、python を動かす確認はしませんでした" : $"{problems.Count} か所に問題があります";
                it.Explanation = problems.Count == 0 ? "オプション → 自分専用モデル → 学習の準備をする で環境を作ると、開始と中止まで確認できます。" : string.Join("\n", problems);
                sec.Items.Add(it);
                return;
            }

            // ---- 3. a tiny data set (4 scenes x 3 pictures, 1280x720), a 1-minute run, started and then stopped ----
            string data = Path.Combine(tmp, "data", "test");
            Directory.CreateDirectory(data);
            await Task.Run(() => TrainEnv.MakeTinyTrainSet(data));
            var o = new TrainOptions { Name = "yctest", Arch = 'S', Loss = "edge", Minutes = 1, DataDirs = new List<string> { data } };

            _settings.TrainPauseHeavy = true;
            _settings.ScalingQuality = "anime4k";       // something heavy to hold
            _settings.Interpolation = true;
            ApplyRenderOptions();
            ScalingQuality qBefore = _video.Quality;
            bool interpBefore = _video.Interpolation;
            var sw = Stopwatch.StartNew();
            job = BeginTraining(o, finalize: false);
            bool held = _trainHold && _video.Quality == ScalingQuality.Standard && !_video.Interpolation;
            // wait for the script to get as far as reading the pictures (importing PyTorch takes a while the first time)
            bool sawFrames = false;
            while (sw.Elapsed.TotalSeconds < 150)
            {
                await Wait(250);
                if (job.LogText.Contains("frames:", StringComparison.Ordinal)) { sawFrames = true; break; }
                if (!job.IsRunning) break;
            }
            bool stillRunning = job.IsRunning;
            double startSec = sw.Elapsed.TotalSeconds;
            TrainStatus st = job.Snapshot();
            string tail = string.Join(" / ", job.LogText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(3));
            job.Cancel();
            bool gone = await Task.Run(() => job.WaitGone(10000));
            int left = job.ProcessCount;
            SetTrainHold(false);
            bool released = !_trainHold && _video.Quality == qBefore && _video.Interpolation == interpBefore;
            bool noModel = !File.Exists(Path.Combine(tmp, "models", "yctest.hlsl"));
            it.Num("開始まで", sawFrames ? $"{startSec:0} 秒で絵を読み込む段階に入りました（学習 {st.TrainFrames} 枚・検証 {st.ValFrames} 枚）" : $"絵を読み込む段階まで進みませんでした（{startSec:0} 秒）")
              .Num("中止", gone ? $"python と子プロセスが {left} 個に（Job Object で確認）" : "10 秒たっても止まりません")
              .Num("学習中の映像の重い処理", held ? "標準の拡大・補間オフに切り替わり" : "切り替わっていません")
              .Num("学習のあと", released ? "元の拡大・補間に戻りました" : "元に戻っていません");
            if (!sawFrames) problems.Add("学習を始めても絵を読み込む段階まで進みませんでした。最後の出力: " + tail);
            if (!stillRunning && sawFrames) problems.Add("中止する前に python が終わっていました: " + tail);
            if (!gone || left > 0) problems.Add($"中止したのに python が残っています（{left} 個）");
            if (!held) problems.Add("学習中に映像の重い処理が止まりませんでした（拡大 " + _video.Quality + "・補間 " + _video.Interpolation + "）");
            if (!released) problems.Add("学習のあとに映像の設定が元に戻りませんでした");
            if (!noModel) problems.Add("中止したのにモデルが書き出されています");
        }
        catch (Exception ex)
        {
            problems.Add("試験を最後まで行えませんでした: " + ex.Message);
        }
        finally
        {
            try { job?.Dispose(); } catch { /* ignore */ }
            if (ReferenceEquals(_trainJob, job)) _trainJob = null;
            if (_trainHold) SetTrainHold(false);
            _settings.TrainPauseHeavy = heavy0;
            _settings.Interpolation = interp0;
            _settings.ScalingQuality = quality0;
            try { ApplyRenderOptions(); } catch { /* ignore */ }
            Environment.SetEnvironmentVariable("YUCAP_TRAIN_DIR", root0);
            Environment.SetEnvironmentVariable("YUCAP_TRAIN_PYTHON", py0);
            try { Directory.Delete(tmp, true); } catch (Exception ex) { Log.Info("train test: temp folder not removed - " + ex.Message); }
        }
        if (problems.Count == 0) { it.Verdict = Verdict.Ok; it.Summary = "スクリプトの取り出し・学習の開始と中止・python が残らないことを確認しました"; }
        else { it.Verdict = Verdict.Ng; it.Summary = $"{problems.Count} か所に問題があります"; it.Explanation = string.Join("\n", problems); }
        sec.Items.Add(it);
    }
}
