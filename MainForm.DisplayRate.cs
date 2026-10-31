using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

public sealed partial class MainForm
{
    // A monitor that refreshes faster than the primary one is held to the primary's rate by DWM while the video is composed instead of flipped
    // straight to the screen (something overlaps it, or for a while after that). With frame interpolation the video wants a new picture every
    // refresh, so a rate that stays at the primary's is recognisable. If nothing overlaps the video, nudge the swap chain once; if something
    // does, say which window in the log. Never more than a few times, never in a loop.
    private readonly Queue<(long Tick, long Presented)> _drSamples = new();
    private long _drSlowSince, _drLastNudge, _drLastReport, _drPrimaryAt;
    private int _drNudges, _drPrimaryHz;


    private void DisplayRateTick()
    {
        try
        {
            long now = Environment.TickCount64;
            if (!_video.IsRunning || _testCts != null || _video.InterpolationState is not { Active: true } ip || _video.PresentMode != "composition vsync")
            {
                _drSamples.Clear(); _drSlowSince = 0;
                return;
            }
            _drSamples.Enqueue((now, _video.FramesPresented));
            while (_drSamples.Count > 0 && now - _drSamples.Peek().Tick > 3200) _drSamples.Dequeue();
            if (_drSamples.Count < 4) return;
            var first = _drSamples.Peek();
            double el = (now - first.Tick) / 1000.0;
            if (el < 2.5) return;
            double rate = (_video.FramesPresented - first.Presented) / el;

            if (now - _drPrimaryAt > 5000 || _drPrimaryHz == 0)
            {
                _drPrimaryAt = now;
                _drPrimaryHz = Displays.List().FirstOrDefault(d => d.Primary)?.RefreshHz ?? 0;
            }
            double panel = ip.RefreshHz;
            // the signature: a fast monitor presenting at about the primary's rate while interpolation asks for the monitor's own
            bool slow = _drPrimaryHz > 0 && panel > _drPrimaryHz + 10 && rate >= _drPrimaryHz * 0.7 && rate <= _drPrimaryHz * 1.15 && rate < panel * 0.75;
            if (!slow)
            {
                _drSlowSince = 0;
                if (rate >= panel * 0.9) { if (now - _drLastNudge > 30000) _drNudges = 0; }
                return;
            }
            if (_drSlowSince == 0) { _drSlowSince = now; return; }
            if (now - _drSlowSince < 3000) return;

            IntPtr top = FindForm()?.Handle ?? Handle;
            Rectangle area = _canvas.RectangleToScreen(_canvas.ClientRectangle);
            List<VideoOverlap.Item> over = VideoOverlap.Find(top, area);
            if (over.Count > 0)
            {
                if (now - _drLastReport > 30000) { _drLastReport = now; Log.Info($"display: presenting {rate:0} fps on a {panel:0} Hz monitor (the primary runs {_drPrimaryHz} Hz) - the video is overlapped by: {VideoOverlap.Describe(over)}"); }
                return;
            }
            if (_drNudges >= 3 || now - _drLastNudge < 20000) return;
            _drNudges++; _drLastNudge = now; _drSlowSince = 0; _drSamples.Clear();
            Log.Info($"display: presenting {rate:0} fps on a {panel:0} Hz monitor (the primary runs {_drPrimaryHz} Hz) and nothing overlaps the video - nudging the swap chain ({_drNudges}/3)");
            Task.Run(() => { try { _video.NudgeDisplay(); } catch (Exception ex) { Log.Info("display: nudge failed: " + ex.Message); } });
        }
        catch (Exception ex) { Log.Info("display rate check failed: " + ex.Message); }
    }
}
