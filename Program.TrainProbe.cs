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
