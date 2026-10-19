using System;
using System.Collections.Generic;

namespace YuCap;

/// <summary>
/// What a profile was used with: a few cheap, stable facts about the input. Every member that is 0 / null is "not known" and
/// is left out of a comparison instead of counting as a difference. Stored in the profile (settings.json).
/// </summary>
public sealed class InputSignature
{
    /// <summary>Video capture device id (symbolic link).</summary>
    public string? DeviceId { get; set; }
    /// <summary>Negotiated capture size and frame rate.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int ModeFps { get; set; }
    /// <summary>How fine the picture really is, in lines (720 for a game drawn at 720p and enlarged by the console); 0 = not known.</summary>
    public int DetailHeight { get; set; }
    /// <summary>The game's own frame rate, rounded to a band (24, 30, 40, 50, 60, 90, 120); 0 = not known.</summary>
    public int GameFpsBand { get; set; }

    public static readonly int[] FpsBands = { 24, 30, 40, 50, 60, 90, 120 };

    /// <summary>The band a measured game frame rate falls in (the nearest one, by ratio); 0 for nothing usable.</summary>
    public static int BandOf(double fps)
    {
        if (!(fps >= 15) || fps > 300) return 0;
        int best = 0; double bestD = double.MaxValue;
        foreach (int b in FpsBands)
        {
            double d = Math.Abs(Math.Log(fps / b));
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    public InputSignature? Normalized()
    {
        var s = (InputSignature)MemberwiseClone();
        if (string.IsNullOrWhiteSpace(s.DeviceId)) s.DeviceId = null;
        s.Width = Math.Clamp(s.Width, 0, 16384);
        s.Height = Math.Clamp(s.Height, 0, 16384);
        s.ModeFps = Math.Clamp(s.ModeFps, 0, 1000);
        s.DetailHeight = Math.Clamp(s.DetailHeight, 0, 16384);
        s.GameFpsBand = BandOf(s.GameFpsBand) == s.GameFpsBand ? s.GameFpsBand : 0;
        bool empty = s.DeviceId == null && s.Width == 0 && s.Height == 0 && s.ModeFps == 0 && s.DetailHeight == 0 && s.GameFpsBand == 0;
        return empty ? null : s;
    }

    public bool SameAs(InputSignature? o) =>
        o != null && DeviceId == o.DeviceId && Width == o.Width && Height == o.Height && ModeFps == o.ModeFps
        && DetailHeight == o.DetailHeight && GameFpsBand == o.GameFpsBand;

    /// <summary>Overwrite the members that <paramref name="live"/> knows; keep the others (an unknown value does not erase a known one).</summary>
    public InputSignature MergedWith(InputSignature live)
    {
        var m = (InputSignature)MemberwiseClone();
        if (live.DeviceId != null) m.DeviceId = live.DeviceId;
        if (live.Width > 0 && live.Height > 0) { m.Width = live.Width; m.Height = live.Height; }
        if (live.ModeFps > 0) m.ModeFps = live.ModeFps;
        if (live.DetailHeight > 0) m.DetailHeight = live.DetailHeight;
        if (live.GameFpsBand > 0) m.GameFpsBand = live.GameFpsBand;
        return m;
    }

    public override string ToString()
    {
        string dev = DeviceId == null ? "?" : (DeviceId.Length > 24 ? "…" + DeviceId[^24..] : DeviceId);
        return $"dev={dev} {Width}x{Height}@{ModeFps} detail={(DetailHeight > 0 ? DetailHeight + "p" : "?")} game={(GameFpsBand > 0 ? "~" + GameFpsBand : "?")}";
    }
}

/// <summary>Which profile fits an input best — and when to say nothing.</summary>
internal static class ProfileMatcher
{
    // Weights: the device and the picture's real detail say the most about which console is connected.
    private const double WDevice = 3, WSize = 2, WFps = 1, WDetail = 2, WGameFps = 2;
    /// <summary>Less comparable evidence than this and no judgement is made.</summary>
    private const double MinEvidence = 4;
    /// <summary>The best other profile must fit at least this well, ...</summary>
    internal const double MinScore = 0.8;
    /// <summary>... beat the active one by at least this much, ...</summary>
    internal const double MinLead = 0.2;
    /// <summary>... and beat the runner-up (any other profile) by at least this much, or the input is ambiguous.</summary>
    internal const double MinGap = 0.1;
    /// <summary>The score assumed for the active profile when it has no signature yet.</summary>
    internal const double UnknownActive = 0.5;
    private const double Eps = 1e-9;

    /// <summary>Similarity 0..1 of <paramref name="live"/> to a stored signature, and the weight of the evidence it rests on.</summary>
    public static double Similarity(InputSignature live, InputSignature stored, out double evidence)
    {
        double sum = 0, ev = 0;
        void Add(double w, double s) { sum += w * s; ev += w; }

        if (live.DeviceId != null && stored.DeviceId != null)
            Add(WDevice, string.Equals(live.DeviceId, stored.DeviceId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

        if (live.Width > 0 && live.Height > 0 && stored.Width > 0 && stored.Height > 0)
        {
            double s = live.Width == stored.Width && live.Height == stored.Height ? 1
                : (long)live.Width * stored.Height == (long)live.Height * stored.Width ? 0.4 : 0;   // same shape, other size
            Add(WSize, s);
        }

        if (live.ModeFps > 0 && stored.ModeFps > 0)
            Add(WFps, Math.Abs(live.ModeFps - stored.ModeFps) <= 1 ? 1 : 0);

        if (live.DetailHeight > 0 && stored.DetailHeight > 0)
        {
            // 1080 vs 1080 = 1, vs 900 = 0.55, vs 720 = 0 (a factor of 1.5 apart is a different source).
            double r = Math.Abs(Math.Log((double)live.DetailHeight / stored.DetailHeight)) / Math.Log(1.5);
            Add(WDetail, Math.Max(0, 1 - r));
        }

        if (live.GameFpsBand > 0 && stored.GameFpsBand > 0)
            Add(WGameFps, live.GameFpsBand == stored.GameFpsBand ? 1 : 0);

        evidence = ev;
        return ev > 0 ? sum / ev : 0;
    }

    /// <summary>
    /// The index of the profile to switch to, or null with the reason in <paramref name="why"/>. A switch needs: some other profile
    /// with a signature fits well (<see cref="MinScore"/>), clearly better than the active one (<see cref="MinLead"/>), and
    /// clearly better than every other profile (<see cref="MinGap"/>). Two consoles that are both 1080p60 look alike: then nothing.
    /// </summary>
    public static int? Choose(InputSignature live, IReadOnlyList<InputSignature?> signatures, int activeIndex, out string why)
    {
        var scores = new double[signatures.Count];
        var ok = new bool[signatures.Count];
        for (int i = 0; i < signatures.Count; i++)
        {
            if (signatures[i] is { } sig)
            {
                scores[i] = Similarity(live, sig, out double ev);
                ok[i] = ev >= MinEvidence;
            }
        }
        double active = activeIndex >= 0 && activeIndex < scores.Length && ok[activeIndex] ? scores[activeIndex] : UnknownActive;

        int best = -1;
        for (int i = 0; i < scores.Length; i++)
            if (ok[i] && i != activeIndex && (best < 0 || scores[i] > scores[best])) best = i;
        if (best < 0) { why = "no other profile has a usable signature"; return null; }

        double bestScore = scores[best];
        if (bestScore < MinScore) { why = $"best other fits only {bestScore:0.00}"; return null; }
        if (bestScore - active < MinLead - Eps) { why = $"not clearly better than the active one ({bestScore:0.00} vs {active:0.00})"; return null; }
        for (int i = 0; i < scores.Length; i++)
            if (ok[i] && i != best && i != activeIndex && bestScore - scores[i] < MinGap - Eps)
            {
                why = $"ambiguous: profiles {best + 1} and {i + 1} fit alike ({bestScore:0.00} / {scores[i]:0.00})";
                return null;
            }
        why = $"profile {best + 1} fits {bestScore:0.00} (active {active:0.00})";
        return best;
    }
}
