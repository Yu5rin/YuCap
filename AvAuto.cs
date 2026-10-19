using System;
using System.Collections.Generic;
using System.Linq;

namespace YuCap;

/// <summary>
/// 音ズレの自動補正 (decision part, no UI and no clock of its own, so the test can drive it too).
/// Every 0.5 s the caller hands in the in-app difference d = audio latency - video latency (both measured with the
/// correction already applied) and says whether the numbers can be trusted. The controller keeps the last
/// <see cref="WindowN"/> samples (5 s) and works on their MEDIAN. The goal is d = trim, i.e. picture and sound agree
/// inside YuCap plus whatever the user trims for the TV / speakers (<c>total = <see cref="AutoMs"/> + trim</c>).
/// Rules: nothing happens inside a +-<see cref="Deadband"/> ms deadband; a far-off start (> <see cref="AcquireBand"/>) is
/// judged on 3 s and corrected by up to <see cref="AcquireStep"/> ms at once, near the goal by at most <see cref="TrackStep"/> ms;
/// after every change the window is emptied and nothing is collected until the change has been fully worked off
/// (<c>settled</c>) plus <see cref="SettleTicks"/> ticks, and nothing at all is collected while the streams are not stable.
/// The change itself is slewed by the engines (audio: one frame per ~64 repeated / skipped, video: 2 ms per 100 ms).
/// </summary>
internal sealed class AvAutoController
{
    public const int WindowN = 10;           // samples (0.5 s apart)
    public const int AcquireN = 6;
    public const double Deadband = 8.0;      // ms
    public const double AcquireBand = 25.0;  // ms
    public const double AcquireStep = 40.0;  // ms per decision when far off
    public const double TrackStep = 4.0;     // ms per decision when near
    public const int SettleTicks = 3;        // 1.5 s after a change was worked off / the streams became stable

    private readonly List<double> _win = new();
    private int _settle;

    /// <summary>The automatic part of the offset (ms; + = delay the sound, - = delay the picture), continuous.</summary>
    public double AutoMs { get; private set; }
    /// <summary>Changes made since <see cref="ResetStats"/>.</summary>
    public int Steps { get; private set; }
    /// <summary>Median of the window at the last decision (NaN before the first).</summary>
    public double LastMedian { get; private set; } = double.NaN;
    /// <summary>True once the window was full and the median was inside the deadband.</summary>
    public bool Converged { get; private set; }
    /// <summary>The last wanted change could not be made (the range ends, or the picture cannot be delayed).</summary>
    public bool AtLimit { get; private set; }
    /// <summary>Samples collected so far (for "measuring...").</summary>
    public int Collected => _win.Count;

    public void SetAuto(double ms) { AutoMs = ms; ResetWindow(); }
    public void ResetStats() { Steps = 0; }

    /// <summary>Forget the collected samples (the numbers no longer mean the same: restart, trim changed, ...).</summary>
    public void ResetWindow()
    {
        _win.Clear(); _settle = 0; Converged = false; LastMedian = double.NaN;
    }

    public static double Median(IEnumerable<double> xs)
    {
        double[] a = xs.OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>One 0.5 s tick. Returns true when <see cref="AutoMs"/> changed (the caller applies it).</summary>
    /// <param name="stable">both streams run, no restart / recovery / pause, both latencies are known.</param>
    /// <param name="settled">the last change is completely worked off by the engines.</param>
    /// <param name="diffMs">audio latency - video latency.</param>
    /// <param name="trimMs">user trim; the goal is diff = trim.</param>
    /// <param name="minAuto">lowest / highest allowed <see cref="AutoMs"/> (range of the total, and no negative total without video delay).</param>
    public bool Tick(bool stable, bool settled, double diffMs, double trimMs, double minAuto, double maxAuto)
    {
        if (!stable || !settled) { _win.Clear(); _settle = 0; Converged = false; return false; }
        if (_settle < SettleTicks) { _settle++; return false; }
        _win.Add(diffMs);
        while (_win.Count > WindowN) _win.RemoveAt(0);

        double mFar = _win.Count >= AcquireN ? Median(_win.Skip(_win.Count - AcquireN)) : double.NaN;
        bool far = !double.IsNaN(mFar) && Math.Abs(mFar - trimMs) > AcquireBand;
        double m;
        if (far) m = mFar;
        else if (_win.Count >= WindowN) m = Median(_win);
        else return false;

        LastMedian = m;
        double err = m - trimMs;
        if (Math.Abs(err) <= Deadband) { Converged = true; AtLimit = false; return false; }
        Converged = false;
        double limit = far ? AcquireStep : TrackStep;
        double step = Math.Clamp(-err, -limit, limit);
        double next = Math.Clamp(AutoMs + step, minAuto, maxAuto);
        _win.Clear(); _settle = 0;
        if (Math.Abs(next - AutoMs) < 0.5) { AtLimit = true; return false; }
        AtLimit = Math.Abs(next - (AutoMs + step)) > 0.5;
        AutoMs = next;
        Steps++;
        return true;
    }
}
