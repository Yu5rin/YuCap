using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace YuCap;

// Hidden developer command for 自分専用モデル (the picture collector, TrainCapture) on the REAL capture device:
//   --train-capture-probe <dir> [--seconds 40] [--display name]
//        opens the capture card like the viewer does, lets the collector run for the given time into <dir>, and writes <dir>\probe.txt:
//        what was saved (count, size, estimated native resolution), how long each grab took on the calling thread, and the render thread's
//        presented rate / drops before and during the collection (it must not change: the render thread only queues one GPU copy per grab).
internal static partial class Program
{
    private static void RunTrainCaptureProbe(string dir, int seconds, string display)
    {
        var log = new List<string>();
        Directory.CreateDirectory(dir);
        _probeDisplay = display.Length > 0 ? display : null;
        Form form = NewProbeWindow("YuCap train-capture-probe", out VideoBox canvas);
        var engine = new D3DVideoEngine();
        form.Shown += (_, _) =>
        {
            TrainCapture? tc = null;
            try
            {
                var dev = VideoEngine.PickPreferred(VideoEngine.EnumerateDevices(), Settings.DeviceKeyword);
                if (dev == null) { log.Add("no capture device"); return; }
                engine.Attach(canvas.Handle);
                engine.Start(dev, null);
                PumpFor(800);
                CaptureMode? mode = engine.GetModes().Where(m => m.Width == 1920 && m.Height == 1080 && m.Fps == 60)
                    .OrderBy(m => m.Compression == "NV12" ? 0 : 1).FirstOrDefault();
                engine.Start(dev, mode);
                engine.SetVideoRect(new Rectangle(0, 0, canvas.ClientSize.Width, canvas.ClientSize.Height));
                PumpFor(2500);
                log.Add($"device {dev.Name}, mode {engine.CurrentMode?.Compression} {engine.CurrentResolution.Width}x{engine.CurrentResolution.Height}@{engine.NegotiatedFps}");
                RenderStats? before = engine.Stats;
                log.Add($"before: input {before?.InputFps:0.0} fps, presented {before?.PresentedFps:0.0} fps, dropped {before?.DroppedFrames}");

                var grabMs = new List<double>();
                int nulls = 0;
                tc = new TrainCapture(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Bitmap? b = engine.GrabOrientedFrame();
                    lock (grabMs) { if (b == null) nulls++; else grabMs.Add(sw.Elapsed.TotalMilliseconds); }
                    return b;
                }, () => true, () => dir, () => 5L << 30);
                PumpFor(seconds * 1000);
                RenderStats? after = engine.Stats;
                log.Add($"during: input {after?.InputFps:0.0} fps, presented {after?.PresentedFps:0.0} fps, dropped {after?.DroppedFrames} (was {before?.DroppedFrames})");
                lock (grabMs)
                    log.Add(grabMs.Count == 0 ? $"no frame grabbed ({nulls} null)" : $"{grabMs.Count} grabs ({nulls} null), each {grabMs.Average():0.0} ms on the grabbing thread (max {grabMs.Max():0.0} ms)");
                log.Add($"collector: {tc.Count} picture(s), {tc.Bytes / 1024.0 / 1024.0:0.0} MB, last state: '{tc.State}'");
                string idx = Path.Combine(dir, "index.jsonl");
                if (File.Exists(idx)) foreach (string l in File.ReadLines(idx)) log.Add("  " + l);
                foreach (string f in Directory.GetFiles(dir, "*.png"))
                {
                    using var im = new Bitmap(f);
                    log.Add($"  {Path.GetFileName(f)}: {im.Width}x{im.Height}, {new FileInfo(f).Length / 1024} KB");
                }
            }
            catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
            finally
            {
                try { tc?.Dispose(); } catch { }
                try { engine.Dispose(); } catch { }
                try { File.WriteAllLines(Path.Combine(dir, "probe.txt"), log, new UTF8Encoding(true)); } catch { }
                form.BeginInvoke(new Action(form.Close));
            }
        };
        Application.Run(form);
    }
}

// Hidden developer command for the in-app training (TrainEnv / TrainJob), without the viewer or any capture device:
//   --train-job-probe <dir>
//        works in <dir>\root (YUCAP_TRAIN_DIR) with the real training venv: writes the scripts out of the exe, makes a tiny picture set, then
//        (A) starts a run and stops it, checking that no python process is left; (B) runs a 1-minute run to the end and reads the result;
//        (C) starts a run, stops it after its first validation and turns the best checkpoint into a model (finalize). Writes <dir>\probe.txt.
internal static partial class Program
{
    private static void RunTrainJobProbe(string dir)
    {
        var log = new List<string>();
        Directory.CreateDirectory(dir);
        string root = Path.Combine(dir, "root");
        string? py = null;
        try
        {
            py = TrainEnv.PythonExe;
            if (!File.Exists(py)) { log.Add("no training venv: " + py); return; }
            Environment.SetEnvironmentVariable("YUCAP_TRAIN_PYTHON", py);
            Environment.SetEnvironmentVariable("YUCAP_TRAIN_DIR", root);
            string script = TrainEnv.EnsureTools();
            log.Add("tools: " + string.Join(", ", Directory.GetFiles(TrainEnv.ToolsDir).Select(Path.GetFileName)) + " | glsl: "
                + string.Join(", ", Directory.GetFiles(Path.Combine(root, "third_party", "Anime4K")).Select(Path.GetFileName)));
            string data = Path.Combine(root, "data", "test");
            Directory.CreateDirectory(data);
            TrainEnv.MakeTinyTrainSet(data);
            int pyBefore = System.Diagnostics.Process.GetProcessesByName("python").Length;
            log.Add($"python processes before: {pyBefore}");

            // (A) start and stop
            using (var a = new TrainJob(new TrainOptions { Name = "pa", Arch = 'S', Minutes = 1, DataDirs = new List<string> { data } }))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                a.Start();
                while (sw.Elapsed.TotalSeconds < 150 && a.IsRunning && !a.LogText.Contains("frames:")) System.Threading.Thread.Sleep(200);
                TrainStatus s = a.Snapshot();
                log.Add($"(A) after {sw.Elapsed.TotalSeconds:0.0}s: running={a.IsRunning} frames={s.Frames} train={s.TrainFrames} val={s.ValFrames} active processes in job={a.ProcessCount}");
                a.Cancel();
                bool gone = a.WaitGone(10000);
                log.Add($"(A) cancelled: gone={gone} active={a.ProcessCount} python processes now={System.Diagnostics.Process.GetProcessesByName("python").Length} phase={a.Snapshot().Phase}");
                log.Add("(A) log tail: " + string.Join(" / ", a.LogText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(3)));
            }

            // (B) a whole 1-minute run
            using (var b = new TrainJob(new TrainOptions { Name = "pb", Arch = 'S', Minutes = 1, DataDirs = new List<string> { data } }))
            {
                var done = new System.Threading.ManualResetEventSlim();
                b.Ended += _ => done.Set();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                b.Start();
                bool ended = done.Wait(TimeSpan.FromMinutes(6));
                System.Threading.Thread.Sleep(300);
                TrainStatus s = b.Snapshot();
                log.Add($"(B) ended={ended} after {sw.Elapsed.TotalSeconds:0}s phase={s.Phase} iter={s.Iter} min={s.Minutes:0.0} best={s.Best} latest={s.Latest} orig={s.Original} bicubic={s.Bicubic}");
                log.Add($"(B) final yours={s.FinalYours} orig={s.FinalOriginal} bicubic={s.FinalBicubic}");
                log.Add("(B) verdict: " + TrainJob.Verdict(s, out bool better) + $" better={better}");
                log.Add("(B) models: " + string.Join(", ", UserModels.List().Select(n => n + "(" + UserModels.ArchOf(UserModels.PathOf(n)) + ")")) + $" resolve={(UserModels.Resolve("pb") != null)}");
                log.Add($"(B) active processes in job after end: {b.ProcessCount}");
                if (!ended) log.Add("(B) log tail: " + string.Join(" / ", b.LogText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(5)));
            }

            // (C) stop after the first validation, then finalize
            var oc = new TrainOptions { Name = "pc", Arch = 'S', Minutes = 3, DataDirs = new List<string> { data } };
            using (var c = new TrainJob(oc))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                c.Start();
                while (sw.Elapsed.TotalSeconds < 240 && c.IsRunning && c.Snapshot().Iter == 0) System.Threading.Thread.Sleep(300);
                log.Add($"(C) first report after {sw.Elapsed.TotalSeconds:0}s: iter={c.Snapshot().Iter} best checkpoint={c.HasBestCheckpoint}");
                c.Cancel();
                log.Add($"(C) cancelled: gone={c.WaitGone(10000)} active={c.ProcessCount}");
                if (c.HasBestCheckpoint)
                {
                    using var f = new TrainJob(oc);
                    var done = new System.Threading.ManualResetEventSlim();
                    f.Ended += _ => done.Set();
                    f.Start(finalize: true);
                    bool ended = done.Wait(TimeSpan.FromMinutes(4));
                    System.Threading.Thread.Sleep(300);
                    f.RefreshResult();
                    TrainStatus s = f.Snapshot();
                    log.Add($"(C) finalize ended={ended} phase={s.Phase} yours={s.FinalYours} orig={s.FinalOriginal}; model resolve={(UserModels.Resolve("pc") != null)}");
                    log.Add("(C) verdict: " + TrainJob.Verdict(s, out _));
                }
            }
            log.Add($"python processes at the end: {System.Diagnostics.Process.GetProcessesByName("python").Length} (before: {pyBefore})");
            _ = script;
        }
        catch (Exception ex) { log.Add("EXCEPTION: " + ex); }
        finally
        {
            try { File.WriteAllLines(Path.Combine(dir, "probe.txt"), log, new UTF8Encoding(true)); } catch { }
        }
    }
}
