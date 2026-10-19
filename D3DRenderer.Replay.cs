using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace YuCap;

// Developer hooks (--interp-seq-capture / --interp-seq-eval, the test mode): dump a contiguous run of ring frames, and feed
// frames back in offline so that the REAL ring, same-picture pass and pair selection can be replayed against a simulated clock.
internal sealed partial class D3DRenderer
{
    /// <summary>Replay: the clock the answer delay is measured against (null = the Stopwatch).</summary>
    internal Func<long>? ClockOverride { get; set; }

    internal sealed class DumpFrame
    {
        public long Seq, Arrival, Smooth;
        public bool Dup, Resolved;
        public Bitmap Picture = null!;
    }

    /// <summary>The newest <paramref name="count"/> ring frames (oldest first), each converted like the display does and shrunk to
    /// <paramref name="target"/>. They are contiguous unless the ring lost frames; the caller checks the sequence numbers.</summary>
    internal bool InterpDebugDump(int count, Size target, List<DumpFrame> into)
    {
        if (!InterpAvailable) return false;
        lock (FrameLock)
        {
            if (!_interpHold) return false;
            DupPoll();
            var slots = _ring.Where(s => s.Seq != 0 && s.W > 0).OrderBy(s => s.Seq).ToList();
            if (slots.Count > count) slots = slots.GetRange(slots.Count - count, count);
            InterpOrientation(out int rot, out bool mir, out VideoAdjustments adj, out System.Windows.Forms.Padding crop);
            OriFrame? sc = null;
            try
            {
                foreach (Slot s in slots)
                {
                    Size o = Oriented(CropMath.Effective(new Size(s.W, s.H), crop).Size, rot);
                    EnsureOriFrame(ref sc, o);
                    ConvertSlot(s, sc!, rot, mir, adj, crop);
                    using Bitmap full = ReadBack(sc!.Tex, new Rectangle(Point.Empty, o));
                    var small = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(full, new Rectangle(Point.Empty, target), new Rectangle(Point.Empty, o), GraphicsUnit.Pixel);
                    }
                    into.Add(new DumpFrame { Seq = s.Seq, Arrival = s.Arrival, Smooth = s.Smooth, Dup = s.Dup, Resolved = s.Resolved, Picture = small });
                }
                return true;
            }
            finally
            {
                Ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                sc?.Dispose();
                _curA = _curB = _meA = _meB = 0;
                _lastAlpha = -1;
            }
        }
    }

    private static byte[] BgraBytes(Bitmap bmp, out int stride)
    {
        BitmapData bd = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = bd.Stride;
            var a = new byte[bd.Stride * bmp.Height];
            Marshal.Copy(bd.Scan0, a, 0, a.Length);
            return a;
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Replay: feed one picture in as if the capture device had delivered it (NV12, BT.709 limited range), through the same
    /// path as a live frame: ring slot, same-picture pass, answer. Returns the frame's number.</summary>
    internal long ReplaySubmit(Bitmap bmp, long arrival, long smooth)
    {
        int w = bmp.Width & ~1, h = bmp.Height & ~1;
        byte[] px = BgraBytes(bmp, out int stride);
        var nv = new byte[w * h * 3 / 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                double b = px[i], g = px[i + 1], r = px[i + 2];
                nv[y * w + x] = (byte)Math.Clamp((int)Math.Round(16 + 0.18259 * r + 0.61423 * g + 0.06201 * b), 16, 235);
            }
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                double r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = (y + dy) * stride + (x + dx) * 4;
                        b += px[i]; g += px[i + 1]; r += px[i + 2];
                    }
                r /= 4; g /= 4; b /= 4;
                int o = w * h + (y / 2) * w + x;
                nv[o] = (byte)Math.Clamp((int)Math.Round(128 - 0.10064 * r - 0.33857 * g + 0.43922 * b), 16, 240);
                nv[o + 1] = (byte)Math.Clamp((int)Math.Round(128 + 0.43922 * r - 0.39894 * g - 0.04027 * b), 16, 240);
            }
        GCHandle hnd = GCHandle.Alloc(nv, GCHandleType.Pinned);
        try { SubmitCpuFrame(hnd.AddrOfPinnedObject(), w, w, h, new YuvInfo(1, false), arrival, smooth); }
        finally { hnd.Free(); }
        lock (FrameLock) return _frameNo;
    }

    /// <summary>Replay: the picture the last <see cref="InterpUpdate"/> left in the oriented texture, as luma shrunk by
    /// <paramref name="div"/> (box average), row-major.</summary>
    internal float[]? ReplayPresentedLuma(int div, out int w, out int h)
    {
        w = h = 0;
        lock (FrameLock)
        {
            if (_oriented == null) return null;
            using Bitmap bmp = ReadBack(_oriented, new Rectangle(Point.Empty, _orientedSize));
            byte[] px = BgraBytes(bmp, out int stride);
            w = bmp.Width / div; h = bmp.Height / div;
            var res = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int dy = 0; dy < div; dy++)
                        for (int dx = 0; dx < div; dx++)
                        {
                            int i = (y * div + dy) * stride + (x * div + dx) * 4;
                            s += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                        }
                    res[y * w + x] = (float)(s / (div * div));
                }
            return res;
        }
    }

    /// <summary>Replay: the pair and blend the last <see cref="InterpUpdate"/> showed (A and B slot sequence numbers; 0 when none).</summary>
    internal (long A, long B, double Alpha) ReplayLastPair() { lock (FrameLock) return (_curA, _curB, _lastAlpha); }
}
