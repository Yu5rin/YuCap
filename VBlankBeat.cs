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
/// Every successful <see cref="Wait"/> counts the vblanks since the previous one (<see cref="Count"/>,
/// <see cref="LastQpc"/>): normally one, more when the loop was too slow to come back within a refresh (a
/// composed monitor can hold Present back to 60 Hz while the panel runs at 144 Hz). The pacing measurement and
/// the latency estimate use this as a clock that does not depend on GetFrameStatistics.
/// A new output is checked once: its vblank period must match the refresh rate Windows reports for the display
/// (some drivers answer WaitForVBlank from another display's clock), otherwise the beat is given up.
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
    private bool _verified;              // the period of this output was compared with the display's refresh rate
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
        _out = null; _name = string.Empty; _failed = false; _verified = false; _fast = 0; _errors = 0;
        _monitor = monitor;
        Volatile.Write(ref _lastQpc, 0);   // the time since the last vblank of the old output says nothing here
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

    /// <summary>A handful of back-to-back waits: their spacing must be the refresh period Windows reports for the display
    /// (within 15%). False (after <see cref="Fail"/>) when this output's vblank runs on another clock.</summary>
    private bool Verify(IDXGIOutput o, IntPtr hwnd)
    {
        int hz = Displays.RefreshHzOfWindow(hwnd);
        if (hz < 20) return true;   // unknown: nothing to compare with
        var iv = new double[9];
        try
        {
            o.WaitForVBlank();
            long prev = Stopwatch.GetTimestamp();
            for (int i = 0; i < iv.Length; i++)
            {
                o.WaitForVBlank();
                long t = Stopwatch.GetTimestamp();
                iv[i] = (t - prev) * 1000.0 / Stopwatch.Frequency;
                prev = t;
            }
        }
        catch (Exception ex) { Fail($"verify hr=0x{ex.HResult:X8}"); return false; }
        Array.Sort(iv);
        double med = iv[iv.Length / 2];
        double want = 1000.0 / hz;
        // Windows reports 59.94 Hz as 59 or 60, so allow for that on top of the 15%.
        if (med < want / 1.15 - 0.3 || med > want * 1.15 + 0.3)
        {
            Fail($"its vblank period is {med:0.0} ms but the display runs at {hz} Hz ({want:0.0} ms)");
            return false;
        }
        Volatile.Write(ref _lastQpc, 0);   // this check blocked for a while: not a gap in the render loop
        Log1($"D3D: pacing beat verified - {med:0.00} ms per vblank ({1000.0 / med:0.0} Hz), display {hz} Hz");
        return true;
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
        if (!_verified)
        {
            _verified = true;
            if (!Verify(o, hwnd)) return false;
        }

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
        long seen = 1;   // vblanks since the previous return
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
            else if (iv >= rough * 1.3)
            {
                // The loop came back later than one refresh: the vblanks in between happened all the same.
                double p = _periodMs > 1 ? _periodMs : rough;
                seen = Math.Max(1, (long)Math.Round(iv / p));
            }
        }
        Volatile.Write(ref _lastQpc, t1);
        Interlocked.Add(ref _count, seen);
        return true;
    }

    public void Dispose()
    {
        try { _out?.Dispose(); } catch { }
        _out = null;
    }
}
