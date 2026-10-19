using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YuCap;

/// <summary>
/// 自分専用モデル: 学習用の絵を集める. While the game picture is live and moving, saves a full-size source frame (the oriented
/// picture before any scaling, lossless PNG) every few seconds into a folder, for tools/train_upscaler.py. Off unless the user turns it on.
/// <list type="bullet">
/// <item>Only real game pictures: not the capture card's own message / black screen, not flat pictures, not a picture that has not moved
/// since the last look (a paused game, a static menu or a HUD-only screen), not a picture that looks like one of the last few saved.</item>
/// <item>Never on the render thread: the render thread only queues a GPU copy (<see cref="IVideoEngine.GrabOrientedFrame"/>, the same
/// route as the native-resolution analysis); the pixels are read, judged and compressed on this thread (below normal priority).</item>
/// <item>Each saved picture gets a line in <c>index.jsonl</c> next to it: file, size, the estimated native resolution (vertical / horizontal,
/// ResEstimate) and the time, so training can prefer pictures whose detail really is 1080p over stretched 720p.</item>
/// <item>A size cap (default 5 GB): the oldest pictures are deleted first. Also stops below 2 GB free on the drive.</item>
/// </list>
/// </summary>
internal sealed class TrainCapture : IDisposable
{
    public const int IntervalMs = 4000;
    public const long MinFreeBytes = 2L << 30;
    private const double MovingMin = 1.5;      // mean thumbnail change (0..255 levels) between two looks that counts as "moving"
    private const double DuplicateMax = 3.0;   // closer than this to one of the recent saves: the same view again
    private const int ThumbW = 64, ThumbH = 36, RecentKeep = 64;

    private readonly Func<Bitmap?> _grab;
    private readonly Func<bool> _isLive;
    private readonly Func<string> _folder;
    private readonly Func<long> _capBytes;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _count, _bytes;
    private string _curFolder = "";
    private volatile string _state = "";
    private readonly List<float[]> _recent = new();
    private float[]? _prevLook;
    private readonly Random _rng = new();

    /// <summary>Pictures in the current folder (saved earlier or now).</summary>
    public long Count => Interlocked.Read(ref _count);
    public long Bytes => Interlocked.Read(ref _bytes);
    public string Folder => _curFolder;
    /// <summary>What the collector is doing: "" (collecting), or the reason it is waiting (no signal, nothing moving, disk full...).</summary>
    public string State => _state;

    public TrainCapture(Func<Bitmap?> grab, Func<bool> isLive, Func<string> folder, Func<long> capBytes)
    {
        _grab = grab; _isLive = isLive; _folder = folder; _capBytes = capBytes;
        _thread = new Thread(Loop) { IsBackground = true, Name = "YuCap.TrainCapture", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Dispose() { _stop = true; }

    private void Loop()
    {
        long nextAt = Environment.TickCount64 + 2000;
        while (!_stop)
        {
            Thread.Sleep(200);
            long now = Environment.TickCount64;
            string folder;
            try { folder = _folder(); } catch { continue; }
            if (folder != _curFolder) { Rescan(folder); _prevLook = null; _recent.Clear(); }
            if (now < nextAt) continue;
            nextAt = now + IntervalMs + _rng.Next(0, 1000);
            try { Step(); }
            catch (Exception ex) { Log.Info("train capture: " + ex.Message.Split('\n')[0]); _state = "error"; }
        }
    }

    private void Rescan(string folder)
    {
        _curFolder = folder;
        long n = 0, b = 0;
        try
        {
            if (Directory.Exists(folder))
                foreach (var fi in new DirectoryInfo(folder).EnumerateFiles("*.png")) { n++; b += fi.Length; }
        }
        catch { }
        Interlocked.Exchange(ref _count, n); Interlocked.Exchange(ref _bytes, b);
    }

    private void Step()
    {
        if (!_isLive()) { _state = "no signal"; _prevLook = null; return; }
        string folder = _curFolder;
        try
        {
            Directory.CreateDirectory(folder);
            string root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
            if (new DriveInfo(root).AvailableFreeSpace < MinFreeBytes) { _state = "disk almost full"; return; }
        }
        catch (Exception ex) { _state = "folder"; Log.Info("train capture: folder unusable - " + ex.Message.Split('\n')[0]); return; }

        using Bitmap? bmp = _grab();
        if (bmp == null) { _state = "no picture"; return; }
        float[] y = ResEstimate.LumaOf(bmp, out int w, out int h);
        if (ResEstimate.LooksLikeCardMessage(y, w, h) || ResEstimate.MeanGradient(y, w, h) < ResEstimate.MinDetail) { _state = "no game picture"; _prevLook = null; return; }
        float[] th = Thumb(y, w, h);
        if (_prevLook == null) { _prevLook = th; _state = "looking"; return; }      // one look is not enough to tell a moving picture from a still one
        double moved = Diff(th, _prevLook);
        _prevLook = th;
        if (moved < MovingMin) { _state = "picture not moving"; return; }       // paused, a static menu, a HUD-only screen
        foreach (float[] r in _recent)
            if (Diff(th, r) < DuplicateMax) { _state = "same view"; return; }
        _recent.Add(th);
        if (_recent.Count > RecentKeep) _recent.RemoveAt(0);

        int estV = h, estH = h;
        try
        {
            ResEstimate.FrameResult fr = ResEstimate.Analyze(y, w, h);
            estV = Math.Min(fr.Vertical.EstimateHeight, h); estH = Math.Min(fr.Horizontal.EstimateHeight, h);
        }
        catch { }

        DateTime nowUtc = DateTime.UtcNow;
        string name = nowUtc.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png";
        string path = Path.Combine(folder, name), tmp = path + ".tmp";
        bmp.Save(tmp, ImageFormat.Png);
        File.Move(tmp, path);
        long size = new FileInfo(path).Length;
        long unix = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        File.AppendAllText(Path.Combine(folder, "index.jsonl"),
            string.Format(CultureInfo.InvariantCulture, "{{\"f\":\"{0}\",\"w\":{1},\"h\":{2},\"estV\":{3},\"estH\":{4},\"t\":{5}}}\n", name, w, h, estV, estH, unix), new UTF8Encoding(false));
        Interlocked.Increment(ref _count); Interlocked.Add(ref _bytes, size);
        _state = "";
        Prune(folder, Math.Max(256L << 20, _capBytes()));
    }

    /// <summary>Over <paramref name="cap"/>: delete the oldest pictures until 5% below it, and drop their lines from the index.</summary>
    private void Prune(string folder, long cap)
    {
        if (Bytes <= cap) return;
        try
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("*.png").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long target = (long)(cap * 0.95);
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo f in files)
            {
                if (Bytes <= target) break;
                long len = f.Length;
                try { f.Delete(); gone.Add(f.Name); Interlocked.Decrement(ref _count); Interlocked.Add(ref _bytes, -len); } catch { }
            }
            string idx = Path.Combine(folder, "index.jsonl");
            if (gone.Count > 0 && File.Exists(idx))
            {
                var keep = File.ReadAllLines(idx, Encoding.UTF8).Where(l =>
                {
                    int i = l.IndexOf("\"f\":\"", StringComparison.Ordinal);
                    if (i < 0) return false;
                    int j = l.IndexOf('"', i + 5);
                    return j > i && !gone.Contains(l.Substring(i + 5, j - i - 5));
                });
                File.WriteAllLines(idx, keep, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) { Log.Info("train capture: pruning failed - " + ex.Message.Split('\n')[0]); }
    }

    /// <summary>Mean of each cell of a ThumbW x ThumbH grid over the luma picture.</summary>
    private static float[] Thumb(float[] y, int w, int h)
    {
        var t = new float[ThumbW * ThumbH];
        for (int ty = 0; ty < ThumbH; ty++)
            for (int tx = 0; tx < ThumbW; tx++)
            {
                int x0 = tx * w / ThumbW, x1 = Math.Max(x0 + 1, (tx + 1) * w / ThumbW), y0 = ty * h / ThumbH, y1 = Math.Max(y0 + 1, (ty + 1) * h / ThumbH);
                double s = 0; int n = 0;
                for (int j = y0; j < y1; j += 2)
                    for (int i = x0; i < x1; i += 2) { s += y[j * w + i]; n++; }
                t[ty * ThumbW + tx] = (float)(s / Math.Max(1, n));
            }
        return t;
    }

    private static double Diff(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    /// <summary>Write (or complete) <c>index.jsonl</c> for pictures that are already in <paramref name="dir"/> (the collector writes it itself;
    /// this is for pictures from elsewhere, e.g. snapshots): the same line per picture, with the file's own time. Pictures that have a line are left alone.
    /// Returns the number of lines added.</summary>
    internal static int IndexFolder(string dir)
    {
        string idx = Path.Combine(dir, "index.jsonl");
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(idx))
            foreach (string l in File.ReadLines(idx, Encoding.UTF8))
            {
                int i = l.IndexOf("\"f\":\"", StringComparison.Ordinal);
                if (i < 0) continue;
                int j = l.IndexOf('"', i + 5);
                if (j > i) have.Add(l.Substring(i + 5, j - i - 5));
            }
        int added = 0;
        foreach (string f in Directory.GetFiles(dir, "*.png").OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(f);
            if (have.Contains(name)) continue;
            try
            {
                using var raw = new Bitmap(f);
                using var bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                float[] y = ResEstimate.LumaOf(bmp, out int w, out int h);
                if (ResEstimate.LooksLikeCardMessage(y, w, h) || ResEstimate.MeanGradient(y, w, h) < ResEstimate.MinDetail) continue;
                ResEstimate.FrameResult fr = ResEstimate.Analyze(y, w, h);
                long unix = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds();
                File.AppendAllText(idx, string.Format(CultureInfo.InvariantCulture, "{{\"f\":\"{0}\",\"w\":{1},\"h\":{2},\"estV\":{3},\"estH\":{4},\"t\":{5}}}\n",
                    name, w, h, Math.Min(fr.Vertical.EstimateHeight, h), Math.Min(fr.Horizontal.EstimateHeight, h), unix), new UTF8Encoding(false));
                added++;
            }
            catch (Exception ex) { Log.Info("index: " + name + " - " + ex.Message.Split('\n')[0]); }
        }
        return added;
    }

    // ------------------------------------------------------------------ test hook

    /// <summary>Self-check of the selection logic with synthetic pictures (no GPU): moving pictures are kept, a still one, a repeat, a card
    /// message and a flat one are not, the size cap deletes the oldest. Returns a report; throws nothing.</summary>
    internal static string SelfCheck(string dir)
    {
        var sb = new StringBuilder();
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
        var seq = new Queue<Bitmap?>();
        var rng = new Random(5);
        Bitmap Scene(int seed)
        {
            var b = new Bitmap(640, 360, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(b);
            var r = new Random(seed);
            g.Clear(Color.FromArgb(r.Next(40, 200), r.Next(40, 200), r.Next(40, 200)));
            for (int i = 0; i < 60; i++)
                using (var br = new SolidBrush(Color.FromArgb(r.Next(256), r.Next(256), r.Next(256))))
                    g.FillEllipse(br, r.Next(600), r.Next(320), r.Next(10, 80), r.Next(10, 80));
            return b;
        }
        Bitmap Black() { var b = new Bitmap(640, 360, PixelFormat.Format24bppRgb); using var g = Graphics.FromImage(b); g.Clear(Color.Black); return b; }
        Bitmap Message()
        {
            var b = Black();
            using var g = Graphics.FromImage(b);
            g.DrawString("No Signal", new Font("Arial", 28), Brushes.White, 200, 150);
            return b;
        }
        bool live = true;
        long cap = 100L << 20;
        TrainCapture? tc = null;
        int calls = 0;
        // the loop is driven by hand through Step(): the thread is parked by an enormous interval, so call Step directly
        tc = new TrainCapture(() => { calls++; return seq.Count > 0 ? seq.Dequeue() : null; }, () => live, () => dir, () => cap);
        tc.Dispose();
        Thread.Sleep(300);
        tc.Rescan(dir);
        void Feed(Bitmap? b, string label)
        {
            seq.Enqueue(b);
            long before = tc!.Count;
            tc.Step();
            sb.AppendLine($"{label}: {(tc.Count > before ? "saved" : "skipped (" + tc.State + ")")}");
        }
        Feed(Scene(1), "first look (one look cannot tell moving from still)");
        Feed(Scene(2), "a different picture");
        Feed(Scene(2), "the same picture again (not moving)");
        Feed(Scene(3), "another new picture");
        Feed(Scene(2), "back to a picture saved before (same view)");
        Feed(Message(), "the card's message");
        Feed(Black(), "black");
        live = false; Feed(Scene(4), "no signal (live = false)"); live = true;
        sb.AppendLine($"files: {Directory.GetFiles(dir, "*.png").Length}, index lines: {File.ReadAllLines(Path.Combine(dir, "index.jsonl")).Length}");
        tc.Prune(dir, 15_000);
        sb.AppendLine($"after a 15 KB cap (the oldest picture goes first): files {Directory.GetFiles(dir, "*.png").Length}, index lines {File.ReadAllLines(Path.Combine(dir, "index.jsonl")).Length}, bytes {tc.Bytes}");
        return sb.ToString();
    }
}
