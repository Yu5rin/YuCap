using System;
using System.Diagnostics;

namespace YuCap;

/// <summary>Whether the console is sending a picture: the capture card draws its own black "No Signal" screen (a little white
/// text and an icon) when the console is off, and the real picture otherwise. <c>Unknown</c> until one of the two was seen.</summary>
public enum SignalState { Unknown, NoSignal, Signal }

/// <summary>What one frame's thumbnail says (Shaders/dup.hlsl).</summary>
/// <param name="Message">black with a little bright text, like the card's own No Signal screen</param>
/// <param name="Black">black all over: not one thumbnail pixel above the video-range black</param>
/// <param name="Exact">bit-for-bit the frame before (what the card's own screen is; a picture through a converter carries noise)</param>
/// <param name="Changed">clearly different from the frame before (above the noise of an analog source)</param>
internal readonly record struct SignalFrame(bool Message, bool Black, bool Exact, bool Changed);

/// <summary>The per-frame tests behind <see cref="SignalDetector"/>, on the 120x68 luma thumbnail the same-picture pass makes
/// (Shaders/dup.hlsl: Stat[4] = pixels darker than 24 levels, Stat[5] = pixels brighter than 40 levels, Stat[6] = pixels of 19 levels or more).</summary>
internal static class SignalMath
{
    public const double DarkMin = 0.85;          // at least this share of the thumbnail is black ...
    public const double LitMin = 0.0005;         // ... with a little bright text (the card's own message) ...
    public const double LitMax = 0.20;           // ... but not much of it
    public const double StillMeanMax = 0.5;      // levels: the mean thumbnail difference to the frame before that still counts as "no change" (analog noise)
    public const double ExactMeanMax = 0.005;    // levels: what the card's own screen differs by (nothing); analog noise is ten times that or more

    public static bool MessageLike(uint dark, uint lit)
    {
        double n = DupMath.ThumbPixels;
        double fd = dark / n, fl = lit / n;
        return fd >= DarkMin && fl >= LitMin && fl <= LitMax;
    }

    /// <summary>Black all over: no thumbnail pixel above the black level (a dark scene with any detail is not).</summary>
    public static bool BlackAll(uint nonBlack) => nonBlack == 0;

    /// <summary>The picture hardly differs from the frame before: mean difference (sum is in 1/64 level) under a limit and no
    /// thumbnail pixel changed by more than the "clearly changed" threshold. Noise-tolerant.</summary>
    public static bool Still(uint sum64, uint c2) => sum64 / 64.0 / DupMath.ThumbPixels < StillMeanMax && c2 == 0;

    /// <summary>The same picture to the bit (up to rounding): nothing changed by even one level.</summary>
    public static bool Exact(uint sum64, uint c1) => sum64 / 64.0 / DupMath.ThumbPixels < ExactMeanMax && c1 == 0;
}

/// <summary>
/// Whether the console is sending a picture: conservative towards "no signal", quick towards "signal".
/// <see cref="SignalState.NoSignal"/> only for: the card's own message (black with a little bright text, bit-exact frame after frame) for
/// <see cref="NoSignalSec"/>; black all over for <see cref="BlackSec"/>; or no frames at all (<see cref="Silent"/>, called by the render loop).
/// A dark scene with any detail, a logo through a converter (analog noise) or anything that moves is never one of these.
/// <see cref="SignalState.Signal"/> as soon as the picture is anything else, or changes: <see cref="SignalSec"/> of such frames.
/// <see cref="SavingPower"/> turns on <see cref="PowerSaveAfterSec"/> into a no-signal stretch and off with the first frame that is not the
/// card's message. Fed from the thumbnail answers (capture/render threads); read from any thread.
/// </summary>
public sealed class SignalDetector
{
    public const double NoSignalSec = 2.0;
    public const double BlackSec = 5.0;
    public const double SignalSec = 0.25;
    public const double SilentSec = 2.0;
    public const double PowerSaveAfterSec = 5.0;
    /// <summary>After a hit ended the power saving, it only starts again after this long without any hit (single repeated frames of a slower game do not count).</summary>
    public const double PowerSaveReentrySec = 2.0;

    private readonly object _gate = new();
    private SignalState _state = SignalState.Unknown;
    private long _msgSince, _blackSince, _realSince, _lastHit, _noSignalSince, _lastFeed;
    private int _hits;
    private bool _lastEmpty;
    private long _changes;

    private static long Ticks(double sec) => (long)(sec * Stopwatch.Frequency);

    internal void Feed(long tick, SignalFrame f)
    {
        lock (_gate)
        {
            _lastFeed = tick;
            bool card = f.Message && f.Exact && !f.Black;
            bool empty = card || f.Black;                    // "nothing is being sent"
            bool hit = !empty || f.Changed;                  // anything but the card's screen, or any change from the frame before
            _lastEmpty = !hit;
            if (hit)
            {
                _msgSince = _blackSince = 0;
                if (_realSince == 0 || tick - _lastHit > Ticks(0.5)) { _realSince = tick; _hits = 0; }
                _lastHit = tick; _hits++;
                if (_state != SignalState.Signal && _hits >= 2 && tick - _realSince >= Ticks(SignalSec)) Set(SignalState.Signal, tick);
                return;
            }
            if (_realSince != 0 && tick - _lastHit > Ticks(0.5)) { _realSince = 0; _hits = 0; }
            if (_state == SignalState.NoSignal) return;      // the card keeps showing it
            if (card)
            {
                _blackSince = 0;
                if (_msgSince == 0) _msgSince = tick;
                if (tick - _msgSince >= Ticks(NoSignalSec)) Set(SignalState.NoSignal, tick);
            }
            else
            {
                _msgSince = 0;
                if (_blackSince == 0) _blackSince = tick;
                if (tick - _blackSince >= Ticks(BlackSec)) Set(SignalState.NoSignal, tick);
            }
        }
    }

    /// <summary>Frames have stopped arriving (the render loop calls this): after <see cref="SilentSec"/> of nothing the console is not sending.</summary>
    internal void Silent(long now)
    {
        lock (_gate)
        {
            if (_state == SignalState.NoSignal || _lastFeed == 0 || now - _lastFeed < Ticks(SilentSec)) return;
            Set(SignalState.NoSignal, now);
            _lastEmpty = false;                              // no card message on screen to keep: nothing to save power on
        }
    }

    private void Set(SignalState s, long tick)
    {
        _state = s; _changes++;
        if (s == SignalState.NoSignal) _noSignalSince = tick;
    }

    public SignalState State { get { lock (_gate) return _state; } }

    /// <summary>Number of state changes so far (a poller that sees it move knows a transition happened, even a quick there-and-back).</summary>
    public long Changes { get { lock (_gate) return _changes; } }

    /// <summary>No signal for more than <see cref="PowerSaveAfterSec"/>, and the card's message (or black) is still what arrives: the
    /// renderer can stop all expensive work. Ends with the first frame that is anything else or differs from the one before, and does not resume before
    /// <see cref="PowerSaveReentrySec"/> without such a frame (no flapping on a game that repeats every other frame).</summary>
    public bool SavingPower(long now)
    {
        lock (_gate)
        {
            if (_state != SignalState.NoSignal || !_lastEmpty || now - _noSignalSince < Ticks(PowerSaveAfterSec)) return false;
            // a hit since the no-signal verdict ended the saving: stay in normal rendering until the picture has been quiet for a full re-entry period
            if (_lastHit > _noSignalSince && now - _lastHit < Ticks(PowerSaveReentrySec)) return false;
            return true;
        }
    }

    /// <summary>Test hook: start over.</summary>
    internal void Reset() { lock (_gate) { _state = SignalState.Unknown; _msgSince = _blackSince = _realSince = _lastHit = _noSignalSince = _lastFeed = 0; _hits = 0; _lastEmpty = false; } }
}
