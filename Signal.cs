using System;
using System.Diagnostics;

namespace YuCap;

/// <summary>Whether the console is sending a picture: the capture card draws its own black "No Signal" screen (a little white
/// text and an icon) when the console is off, and the real picture otherwise. <c>Unknown</c> until one of the two was seen.</summary>
public enum SignalState { Unknown, NoSignal, Signal }

/// <summary>The per-frame tests behind <see cref="SignalDetector"/>, on the 120x68 luma thumbnail the same-picture pass makes
/// (Shaders/dup.hlsl: Stat[4] = pixels darker than 24 levels, Stat[5] = pixels brighter than 40 levels).</summary>
internal static class SignalMath
{
    public const double DarkMin = 0.85;          // at least this share of the thumbnail is black ...
    public const double LitMin = 0.0005;         // ... with a little bright text (the card's own message) ...
    public const double LitMax = 0.20;           // ... but not much of it
    public const double BlackMin = 0.99;         // a picture that is black all over (what some cards / HDMI states send with nothing connected)
    public const double StillMeanMax = 0.5;      // levels: the mean thumbnail difference to the frame before

    public static bool MessageLike(uint dark, uint lit)
    {
        double n = DupMath.ThumbPixels;
        double fd = dark / n, fl = lit / n;
        return fd >= DarkMin && fl >= LitMin && fl <= LitMax;
    }

    public static bool BlackAll(uint dark, uint lit)
    {
        double n = DupMath.ThumbPixels;
        return dark / n >= BlackMin && lit / n < LitMin;
    }

    /// <summary>The picture hardly differs from the frame before: mean difference (sum is in 1/64 level) under a limit and no
    /// thumbnail pixel changed by more than the "clearly changed" threshold.</summary>
    public static bool Still(uint sum64, uint c2) => sum64 / 64.0 / DupMath.ThumbPixels < StillMeanMax && c2 == 0;
}

/// <summary>
/// No signal for at least <see cref="NoSignalSec"/> in a row (every frame the card's message, unchanging; <see cref="BlackSec"/> when it
/// is plain black instead, which a game's own black screen also is) -> <see cref="SignalState.NoSignal"/>;
/// anything else for <see cref="SignalSec"/> in a row -> <see cref="SignalState.Signal"/>. <see cref="SavingPower"/> turns on
/// <see cref="PowerSaveAfterSec"/> into a no-signal stretch and off with the first frame that is not the message.
/// Fed from the thumbnail answers (capture/render threads); read from any thread.
/// </summary>
public sealed class SignalDetector
{
    public const double NoSignalSec = 2.0;
    public const double BlackSec = 3.0;
    public const double SignalSec = 1.5;
    public const double PowerSaveAfterSec = 5.0;

    private readonly object _gate = new();
    private SignalState _state = SignalState.Unknown;
    private long _msgSince, _realSince, _noSignalSince, _lastFeed;
    private bool _lastMessage, _runHasMessage;
    private long _changes;

    private static long Ticks(double sec) => (long)(sec * Stopwatch.Frequency);

    /// <param name="message">the card's message (black with a little bright text)</param>
    /// <param name="black">plain black all over</param>
    /// <param name="still">unchanged from the frame before</param>
    internal void Feed(long tick, bool message, bool black, bool still)
    {
        lock (_gate)
        {
            bool empty = message || black;       // "nothing is being sent"
            _lastFeed = tick; _lastMessage = empty;
            if (empty)
            {
                _realSince = 0;
                if (_state == SignalState.NoSignal) return;                 // the card keeps showing it
                if (!still) { _msgSince = 0; _runHasMessage = false; return; }
                if (_msgSince == 0) { _msgSince = tick; _runHasMessage = false; }
                if (message) _runHasMessage = true;
                if (tick - _msgSince >= Ticks(_runHasMessage ? NoSignalSec : BlackSec)) Set(SignalState.NoSignal, tick);
            }
            else
            {
                _msgSince = 0; _runHasMessage = false;
                if (_realSince == 0) _realSince = tick;
                if (_state != SignalState.Signal && tick - _realSince >= Ticks(SignalSec)) Set(SignalState.Signal, tick);
            }
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

    /// <summary>No signal for more than <see cref="PowerSaveAfterSec"/>, and the card's message is still what arrives: the renderer
    /// can stop all expensive work. Ends with the first frame that is anything else.</summary>
    public bool SavingPower(long now)
    {
        lock (_gate) return _state == SignalState.NoSignal && _lastMessage && now - _noSignalSince >= Ticks(PowerSaveAfterSec);
    }

    /// <summary>Test hook: start over.</summary>
    internal void Reset() { lock (_gate) { _state = SignalState.Unknown; _msgSince = _realSince = _noSignalSince = _lastFeed = 0; _lastMessage = _runHasMessage = false; } }
}
