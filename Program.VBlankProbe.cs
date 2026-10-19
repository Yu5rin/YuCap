using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using FrameStatistics = Vortice.DXGI.FrameStatistics;
using Size = System.Drawing.Size;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace YuCap;

//   --vblank-probe <out.txt>   which clock does each display's vertical blank really run on?
// Per display: the refresh Windows reports (EnumDisplaySettings, DWM), the measured period of
// IDXGIOutput.WaitForVBlank and of D3DKMTWaitForVerticalBlankEvent, and how fast a composition and a window
// flip-model swap chain (sync interval 1) present on that display. Needs no capture device.
internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DwmRatio { public uint Num, Den; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmTimingInfo
    {
        public uint cbSize;
        public DwmRatio rateRefresh;
        public ulong qpcRefreshPeriod;
        public DwmRatio rateCompose;
        public ulong qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete,
            cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable,
            cFramesDropped, cFramesMissed, cRefreshNextDisplayed, cRefreshNextPresented, cRefreshesDisplayed,
            cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? dev, int mode, ref ProbeDevMode dm);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProbeDevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    private static string ProbeHz(List<double> intervalsMs)
    {
        var a = intervalsMs.OrderBy(x => x).ToList();
        if (a.Count == 0) return "n/a";
        double med = a[a.Count / 2];
        double mean = a.Average();
        return $"median {med:0.000} ms ({1000.0 / med:0.00} Hz), mean {mean:0.000} ms ({1000.0 / mean:0.00} Hz), min {a[0]:0.00}, max {a[^1]:0.00}, n={a.Count}";
    }

    /// <summary>Intervals between successive returns of <paramref name="wait"/>; a -1 entry means it failed.</summary>
    private static List<double> TimeWaits(Func<bool> wait, int n)
    {
        var iv = new List<double>();
        long prev = 0;
        for (int i = 0; i < n + 3; i++)
        {
            if (!wait()) { iv.Add(-1); break; }
            long t = Stopwatch.GetTimestamp();
            if (i >= 3 && prev != 0) iv.Add((t - prev) * 1000.0 / Stopwatch.Frequency);
            prev = t;
        }
        return iv;
    }

    private static void RunVBlankProbe(string outPath)
    {
        var log = new List<string>();
        log.Add($"YuCap {Application.ProductVersion} - vblank probe");
        log.Add(string.Empty);
        List<DisplayInfo> displays = Displays.List();
        log.Add("--- Windows displays ---");
        foreach (DisplayInfo d in displays)
        {
            var dm = new ProbeDevMode { dmSize = (ushort)Marshal.SizeOf<ProbeDevMode>() };
            uint hz = EnumDisplaySettingsW(d.DeviceName, -1, ref dm) ? dm.dmDisplayFrequency : 0;
            log.Add($"{d.DeviceName} {d.FriendlyName} {d.Bounds.Width}x{d.Bounds.Height} primary={d.Primary} EnumDisplaySettings={hz} Hz");
        }
        var ti = new DwmTimingInfo { cbSize = (uint)Marshal.SizeOf<DwmTimingInfo>() };
        int dhr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref ti);
        log.Add(dhr == 0
            ? $"DWM composition timing: rateRefresh {ti.rateRefresh.Num}/{ti.rateRefresh.Den} = {(ti.rateRefresh.Den > 0 ? ti.rateRefresh.Num / (double)ti.rateRefresh.Den : 0):0.000} Hz"
            : $"DWM composition timing failed 0x{dhr:X8}");
        log.Add(string.Empty);

        log.Add("--- vblank waits (200 each) ---");
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1? adapter).Success && adapter != null; ai++)
            {
                using (adapter)
                {
                    log.Add($"adapter {ai}: {adapter.Description1.Description}");
                    for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput? output).Success && output != null; oi++)
                    {
                        using (output)
                        {
                            string name = output.Description.DeviceName;
                            log.Add($"  output {oi}: {name}");
                            List<double> a = TimeWaits(() => { try { output.WaitForVBlank(); return true; } catch { return false; } }, 200);
                            log.Add($"    IDXGIOutput.WaitForVBlank:               {ProbeHz(a.Where(x => x >= 0).ToList())}");
                            using KmtVBlank? k = KmtVBlank.Open(name, out int st);
                            if (k == null) { log.Add($"    D3DKMTWaitForVerticalBlankEvent: open failed 0x{st:X8}"); continue; }
                            List<double> b = TimeWaits(k.Wait, 200);
                            log.Add(b.Contains(-1)
                                ? $"    D3DKMTWaitForVerticalBlankEvent: failed 0x{k.LastStatus:X8}"
                                : $"    D3DKMTWaitForVerticalBlankEvent:         {ProbeHz(b)}");
                        }
                    }
                }
            }
        }
        catch (Exception ex) { log.Add("vblank wait probe failed: " + ex.Message); }
        log.Add(string.Empty);

        log.Add("--- swap chain present rate per display (3 s each, sync interval 1, Present on every latency-waitable signal) ---");
        foreach (DisplayInfo d in displays)
        {
            foreach (string kind in new[] { "composition", "hwnd", "composition+overlay", "hwnd+overlay", "composition,child", "hwnd,child", "composition,beat", "composition,child,beat", "composition,hdr10", "composition,scrgb", "composition,rgb10sdr", "composition,hdr10,child", "hwnd,hdr10" })
            {
                try { log.Add(ProbePresent(d, kind)); }
                catch (Exception ex) { log.Add($"{d.DeviceName} {kind}: failed {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            }
        }

        string text = string.Join(Environment.NewLine, log);
        try { File.WriteAllText(outPath, text); } catch { }
        Console.WriteLine(text);
    }

    private static string ProbePresent(DisplayInfo disp, string kind)
    {
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = disp.Bounds,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Color.Black,
        };
        form.Show();
        using var overlay = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            TopMost = true, BackColor = Color.Magenta, Opacity = 0.5, Bounds = new Rectangle(disp.Bounds.X + 50, disp.Bounds.Y + 50, 300, 120),
        };
        if (kind.EndsWith("+overlay")) { overlay.Show(form); }
        PumpFor(300);
        IntPtr hwnd = form.Handle;
        Size cs = form.ClientSize;
        if (kind.Contains(",child"))
        {
            var child = new VideoBox { Dock = DockStyle.Fill, BackColor = Color.Black };
            form.Controls.Add(child);
            PumpFor(200);
            hwnd = child.Handle;
            cs = child.ClientSize;
        }

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { D3DFeatureLevel.Level_11_0 },
            out ID3D11Device? dev).CheckError();
        using (dev!)
        {
            using IDXGIDevice1 dxgiDev = dev!.QueryInterface<IDXGIDevice1>();
            dxgiDev.SetMaximumFrameLatency(1);
            using IDXGIAdapter ad = dxgiDev.GetAdapter();
            using IDXGIFactory2 fac = ad.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)cs.Width, Height = (uint)cs.Height,
                Format = kind.Contains(",hdr10") || kind.Contains(",rgb10sdr") ? Format.R10G10B10A2_UNorm : kind.Contains(",scrgb") ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 3, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore, Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            IDXGISwapChain1 sc;
            IDCompositionDevice? dc = null; IDCompositionTarget? tg = null; IDCompositionVisual? vis = null;
            if (kind.StartsWith("composition"))
            {
                sc = fac.CreateSwapChainForComposition(dev, desc);
                dc = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDev);
                dc.CreateTargetForHwnd(hwnd, false, out tg);
                dc.CreateVisual(out vis);
                vis.SetContent(sc);
                tg.SetRoot(vis);
                dc.Commit();
            }
            else sc = fac.CreateSwapChainForHwnd(dev, hwnd, desc);

            using (sc)
            {
                if (kind.Contains(",hdr10") || kind.Contains(",scrgb"))
                {
                    using IDXGISwapChain3 sc3 = sc.QueryInterface<IDXGISwapChain3>();
                    ColorSpaceType cst = kind.Contains(",scrgb") ? ColorSpaceType.RgbFullG10NoneP709 : ColorSpaceType.RgbFullG2084NoneP2020;
                    sc3.SetColorSpace1(cst);
                }
                using IDXGISwapChain2 sc2 = sc.QueryInterface<IDXGISwapChain2>();
                sc2.MaximumFrameLatency = 1;
                IntPtr waitable = sc2.FrameLatencyWaitableObject;
                var stop = new ManualResetEventSlim();
                var res = new double[4];   // presents, seconds, presentRefreshDelta, syncRefreshDelta
                IDXGIOutput? beatOut = null;
                if (kind.EndsWith(",beat"))
                {
                    using IDXGIAdapter ad2 = dxgiDev.GetAdapter();
                    for (uint oi = 0; ad2.EnumOutputs(oi, out IDXGIOutput? o).Success && o != null; oi++)
                    { if (o.Description.DeviceName == disp.DeviceName) { beatOut = o; break; } o.Dispose(); }
                }
                var worker = new Thread(() =>
                {
                    uint pr0 = 0, sr0 = 0, pr1 = 0, sr1 = 0; bool have0 = false;
                    long t0 = 0; int n = 0;
                    long tEnd = Stopwatch.GetTimestamp() + 4 * Stopwatch.Frequency;
                    long tStart = Stopwatch.GetTimestamp() + Stopwatch.Frequency;   // 1 s warm-up
                    while (Stopwatch.GetTimestamp() < tEnd)
                    {
                        if (beatOut != null) { beatOut.WaitForVBlank(); WaitForSingleObject(waitable, 2); }
                        else WaitForSingleObject(waitable, 100);
                        using (ID3D11Texture2D bb = sc.GetBuffer<ID3D11Texture2D>(0))
                        using (ID3D11RenderTargetView rtv = dev.CreateRenderTargetView(bb))
                        {
                            float v = (n & 1) == 0 ? 0.2f : 0.6f;
                            dev.ImmediateContext.ClearRenderTargetView(rtv, new Color4(v, v, v, 1f));
                        }
                        if (sc.Present(1, PresentFlags.None).Failure) break;
                        long now = Stopwatch.GetTimestamp();
                        if (now >= tStart)
                        {
                            if (t0 == 0) t0 = now;
                            n++;
                            if (sc.GetFrameStatistics(out FrameStatistics fs).Success)
                            {
                                if (!have0) { pr0 = fs.PresentRefreshCount; sr0 = fs.SyncRefreshCount; have0 = true; }
                                pr1 = fs.PresentRefreshCount; sr1 = fs.SyncRefreshCount;
                            }
                            res[0] = n; res[1] = (now - t0) / (double)Stopwatch.Frequency;
                            res[2] = pr1 - pr0; res[3] = sr1 - sr0;
                        }
                    }
                    stop.Set();
                }) { IsBackground = true };
                worker.Start();
                var swait = Stopwatch.StartNew();
                while (!stop.IsSet && swait.ElapsedMilliseconds < 8000) { Application.DoEvents(); Thread.Sleep(10); }
                worker.Join(2000);
                vis?.Dispose(); tg?.Dispose(); dc?.Dispose();
                double fps = res[1] > 0 ? res[0] / res[1] : 0;
                double refreshHz = res[1] > 0 ? res[3] / res[1] : 0;
                return $"{disp.DeviceName} {kind,-23}: {res[0]:0} presents in {res[1]:0.00} s = {fps:0.0} fps; SyncRefreshCount delta {res[3]:0} = {refreshHz:0.0} Hz, PresentRefreshCount delta {res[2]:0}";
            }
        }
    }
}
