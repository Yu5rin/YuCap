using System;
using System.Collections.Generic;

namespace YuCap;

/// <summary>
/// GPU budget guard: when the measured GPU time per presented frame stays above ~85% of one refresh for about 2 s, the cost of the
/// scaling is stepped down (Anime4K L/M -> S, then the Restore pre-pass off, then FSR instead of Anime4K); when it has been below ~60%
/// for about 10 s the last step is undone. Frame interpolation is never touched. Pure logic (fed once a second from the render loop),
/// so the decisions can be checked without a GPU. The user's settings are never changed: the guard only decides what is drawn.
/// </summary>
internal sealed class QualityGuard
{
    public enum Step { A4kSmall, NoRestore, Fsr }

    public const double OverShare = 0.85, UnderShare = 0.60;
    /// <summary>Test hook: scales the refresh budget the measurements are compared with (0.4 = pretend the GPU is 2.5x slower).</summary>
    public static volatile float BudgetScale = 1f;
    public const int OverSeconds = 2, UnderSeconds = 10;

    private Step[] _steps = Array.Empty<Step>();
    private int _level, _over, _under;
    private long _upBlockUntil, _lastDownAt = -1_000_000;
    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private long _t0 = Environment.TickCount64;

    /// <summary>How many steps are in force (0 = none).</summary>
    public int Level { get { lock (_gate) return _level; } }
    /// <summary>Counts every change of the level (the UI shows a notice when it moves).</summary>
    public long Serial { get { lock (_gate) return _serial; } }
    private long _serial;
    private bool _nvMode;
    private Step[] Applied() { var a = new Step[_level]; Array.Copy(_steps, a, _level); return a; }

    public IReadOnlyList<string> Log { get { lock (_gate) return _log.ToArray(); } }

    /// <summary>The user's choice changed (quality / Restore): the steps that exist for it are recomputed and the guard starts from the full quality.</summary>
    public void Configure(ScalingQuality quality, char variant, bool restoreOn)
    {
        var steps = new List<Step>();
        if (quality == ScalingQuality.Anime4K)
        {
            if (variant != 'S') steps.Add(Step.A4kSmall);
            if (restoreOn) steps.Add(Step.NoRestore);
            steps.Add(Step.Fsr);
        }
        else if (quality == ScalingQuality.Fsr && restoreOn) steps.Add(Step.NoRestore);
        else if (quality == ScalingQuality.NvRtx) steps.Add(Step.Fsr);
        lock (_gate)
        {
            _nvMode = quality == ScalingQuality.NvRtx;
            bool same = steps.Count == _steps.Length;
            for (int i = 0; same && i < steps.Count; i++) same = steps[i] == _steps[i];
            if (same) return;
            _steps = steps.ToArray();
            if (_level != 0) { _level = 0; _serial++; }
            _over = _under = 0;
        }
    }

    public ScalingQuality Effective(ScalingQuality q)
    {
        lock (_gate) { for (int i = 0; i < _level; i++) if (_steps[i] == Step.Fsr) return ScalingQuality.Fsr; }
        return q;
    }

    /// <summary>'S' when the network was made smaller, else '\0'.</summary>
    public char Variant { get { lock (_gate) { for (int i = 0; i < _level; i++) if (_steps[i] == Step.A4kSmall) return 'S'; return '\0'; } } }

    public bool NoRestore { get { lock (_gate) { for (int i = 0; i < _level; i++) if (_steps[i] == Step.NoRestore) return true; return false; } } }

    /// <summary>The notice for the active downgrade (null when none).</summary>
    public string? Text
    {
        get
        {
            lock (_gate)
            {
                if (_level == 0) return null;
                Step last = _steps[_level - 1];
                return last switch
                {
                    Step.Fsr => _nvMode ? L.T("負荷が高いため AI 拡大を FSR に切り替えています") : L.T("負荷が高いため くっきり を FSR に切り替えています"),
                    Step.NoRestore => L.T("負荷が高いため ぼやけ取りを止めています"),
                    _ => L.T("負荷が高いため くっきり を軽くしています"),
                };
            }
        }
    }

    /// <summary>Feed one second of GPU measurements (<paramref name="avgMs"/>/<paramref name="maxMs"/> per present). Returns true when the level changed.</summary>
    public bool Tick(double avgMs, double maxMs, double budgetMs, bool active, long nowMs)
    {
        lock (_gate)
        {
            if (_steps.Length == 0 || !active || budgetMs <= 0 || avgMs <= 0) { _over = _under = 0; return false; }
            bool over = avgMs > OverShare * budgetMs || (maxMs > 0.95 * budgetMs && avgMs > 0.4 * budgetMs);   // a frame that nearly misses the refresh counts
            bool under = avgMs < UnderShare * budgetMs && maxMs < 0.95 * budgetMs;
            _over = over ? _over + 1 : 0;
            _under = under ? _under + 1 : 0;
            string at = $"+{(nowMs - _t0) / 1000.0:0.0}s";
            if (_over >= OverSeconds && _level < _steps.Length)
            {
                _level++; _over = _under = 0; _serial++;
                // coming back too soon after a step down would only make it flap: wait longer when it happened a minute ago already
                _upBlockUntil = nowMs + (nowMs - _lastDownAt < 60_000 ? 60_000 : 20_000);
                _lastDownAt = nowMs;
                _log.Add($"{at}: GPU {avgMs:0.00} ms avg / {maxMs:0.00} ms max of {budgetMs:0.00} ms -> step {_level}/{_steps.Length}: {_steps[_level - 1]}");
                return true;
            }
            if (_under >= UnderSeconds && _level > 0 && nowMs >= _upBlockUntil)
            {
                _level--; _over = _under = 0; _serial++;
                _log.Add($"{at}: GPU {avgMs:0.00} ms avg / {maxMs:0.00} ms max of {budgetMs:0.00} ms -> back to step {_level}/{_steps.Length}");
                return true;
            }
            return false;
        }
    }

    /// <summary>Self-check of the decisions on synthetic seconds (used by --guard-check and the tests).</summary>
    public static string SelfCheck()
    {
        var sb = new System.Text.StringBuilder();
        int bad = 0;
        void Expect(bool ok, string what) { if (!ok) { bad++; sb.AppendLine("FAIL: " + what); } else sb.AppendLine("ok: " + what); }
        var g = new QualityGuard();
        g.Configure(ScalingQuality.Anime4K, 'M', false);
        long t = 0; double b = 6.94;
        for (int i = 0; i < 1; i++) g.Tick(6.0, 6.9, b, true, t += 1000);
        Expect(g.Level == 0, "one slow second changes nothing");
        g.Tick(6.0, 6.9, b, true, t += 1000);
        Expect(g.Level == 1 && g.Variant == 'S' && g.Effective(ScalingQuality.Anime4K) == ScalingQuality.Anime4K, "two slow seconds: Anime4K M -> S");
        g.Tick(6.5, 6.9, b, true, t += 1000); g.Tick(6.5, 6.9, b, true, t += 1000);
        Expect(g.Level == 2 && g.Effective(ScalingQuality.Anime4K) == ScalingQuality.Fsr, "still slow: S -> FSR");
        for (int i = 0; i < 5; i++) g.Tick(6.5, 6.9, b, true, t += 1000);
        Expect(g.Level == 2, "no step below FSR");
        for (int i = 0; i < 9; i++) g.Tick(2.0, 3.0, b, true, t += 1000);
        Expect(g.Level == 2, "9 calm seconds: nothing yet");
        long calm0 = t; int guard = 0;
        while (g.Level == 2 && guard++ < 100) g.Tick(2.0, 3.0, b, true, t += 1000);
        Expect(g.Level == 1 && t - calm0 >= 1000, $"calm again: back up one step after {(t - calm0) / 1000} s more (waits out the flap guard)");
        g.Configure(ScalingQuality.Anime4K, 'M', true);
        Expect(g.Level == 0, "a settings change starts from full quality");
        g.Configure(ScalingQuality.Standard, 'M', false);
        for (int i = 0; i < 6; i++) g.Tick(9, 9, b, true, t += 1000);
        Expect(g.Level == 0, "standard scaling has nothing to step down");
        sb.AppendLine(bad == 0 ? "ALL OK" : bad + " FAILED");
        return sb.ToString();
    }
}
