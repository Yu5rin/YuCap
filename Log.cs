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

    public static void Init()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuCap");
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, "yucap.log");
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2_000_000)
            {
                string old = System.IO.Path.Combine(dir, "yucap.old.log");
                if (File.Exists(old)) File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch { _path = null; }
        Info($"==== YuCap start v{System.Windows.Forms.Application.ProductVersion} ====");
    }

    public static void Info(string message)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {message}\r\n");
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
