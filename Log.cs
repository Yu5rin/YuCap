using System;
using System.IO;
using System.Threading;

namespace YuCap;

/// <summary>
/// Lightweight always-on diagnostic log at %APPDATA%\YuCap\yucap.log.
/// Write-through (append per line) so when the app hangs or dies, the last
/// line shows the operation that was in flight. Rotates to yucap.old.log at 2MB.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? Path => _path;

    /// <summary>Open the log for this process. Does NOT rotate: a second instance or a command-line run must not move the
    /// running instance's log away from under it (see <see cref="RotateIfLarge"/>).</summary>
    public static void Init()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuCap");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "yucap.log");
        }
        catch { _path = null; }
        Info($"==== YuCap start v{System.Windows.Forms.Application.ProductVersion} ====");
    }

    /// <summary>Move a log over 2 MB to yucap.old.log. Called only by the process that holds the single-instance mutex.
    /// A failure leaves the log where it is and logging carries on appending to it.</summary>
    public static void RotateIfLarge()
    {
        string? path = _path;
        if (path == null) return;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= 2_000_000) return;
                string old = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "yucap.old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { /* keep appending to the big file rather than lose the log */ }
    }

    public static void Info(string message)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
            {
                // Shared write access: a command-line run next to the viewer (or two processes starting together) can both log.
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                byte[] line = new System.Text.UTF8Encoding(false).GetBytes(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {message}\r\n");
                fs.Write(line, 0, line.Length);
            }
        }
        catch { /* logging must never break the app */ }
    }
}

/// <summary>
/// Detects a wedged UI thread from the outside. The UI thread stamps a heartbeat
/// and names the operation it is about to perform; a background thread notices
/// when the heartbeat goes stale and records which operation was in flight.
/// This is the only way to capture a hang: once the UI thread blocks inside a
/// COM call, nothing running on it (timer, log line) can report anything.
/// </summary>
internal static class Watchdog
{
    private static long _beatTick;
    private static string _op = "(idle)";
    private static Thread? _thread;
    private static bool _reported;

    /// <summary>Called by the UI thread's periodic timer: "I am still alive".</summary>
    public static void Beat()
    {
        Volatile.Write(ref _beatTick, Environment.TickCount64);
        if (_reported)
        {
            _reported = false;
            Log.Info("UI thread recovered");
        }
    }

    /// <summary>Names the operation the UI thread is entering, so a hang can be
    /// attributed precisely. Cleared with <see cref="Done"/>.</summary>
    public static void Mark(string op) => Volatile.Write(ref _op, op);

    public static void Done() => Volatile.Write(ref _op, "(idle)");

    public static void Start()
    {
        if (_thread is { IsAlive: true }) return;
        Volatile.Write(ref _beatTick, Environment.TickCount64);
        _thread = new Thread(Loop) { IsBackground = true, Name = "YuCap.Watchdog" };
        _thread.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(1000);
            long last = Volatile.Read(ref _beatTick);
            if (last == 0) continue;
            long stale = Environment.TickCount64 - last;
            if (stale > 4000 && !_reported)
            {
                _reported = true;
                Log.Info($"*** UI THREAD STUCK for {stale}ms — in-flight operation: {Volatile.Read(ref _op)}");
            }
        }
    }
}
