using System;
using System.Collections.Generic;

namespace YuCap;

// Pure arithmetic of the game frame rate reading and of the interpolation timeline on UNIQUE pictures.
// The Direct3D renderer feeds it and the offline replay (--interp-seq-eval) uses the very same code,
// so what is measured offline is what runs live. No device, no threads.

/// <summary>Phase-locked smoothing of the arrival times of captured frames: a few ms of USB / scheduling jitter in
/// the raw arrival times would otherwise wobble the interpolation phase. Capture thread.</summary>
internal sealed class ArrivalSmoother
{
    private double _smoothTicks;
    private bool _valid;

    public void Reset() => _valid = false;

    /// <param name="arrival">Stopwatch ticks at which the frame reached us.</param>
    /// <param name="nominalTicks">The nominal frame interval in ticks (0 = unknown: no smoothing).</param>
    public long Track(long arrival, double nominalTicks)
    {
        double sm;
        if (!_valid || nominalTicks <= 0) sm = arrival;
        else
        {
            double pred = _smoothTicks + nominalTicks;
            double err = arrival - pred;
            if (Math.Abs(err) > 6 * nominalTicks) sm = arrival;          // a long stall: start over
            else
            {
                double k = Math.Round(err / nominalTicks);                // dropped (k>0) or duplicated (k<0) frames
                pred += k * nominalTicks;
                err -= k * nominalTicks;
                sm = pred + 0.05 * err;
            }
            if (sm < _smoothTicks + 0.2 * nominalTicks) sm = _smoothTicks + 0.2 * nominalTicks;   // always moves forward
        }
        _valid = true;
        _smoothTicks = sm;
        return (long)sm;
    }
}

/// <summary>Decides from the GPU's thumbnail difference of a frame to the previous one whether it is the same picture.
/// The thumbnail is 120x68 luma levels (0..255), each pixel the mean of up to 16x16 samples, so the capture noise of a
/// still picture is averaged away (see README, "ゲームの実際のコマ数").</summary>
internal static class DupMath
{
    // Calibrated on real footage of the capture card (JVA14, YUY2/NV12 1080p60, a Wii U game at 30 fps and a still desktop): a repeated
    // picture gives a thumbnail difference of EXACTLY 0 (the chain is bit-exact), while the smallest change of a real picture seen (the
    // first frame of a fade, a spinner a few pixels wide) is a mean of 0.0006 levels / max 0.3 levels and ordinary motion is a mean of
    // 0.07 .. 40 levels / max 5 .. 200 levels. So the limits sit just above the arithmetic floor: they must stay strict, because
    // every frame is compared with the one BEFORE it (not with the last picture kept), so a slow pan whose frames differ by a fraction
    // of a level must not read as "the same" or the picture would never advance. A capture format that is compressed (Motion-JPEG)
    // repeats no frame exactly; every frame then reads as a new picture, which is the former behaviour.
    public const float T1 = 1.0f;            // levels: a thumbnail pixel "changed"
    public const float T2 = 4.0f;            // levels: a thumbnail pixel "changed clearly"
    public const double MeanLimit = 0.0005;  // levels
    public const double MaxLimit = 0.08;     // levels
    public const int ThumbW = 120, ThumbH = 68;
    public const int ThumbPixels = ThumbW * ThumbH;

    /// <summary>Raw reduction numbers: sum and max are in 1/64 level, counts are pixels.</summary>
    public static bool Same(uint sum64, uint max64, uint c1, uint c2)
    {
        double mean = sum64 / 64.0 / ThumbPixels;
        double max = max64 / 64.0;
        return mean < MeanLimit && max < MaxLimit && c1 == 0;
    }
}

/// <summary>Unique pictures per second over about the last second of frames.</summary>
internal sealed class GameRateMeter
{
    private readonly object _gate = new();
    private readonly Queue<(long Arrival, bool Unique)> _win = new();
    private int _uniq;
    private double _ema;
    private bool _emaValid;
    private long _lastArrival;
    private volatile bool _still, _valid;
    private double _fps;

    public const double WindowSec = 1.0;

    /// <summary>Unique pictures per second (smoothed); 0 until a window is full or while the picture is still.</summary>
    public double Fps { get { lock (_gate) return _valid && !_still ? _fps : 0; } }
    /// <summary>A rate was measured and the picture is moving.</summary>
    public bool Reliable => _valid && !_still;
    /// <summary>The picture did not change (at most one new picture in the window): a still screen, not 0 fps.</summary>
    public bool Still => _valid && _still;
    /// <summary>Stopwatch tick of the newest frame fed in.</summary>
    public long LastArrival => _lastArrival;

    public void Reset()
    {
        lock (_gate)
        {
            _win.Clear(); _uniq = 0; _emaValid = false; _valid = false; _still = false; _fps = 0; _lastArrival = 0;
        }
    }

    public void Add(long arrival, bool unique)
    {
        long hz = System.Diagnostics.Stopwatch.Frequency;
        lock (_gate)
        {
            _win.Enqueue((arrival, unique));
            if (unique) _uniq++;
            _lastArrival = arrival;
            while (_win.Count > 0 && arrival - _win.Peek().Arrival > (long)(WindowSec * hz))
            {
                if (_win.Dequeue().Unique) _uniq--;
            }
            int n = _win.Count;
            long first = _win.Peek().Arrival;
            double span = (arrival - first) / (double)hz;
            if (n < 12 || span < 0.6 * WindowSec) { _valid = false; return; }     // window not full yet
            double inFps = (n - 1) / span;                                         // frames per second received
            double fps = _uniq / (n / inFps);
            _still = _uniq <= 1;
            if (_still) { _emaValid = false; _fps = 0; _valid = true; return; }
            _ema = _emaValid ? _ema * 0.96 + fps * 0.04 : fps;
            _emaValid = true;
            _fps = _ema;
            _valid = true;
        }
    }
}

/// <summary>Timeline of the UNIQUE pictures of a stream whose pictures repeat. The smoothed arrival time S of the first
/// frame of each new picture is the picture's time, except that when pictures repeat (30 fps game in a 60 fps stream, or
/// 40 in 60 where they come 1, 2, 1, 2 frames apart) a second phase-locked loop on the unique-picture interval spaces
/// them evenly. When every frame is a new picture (no repeats) it returns S unchanged, i.e. the old behaviour.</summary>
internal sealed class UniqueTimeline
{
    private const int Window = 12;
    private readonly long[] _s = new long[Window];
    private readonly long[] _lag = new long[Window];
    private int _n, _pos;
    private double _lastU;
    private bool _have, _cad;
    private int _shortRun;                // consecutive unique pictures that came much sooner than the current period

    /// <summary>Mean spacing of the unique pictures (ticks); 0 until three have been seen.</summary>
    public double PeriodTicks { get; private set; }
    /// <summary>Pictures repeat (period at least 1.3 frames, until it falls under 1.15).</summary>
    public bool Cadence => _cad;
    /// <summary>How far the evened-out time of a picture lies EARLIER than its smoothed arrival, worst case of the window (ticks, >= 0):
    /// the picture can be needed that much before it exists, which the hold-back has to cover.</summary>
    public long PhaseLagTicks { get; private set; }

    public void Reset() { _n = 0; _pos = 0; _have = false; _cad = false; _shortRun = 0; PeriodTicks = 0; PhaseLagTicks = 0; }

    /// <summary>Time of the next unique picture, which has smoothed arrival <paramref name="smooth"/>.</summary>
    public long Resolve(long smooth, double nominalTicks)
    {
        if (nominalTicks <= 0) nominalTicks = 1;
        if (_n > 0)
        {
            long last = _s[(_pos + Window - 1) % Window];
            double gap = smooth - last;
            // a pause (still screen, loading): the window would span it and read as a very slow game
            if (gap > 8 * nominalTicks && gap > 4 * Math.Max(PeriodTicks, nominalTicks)) { Reset(); }
            else if (PeriodTicks > 0)
            {
                // the game changed its rate (3 pictures per frame-group to one per frame): two pictures in a row far sooner than the
                // period say so, and the old window would only smear the new rate over the next dozen pictures
                _shortRun = gap < 0.6 * PeriodTicks ? _shortRun + 1 : 0;
                if (_shortRun >= 2) Reset();
            }
        }
        _s[_pos] = smooth;
        _pos = (_pos + 1) % Window;
        if (_n < Window) _n++;

        double p = nominalTicks;
        if (_n >= 3)
        {
            long newest = smooth;
            long oldest = _s[(_pos + Window - _n) % Window];
            p = (newest - oldest) / (double)(_n - 1);
        }
        PeriodTicks = _n >= 3 ? p : 0;
        double ratio = p / nominalTicks;
        if (!_cad && _n >= 3 && ratio >= 1.3) _cad = true;
        else if (_cad && (ratio < 1.15 || _n < 3)) _cad = false;

        double u;
        if (!_cad || !_have) u = smooth;
        else
        {
            double pred = _lastU + p;
            double err = smooth - pred;
            u = Math.Abs(err) > 2.5 * p ? smooth : pred + 0.15 * err;
            if (u < _lastU + 0.3 * p) u = _lastU + 0.3 * p;
        }
        _lastU = u;
        _have = true;
        long lag = Math.Max(0L, smooth - (long)u);
        _lag[(_pos + Window - 1) % Window] = lag;
        long worst = 0;
        for (int i = 0; i < _n; i++) worst = Math.Max(worst, _lag[(_pos + Window - 1 - i) % Window]);
        PhaseLagTicks = _cad ? worst : 0;
        return (long)u;
    }
}

/// <summary>One frame of the ring as the pair selection sees it.</summary>
internal readonly record struct TimelineEntry(long Seq, long Smooth, long U, bool Dup);

internal static class PairSelect
{
    /// <summary>Picks the pair (A, B) around time <paramref name="t"/> and the blend <paramref name="alpha"/>.
    /// <paramref name="uniqueAware"/>: repeats are skipped, unique pictures use their own timeline U; otherwise the
    /// former behaviour (A = newest with S &lt;= t, B = the next frame). Returns the index of A in
    /// <paramref name="e"/>, or -1 when it is empty. <paramref name="b"/> is -1 when A has no successor yet.</summary>
    public static int Pick(IReadOnlyList<TimelineEntry> e, long t, bool uniqueAware, double periodTicks, double nominalTicks,
        out int b, out double alpha)
    {
        b = -1; alpha = 0;
        if (e.Count == 0) return -1;
        int a = -1, oldest = -1, oldestAny = -1, newestAny = -1;
        for (int i = 0; i < e.Count; i++)
        {
            TimelineEntry s = e[i];
            if (oldestAny < 0 || s.Seq < e[oldestAny].Seq) oldestAny = i;
            if (s.Smooth <= t && (newestAny < 0 || s.Seq > e[newestAny].Seq)) newestAny = i;
            if (uniqueAware && s.Dup) continue;
            long time = uniqueAware ? s.U : s.Smooth;
            if (oldest < 0 || s.Seq < e[oldest].Seq) oldest = i;
            if (time <= t && (a < 0 || s.Seq > e[a].Seq)) a = i;
        }
        if (a < 0 && uniqueAware) a = newestAny;           // nothing but repeats is old enough (a still picture): any frame of it
        if (a < 0) a = oldest >= 0 ? oldest : oldestAny;
        if (a < 0) return -1;

        if (uniqueAware)
        {
            for (int i = 0; i < e.Count; i++)
            {
                if (e[i].Dup || e[i].Seq <= e[a].Seq) continue;
                if (b < 0 || e[i].Seq < e[b].Seq) b = i;
            }
        }
        else
        {
            for (int i = 0; i < e.Count; i++) if (e[i].Seq == e[a].Seq + 1) { b = i; break; }
        }
        if (b < 0) return a;

        long ta = uniqueAware ? e[a].U : e[a].Smooth, tb = uniqueAware ? e[b].U : e[b].Smooth;
        double span = tb - ta;
        if (uniqueAware)
        {
            // a long wait for the next new picture (a hitch, a still screen that starts moving): blend over the last
            // few intervals before it instead of crawling from the old picture across the whole wait
            double maxSpan = Math.Max(2.5 * periodTicks, 3 * nominalTicks);
            if (span > maxSpan) span = maxSpan;
            alpha = span > 0 ? Math.Clamp(1.0 - (tb - t) / span, 0.0, 1.0) : (t >= tb ? 1.0 : 0.0);
        }
        else
        {
            alpha = span > 0 ? Math.Clamp((t - ta) / span, 0.0, 1.0) : (t >= tb ? 1.0 : 0.0);
        }
        return a;
    }
}

/// <summary>The extra hold-back of the interpolation while pictures repeat, as the render loop applies it: it rises quickly (the picture
/// then runs at half speed for a moment instead of waiting for a picture that is not there yet) and falls slowly (0.1 ms per refresh, i.e.
/// the picture runs 1% fast) so the timeline is not jerked when the estimate wobbles.</summary>
internal sealed class HoldGovernor
{
    private double _v;
    public double Value => _v;
    public void Reset() => _v = 0;
    /// <param name="wantMs">The extra hold-back the estimates ask for.</param>
    /// <param name="refreshMs">One display refresh (ms): the rise per refresh is half of it.</param>
    public double Step(double wantMs, double refreshMs)
    {
        _v = wantMs > _v ? Math.Min(wantMs, _v + 0.5 * Math.Max(refreshMs, 2.0)) : Math.Max(wantMs, _v - 0.1);
        return _v;
    }
}
