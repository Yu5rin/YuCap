using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice.DXGI;

namespace YuCap;

// Measuring helpers for the test mode (テストモード): system information, frame-pacing analysis, the edge
// metric used to compare scalers, and screen sampling. Report wording is Japanese only on purpose
// (see TestReport.cs); nothing here is routed through L.T.

internal sealed record GpuInfo(string Name, string DriverVersion, string DriverDate, string Vendor, long DedicatedMb);

internal static class TestProbes
{
    // ================================================================ system

    public static string OsDescription()
    {
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k != null)
            {
                string name = k.GetValue("ProductName") as string ?? "Windows";
                string disp = k.GetValue("DisplayVersion") as string ?? string.Empty;
                string build = k.GetValue("CurrentBuildNumber") as string ?? string.Empty;
                object? ubr = k.GetValue("UBR");
                // "Windows 10 Pro" is what the registry still says on Windows 11; the build number tells them apart.
                if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                return $"{name} {disp} (build {build}{(ubr != null ? "." + ubr : "")}) {(Environment.Is64BitOperatingSystem ? "64bit" : "32bit")}";
            }
        }
        catch { }
        return Environment.OSVersion.ToString();
    }

    /// <summary>Adapter 0 (the one the renderer's default device lives on), with the driver version from the
    /// display-class registry key that matches its PCI ids.</summary>
    public static GpuInfo QueryGpu()
    {
        string name = "?", vendor = "?", ver = "?", date = "?";
        long mb = 0;
        try
        {
            using IDXGIFactory1 f = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (f.EnumAdapters1(0, out IDXGIAdapter1? a).Success && a != null)
            {
                using (a)
                {
                    AdapterDescription1 d = a.Description1;
                    name = d.Description;
                    mb = (long)(d.DedicatedVideoMemory / (1024UL * 1024UL));
                    vendor = d.VendorId switch
                    {
                        0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", 0x1414 => "Microsoft", _ => $"0x{d.VendorId:X4}",
                    };
                    string want = $"VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4}";
                    using RegistryKey? cls = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                    if (cls != null)
                    {
                        foreach (string sub in cls.GetSubKeyNames())
                        {
                            using RegistryKey? k = cls.OpenSubKey(sub);
                            string id = (k?.GetValue("MatchingDeviceId") as string) ?? string.Empty;
                            if (k == null || !id.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
                            ver = k.GetValue("DriverVersion") as string ?? ver;
                            date = k.GetValue("DriverDate") as string ?? date;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Info("test: gpu query failed - " + ex.Message); }
        return new GpuInfo(name, ver, date, vendor, mb);
    }

    public static bool? TearingSupported()
    {
        try
        {
            using IDXGIFactory5? f5 = DXGI.CreateDXGIFactory1<IDXGIFactory5>();
            return f5?.PresentAllowTearing;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint x, out uint y);

    /// <summary>Windows display scaling in percent for the monitor that contains the centre of <paramref name="bounds"/>.</summary>
    public static int ScalePercent(Rectangle bounds)
    {
        try
        {
            IntPtr mon = MonitorFromPoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), 2);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint x, out _) == 0)
                return (int)Math.Round(x * 100.0 / 96.0);
        }
        catch { }
        return 0;
    }

    // ================================================================ pacing

    public sealed class PacingAnalysis
    {
        public bool UsedStats;
        public string Source = string.Empty;
        public int Frames;
        public double ObservedHz;              // 0 = unknown
        public double MeanMs, StdMs, P5Ms, P95Ms;
        public double JudderShare;             // frames held longer than 1.25 x the source frame time
        public double SameRefreshShare;        // frames that shared a refresh with the previous one (tearing)
        public List<(string Label, double Fraction, string Text)> Histogram = new();
        public string Vrr = string.Empty;      // one-line verdict about variable refresh
        public bool VrrWorking, VrrKnown;
        public string StatsNote = string.Empty;
        public int StatSamples, StatPairs, CleanPairs;
        public bool UsedSpread;                 // some per-frame times were shared out over skipped frames
    }

    public static PacingAnalysis AnalyzePacing(PacingCapture cap, double sourceFps, double panelHz)
    {
        var a = new PacingAnalysis();
        double freq = Stopwatch.Frequency;
        // A display slower than the source can only show every Nth frame: what counts as "held too long"
        // is measured against the rate the screen can really deliver.
        double effective = panelHz > 0 ? Math.Min(sourceFps, panelHz) : sourceFps;
        double T = 1000.0 / Math.Max(1.0, effective);

        StatSample[] st = cap.Stats;
        a.StatSamples = st.Length;
        var durs = new List<double>();       // exactly one frame between the two looks
        var spread = new List<double>();     // several frames between two looks: the time divided among them
        long sumRefresh = 0; double sumMs = 0; int same = 0, pairs = 0;
        for (int i = 1; i < st.Length; i++)
        {
            long dp = (long)st[i].PresentCount - st[i - 1].PresentCount;
            if (dp <= 0) continue;
            long dr = (long)st[i].SyncRefreshCount - st[i - 1].SyncRefreshCount;
            double ms = (st[i].SyncQpc - st[i - 1].SyncQpc) * 1000.0 / freq;
            pairs++;
            if (dr <= 0 || ms <= 0) { same++; continue; }
            if (ms > 500) continue;
            sumRefresh += dr; sumMs += ms;
            if (dp == 1) durs.Add(ms);
            else for (int k = 0; k < Math.Min(dp, 8); k++) spread.Add(ms / dp);
        }
        a.StatPairs = pairs;
        a.CleanPairs = durs.Count;
        a.StatsNote = cap.StatsAttempts == 0 ? "計測中に表示されたコマがありませんでした"
            : cap.StatsFailures == 0 ? "GetFrameStatistics: すべて成功"
            : $"GetFrameStatistics: {cap.StatsAttempts} 回中 {cap.StatsFailures} 回失敗（最後のコード {cap.LastFailure}）";
        a.StatsNote += $"／表示されたコマの記録 {st.Length} 件、続けて比べられた組 {durs.Count} 組";
        if (durs.Count < 20 && durs.Count + spread.Count >= 20)
        {
            durs.AddRange(spread);           // looks skipped some frames: use the time shared out per frame
            a.UsedSpread = true;
            a.StatsNote += $"（記録の間隔が空いた分 {spread.Count} コマは平均で補いました）";
        }
        bool statsGood = durs.Count >= 20 && cap.StatsFailures <= cap.StatsAttempts / 2;

        if (statsGood)
        {
            a.UsedStats = true;
            a.Source = "表示側の記録（GetFrameStatistics）";
            a.Frames = durs.Count;
            a.SameRefreshShare = pairs > 0 ? (double)same / pairs : 0;
            a.ObservedHz = sumMs > 0 ? sumRefresh * 1000.0 / sumMs : 0;
        }
        else
        {
            long[] t = cap.PresentTicks;
            durs.Clear();
            for (int i = 1; i < t.Length; i++)
            {
                double ms = (t[i] - t[i - 1]) * 1000.0 / freq;
                if (ms > 0 && ms < 500) durs.Add(ms);
            }
            a.UsedStats = false;
            a.Source = "描画スレッドの Present 間隔（表示側の記録が取れなかったための代用）";
            a.Frames = durs.Count;
        }

        if (durs.Count == 0)
        {
            a.Vrr = "計測できませんでした";
            return a;
        }

        double mean = durs.Average();
        double variance = durs.Sum(d => (d - mean) * (d - mean)) / durs.Count;
        var sorted = durs.OrderBy(x => x).ToList();
        a.MeanMs = mean;
        a.StdMs = Math.Sqrt(variance);
        a.P5Ms = sorted[Math.Clamp((int)(sorted.Count * 0.05), 0, sorted.Count - 1)];
        a.P95Ms = sorted[Math.Clamp((int)(sorted.Count * 0.95), 0, sorted.Count - 1)];
        a.JudderShare = (double)durs.Count(d => d > T * 1.25) / durs.Count;

        // 1 ms bins; show the ones holding at least 1.5% of the frames, the rest summed up.
        var bins = durs.GroupBy(d => (int)Math.Round(d)).OrderBy(g => g.Key).ToList();
        int other = 0;
        foreach (var g in bins)
        {
            double share = (double)g.Count() / durs.Count;
            if (share >= 0.015) a.Histogram.Add(($"{g.Key} ms 前後", share, $"{share:P0}"));
            else other += g.Count();
        }
        if (other > 0) a.Histogram.Add(("その他", (double)other / durs.Count, $"{(double)other / durs.Count:P0}"));

        // ---- variable refresh ----
        if (a.UsedStats && a.UsedSpread)
        {
            a.Vrr = "判定できません（表示側の記録に飛びがあり、コマごとの表示時間を求められませんでした）";
            return a;
        }
        if (!a.UsedStats)
        {
            a.Vrr = "判定できません（表示側の時刻が取れませんでした）";
            return a;
        }
        double hz = a.ObservedHz > 20 ? a.ObservedHz : panelHz;
        if (hz <= 0)
        {
            a.Vrr = "判定できません（モニターのリフレッシュレートが分かりません）";
            return a;
        }
        double P = 1000.0 / hz;
        double r = T / P;
        if (Math.Abs(r - Math.Round(r)) < 0.1)
        {
            a.Vrr = $"判定できません（映像の1コマ {T:0.0}ms がリフレッシュ {P:0.0}ms の整数倍で、可変リフレッシュの有無が数字に出ません）";
            return a;
        }
        double aligned = (double)durs.Count(d =>
        {
            double q = d / P;
            return Math.Abs(q - Math.Round(q)) < 0.12;
        }) / durs.Count;
        double cv = a.StdMs / Math.Max(0.001, mean);
        a.VrrKnown = true;
        if (aligned >= 0.9 && a.P95Ms - a.P5Ms > P * 0.6)
        {
            a.Vrr = "可変リフレッシュレートは効いていません（コマの表示時間がリフレッシュ単位に丸められ、長短が混ざっています）";
        }
        else if (aligned < 0.6 && cv < 0.15)
        {
            a.VrrWorking = true;
            a.Vrr = "可変リフレッシュレートが効いています（コマの表示時間がほぼ一定で、リフレッシュ単位に揃っていません）";
        }
        else if (aligned >= 0.9)
        {
            a.Vrr = "可変リフレッシュレートは効いていないようです（表示時間がリフレッシュ単位に揃っています）";
        }
        else
        {
            a.VrrKnown = false;
            a.Vrr = "はっきりしません（表示時間がリフレッシュ単位でも一定でもありません）";
        }
        return a;
    }

    // ================================================================ edge metric (FSR comparison)

    public sealed class EdgeStats
    {
        public double MeanGradient;
        public int CellW = 120, CellH = 90;
        public long[,] Cells = new long[0, 0];     // [cx, cy] sum of gradient inside each cell
    }

    /// <summary>Mean gradient magnitude (|dx| + |dy| of the Rec.709 luma) and per-cell sums for the tile search.</summary>
    public static EdgeStats MeasureEdges(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var es = new EdgeStats();
        int cw = (w + es.CellW - 1) / es.CellW, ch = (h + es.CellH - 1) / es.CellH;
        es.Cells = new long[cw, ch];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var prev = new int[w];
            var cur = new int[w];
            var row = new byte[w * 4];
            long total = 0;
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int o = x * 4;
                    cur[x] = (54 * row[o + 2] + 183 * row[o + 1] + 19 * row[o]) >> 8;
                }
                if (y > 0)
                {
                    for (int x = 1; x < w; x++)
                    {
                        int g = Math.Abs(cur[x] - cur[x - 1]) + Math.Abs(cur[x] - prev[x]);
                        total += g;
                        es.Cells[x / es.CellW, y / es.CellH] += g;
                    }
                }
                (prev, cur) = (cur, prev);
            }
            es.MeanGradient = total / (double)Math.Max(1, (long)w * h);
        }
        finally { bmp.UnlockBits(bd); }
        return es;
    }

    /// <summary>The 480x270 tile (aligned to the cell grid) holding the most edges, or null if the picture is smaller.</summary>
    public static Rectangle? BestTile(EdgeStats es, Size image)
    {
        const int tw = 480, th = 270;
        if (image.Width < tw || image.Height < th) return null;
        int nx = tw / es.CellW, ny = th / es.CellH;
        long best = -1; int bx = 0, by = 0;
        for (int cx = 0; cx + nx <= es.Cells.GetLength(0); cx++)
            for (int cy = 0; cy + ny <= es.Cells.GetLength(1); cy++)
            {
                long sum = 0;
                for (int i = 0; i < nx; i++) for (int j = 0; j < ny; j++) sum += es.Cells[cx + i, cy + j];
                if (sum > best) { best = sum; bx = cx; by = cy; }
            }
        var r = new Rectangle(bx * es.CellW, by * es.CellH, tw, th);
        r.Intersect(new Rectangle(Point.Empty, image));
        return r.Width == tw && r.Height == th ? r : null;
    }

    public static void SaveSideBySide(Bitmap left, Bitmap right, Rectangle tile, string leftLabel, string rightLabel, string path)
    {
        const int head = 26, gap = 8;
        using var outBmp = new Bitmap(tile.Width * 2 + gap, tile.Height + head, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(outBmp))
        {
            g.Clear(Color.FromArgb(24, 24, 24));
            using var font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            g.DrawString(leftLabel, font, Brushes.White, 4, 3);
            g.DrawString(rightLabel, font, Brushes.White, tile.Width + gap + 4, 3);
            g.DrawImage(left, new Rectangle(0, head, tile.Width, tile.Height), tile, GraphicsUnit.Pixel);
            g.DrawImage(right, new Rectangle(tile.Width + gap, head, tile.Width, tile.Height), tile, GraphicsUnit.Pixel);
        }
        outBmp.Save(path, ImageFormat.Png);
    }

    // ================================================================ screen sampling

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private const uint GaRoot = 2;

    /// <summary>True only when the topmost window at the corners and the centre of <paramref name="screenRect"/>
    /// belongs to <paramref name="rootHwnd"/> (the YuCap main window). Anything else on top - another
    /// application, a notification - means the screen must not be sampled there.</summary>
    public static bool OwnedByWindow(IntPtr rootHwnd, Rectangle screenRect)
    {
        Point[] pts =
        {
            new(screenRect.Left, screenRect.Top), new(screenRect.Right - 1, screenRect.Top),
            new(screenRect.Left, screenRect.Bottom - 1), new(screenRect.Right - 1, screenRect.Bottom - 1),
            new(screenRect.Left + screenRect.Width / 2, screenRect.Top + screenRect.Height / 2),
        };
        foreach (Point p in pts)
        {
            IntPtr w = WindowFromPoint(p);
            if (w == IntPtr.Zero) return false;
            if (GetAncestor(w, GaRoot) != rootHwnd) return false;
        }
        return true;
    }

    /// <summary>Copy ONLY <paramref name="screenRect"/> from the screen and return how much of it matches
    /// <paramref name="expected"/> (each channel within <paramref name="tol"/>), plus the mean colour.</summary>
    public static (double Match, Color Mean) SampleMatch(Rectangle screenRect, Color expected, int tol = 18)
    {
        using var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
            g.CopyFromScreen(screenRect.Location, Point.Empty, screenRect.Size);
        int hit = 0, n = 0; long sr = 0, sg = 0, sb = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                Color c = bmp.GetPixel(x, y);
                n++; sr += c.R; sg += c.G; sb += c.B;
                if (Math.Abs(c.R - expected.R) <= tol && Math.Abs(c.G - expected.G) <= tol && Math.Abs(c.B - expected.B) <= tol) hit++;
            }
        return (n == 0 ? 0 : (double)hit / n, n == 0 ? Color.Black : Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n)));
    }
}
