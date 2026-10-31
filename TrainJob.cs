using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace YuCap;

/// <summary>
/// 自分専用モデルの学習を、アプリの中から動かすための部品（画面を持たない）。
/// 学習スクリプト（tools\train_upscaler.py ほか）は exe に埋め込んであり、<see cref="EnsureTools"/> が
/// <c>%LOCALAPPDATA%\YuCapTrain\tools</c> に取り出す。学習は学習用の venv の python を別プロセスで動かし、
/// 出力を読んで経過を取り出す。python（とその子プロセス）は Job Object に入れるので、中止でも YuCap が落ちても残らない。
/// </summary>
internal static class TrainEnv
{
    public static string ToolsDir => Path.Combine(UserModels.Root, "tools");
    public static string LogsDir => Path.Combine(UserModels.Root, "logs");
    public static string RunsDir => Path.Combine(UserModels.Root, "runs");

    /// <summary>The training python: the venv that tools\setup_train_env.ps1 makes (YUCAP_TRAIN_PYTHON overrides it - used by the test mode, which runs in a throw-away folder).</summary>
    public static string PythonExe
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("YUCAP_TRAIN_PYTHON");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(UserModels.Root, "venv", "Scripts", "python.exe");
        }
    }

    public static bool Ready => File.Exists(PythonExe);

    /// <summary>Names a model may not have: the built-in networks of train_upscaler.py's registry.</summary>
    private static readonly string[] Reserved = { "orig", "original", "m", "s", "l", "origm", "origs", "origl", "best", "last" };

    /// <summary>Why <paramref name="name"/> cannot be a model name (null when it can): ASCII letters, digits, "_" and "-" only
    /// (the name goes into a file name and into the model file's first line, which is plain ASCII).</summary>
    public static string? NameProblem(string name)
    {
        if (name.Length == 0) return L.T("モデルの名前を入れてください。");
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,32}$"))
            return L.T("モデルの名前には、半角の英数字と _ - だけを使えます（32 文字まで）。");
        if (Reserved.Contains(name.ToLowerInvariant()))
            return L.T("その名前は内蔵のモデルが使っています。別の名前にしてください。");
        return null;
    }

    /// <summary>Name suggestion for an architecture: mine, mine_l, mine_s.</summary>
    public static string SuggestName(char arch) => arch == 'L' ? "mine_l" : arch == 'S' ? "mine_s" : "mine";

    // ---- the scripts ----------------------------------------------------------------------------------------------

    private static readonly object ExtractLock = new();

    /// <summary>Write the embedded scripts to <see cref="ToolsDir"/> (and the Anime4K network files they read to
    /// <c>third_party\Anime4K</c> beside it - a4k_gen.py looks for them there). A file whose content already matches is left alone, one that
    /// differs (an older YuCap's copy) is overwritten. Returns the path of train_upscaler.py.</summary>
    public static string EnsureTools()
    {
        lock (ExtractLock)
        {
            Assembly asm = typeof(TrainEnv).Assembly;
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("Tools.", StringComparison.Ordinal)) continue;
                string rest = res.Substring("Tools.".Length);
                string target = rest.StartsWith("glsl.", StringComparison.Ordinal)
                    ? Path.Combine(UserModels.Root, "third_party", "Anime4K", rest.Substring("glsl.".Length))
                    : Path.Combine(ToolsDir, rest);
                using Stream? s = asm.GetManifestResourceStream(res);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] data = ms.ToArray();
                try
                {
                    if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                }
                catch (Exception ex) { throw new IOException(L.F("学習用のスクリプトを書き出せませんでした: {0}", target) + " (" + ex.Message + ")", ex); }
            }
            return Path.Combine(ToolsDir, "train_upscaler.py");
        }
    }

    // ---- environment set-up (a visible console) -------------------------------------------------------------------

    /// <summary>Run tools\setup_train_env.ps1 in a console window of its own (the download is large and slow: the user watches it). The
    /// window stays until Enter is pressed; <paramref name="done"/> is called (on a thread-pool thread) with the exit code.</summary>
    public static void RunSetup(Action<int> done)
    {
        string script = Path.Combine(ToolsDir, "setup_train_env.ps1");
        EnsureTools();
        string cmd = "& '" + script.Replace("'", "''") + "'; $c = $LASTEXITCODE; Write-Host ''; "
                   + "Read-Host 'Enter' | Out-Null; exit $c";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            WorkingDirectory = UserModels.Root,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(cmd);
        Directory.CreateDirectory(UserModels.Root);
        Process p = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe");
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) =>
        {
            int code = -1;
            try { code = p.ExitCode; } catch { /* gone */ }
            try { p.Dispose(); } catch { /* ignore */ }
            done(code);
        };
    }

    // ---- data folders -----------------------------------------------------------------------------------------------

    /// <summary>The picture folders under the training data folder (one per profile, plus the starter set).</summary>
    public static List<string> DataFolders()
    {
        try
        {
            if (!Directory.Exists(UserModels.DataRoot)) return new List<string>();
            return Directory.EnumerateDirectories(UserModels.DataRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    public static int CountPictures(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (string _ in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)) n++;
            return n;
        }
        catch { return 0; }
    }

    /// <summary>Four scenes (t01..t04) of three flat-colour-and-noise pictures: enough for the script's train / validation split.</summary>
    internal static void MakeTinyTrainSet(string dir)
    {
        var rng = new Random(5);
        for (int scene = 1; scene <= 4; scene++)
            for (int f = 1; f <= 3; f++)
            {
                using var bmp = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256)));
                    for (int k = 0; k < 80; k++)
                    {
                        using var br = new SolidBrush(Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256)));
                        g.FillRectangle(br, rng.Next(1200), rng.Next(660), 20 + rng.Next(160), 12 + rng.Next(100));
                    }
                }
                bmp.Save(Path.Combine(dir, $"t{scene:00}_f{f}.png"), ImageFormat.Png);
            }
    }

    // ---- Job Object ---------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int len);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccounting info, int len, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>A Job Object whose processes are all killed when it is closed (so a crashed YuCap leaves no python behind).</summary>
    internal sealed class KillJob : IDisposable
    {
        private IntPtr _h;

        public KillJob()
        {
            _h = CreateJobObjectW(IntPtr.Zero, null);
            if (_h == IntPtr.Zero) return;
            var info = new ExtendedLimit();
            info.Basic.LimitFlags = 0x2000;      // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(_h, 9, ref info, Marshal.SizeOf<ExtendedLimit>())) Log.Info("train: job object limit failed");
        }

        public bool Assign(Process p)
        {
            if (_h == IntPtr.Zero) return false;
            try { return AssignProcessToJobObject(_h, p.Handle); }
            catch { return false; }
        }

        /// <summary>Processes still alive in the job (python and anything it started); -1 when unknown.</summary>
        public int Active
        {
            get
            {
                if (_h == IntPtr.Zero) return -1;
                return QueryInformationJobObject(_h, 1, out BasicAccounting a, Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero) ? (int)a.ActiveProcesses : -1;
            }
        }

        public void Kill()
        {
            if (_h != IntPtr.Zero) TerminateJobObject(_h, 1);
        }

        public void Dispose()
        {
            IntPtr h = Interlocked.Exchange(ref _h, IntPtr.Zero);
            if (h != IntPtr.Zero) CloseHandle(h);     // KILL_ON_JOB_CLOSE ends whatever is left
        }
    }
}

/// <summary>What to train.</summary>
internal sealed class TrainOptions
{
    public string Name = "mine";
    public char Arch = 'M';                 // 'M' | 'L' | 'S'
    public string Loss = "edge";            // "edge" | "texture"
    public double Minutes = 30;
    public List<string> DataDirs = new();
}

/// <summary>One row of a validation table.</summary>
internal readonly record struct TrainScore(double Psnr, double Ssim);

/// <summary>A snapshot of how a training run is going, for the progress window.</summary>
internal sealed class TrainStatus
{
    public enum PhaseKind { Starting, Training, Finishing, Done, Failed, Cancelled }

    public PhaseKind Phase;
    public int Frames, TrainFrames, ValFrames;
    public int Iter;
    public double Minutes;                    // trained so far, as the script last reported it
    public DateTime MinutesAt;                // when that report arrived (to interpolate between reports)
    public TrainScore? Bicubic, Original, Best, Latest;
    public double BestScore = double.NegativeInfinity;
    public TrainScore? FinalYours, FinalOriginal, FinalBicubic;
    public string LastLine = string.Empty;
}

/// <summary>One training run: starts the python process, keeps the log and the parsed progress, and can stop it (the process tree).</summary>
internal sealed class TrainJob : IDisposable
{
    private readonly object _lock = new();
    private readonly StringBuilder _log = new();
    private readonly TrainStatus _st = new();
    private Process? _proc;
    private TrainEnv.KillJob? _job;
    private StreamWriter? _file;
    private string _ctx = string.Empty;       // which table the next rows belong to: "before" | "final" | "other"
    private volatile bool _cancelled;
    private bool _disposed;

    public TrainOptions Options { get; }
    public bool Finalizing { get; private set; }
    public DateTime StartedAt { get; private set; }
    public bool IsRunning { get { lock (_lock) return _proc != null && !_proc.HasExited; } }
    public int ProcessCount => _job?.Active ?? -1;

    /// <summary>A line of output arrived (thread-pool thread).</summary>
    public event Action<string>? Line;
    /// <summary>The run ended: exit code (thread-pool thread).</summary>
    public event Action<int>? Ended;

    public TrainJob(TrainOptions o) { Options = o; }

    public string LogText { get { lock (_lock) return _log.ToString(); } }

    public TrainStatus Snapshot()
    {
        lock (_lock)
        {
            return new TrainStatus
            {
                Phase = _st.Phase, Frames = _st.Frames, TrainFrames = _st.TrainFrames, ValFrames = _st.ValFrames, Iter = _st.Iter,
                Minutes = _st.Minutes, MinutesAt = _st.MinutesAt, Bicubic = _st.Bicubic, Original = _st.Original, Best = _st.Best,
                Latest = _st.Latest, BestScore = _st.BestScore, FinalYours = _st.FinalYours, FinalOriginal = _st.FinalOriginal,
                FinalBicubic = _st.FinalBicubic, LastLine = _st.LastLine,
            };
        }
    }

    /// <summary>The newest checkpoint of the best state exists (so a stopped run can still be turned into a model).</summary>
    public bool HasBestCheckpoint => File.Exists(Path.Combine(TrainEnv.RunsDir, Options.Name, "ckpt_best.pt"));

    /// <summary>Start <c>train</c> (or <c>finalize</c> for a run that was stopped).</summary>
    public void Start(bool finalize = false)
    {
        string script = TrainEnv.EnsureTools();
        string py = TrainEnv.PythonExe;
        if (!File.Exists(py)) throw new FileNotFoundException(L.T("学習の環境がありません。"), py);
        Directory.CreateDirectory(TrainEnv.LogsDir);

        var psi = new ProcessStartInfo(py)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = UserModels.Root,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["YUCAP_TRAIN_DIR"] = UserModels.Root;
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(finalize ? "finalize" : "train");
        psi.ArgumentList.Add("--name"); psi.ArgumentList.Add(Options.Name);
        psi.ArgumentList.Add("--data");
        foreach (string d in Options.DataDirs) psi.ArgumentList.Add(d);
        if (!finalize)
        {
            psi.ArgumentList.Add("--minutes"); psi.ArgumentList.Add(Options.Minutes.ToString("0.###", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--arch"); psi.ArgumentList.Add(Options.Arch.ToString());
            psi.ArgumentList.Add("--loss"); psi.ArgumentList.Add(Options.Loss);
        }

        lock (_lock)
        {
            Finalizing = finalize;
            _ctx = finalize ? "final" : string.Empty;
            _st.Phase = finalize ? TrainStatus.PhaseKind.Finishing : TrainStatus.PhaseKind.Starting;
            StartedAt = DateTime.UtcNow;
        }
        try { _file = new StreamWriter(Path.Combine(TrainEnv.LogsDir, "train-last.log"), finalize, new UTF8Encoding(false)) { AutoFlush = true }; }
        catch { _file = null; }
        AddLine("> " + py + " " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));

        _job?.Dispose();
        _job = new TrainEnv.KillJob();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) AddLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) AddLine(e.Data); };
        p.Exited += (_, _) => OnExited(p);
        p.Start();
        if (!_job.Assign(p)) Log.Info("train: could not put the python process into the job object");
        lock (_lock) _proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Log.Info($"train: started {(finalize ? "finalize" : "train")} '{Options.Name}' arch {Options.Arch} loss {Options.Loss} {Options.Minutes:0.#} min, {Options.DataDirs.Count} folder(s), pid {p.Id}");
    }

    private void OnExited(Process p)
    {
        // output events can trail the exit event: give the reader a moment to drain
        try { p.WaitForExit(2000); } catch { /* ignore */ }
        int code = -1;
        try { code = p.ExitCode; } catch { /* ignore */ }
        lock (_lock)
        {
            if (_st.Phase is not (TrainStatus.PhaseKind.Cancelled))
                _st.Phase = code == 0 && !_cancelled ? TrainStatus.PhaseKind.Done : _cancelled ? TrainStatus.PhaseKind.Cancelled : TrainStatus.PhaseKind.Failed;
        }
        if (code == 0 && !_cancelled) ReadFinalJson();
        Log.Info($"train: ended, exit {code}{(_cancelled ? " (cancelled)" : string.Empty)}");
        try { _file?.Dispose(); } catch { /* ignore */ }
        _file = null;
        Ended?.Invoke(code);
    }

    /// <summary>Stop the run now: the whole process tree (the venv's python.exe starts the real one as a child).</summary>
    public void Cancel()
    {
        _cancelled = true;
        lock (_lock)
        {
            if (_st.Phase is TrainStatus.PhaseKind.Starting or TrainStatus.PhaseKind.Training or TrainStatus.PhaseKind.Finishing)
                _st.Phase = TrainStatus.PhaseKind.Cancelled;
        }
        try { _job?.Kill(); } catch { /* ignore */ }
        try { _proc?.Kill(entireProcessTree: true); } catch { /* already gone */ }
        AddLine("> " + L.T("中止しました"));
    }

    /// <summary>Wait until the python process (and everything it started) is gone.</summary>
    public bool WaitGone(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            int active = _job?.Active ?? 0;
            bool exited;
            lock (_lock) exited = _proc == null || _proc.HasExited;
            if (exited && active <= 0) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private void AddLine(string line)
    {
        lock (_lock)
        {
            _log.AppendLine(line);
            if (_log.Length > 400_000) _log.Remove(0, _log.Length - 300_000);
            Parse(line);
            _st.LastLine = line;
        }
        try { _file?.WriteLine(line); } catch { /* ignore */ }
        Line?.Invoke(line);
    }

    // ---- parsing (under _lock) ----------------------------------------------------------------------------------------

    private static readonly Regex RxFrames = new(@"^frames:\s*(\d+)\s+train\s+(\d+)\s*/\s*validation\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex RxIt = new(@"^it\s+(\d+)\s+([\d.]+)min.*?val PSNR yours (-?[\d.]+) orig (-?[\d.]+) bicubic (-?[\d.]+)\s*\|\s*SSIM yours (-?[\d.]+) orig (-?[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex RxRow = new(@"^(bilinear|bicubic|anime4k-orig|anime4k-M|yours)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex RxFinished = new(@"^finished:\s*(\d+) iterations", RegexOptions.Compiled);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private void Parse(string line)
    {
        Match m;
        if ((m = RxFrames.Match(line)).Success)
        {
            _st.Frames = int.Parse(m.Groups[1].Value); _st.TrainFrames = int.Parse(m.Groups[2].Value); _st.ValFrames = int.Parse(m.Groups[3].Value);
        }
        else if (line.StartsWith("before training", StringComparison.Ordinal)) _ctx = "before";
        else if (line.StartsWith("final validation", StringComparison.Ordinal)) _ctx = "final";
        else if (line.StartsWith("same frames shrunk", StringComparison.Ordinal)) _ctx = "other";
        else if ((m = RxIt.Match(line)).Success)
        {
            _st.Phase = TrainStatus.PhaseKind.Training;
            _st.Iter = int.Parse(m.Groups[1].Value);
            _st.Minutes = D(m.Groups[2].Value);
            _st.MinutesAt = DateTime.UtcNow;
            var yours = new TrainScore(D(m.Groups[3].Value), D(m.Groups[6].Value));
            _st.Original = new TrainScore(D(m.Groups[4].Value), D(m.Groups[7].Value));
            _st.Bicubic = new TrainScore(D(m.Groups[5].Value), _st.Bicubic?.Ssim ?? double.NaN);
            _st.Latest = yours;
            // the script's own rule for the best checkpoint: PSNR, plus an SSIM bonus for the texture loss
            double score = yours.Psnr + (Options.Loss == "texture" ? 25.0 * yours.Ssim : 0.0);
            if (score > _st.BestScore) { _st.BestScore = score; _st.Best = yours; }
        }
        else if ((m = RxFinished.Match(line)).Success)
        {
            _st.Iter = int.Parse(m.Groups[1].Value);
            _st.Phase = TrainStatus.PhaseKind.Finishing;
            _ctx = string.Empty;
        }
        else if ((m = RxRow.Match(line)).Success)
        {
            var sc = new TrainScore(D(m.Groups[2].Value), D(m.Groups[3].Value));
            string label = m.Groups[1].Value;
            if (_ctx == "before")
            {
                if (label == "bicubic") _st.Bicubic = sc;
                else if (label == "anime4k-orig") _st.Original = sc;
            }
            else if (_ctx == "final")
            {
                if (label == "bicubic") _st.FinalBicubic = sc;
                else if (label == "anime4k-orig") _st.FinalOriginal = sc;
                else if (label == "yours") _st.FinalYours = sc;
            }
        }
    }

    /// <summary>The validation the script wrote (runs\NAME\final.json) is what the result is built from when it exists: it is the whole held-out set, not the last log lines.</summary>
    private void ReadFinalJson()
    {
        try
        {
            string p = Path.Combine(TrainEnv.RunsDir, Options.Name, "final.json");
            if (!File.Exists(p)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(p));
            if (!doc.RootElement.TryGetProperty("final", out JsonElement fin)) return;
            TrainScore? Get(string k) =>
                fin.TryGetProperty(k, out JsonElement e) && e.TryGetProperty("psnr", out JsonElement ps) && e.TryGetProperty("ssim", out JsonElement ss)
                    ? new TrainScore(ps.GetDouble(), ss.GetDouble()) : null;
            lock (_lock)
            {
                _st.FinalYours = Get("yours") ?? _st.FinalYours;
                _st.FinalOriginal = Get("anime4k-orig") ?? _st.FinalOriginal;
                _st.FinalBicubic = Get("bicubic") ?? _st.FinalBicubic;
            }
        }
        catch (Exception ex) { Log.Info("train: final.json unreadable - " + ex.Message.Split('\n')[0]); }
    }

    /// <summary>Re-read the result after a <c>finalize</c> run (which writes final.json too).</summary>
    public void RefreshResult() => ReadFinalJson();

    /// <summary>Plain-words verdict of a finished run: how "yours" compares with the original Anime4K network on the held-out pictures.</summary>
    public static string Verdict(TrainStatus s, out bool better)
    {
        better = false;
        if (s.FinalYours is not { } y || s.FinalOriginal is not { } o) return L.T("検証の結果を読み取れませんでした。「詳しい経過」を見てください。");
        double dp = y.Psnr - o.Psnr, ds = y.Ssim - o.Ssim;
        better = dp >= 0 && ds >= 0;
        string nums = L.F("元の Anime4K より PSNR {0} dB / SSIM {1}", dp.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture), ds.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture));
        return nums + L.Par(better ? L.T("良くなりました") : dp < 0 && ds < 0 ? L.T("元より悪くなりました。使わないほうがよいかもしれません") : L.T("一部だけ良くなりました"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _job?.Kill(); } catch { /* ignore */ }
        try { _job?.Dispose(); } catch { /* ignore */ }
        try { _file?.Dispose(); } catch { /* ignore */ }
    }
}
