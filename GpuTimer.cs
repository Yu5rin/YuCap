using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vortice.Direct3D11;

namespace YuCap;

/// <summary>
/// GPU time of a span of rendering commands, measured with D3D11 timestamp queries. A small ring of
/// query sets lets several frames be in flight, and results are only ever collected with
/// DONOTFLUSH (never blocking): a set whose answer is not ready yet is simply looked at again on
/// a later call, and when every set is still pending the frame goes unmeasured rather than
/// stalling the pipeline. Render thread only.
/// </summary>
internal sealed class GpuTimer : IDisposable
{
    private sealed class QuerySet
    {
        public ID3D11Query Disjoint = null!, Start = null!, End = null!;
        public bool Pending;
    }

    private readonly ID3D11DeviceContext _ctx;
    private readonly QuerySet[] _sets;
    private int _next;
    private QuerySet? _open;                       // between Begin and End
    private readonly Queue<(long Tick, double Ms)> _window = new();
    private readonly List<double> _fresh = new();  // completed since the last TakeSamples

    public GpuTimer(ID3D11Device device, ID3D11DeviceContext ctx, int ringSize = 4)
    {
        _ctx = ctx;
        _sets = new QuerySet[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            _sets[i] = new QuerySet
            {
                Disjoint = device.CreateQuery(QueryType.TimestampDisjoint, QueryFlags.None),
                Start = device.CreateQuery(QueryType.Timestamp, QueryFlags.None),
                End = device.CreateQuery(QueryType.Timestamp, QueryFlags.None),
            };
        }
    }

    public bool IsOpen => _open != null;

    /// <summary>Start measuring. Does nothing when a span is already open (so the caller may call it
    /// at the top of several code paths) or when no query set is free. Returns whether a span is open.</summary>
    public bool Begin()
    {
        if (_open != null) return true;
        Poll();
        QuerySet s = _sets[_next];
        if (s.Pending) return false;               // the oldest answer is still not back: skip this frame
        _ctx.Begin(s.Disjoint);
        _ctx.End(s.Start);
        _open = s;
        return true;
    }

    /// <summary>Finish the open span (no-op when none is open).</summary>
    public void End()
    {
        QuerySet? s = _open;
        if (s == null) return;
        _ctx.End(s.End);
        _ctx.End(s.Disjoint);
        s.Pending = true;
        _open = null;
        _next = (_next + 1) % _sets.Length;
    }

    /// <summary>Drop the open span without recording it (the frame's work was abandoned).</summary>
    public void Cancel()
    {
        QuerySet? s = _open;
        if (s == null) return;
        _ctx.End(s.Disjoint);
        _open = null;
    }

    /// <summary>Collect every finished set, oldest first, without blocking.</summary>
    public void Poll()
    {
        for (int k = 0; k < _sets.Length; k++)
        {
            QuerySet s = _sets[(_next + k) % _sets.Length];
            if (!s.Pending) continue;
            if (!_ctx.GetData(s.Disjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint dj)) break;
            if (!_ctx.GetData(s.Start, AsyncGetDataFlags.DoNotFlush, out ulong t0)
                || !_ctx.GetData(s.End, AsyncGetDataFlags.DoNotFlush, out ulong t1)) break;
            s.Pending = false;
            if (dj.Disjoint || dj.Frequency == 0 || t1 < t0) continue;   // clock changed during the span: discard
            double ms = (t1 - t0) * 1000.0 / dj.Frequency;
            long now = Stopwatch.GetTimestamp();
            _window.Enqueue((now, ms));
            _fresh.Add(ms);
        }
        long cutoff = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        while (_window.Count > 0 && _window.Peek().Tick < cutoff) _window.Dequeue();
    }

    /// <summary>Average and maximum over roughly the last second (0, 0 when nothing was measured).</summary>
    public void Recent(out double avg, out double max)
    {
        avg = max = 0;
        if (_window.Count == 0) return;
        double sum = 0;
        foreach ((long _, double ms) in _window) { sum += ms; if (ms > max) max = ms; }
        avg = sum / _window.Count;
    }

    /// <summary>Measurements completed since the last call (used by the benchmark).</summary>
    public List<double> TakeSamples()
    {
        var r = new List<double>(_fresh);
        _fresh.Clear();
        return r;
    }

    /// <summary>True while any set still waits for its answer.</summary>
    public bool AnyPending
    {
        get { foreach (QuerySet s in _sets) if (s.Pending) return true; return false; }
    }

    public void Dispose()
    {
        foreach (QuerySet s in _sets) { s.Disjoint.Dispose(); s.Start.Dispose(); s.End.Dispose(); }
    }
}
