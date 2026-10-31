using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

// Test mode, section 9: the right-click menu and its sub-menus are opened on the test monitor and every item is
// measured. A sub-menu laid out for another display scaling than the rest (items twice as tall, bigger arrows)
// is what this is meant to catch - see MainForm.OnCtxOpening.
public sealed partial class MainForm
{
    private static string Tx(ToolStripItem i) => (i.Text ?? "").Replace("&", "");

    private async Task MenuStep(TestSection sec)
    {
        bool lang0 = L.English;
        Rectangle cr = _canvas.RectangleToScreen(_canvas.ClientRectangle);
        Point at = new(cr.Left + cr.Width / 3, cr.Top + cr.Height / 4);
        int dpi = DpiAtPoint(at);
        var problems = new List<string>();
        var it = new TestItem { Name = "メニュー: 項目の高さと文字の大きさ（右クリックのメニュー・メニューバーとその下のメニュー）" };
        int checkedItems = 0, menusOpened = 0;
        double worst = 0;
        string rootInfo = "", fontInfo = "";
        int fontChecked = 0;
        double fontWorst = 0;
        int textsChecked = 0, topCount = 0, mismatches = 0;

        // pt per 100% of scaling: the same on every monitor when the font really follows the scaling
        double Norm(Font f, int d) => f.SizeInPoints * 96.0 / Math.Max(96, d);
        void FontCheck(string where, Font cf, int d, double reference, double formRef)
        {
            fontChecked++;
            double n = Norm(cf, d);
            double dev = Math.Abs(n - reference) / reference;
            fontWorst = Math.Max(fontWorst, dev);
            if (dev > 0.05)
                problems.Add($"{where}: 文字の大きさが {cf.SizeInPoints:0.#}pt（{d} dpi。基準の {reference:0.#}pt/100% に対して {n:0.#}）");
            else if (Math.Abs(n - formRef) / formRef > 0.05)
                problems.Add($"{where}: ウィンドウの文字（{formRef:0.#}pt/100%）と大きさが違います（{n:0.#}）");
        }

        // "moved from another scaling to this one": build the menus for the other scaling first, then go through the
        // same rebuild that OnDpiChanged does
        RebuildMenus(dpi == 96 ? 192 : 96);
        await Wait(50);

        bool barWasVisible = _menu.Visible;
        _ctxAudit = true;
        try
        {
            foreach (bool english in new[] { false, true })
            {
                string lang = english ? "[英語] " : "[日本語] ";
                // rebuild for each language, from the scaling set up above
                {
                    L.English = english;
                    RebuildMenus(dpi);
                    await Wait(50);
                }

                // every menu bar item is also in the right-click menu, and the two copies agree on check marks / enabled state
                {
                    var barTexts = new SortedSet<string>(StringComparer.Ordinal);
                    var ctxTexts = new HashSet<string>(StringComparer.Ordinal);
                    CollectMenuTexts(_menu.Items, barTexts);
                    CollectMenuTexts(_ctx.Items, ctxTexts);
                    foreach (string t in barTexts)
                        if (!ctxTexts.Contains(t)) problems.Add($"{lang}メニューバーの「{t}」が右クリックのメニューにありません");
                    textsChecked += barTexts.Count;
                    topCount = _ctx.Items.OfType<ToolStripMenuItem>().Count();
                    UpdateChecks();
                    int mm = CountMirrorMismatches();
                    mismatches += mm;
                    if (mm > 0) problems.Add($"{lang}メニューバーと右クリックのメニューで、チェックや有効・無効が食い違う項目が {mm} 個あります");
                }

                _ctx.Show(at);
                await Wait(150);
                try
                {
                    var rootH = _ctx.Items.OfType<ToolStripMenuItem>().Where(m => m.Available).Select(m => m.Height).OrderBy(h => h).ToList();
                    if (rootH.Count == 0) { problems.Add(lang + "右クリックのメニューが開きませんでした"); continue; }
                    int baseH = rootH[rootH.Count / 2];
                    rootInfo = $"{baseH}px（{_ctx.DeviceDpi} dpi）";
                    double formRef = Norm(Font, DeviceDpi);
                    double fontRef = Norm(_ctx.Font, _ctx.DeviceDpi);
                    fontInfo = $"右クリック {_ctx.Font.SizeInPoints:0.#}pt（{_ctx.DeviceDpi} dpi）／ メニューバー {_menu.Font.SizeInPoints:0.#}pt（{_menu.DeviceDpi} dpi）／ ウィンドウ {Font.SizeInPoints:0.#}pt（{DeviceDpi} dpi）";
                    if (Math.Abs(fontRef - formRef) / formRef > 0.05)
                        problems.Add($"{lang}右クリックのメニューの文字がウィンドウの文字と違います（{_ctx.Font.SizeInPoints:0.#}pt/{_ctx.DeviceDpi}dpi と {Font.SizeInPoints:0.#}pt/{DeviceDpi}dpi）");

                    async Task Walk(string path, ToolStripItemCollection items, int ownerDpi, double reference)
                    {
                        foreach (ToolStripMenuItem m in items.OfType<ToolStripMenuItem>().Where(m => m.Available && m.HasDropDownItems).ToList())
                        {
                            m.ShowDropDown();
                            await Wait(60);
                            menusOpened++;
                            string name = path + Tx(m);
                            if (m.DropDown.DeviceDpi != ownerDpi)
                                problems.Add($"{lang}{name}: 開いたメニューのスケールが {m.DropDown.DeviceDpi} dpi（親は {ownerDpi} dpi）");
                            foreach (ToolStripMenuItem c in m.DropDownItems.OfType<ToolStripMenuItem>().Where(c => c.Available))
                            {
                                checkedItems++;
                                double dev = Math.Abs(c.Height - baseH) / (double)baseH;
                                worst = Math.Max(worst, dev);
                                if (dev > 0.15)
                                    problems.Add($"{lang}{name} > {Tx(c)}: 高さ {c.Height}px（親のメニューは {baseH}px）");
                                FontCheck($"{lang}{name} > {Tx(c)}", c.Font, ownerDpi, reference, formRef);
                            }
                            await Walk(name + " > ", m.DropDownItems, ownerDpi, reference);
                            m.HideDropDown();
                        }
                    }
                    foreach (ToolStripMenuItem m in _ctx.Items.OfType<ToolStripMenuItem>().Where(m => m.Available))
                    {
                        checkedItems++;
                        double dev = Math.Abs(m.Height - baseH) / (double)baseH;
                        worst = Math.Max(worst, dev);
                        if (dev > 0.15) problems.Add($"{lang}{Tx(m)}: 高さ {m.Height}px（他は {baseH}px）");
                        FontCheck($"{lang}{Tx(m)}", m.Font, _ctx.DeviceDpi, fontRef, formRef);
                    }
                    await Walk("", _ctx.Items, _ctx.DeviceDpi, fontRef);

                    // the menu bar and its drop-downs: shown for the check if the bar is hidden during the test
                    if (_menu.Items.Count > 0)
                    {
                        if (!_menu.Visible) { _menu.Visible = true; LayoutCanvas(); await Wait(100); }
                        int barDpi = _menu.DeviceDpi;
                        var barH = _menu.Items.OfType<ToolStripMenuItem>().Where(m => m.Available).ToList();
                        foreach (var m in barH)
                        {
                            checkedItems++;
                            FontCheck($"{lang}メニューバー {Tx(m)}", m.Font, barDpi, fontRef, formRef);
                        }
                        FontCheck($"{lang}メニューバー", _menu.Font, barDpi, fontRef, formRef);
                        await Walk("メニューバー > ", _menu.Items, barDpi, fontRef);
                    }
                }
                finally { _ctx.Close(); await Wait(50); }
            }
        }
        finally
        {
            _ctxAudit = false;
            if (_menu.Visible != barWasVisible) { _menu.Visible = barWasVisible; LayoutCanvas(); }
            L.English = lang0;
            RebuildMenus(dpi);
        }

        it.Num("メニューバーの項目名が右クリックにもある", $"{textsChecked} 個（日本語・英語の合計）").Num("右クリックの最上位の項目", $"{topCount} 個")
          .Num("メニューバーと右クリックの状態の食い違い", $"{mismatches} 個");
        it.Num("開いたメニュー", $"{menusOpened} 個").Num("調べた項目", $"{checkedItems} 個")
          .Num("文字の大きさ", fontInfo).Num("文字を調べた項目", $"{fontChecked} 個（最大のずれ {Math.Round(fontWorst * 100)}%、5% まで許容）")
          .Num("親のメニューの項目の高さ", rootInfo).Num("最大のずれ", $"{Math.Round(worst * 100)}%（15% まで許容）");
        if (!barWasVisible) it.Num("メニューバー", "試験中は非表示のため、調べる間だけ表示しました");
        if (problems.Count == 0) { it.Verdict = Verdict.Ok; it.Summary = "どのメニューも項目の高さと文字の大きさがそろっています"; }
        else
        {
            it.Verdict = Verdict.Ng;
            it.Summary = $"{problems.Count} か所で高さまたは文字の大きさがそろっていません";
            it.Explanation = string.Join("\n", problems.Distinct().Take(30));
        }
        sec.Items.Add(it);
    }

    private static void CollectMenuTexts(ToolStripItemCollection items, ISet<string> set)
    {
        foreach (ToolStripItem i in items)
        {
            if (i is ToolStripSeparator) continue;
            string t = StripMnemonic(i.Text ?? string.Empty);
            if (t.Length > 0) set.Add(t);
            if (i is ToolStripMenuItem m) CollectMenuTexts(m.DropDownItems, set);
        }
    }

    /// <summary>Items of the menu bar whose twin in the right-click menu shows another check mark or enabled state (after <see cref="UpdateChecks"/> there must be none).</summary>
    private int CountMirrorMismatches()
    {
        int[] barIndex = { 0, 1, 2, 3, 4, 6, 7 };
        int n = 0;
        int Walk(ToolStripMenuItem a, ToolStripMenuItem b)
        {
            int c = 0;
            for (int i = 0; i < Math.Min(a.DropDownItems.Count, b.DropDownItems.Count); i++)
            {
                if (a.DropDownItems[i] is not ToolStripMenuItem x || b.DropDownItems[i] is not ToolStripMenuItem y) continue;
                if (!((!string.IsNullOrEmpty(x.Name) && x.Name == y.Name) || x.Text == y.Text)) continue;
                if (x.Checked != y.Checked || x.Enabled != y.Enabled) c++;
                if (x.HasDropDownItems && y.HasDropDownItems) c += Walk(x, y);
            }
            return c;
        }
        for (int i = 0; i < _ctxFull.Count && i < barIndex.Length; i++)
            if (_menu.Items[barIndex[i]] is ToolStripMenuItem bar) n += Walk(bar, _ctxFull[i]);
        return n;
    }

    /// <summary>FPS だけ: the dark box hugs the number with the same margin on every side, in the state with one line and with two.</summary>
    private Task FpsPanelLayoutStep(TestSection sec)
    {
        var it = new TestItem { Name = "情報表示（FPS だけ）の余白" };
        var problems = new List<string>();
        using var o = new InfoOverlay();
        o.ApplyScale(_info.TextFactor);
        int pad = o.FpsPad;
        int tol = Math.Max(2, pad / 8);
        var sizes = new Dictionary<string, Size>();
        foreach ((string big, string small) in new[] { ("39", ""), ("39", L.F("ゲーム {0:0}", 39)), ("144", L.F("ゲーム {0:0}", 39)), ("7", ""), ("999", "") })
        {
            o.SetFps(big, small);
            var m = o.FpsInkMargins();
            int[] v = { m.L, m.T, m.R, m.B };
            string label = small.Length == 0 ? $"「{big}」" : $"「{big}」＋2行目";
            it.Num(label, $"{o.Width}x{o.Height}px　余白 左{m.L} 上{m.T} 右{m.R} 下{m.B}");
            sizes[big + "|" + small] = o.Size;
            if (v.Max() - v.Min() > tol) problems.Add($"{label}: 余白が不ぞろいです（左{m.L} 上{m.T} 右{m.R} 下{m.B}、許容 {tol}px）");
        }
        // the box must not change size while the digits change
        o.SetFps("12", ""); Size a = o.Size; o.SetFps("98", ""); Size b = o.Size;
        if (a != b) problems.Add($"数字が変わると枠の大きさが変わります（12: {a.Width}x{a.Height}、98: {b.Width}x{b.Height}）");
        it.Num("余白の基準", $"{pad}px（許容 {tol}px）");
        if (problems.Count == 0) { it.Verdict = Verdict.Ok; it.Summary = "枠は数字にぴったりで、四方の余白がそろっています"; }
        else { it.Verdict = Verdict.Ng; it.Summary = $"{problems.Count} か所で余白がそろっていません"; it.Explanation = string.Join("\n", problems); }
        sec.Items.Add(it);
        return Task.CompletedTask;
    }
}
