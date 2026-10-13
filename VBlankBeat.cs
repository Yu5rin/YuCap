using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuCap;

/// <summary>
/// The once-per-refresh beat of the display-driven render loop: the vertical blank of the output the canvas
/// window is on (<c>IDXGIOutput.WaitForVBlank</c>). The output is found by matching its monitor handle with
/// <c>MonitorFromWindow</c> over the outputs of the render device's adapter (a composition swap chain has no
/// usable GetContainingOutput), and is looked up again when the window's monitor changes.
///
/// Every successful <see cref="Wait"/> counts one vblank (<see cref="Count"/>, <see cref="LastQpc"/>), which the
/// pacing measurement and the latency estimate use as a clock that does not depend on GetFrameStatistics.
/// When no output can be found (the monitor hangs off another GPU) or WaitForVBlank misbehaves (fails, returns
/// at once repeatedly, or blocks far too long) the beat reports itself inactive and the caller keeps the older
/// DwmFlush / latency-waitable pacing. Render thread only (except reading the counters).
/// </summary>
internal sealed class VBlankBeat : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private IDXGIOutput? _out;
    private IntPtr _monitor;
    private string _name = string.Empty;
    private long _nextCheck;             // Environment.TickCount64 at which the monitor is compared again
    private bool _failed;                // gave up on this output (until the monitor changes)
    private int _fast, _errors;
    private string? _lastLogged;
    private long _count, _lastQpc;
    private int _epoch;
    private double _periodMs;

    /// <summary>Vblanks seen so far (monotonic over the life of the renderer).</summary>
    public long Count => Volatile.Read(ref _count);
    /// <summary>QPC time at which the last vblank was observed (the wait returned).</summary>
    public long LastQpc => Volatile.Read(ref _lastQpc);
    /// <summary>Changes whenever the beat moves to another output, so index differences across the change are not trusted.</summary>
    public int Epoch => Volatile.Read(ref _epoch);
    /// <summary>Measured refresh period in ms (smoothed); 0 until known.</summary>
    public double PeriodMs => Volatile.Read(ref _periodMs);
    public string OutputName => _name;
    public bool Active => _out != null && !_failed;

    /// <summary>Compare the window's monitor again at the next <see cref="Wait"/> (call on resize / move).</summary>
    public void Recheck() { _nextCheck = 0; }

    /// <summary>The current mark for <see cref="PacingCapture"/> (Index &lt; 0 when no beat is active).</summary>
    public VBlankMark Mark() => Active ? new VBlankMark(Count, LastQpc, Epoch) : new VBlankMark(-1, 0, Epoch);

    private void Log1(string msg)
    {
        if (_lastLogged == msg) return;
        _lastLogged = msg;
        Log.Info(msg);
    }

    private void Resolve(ID3D11Device device, IntPtr monitor)
    {
        IDXGIOutput? old = _out;
        _out = null; _name = string.Empty; _failed = false; _fast = 0; _errors = 0;
        _monitor = monitor;
        old?.Dispose();
        Interlocked.Increment(ref _epoch);
        try
        {
            if (monitor == IntPtr.Zero) { Log1("D3D: pacing beat = none (window has no monitor) - using DWM / latency waitable"); return; }
            using IDXGIDevice dxgi = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgi.GetAdapter();
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output != null; o++)
            {
                bool match = false;
                try { match = output.Description.Monitor == monitor; } catch { }
                if (match)
                {
                    _out = output;
                    _name = output.Description.DeviceName;
                    Log1($"D3D: pacing beat = vblank of {_name}");
                    return;
                }
                output.Dispose();
            }
            Log1("D3D: pacing beat = none (the window's monitor is not on the render GPU) - using DWM / latency waitable");
        }
        catch (Exception ex)
        {
            Log1($"D3D: pacing beat lookup failed ({ex.Message.Split('\n')[0]}) - using DWM / latency waitable");
        }
    }

    private void Fail(string why)
    {
        _failed = true;
        Log1($"D3D: pacing beat = vblank of {_name} gave up ({why}) - using DWM / latency waitable");
    }

    /// <summary>Block until the next vertical blank of the window's output. Returns false when no beat is available
    /// (the caller then paces the old way); it has not waited in that case.</summary>
    public bool Wait(ID3D11Device device, IntPtr hwnd, double roughPeriodMs)
    {
        long tick = Environment.TickCount64;
        if (tick >= _nextCheck)
        {
            _nextCheck = tick + 1000;
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon != _monitor) Resolve(device, mon);
        }
        IDXGIOutput? o = _out;
        if (o == null || _failed) return false;

        long t0 = Stopwatch.GetTimestamp();
        try { o.WaitForVBlank(); }
        catch (Exception ex)
        {
            if (++_errors >= 3) Fail($"hr=0x{ex.HResult:X8}");
            return false;
        }
        long t1 = Stopwatch.GetTimestamp();
        double waitedMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        _errors = 0;
        if (waitedMs > 250) { Fail($"blocked {waitedMs:0} ms"); return false; }

        long last = Volatile.Read(ref _lastQpc);
        if (last != 0)
        {
            double iv = (t1 - last) * 1000.0 / Stopwatch.Frequency;
            double rough = roughPeriodMs > 1 ? roughPeriodMs : 16.7;
            if (iv < rough * 0.4) { if (++_fast >= 10) { Fail("returns at once"); return false; } }
            else _fast = 0;
            if (iv > rough * 0.7 && iv < rough * 1.3)
            {
                double p = _periodMs;
                Volatile.Write(ref _periodMs, p <= 0 ? iv : p * 0.97 + iv * 0.03);
            }
        }
        Volatile.Write(ref _lastQpc, t1);
        Interlocked.Increment(ref _count);
        return true;
    }

    public void Dispose()
    {
        try { _out?.Dispose(); } catch { }
        _out = null;
    }
}
