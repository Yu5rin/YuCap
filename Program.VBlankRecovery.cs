using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using Size = System.Drawing.Size;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace YuCap;

//   --vblank-probe <out.txt> --exp variants,recovery,triggers,format [--display NAME] [--reps N] [--gap ms] [--seed N] [--overlay layered|opaque|menu]
// Does DWM drop a fullscreen flip-model swap chain to composed presentation (and a slower rate) once something overlaps it,
// and what brings it back? Everything runs on one display, in randomised order, with an idle gap between runs.
internal static partial class Program
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rc, IntPtr rgn, uint flags);

    /// <summary>A fullscreen window on one display with a flip-model swap chain presented in a loop from a worker thread; every present is time-stamped.</summary>
    internal sealed class PresentSession : IDisposable
    {
        public readonly Form Form;
        public readonly DisplayInfo Disp;
        private readonly ID3D11Device _dev;
        private readonly IDXGIDevice1 _dxgiDev;
        private readonly IDXGIFactory2 _fac;
        private IDXGISwapChain1 _sc;
        private IDXGISwapChain2 _sc2;
        private IntPtr _waitable;
        private IDCompositionDevice? _dc; private IDCompositionTarget? _tg; private IDCompositionVisual? _vis;
        private readonly bool _composition;
        private Format _fmt;
        private readonly object _frame = new();
        private readonly List<long> _stamps = new();
        private volatile bool _stop;
        private readonly Thread _worker;
        public readonly long T0 = Stopwatch.GetTimestamp();
        public int Width, Height;

        public PresentSession(DisplayInfo disp, bool composition, Format fmt)
        {
            Disp = disp; _composition = composition; _fmt = fmt;
            Form = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = disp.Bounds,
                ShowInTaskbar = false, TopMost = true, BackColor = Color.Black,
            };
            Form.Show();
            PumpFor(300);
            Size cs = Form.ClientSize; Width = cs.Width; Height = cs.Height;
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { D3DFeatureLevel.Level_11_0 }, out ID3D11Device? dev).CheckError();
            _dev = dev!;
            _dxgiDev = _dev.QueryInterface<IDXGIDevice1>();
            _dxgiDev.SetMaximumFrameLatency(1);
            using IDXGIAdapter ad = _dxgiDev.GetAdapter();
            _fac = ad.GetParent<IDXGIFactory2>();
            _sc = CreateChain();
            _sc2 = _sc.QueryInterface<IDXGISwapChain2>();
            _sc2.MaximumFrameLatency = 1;
            _waitable = _sc2.FrameLatencyWaitableObject;
            _worker = new Thread(Loop) { IsBackground = true };
            _worker.Start();
        }

        private IDXGISwapChain1 CreateChain()
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)Width, Height = (uint)Height, Format = _fmt, SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 3, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore, Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            if (_composition)
            {
                IDXGISwapChain1 sc = _fac.CreateSwapChainForComposition(_dev, desc);
                if (_dc == null)
                {
                    _dc = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDev);
                    _dc.CreateTargetForHwnd(Form.Handle, false, out _tg);
                    _dc.CreateVisual(out _vis);
                    _tg!.SetRoot(_vis);
                }
                _vis!.SetContent(sc);
                _dc.Commit();
                return sc;
            }
            return _fac.CreateSwapChainForHwnd(_dev, Form.Handle, desc);
        }

        private void Loop()
        {
            int n = 0;
            while (!_stop)
            {
                try
                {
                    WaitForSingleObject(_waitable, 100);
                    lock (_frame)
                    {
                        using (ID3D11Texture2D bb = _sc.GetBuffer<ID3D11Texture2D>(0))
                        using (ID3D11RenderTargetView rtv = _dev.CreateRenderTargetView(bb))
                        {
                            float v = (n++ & 1) == 0 ? 0.2f : 0.6f;
                            _dev.ImmediateContext.ClearRenderTargetView(rtv, new Color4(v, v, v, 1f));
                        }
                        _dev.ImmediateContext.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                        if (_sc.Present(1, PresentFlags.None).Failure) { Thread.Sleep(5); continue; }
                    }
                    long t = Stopwatch.GetTimestamp();
                    lock (_stamps) _stamps.Add(t);
                }
                catch { Thread.Sleep(5); }
            }
        }

        public long Now => Stopwatch.GetTimestamp();

        /// <summary>Presents per second between two time stamps.</summary>
        public double Fps(long from, long to)
        {
            if (to <= from) return 0;
            int c; lock (_stamps) c = _stamps.Count(t => t >= from && t < to);
            return c * (double)Stopwatch.Frequency / (to - from);
        }

        public string Bins(long from, double seconds, int binMs = 250)
        {
            var sb = new StringBuilder();
            long step = Stopwatch.Frequency * binMs / 1000;
            for (int i = 0; i < seconds * 1000 / binMs; i++) sb.Append(((int)Math.Round(Fps(from + i * step, from + (i + 1) * step))).ToString()).Append(' ');
            return sb.ToString().TrimEnd();
        }

        /// <summary>Seconds from <paramref name="from"/> until two consecutive 250 ms bins reach 90 % of <paramref name="target"/>; -1 = never within <paramref name="seconds"/>.</summary>
        public double RecoverySeconds(long from, double seconds, double target)
        {
            long step = Stopwatch.Frequency / 4;
            int bins = (int)(seconds * 4);
            for (int i = 0; i + 1 < bins; i++)
                if (Fps(from + i * step, from + (i + 1) * step) >= target * 0.9 && Fps(from + (i + 1) * step, from + (i + 2) * step) >= target * 0.9) return i * 0.25;
            return -1;
        }

        public void Wait(int ms) => PumpFor(ms);

        /// <summary>Wait until the rate is at least 90 % of <paramref name="target"/> for 1 s, at most <paramref name="maxMs"/>; returns the seconds it took (-1 = not reached).</summary>
        public double WaitFull(double target, int maxMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxMs)
            {
                PumpFor(250);
                long n = Now;
                if (sw.ElapsedMilliseconds >= 1200 && Fps(n - Stopwatch.Frequency, n) >= target * 0.9) return sw.ElapsedMilliseconds / 1000.0 - 1.0;
            }
            return -1;
        }

        public void Trigger(string t)
        {
            switch (t)
            {
                case "none": break;
                case "resize-same":
                    lock (_frame) _sc.ResizeBuffers(0, (uint)Width, (uint)Height, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
                    break;
                case "resize-1px":
                    lock (_frame)
                    {
                        _sc.ResizeBuffers(0, (uint)Width - 1, (uint)Height, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
                        _sc.ResizeBuffers(0, (uint)Width, (uint)Height, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
                    }
                    break;
                case "srcsize":
                    lock (_frame) { _sc2.SourceSize = new Vortice.Mathematics.SizeI(Width - 1, Height); _sc2.SourceSize = new Vortice.Mathematics.SizeI(Width, Height); }
                    break;
                case "recreate":
                    lock (_frame)
                    {
                        IDXGISwapChain1 old = _sc; IDXGISwapChain2 old2 = _sc2;
                        _sc = CreateChain();
                        _sc2 = _sc.QueryInterface<IDXGISwapChain2>();
                        _sc2.MaximumFrameLatency = 1;
                        _waitable = _sc2.FrameLatencyWaitableObject;
                        old2.Dispose(); old.Dispose();
                    }
                    break;
                case "recommit":
                    if (_vis != null) { lock (_frame) { _vis.SetContent(_sc); _vis.SetOffsetX(1f); _dc!.Commit(); _vis.SetOffsetX(0f); _dc.Commit(); } }
                    break;
                case "winsize":
                    Form.Size = new Size(Form.Width, Form.Height + 1); PumpFor(50); Form.Size = new Size(Form.Width, Form.Height - 1); PumpFor(50);
                    break;
                case "setwindowpos":
                    SetWindowPos(Form.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020 | 0x0010);   // NOSIZE NOMOVE NOZORDER FRAMECHANGED NOACTIVATE
                    RedrawWindow(Form.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);          // INVALIDATE ERASE ALLCHILDREN UPDATENOW
                    break;
                case "topmost":
                    SetWindowPos(Form.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_NOTOPMOST
                    SetWindowPos(Form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_TOPMOST
                    break;
                case "restart":
                    lock (_frame) { _sc.Present(0, PresentFlags.Restart); }
                    break;
                case "dwmflush": DwmFlush(); break;
                case "hide-show": Form.Hide(); PumpFor(30); Form.Show(); PumpFor(30); break;
                case "minmax": Form.WindowState = FormWindowState.Minimized; PumpFor(80); Form.WindowState = FormWindowState.Normal; PumpFor(80); Form.Bounds = Disp.Bounds; break;
                case "resize+srcsize+commit": Trigger("resize-1px"); Trigger("srcsize"); Trigger("recommit"); break;
                default: throw new ArgumentException(t);
            }
        }

        private Thread? _cap; private volatile bool _capStop;
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        /// <summary>Another program reading the screen: "dup" = DXGI desktop duplication of this output, "gdi" = BitBlt of a 640x360 corner, 4 times a second. The pixels are thrown away.</summary>
        public void StartCapture(string how)
        {
            _capStop = false;
            _cap = new Thread(() =>
            {
                if (how == "dup")
                {
                    try
                    {
                        using IDXGIAdapter ad = _dxgiDev.GetAdapter();
                        for (uint oi = 0; ad.EnumOutputs(oi, out IDXGIOutput? o).Success && o != null; oi++)
                        {
                            using (o)
                            {
                                if (o.Description.DeviceName != Disp.DeviceName) continue;
                                using IDXGIOutput1 o1 = o.QueryInterface<IDXGIOutput1>();
                                using IDXGIOutputDuplication dup = o1.DuplicateOutput(_dev);
                                while (!_capStop)
                                {
                                    if (dup.AcquireNextFrame(100, out _, out IDXGIResource? res).Success) { res?.Dispose(); dup.ReleaseFrame(); }
                                }
                            }
                            break;
                        }
                    }
                    catch { }
                }
                else
                {
                    IntPtr sdc = GetDC(IntPtr.Zero), mdc = CreateCompatibleDC(sdc), bmp = CreateCompatibleBitmap(sdc, 640, 360);
                    IntPtr old = SelectObject(mdc, bmp);
                    while (!_capStop) { BitBlt(mdc, 0, 0, 640, 360, sdc, Disp.Bounds.X, Disp.Bounds.Y, 0x00CC0020); Thread.Sleep(250); }
                    SelectObject(mdc, old); DeleteObject(bmp); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
                }
            }) { IsBackground = true };
            _cap.Start();
        }
        public void StopCapture() { _capStop = true; _cap?.Join(2000); _cap = null; }

        public void Dispose()
        {
            StopCapture();
            _stop = true;
            _worker.Join(2000);
            try { _vis?.Dispose(); _tg?.Dispose(); _dc?.Dispose(); } catch { }
            try { _sc2.Dispose(); _sc.Dispose(); } catch { }
            try { _fac.Dispose(); _dxgiDev.Dispose(); _dev.Dispose(); } catch { }
            Form.Close(); Form.Dispose();
            PumpFor(100);
        }
    }

    /// <summary>Something drawn over part of the video, like YuCap's own menus or a foreign window.</summary>
    internal sealed class ProbeOverlay : IDisposable
    {
        private Form? _f; private ContextMenuStrip? _m;
        private readonly string _kind;
        public ProbeOverlay(string kind, Form owner, DisplayInfo d)
        {
            _kind = kind;
            Rectangle r = new(d.Bounds.X + 200, d.Bounds.Y + 200, 300, 120);
            if (kind == "menu")
            {
                _m = new ContextMenuStrip();
                for (int i = 0; i < 8; i++) _m.Items.Add("Item " + i);
                _m.Show(new Point(r.X, r.Y));
            }
            else
            {
                _f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
                    BackColor = Color.Magenta, Opacity = kind == "opaque" ? 1.0 : 0.5, Bounds = r,
                };
                _f.Show(owner);
            }
            PumpFor(150);
        }

        /// <summary>destroy | hide | minimize | offscreen | opacity0</summary>
        public void Remove(string how)
        {
            if (_m != null) { _m.Close(); _m.Dispose(); _m = null; PumpFor(50); return; }
            if (_f == null) return;
            switch (how)
            {
                case "hide": _f.Hide(); break;
                case "minimize": _f.WindowState = FormWindowState.Minimized; break;
                case "offscreen": _f.Location = new Point(-5000, -5000); break;
                case "opacity0": _f.Opacity = 0; break;
                default: _f.Close(); _f.Dispose(); _f = null; break;
            }
            PumpFor(50);
        }

        public void Dispose() { try { _m?.Dispose(); } catch { } try { _f?.Close(); _f?.Dispose(); } catch { } }
    }

    private static string Opt(string[] a, string name, string def)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return def;
    }

    private static List<T> Shuffle<T>(IEnumerable<T> src, Random rng) => src.OrderBy(_ => rng.Next()).ToList();

    private static void RunVBlankExperiments(string outPath, string[] args)
    {
        var log = new List<string>();
        void P(string s) { log.Add(s); Console.WriteLine(s); try { File.WriteAllText(outPath, string.Join(Environment.NewLine, log)); } catch { } }
        string[] exps = Opt(args, "--exp", "recovery").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int reps = int.Parse(Opt(args, "--reps", "2"));
        int gap = int.Parse(Opt(args, "--gap", "2000"));
        int seed = int.Parse(Opt(args, "--seed", Environment.TickCount.ToString()));
        string ovKind = Opt(args, "--overlay", "layered");
        string dispName = Opt(args, "--display", "");
        var rng = new Random(seed);
        List<DisplayInfo> displays = Displays.List();
        DisplayInfo? disp = dispName.Length == 0
            ? displays.OrderByDescending(d => d.RefreshHz).First()
            : displays.FirstOrDefault(d => d.FriendlyName.Contains(dispName, StringComparison.OrdinalIgnoreCase) || d.DeviceName.Contains(dispName, StringComparison.OrdinalIgnoreCase));
        if (disp == null) { P("display not found: " + dispName); return; }
        double target = disp.RefreshHz;
        P($"YuCap {Application.ProductVersion} - dwm recovery probe  display {disp.DeviceName} {disp.FriendlyName} {disp.RefreshHz} Hz  exp={string.Join(",", exps)} reps={reps} gap={gap} ms seed={seed} overlay={ovKind}");
        P("displays: " + string.Join("; ", displays.Select(d => $"{d.DeviceName} {d.FriendlyName} {d.RefreshHz} Hz")));

        string[] removes = { "destroy", "hide", "minimize", "offscreen", "opacity0" };
        string[] triggers = { "none", "resize-same", "resize-1px", "srcsize", "recreate", "recommit", "winsize", "setwindowpos", "topmost", "restart", "dwmflush", "hide-show", "minmax", "resize+srcsize+commit" };

        // the start of every run: fresh window, wait for the full rate; a run that cannot reach it says so
        PresentSession Start(bool comp, Format fmt, out double waited, int maxWaitMs = 30000)
        {
            Thread.Sleep(gap);
            var s = new PresentSession(disp, comp, fmt);
            string pre = VideoOverlap.Describe(VideoOverlap.Find(s.Form.Handle, disp.Bounds, true));
            waited = s.WaitFull(target, maxWaitMs);
            if (pre != "none" || waited < 0 || waited > 1.5) P($"  [start] waited {waited:0.0} s; windows above the video at start: {pre}");
            return s;
        }

        foreach (string exp in exps)
        {
            P(string.Empty);
            P($"=== {exp} ===");
            if (exp == "variants")
            {
                var items = new List<(bool comp, Format fmt, string ov, string name)>();
                foreach (bool comp in new[] { true, false })
                    foreach (Format f in new[] { Format.B8G8R8A8_UNorm, Format.R10G10B10A2_UNorm })
                        foreach (string ov in new[] { "", ovKind })
                            items.Add((comp, f, ov, $"{(comp ? "composition" : "hwnd")} {f}{(ov.Length > 0 ? " +overlay(" + ov + ")" : "")}"));
                var order = new List<(bool comp, Format fmt, string ov, string name)>();
                for (int r = 0; r < reps; r++) order.AddRange(Shuffle(items, rng));
                foreach (var it in order)
                {
                    using PresentSession s = Start(it.comp, it.fmt, out double w, 8000);
                    long a = s.Now;
                    ProbeOverlay? ov = it.ov.Length > 0 ? new ProbeOverlay(it.ov, s.Form, disp) : null;
                    s.Wait(3000);
                    long b = s.Now;
                    ov?.Dispose();
                    P($"{it.name,-52} start-wait {w:0.0} s  3 s rate {s.Fps(a + Stopwatch.Frequency, b):0.0} fps");
                }
            }
            else if (exp == "recovery")
            {
                // full rate -> overlay for 1 s -> remove -> 250 ms bins for 30 s
                for (int r = 0; r < reps; r++)
                    foreach (string kind in Shuffle(ovKind.Split('+'), rng))
                    {
                        using PresentSession s = Start(true, Format.B8G8R8A8_UNorm, out double w);
                        long t0 = s.Now; s.Wait(2000);
                        double baseline = s.Fps(t0 + Stopwatch.Frequency, s.Now);
                        using var ov = new ProbeOverlay(kind, s.Form, disp);
                        long tOv = s.Now; s.Wait(1000);
                        double during = s.Fps(tOv + 300 * Stopwatch.Frequency / 1000, s.Now);
                        ov.Remove("destroy");
                        long tr = s.Now; s.Wait(30000);
                        P($"rep {r} overlay={kind}: start-wait {w:0.0} s, baseline {baseline:0.0}, during overlay {during:0.0}, recovery after removal {s.RecoverySeconds(tr, 30, target):0.00} s (-1 = never in 30 s)");
                        P("    bins/250ms: " + s.Bins(tr, 30));
                    }
            }
            else if (exp == "removal")
            {
                // how the overlay goes away (destroy vs hide vs minimise ...) and what that does to the recovery, 12 s bins
                for (int r = 0; r < reps; r++)
                    foreach (string how in Shuffle(removes, rng))
                    {
                        using PresentSession s = Start(true, Format.B8G8R8A8_UNorm, out double w);
                        s.Wait(1000);
                        using var ov = new ProbeOverlay(ovKind, s.Form, disp);
                        s.Wait(1000);
                        ov.Remove(how);
                        long tr = s.Now; s.Wait(12000);
                        P($"rep {r} removal={how}: start-wait {w:0.0} s, recovery {s.RecoverySeconds(tr, 12, target):0.00} s   bins: {s.Bins(tr, 6)}");
                    }
            }
            else if (exp == "triggers")
            {
                // stuck state for 1 s after the overlay is gone, then the trigger; rate bins for 6 s after it
                var order = new List<string>();
                for (int r = 0; r < reps; r++) order.AddRange(Shuffle(triggers, rng));
                foreach (string t in order)
                {
                    using PresentSession s = Start(true, Format.B8G8R8A8_UNorm, out double w);
                    s.Wait(1000);
                    using var ov = new ProbeOverlay(ovKind, s.Form, disp);
                    s.Wait(1000);
                    ov.Remove("destroy");
                    long tr = s.Now; s.Wait(1000);
                    double stuck = s.Fps(tr + Stopwatch.Frequency / 4, s.Now);
                    long tt = s.Now;
                    string err = "";
                    var swt = Stopwatch.StartNew();
                    try { s.Trigger(t); } catch (Exception ex) { err = " ERR " + ex.Message.Split('\n')[0]; }
                    double ms = swt.Elapsed.TotalMilliseconds;
                    s.Wait(6000);
                    P($"{t,-24} start-wait {w:0.0} s, 1 s after removal {stuck:0.0} fps, trigger took {ms:0} ms, recovery after trigger {s.RecoverySeconds(tt, 6, target):0.00} s{err}   bins: {s.Bins(tt, 3)}");
                }
            }
            else if (exp == "unstick")
            {
                // one session, no overlay: every trigger in turn, 5 s of rate after each (when the display is stuck at the primary's rate from the start)
                using PresentSession s = Start(true, Format.B8G8R8A8_UNorm, out double w, 3000);
                P($"start-wait {w:0.0} s, bounds {disp.Bounds}");
                long t = s.Now; s.Wait(4000); P("idle: " + s.Bins(t, 4));
                foreach (string tr in triggers.Where(x => x != "none"))
                {
                    long tt = s.Now;
                    string err = "";
                    try { s.Trigger(tr); } catch (Exception ex) { err = " ERR " + ex.Message.Split((char)10)[0]; }
                    s.Wait(5000);
                    P($"{tr,-24} recovery {s.RecoverySeconds(tt, 5, target):0.00} s{err}   bins: {s.Bins(tt, 5)}");
                }
            }
            else if (exp == "capture")
            {
                // does another program reading the screen hold the display to a slower rate, and does it let go afterwards?
                for (int r = 0; r < reps; r++)
                    foreach (string how in Shuffle(new[] { "dup", "gdi" }, rng))
                    {
                        using PresentSession s = Start(true, Format.B8G8R8A8_UNorm, out double w);
                        long a = s.Now; s.Wait(2000);
                        double baseline = s.Fps(a + Stopwatch.Frequency, s.Now);
                        long c0 = s.Now; s.StartCapture(how); s.Wait(4000);
                        double during = s.Fps(c0 + 1000L * Stopwatch.Frequency / 1000, s.Now);
                        long c1 = s.Now; s.StopCapture(); s.Wait(8000);
                        P($"rep {r} capture={how}: baseline {baseline:0.0}, during capture {during:0.0}, after capture stopped: recovery {s.RecoverySeconds(c1, 8, target):0.00} s   bins {s.Bins(c1, 4)}");
                    }
            }
            else if (exp == "watch")
            {
                // one long session with nothing of ours over the video: log the second-by-second rate, and what else is going on whenever it changes
                int secs = int.Parse(Opt(args, "--seconds", "300"));
                using PresentSession s = new(disp, true, Format.B8G8R8A8_UNorm);
                string lastState = "";
                var sw = Stopwatch.StartNew();
                long prev = s.Now;
                while (sw.Elapsed.TotalSeconds < secs)
                {
                    s.Wait(1000);
                    long n = s.Now;
                    double f = s.Fps(prev, n); prev = n;
                    string above = VideoOverlap.Describe(VideoOverlap.Find(s.Form.Handle, disp.Bounds, true));
                    IntPtr fg = GetForegroundWindow();
                    var tb = new StringBuilder(80); GetWindowTextW(fg, tb, 80);
                    GetCursorPos(out Point cp);
                    string st = $"{(f >= target * 0.9 ? "FULL" : "SLOW")} | above: {above} | fg: {tb} | cursor on TV: {disp.Bounds.Contains(cp)}";
                    if (st != lastState) { P($"t={sw.Elapsed.TotalSeconds,6:0.0} {f,6:0.0} fps  {st}"); lastState = st; }
                }
            }
            else if (exp == "format")
            {
                // B8G8R8A8 against R10G10B10A2 (SDR content), composition and hwnd, nothing overlapping, 5 s each
                var items = new List<(bool comp, Format fmt)>();
                foreach (bool comp in new[] { true, false })
                    foreach (Format f in new[] { Format.B8G8R8A8_UNorm, Format.R10G10B10A2_UNorm }) items.Add((comp, f));
                var order = new List<(bool comp, Format fmt)>();
                for (int r = 0; r < reps; r++) order.AddRange(Shuffle(items, rng));
                foreach (var it in order)
                {
                    using PresentSession s = Start(it.comp, it.fmt, out double w);
                    long a = s.Now; s.Wait(5000);
                    P($"{(it.comp ? "composition" : "hwnd"),-12} {it.fmt,-16} start-wait {w:0.0} s, 5 s rate {s.Fps(a, s.Now):0.0} fps, bins {s.Bins(a, 2)}");
                }
            }
        }
        P(string.Empty);
        P("done");
    }
}
