using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace YuCap;

//   --audio-probe <out.txt> [--seconds N] [--buffer ms] [--capbuf ms] [--poll] [--periods x] [--volume pct] [--keyword text]
// Runs the audio passthrough alone (no video, no window) from the capture card's audio to the default output and logs once a
// second: packet interval, our backlog, the device queue, the total, and the dropouts. The volume defaults to 0 so nothing is
// heard (the queue is still filled and played, so the padding and dropouts are real).
internal static partial class Program
{
    private static void RunAudioProbe(string[] args)
    {
        string outPath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : Path.Combine(Path.GetTempPath(), "yucap_audio_probe.txt");
        int seconds = 20, buffer = AudioEngine.DefaultBufferMs, volume = 0;
        string keyword = "JVA14";
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--seconds" && more) seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a == "--buffer" && more) buffer = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a == "--capbuf" && more) AudioEngine.CaptureBufferMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a == "--poll") AudioEngine.CaptureEventSync = false;
            else if (a == "--periods" && more) AudioEngine.RenderQueuePeriods = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a == "--volume" && more) volume = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a == "--keyword" && more) keyword = args[++i];
        }

        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); }
        using var audio = new AudioEngine { BufferMilliseconds = buffer };
        audio.VolumePercent = volume;
        try
        {
            var dev = AudioEngine.PickPreferred(AudioEngine.EnumerateDevices(), keyword);
            if (dev == null) { Line("no capture device"); File.WriteAllText(outPath, sb.ToString()); return; }
            Line($"capture={dev.Name}  buffer={buffer}ms capbuf={AudioEngine.CaptureBufferMs}ms eventSync={AudioEngine.CaptureEventSync} periods={AudioEngine.RenderQueuePeriods} volume={volume}%");
            audio.Start(dev);
            Line($"output={audio.RenderDeviceName}  lowLatencyApi={audio.LowLatencyOutput}  period={audio.OutputPeriodMs:0.0}ms");
            Line("t   packet  backlog  deviceQ  total   underruns");
            var sums = new List<double[]>();
            for (int t = 1; t <= seconds; t++)
            {
                Thread.Sleep(1000);
                double lat = audio.LatencyMs;
                Line($"{t,2}  {audio.PacketMs,6:0.0}  {audio.BacklogMs,7:0.0}  {audio.RenderQueueMs,7:0.0}  {lat,6:0.0}  {audio.Underruns,5}{(audio.IsFaulted ? "  FAULTED" : "")}");
                if (t > 3) sums.Add(new[] { audio.PacketMs, audio.BacklogMs, audio.RenderQueueMs, lat });
            }
            if (sums.Count > 0)
            {
                var m = new double[4];
                foreach (var r in sums) for (int k = 0; k < 4; k++) m[k] += r[k] / sums.Count;
                Line($"AVG(after 3s)  packet {m[0]:0.0}  backlog {m[1]:0.0}  deviceQ {m[2]:0.0}  total {m[3]:0.0}  underruns {audio.Underruns}");
            }
        }
        catch (Exception ex) { Line("ERROR: " + ex); }
        finally { audio.Stop(); }
        File.WriteAllText(outPath, sb.ToString());
    }
}
