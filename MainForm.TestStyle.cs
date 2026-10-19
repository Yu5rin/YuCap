using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace YuCap;

/// <summary>Test mode: the なめらか ⇔ 低遅延 switch, and the profile auto-switch's matcher.</summary>
public sealed partial class MainForm
{
    /// <summary>Switch to 低遅延 and back to なめらか, as the hotkey does. Each switch must land in the right state, keep
    /// fullscreen and be showing pictures again within about 2 seconds.</summary>
    private async Task FeatureStyle(TestSection sec, CaptureMode? mode)
    {
        var item = new TestItem { Name = "なめらか ⇔ 低遅延の切り替え" };
        EnsureEngine(false, mode);
        if (!CanSwitchStyle)
        {
            item.Verdict = Verdict.Info;
            item.Summary = "[情報] この描画では切り替えを使えません";
            sec.Items.Add(item);
            return;
        }
        _settings.Interpolation = true;
        ApplyRenderOptions();
        await Wait(SettleMs);
        bool fsBefore = _isFullscreen;

        bool allOk = true, slow = false;
        var notes = new List<string>();
        for (int n = 0; n < 2; n++)
        {
            bool toLow = n == 0;
            string label = toLow ? "なめらか → 低遅延" : "低遅延 → なめらか";
            long t0 = Stopwatch.GetTimestamp();
            bool done;
            _devOpBusy++;
            try { done = await SwitchStyleAsync(); }
            finally { _devOpBusy--; }
            double switchMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            // presenting again: new frames reach the screen after the switch
            long f0 = _video.FramesPresented;
            bool presenting = false;
            while (Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 4000)
            {
                if (_video.IsRunning && _video.FramesPresented >= f0 + 3) { presenting = true; break; }
                await Wait(40);
            }
            double totalMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            PlayStyle now = CurrentStyle;
            bool stateOk = toLow ? _settings.LowLatency && _video.LowLatency : !_settings.LowLatency && !_video.LowLatency && _settings.Interpolation;
            bool fsOk = _isFullscreen == fsBefore;
            InterpolationStatus? st = _video.InterpolationState;
            string interp = st == null ? "?" : st.Active ? "働いています" : InterpIdleText(st);
            bool fast = totalMs < 2000;
            item.Num(label, $"{totalMs:0} ms（切り替え {switchMs:0} ms）/ {StyleName(now)} / 補間: {interp}"
                + (stateOk ? "" : " / 状態が違う") + (presenting ? "" : " / 映像が出ていない") + (fsOk ? "" : " / 全画面が外れた"));
            if (!done || !stateOk || !presenting || !fsOk) allOk = false;
            if (!fast) slow = true;
            notes.Add($"{label} {totalMs:0} ms");
        }

        item.Verdict = !allOk ? Verdict.Ng : slow ? Verdict.Warn : Verdict.Ok;
        item.Summary = !allOk ? "切り替えが期待どおりに終わりませんでした"
            : slow ? "切り替わりますが、2 秒より時間がかかりました" : "切り替えは速く、切り替え後すぐに映像が出ます";
        item.Explanation = !allOk
            ? "切り替え後の設定（低遅延・補間）、映像が出ているか、全画面のままかのどれかが期待と違いました。"
            : $"ホットキー（既定 Ctrl+Alt+L）や映像メニューと同じ動きで、低遅延 → なめらかの順に切り替えました（{string.Join("、", notes)}）。時間は、操作してから新しいコマが画面に出始めるまでです。";
        sec.Items.Add(item);
    }

    private sealed record MatchCase(string Name, InputSignature Live, InputSignature?[] Sigs, int Active, int? Expect);

    private static InputSignature Sig(string dev, int w, int h, int fps, int detail, int band) =>
        new() { DeviceId = dev, Width = w, Height = h, ModeFps = fps, DetailHeight = detail, GameFpsBand = band };

    /// <summary>The matcher on made-up signatures (the choice, and the cases where it must say nothing), then what the live
    /// input looks like and which profile would match it.</summary>
    private Task AutoProfileStep(TestSection sec)
    {
        var item = new TestItem { Name = "プロファイルの自動切り替え（照合の判定）" };
        InputSignature pc = Sig("A", 1920, 1080, 60, 1080, 60);
        InputSignature sw = Sig("A", 1920, 1080, 60, 720, 30);
        InputSignature ps = Sig("B", 1920, 1080, 60, 1080, 60);
        var cases = new List<MatchCase>
        {
            new("Switch の映像（720p 相当・30fps）で、PC のプロファイルが選ばれている", sw, new InputSignature?[] { pc, sw }, 0, 1),
            new("PC の映像で、PC のプロファイルが選ばれている（そのまま）", pc, new InputSignature?[] { pc, sw }, 0, null),
            new("別の機器（デバイスが違う）の映像に、より合うプロファイルがある", ps, new InputSignature?[] { pc, sw, ps }, 0, 2),
            new("同じ 1080p60 で同じ特徴の機器が2つ（見分けられない）", pc, new InputSignature?[] { Sig("C", 1280, 720, 30, 0, 0), pc, Sig("A", 1920, 1080, 60, 1080, 60) }, 0, null),
            new("どれにも合わない新しい入力", Sig("Z", 3840, 2160, 30, 2160, 30), new InputSignature?[] { pc, sw }, 0, null),
            new("手がかりが少ない（大きさだけ）", new InputSignature { Width = 1920, Height = 1080 }, new InputSignature?[] { pc, sw }, 0, null),
            new("選択中のプロファイルに特徴がまだ無い", sw, new InputSignature?[] { null, sw }, 0, 1),
            new("特徴のあるプロファイルが選択中のものしかない", sw, new InputSignature?[] { sw, null }, 0, null),
            new("コマ数の帯だけが違う 2 つ（30 と 60）から選ぶ", Sig("A", 1920, 1080, 60, 1080, 30),
                new InputSignature?[] { Sig("A", 1920, 1080, 60, 1080, 60), Sig("A", 1920, 1080, 60, 1080, 30) }, 0, 1),
        };

        int bad = 0;
        foreach (MatchCase c in cases)
        {
            int? got = ProfileMatcher.Choose(c.Live, c.Sigs, c.Active, out string why);
            bool ok = got == c.Expect;
            if (!ok) bad++;
            item.Num(c.Name, $"{(ok ? "OK" : "NG")}: {(got is int g ? $"{g + 1} 番に切り替え" : "切り替えない")}（期待: {(c.Expect is int e ? $"{e + 1} 番" : "切り替えない")}）/ {why}");
        }
        bool bands = InputSignature.BandOf(59.94) == 60 && InputSignature.BandOf(29.97) == 30 && InputSignature.BandOf(0) == 0 && InputSignature.BandOf(5) == 0;
        item.Num("コマ数の帯（59.94→60、29.97→30、0 と 5 は不明）", bands ? "OK" : "NG");
        if (!bands) bad++;

        item.Verdict = bad == 0 ? Verdict.Ok : Verdict.Ng;
        item.Summary = bad == 0 ? "照合は期待どおりです（合うものがあれば選び、紛らわしいときは何もしません）" : $"{bad} 件で期待と違う結果になりました";
        item.Explanation = "作り物の特徴を並べて、照合だけを調べました。選ぶのは「十分に合い・選択中のものより明らかに合い・他の候補とも差があるとき」だけで、同じ特徴の機器が複数あるときや手がかりが足りないときは何もしません。";
        sec.Items.Add(item);

        var live = new TestItem { Name = "今の入力の特徴と、合うプロファイル" };
        string text = DescribeInputMatch(out InputSignature? sig, out string chosen);
        live.Verdict = Verdict.Info;
        live.Summary = "[情報] " + (sig == null ? "映像が動いていないため、特徴を取れませんでした" : "今の入力の特徴を取れました");
        live.Num("特徴", text).Num("合うプロファイル", chosen)
            .Num("登録されているプロファイル", Profiles.Count == 0 ? "なし" : string.Join("、", Profiles.Select(p => p.Name + (p.Signature == null ? "（特徴なし）" : ""))))
            .Num("自動切り替えの設定", _settings.AutoProfileMode switch { "off" => "オフ", "auto" => "自動で切り替える", _ => "確認する" });
        live.Explanation = "切り替えを提案するのは、入力が変わってから約10秒たち、30秒以内に設定を触っておらず、他のプロファイルが選択中のものより明らかに合うときだけです。テスト中は提案しません。";
        sec.Items.Add(live);
        return Task.CompletedTask;
    }
}
