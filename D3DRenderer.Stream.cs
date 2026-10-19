using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace YuCap;

// 配信用ウィンドウ (OBS etc. capture this window): a second, flip-model swap chain on another top-level window that shows only the picture.
//
// After every present of the main window the same oriented picture (so interpolation, the picture adjustments, crop, rotation and
// mirroring are all in it) is composed once more at the stream window's own size, aspect-fit on black. No overlay is ever drawn into it.
// The main window's present has already been handed over when this runs, so its pacing is not delayed by the GPU work.
// The scaling quality is the one on screen, except that the CNN upscaler (whose cached result is sized for the main window) is
// replaced by FSR, and the "match the original detail" shrink is not applied.
//
// The Present call itself is not run on the render thread: it costs 2-4 ms of CPU even with DO_NOT_WAIT (measured on a 144 Hz monitor),
// which is a large part of a 6.9 ms refresh, once per main frame. A small worker thread presents instead; while it is still busy with the
// previous picture the next one is skipped (the stream is "latest wins"), so the render thread never waits for it (0.02 ms per picture).
internal sealed partial class D3DRenderer
{
    private IDXGISwapChain1? _ssw;
    private Size _sSize;
    private IntPtr _sHwnd;
    private ID3D11Texture2D? _sInter, _sLast;
    private ID3D11ShaderResourceView? _sInterSrv;
    private ID3D11RenderTargetView? _sInterRtv;
    private long _sPresented, _sSkipped;
    private bool _sHasLast;
    private Thread? _sThread;
    private readonly AutoResetEvent _sGo = new(false);
    private volatile bool _sStop;
    private int _sBusy;                     // 1 from the hand-over until the worker's Present has returned
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    public bool StreamActive => _ssw != null;
    public long StreamPresented => Interlocked.Read(ref _sPresented);
    public long StreamSkipped => Interlocked.Read(ref _sSkipped);

    /// <summary>Render thread. Open the stream window's swap chain (<paramref name="hwnd"/> 0 = close it). A change of size makes a new one.</summary>
    public void StreamConfigure(IntPtr hwnd, Size size)
    {
        StreamFree();
        if (hwnd == IntPtr.Zero || size.Width < 16 || size.Height < 16) return;
        lock (FrameLock) { Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>()); Ctx.Flush(); }
        using IDXGIDevice1 dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)size.Width, Height = (uint)size.Height,
            Format = Format.B8G8R8A8_UNorm, Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _ssw = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        _sHwnd = hwnd; _sSize = size; _sHasLast = false;
        _sPresented = 0; _sSkipped = 0; _sCpuSum = _sCpuMax = 0; _sCpuN = 0; _sPresentTicks = 0; _sBusy = 0; _sStop = false;
        _sInter = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _sInterSrv = Device.CreateShaderResourceView(_sInter);
        _sInterRtv = Device.CreateRenderTargetView(_sInter);
        _sLast = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        _sThread = new Thread(StreamWorker) { IsBackground = true, Name = "YuCap.D3D.StreamPresent" };
        _sThread.Start(_ssw);
        Log.Info($"D3D: stream window swap chain {size.Width}x{size.Height}");
    }

    private void StreamWorker(object? arg)
    {
        var sw = (IDXGISwapChain1)arg!;
        IntPtr hwnd = _sHwnd;
        while (true)
        {
            _sGo.WaitOne();
            if (_sStop) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                // DO_NOT_WAIT: a window DWM is slow to take (covered, minimised) must not keep the worker for long either
                Result pr = sw.Present(0, PresentFlags.DoNotWait);
                if (pr.Success) Interlocked.Increment(ref _sPresented);
                else if (pr.Code != unchecked((int)0x887A000A))      // DXGI_ERROR_WAS_STILL_DRAWING: skipped, the next one will do
                {
                    if (IsWindow(hwnd)) Log.Info($"D3D: stream window present failed hr=0x{pr.Code:X8}");
                    Volatile.Write(ref _sBusy, 0);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Info("D3D: stream window present failed - " + ex.Message.Split('\n')[0]);
                Volatile.Write(ref _sBusy, 0);
                return;
            }
            Interlocked.Add(ref _sPresentTicks, Stopwatch.GetTimestamp() - t0);
            Volatile.Write(ref _sBusy, 0);
        }
    }

    private void StreamFree()
    {
        if (_ssw == null && _sInter == null) return;
        Thread? t = _sThread;
        _sThread = null;
        if (t != null)
        {
            _sStop = true;
            _sGo.Set();
            t.Join(1500);        // the worker's Present is DO_NOT_WAIT: it is back long before this
        }
        try { lock (FrameLock) { Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>()); Ctx.Flush(); } } catch { }
        _sInterRtv?.Dispose(); _sInterSrv?.Dispose(); _sInter?.Dispose(); _sLast?.Dispose();
        _sInterRtv = null; _sInterSrv = null; _sInter = null; _sLast = null;
        _ssw?.Dispose(); _ssw = null;
        _sHwnd = IntPtr.Zero; _sHasLast = false; _sBusy = 0;
    }

    /// <summary>The picture of size <paramref name="o"/> fitted into <paramref name="box"/>, centred; with integer scaling, the largest whole multiple that fits.</summary>
    internal static Rectangle StreamFit(Size o, Size box, bool pixelPerfect)
    {
        double sc = Math.Min((double)box.Width / o.Width, (double)box.Height / o.Height);
        if (pixelPerfect && sc >= 1) sc = Math.Floor(sc);
        int w = Math.Max(1, (int)Math.Round(o.Width * sc, MidpointRounding.AwayFromZero)), h = Math.Max(1, (int)Math.Round(o.Height * sc, MidpointRounding.AwayFromZero));
        w = Math.Min(w, box.Width); h = Math.Min(h, box.Height);
        return new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h);
    }

    /// <summary>Render thread, right after the main window's Present: compose the current oriented picture for the stream window and hand it
    /// to the worker to present. Skipped (counted) while the worker still has the previous picture.</summary>
    public void StreamPresent(ScalingQuality quality, bool pixelPerfect, float sharpness)
    {
        IDXGISwapChain1? sw = _ssw;
        Size o = _orientedSize;
        if (sw == null || _orientedSrv == null || _sLast == null || o.Width <= 0 || o.Height <= 0) return;
        if (Volatile.Read(ref _sBusy) != 0) { Interlocked.Increment(ref _sSkipped); return; }
        int savedPre = _prescaleActiveH;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (quality is ScalingQuality.Anime4K or ScalingQuality.NvRtx) quality = ScalingQuality.Fsr;
            Rectangle dst = StreamFit(o, _sSize, pixelPerfect);
            using (ID3D11Texture2D bb = sw.GetBuffer<ID3D11Texture2D>(0))
            using (ID3D11RenderTargetView rtv = Device.CreateRenderTargetView(bb))
            {
                Compose(rtv, _sSize, dst, quality, pixelPerfect, sharpness, _sInterRtv, _sInterSrv, 0);
                Ctx.CopyResource(_sLast, bb);          // a flip-model back buffer is undefined after Present: keep a copy for the test mode
                _sHasLast = true;
            }
            Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            Volatile.Write(ref _sBusy, 1);
            _sGo.Set();
        }
        catch (Exception ex)
        {
            Log.Info("D3D: stream window failed - " + ex.Message.Split('\n')[0]);
            StreamFree();
        }
        finally
        {
            _prescaleActiveH = savedPre;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _sCpuSum += ms; _sCpuN++; if (ms > _sCpuMax) _sCpuMax = ms;
        }
    }

    private double _sCpuSum, _sCpuMax; private long _sCpuN, _sPresentTicks;
    /// <summary>Test mode: the render thread's CPU time spent per stream picture (average / worst, ms) since the window was set, and how many.</summary>
    public (double Avg, double Max, long N) StreamCpu => (_sCpuN > 0 ? _sCpuSum / _sCpuN : 0, _sCpuMax, _sCpuN);
    /// <summary>Test mode: what the worker's Present calls cost on average (ms, not on the render thread) and how many pictures were skipped.</summary>
    public string StreamParts
    {
        get
        {
            long n = Math.Max(1, Interlocked.Read(ref _sPresented));
            return $"Present（別スレッド）平均 {Interlocked.Read(ref _sPresentTicks) * 1000.0 / Stopwatch.Frequency / n:0.00} ms、見送り {Interlocked.Read(ref _sSkipped)} 回";
        }
    }

    /// <summary>Render thread (test mode): the stream window's last composed picture, read back.</summary>
    public Bitmap? ReadStream()
    {
        if (_sLast == null || !_sHasLast) return null;
        return ReadBack(_sLast, new Rectangle(Point.Empty, _sSize));
    }
}
