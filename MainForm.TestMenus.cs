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
        var it = new TestItem { Name = "メニュー: 項目の高さ（右クリックのメニューとその下のメニュー）" };
        int checkedItems = 0, menusOpened = 0;
        double worst = 0;
        string rootInfo = "";

        _ctxAudit = true;
        try
        {
            foreach (bool english in new[] { false, true })
            {
                string lang = english ? "[英語] " : "[日本語] ";
                if (L.English != english || english != lang0 || dpi != _ctxDpi)
                {
                    L.English = english;
                    RebuildMenus(dpi);
                    await Wait(50);
                }

                _ctx.Show(at);
                await Wait(150);
                try
                {
                    var rootH = _ctx.Items.OfType<ToolStripMenuItem>().Where(m => m.Available).Select(m => m.Height).OrderBy(h => h).ToList();
                    if (rootH.Count == 0) { problems.Add(lang + "右クリックのメニューが開きませんでした"); continue; }
                    int baseH = rootH[rootH.Count / 2];
                    rootInfo = $"{baseH}px（{_ctx.DeviceDpi} dpi）";

                    async Task Walk(string path, ToolStripItemCollection items, int ownerDpi)
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
                            }
                            await Walk(name + " > ", m.DropDownItems, ownerDpi);
                            m.HideDropDown();
                        }
                    }
                    foreach (ToolStripMenuItem m in _ctx.Items.OfType<ToolStripMenuItem>().Where(m => m.Available))
                    {
                        checkedItems++;
                        double dev = Math.Abs(m.Height - baseH) / (double)baseH;
                        worst = Math.Max(worst, dev);
                        if (dev > 0.15) problems.Add($"{lang}{Tx(m)}: 高さ {m.Height}px（他は {baseH}px）");
                    }
                    await Walk("", _ctx.Items, _ctx.DeviceDpi);

                    // the menu bar's drop-downs, when the bar is on screen
                    if (_menu.Visible && _menu.Items.Count > 0)
                        await Walk("メニューバー > ", _menu.Items, _menu.DeviceDpi);
                }
                finally { _ctx.Close(); await Wait(50); }
            }
        }
        finally
        {
            _ctxAudit = false;
            if (L.English != lang0 || dpi != _ctxDpi) { L.English = lang0; RebuildMenus(dpi); }
        }

        it.Num("開いたメニュー", $"{menusOpened} 個").Num("調べた項目", $"{checkedItems} 個")
          .Num("親のメニューの項目の高さ", rootInfo).Num("最大のずれ", $"{Math.Round(worst * 100)}%（15% まで許容）");
        if (!_menu.Visible) it.Num("メニューバー", "試験中は非表示のため、右クリックのメニューだけ調べました");
        if (problems.Count == 0) { it.Verdict = Verdict.Ok; it.Summary = "どのメニューも項目の高さがそろっています"; }
        else
        {
            it.Verdict = Verdict.Ng;
            it.Summary = $"{problems.Count} か所で高さがそろっていません";
            it.Explanation = string.Join("\n", problems.Distinct().Take(30));
        }
        sec.Items.Add(it);
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
