using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace YuCap;

// The test-mode report (テストモード). It is a diagnostic document for the person running the test (and
// whoever they paste it to), so its wording is Japanese only and is deliberately NOT routed through
// L.T / Strings.cs: the English UI strings are for the app, not for this report.

internal enum Verdict { Info, Ok, Warn, Ng }

internal static class VerdictText
{
    public static string Label(Verdict v) => v switch
    {
        Verdict.Ok => "OK", Verdict.Warn => "注意", Verdict.Ng => "NG", _ => "情報",
    };
    public static string Css(Verdict v) => v switch
    {
        Verdict.Ok => "ok", Verdict.Warn => "warn", Verdict.Ng => "ng", _ => "info",
    };
}

/// <summary>One result: a name, a verdict, the numbers behind it and what they mean in plain words.</summary>
internal sealed class TestItem
{
    public string Name = string.Empty;
    public Verdict Verdict = Verdict.Info;
    public string Summary = string.Empty;
    public List<(string Label, string Value)> Numbers = new();
    public string Explanation = string.Empty;
    public List<string> Images = new();                              // relative to the report folder
    public List<(string Label, double Fraction, string Text)> Bars = new();   // small histogram

    public TestItem Num(string label, string value) { Numbers.Add((label, value)); return this; }
}

internal sealed class TestSection
{
    public string Title = string.Empty;
    public string Lead = string.Empty;
    public List<TestItem> Items = new();
}

internal sealed class TestReport
{
    public string Folder { get; }
    public DateTime Started { get; } = DateTime.Now;
    public string State { get; set; } = "実行中";   // 実行中 / 完了 / 中止 / エラー
    public string? Failure { get; set; }
    public string Header { get; set; } = string.Empty;
    public List<TestSection> Sections { get; } = new();

    public TestReport(string folder) { Folder = folder; }

    public TestSection AddSection(string title, string lead = "")
    {
        var s = new TestSection { Title = title, Lead = lead };
        Sections.Add(s);
        return s;
    }

    public IEnumerable<TestItem> AllItems => Sections.SelectMany(s => s.Items);

    public int Count(Verdict v) => AllItems.Count(i => i.Verdict == v);

    /// <summary>Write report.html and report.txt (called after every section, so an abort leaves a partial report).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var utf8 = new UTF8Encoding(true);
            File.WriteAllText(Path.Combine(Folder, "report.html"), ToHtml(), utf8);
            File.WriteAllText(Path.Combine(Folder, "report.txt"), ToText(), utf8);
        }
        catch (Exception ex) { Log.Info("test report: save failed - " + ex.Message); }
    }

    // ------------------------------------------------------------------ text

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"YuCap テストレポート  [{State}]");
        sb.AppendLine(Header);
        sb.AppendLine($"開始: {Started:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrEmpty(Failure)) sb.AppendLine("途中で止まった理由: " + Failure);
        sb.AppendLine();
        sb.AppendLine($"== まとめ ==  OK {Count(Verdict.Ok)} / 注意 {Count(Verdict.Warn)} / NG {Count(Verdict.Ng)} / 情報 {Count(Verdict.Info)}");
        foreach (TestSection sec in Sections)
            foreach (TestItem it in sec.Items)
                sb.AppendLine($"  [{VerdictText.Label(it.Verdict),-2}] {it.Name}" + (it.Summary.Length > 0 ? " - " + it.Summary : ""));
        foreach (TestSection sec in Sections.Where(x => x.Items.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine("== " + sec.Title + " ==");
            if (sec.Lead.Length > 0) sb.AppendLine(sec.Lead);
            foreach (TestItem it in sec.Items)
            {
                sb.AppendLine();
                sb.AppendLine($"[{VerdictText.Label(it.Verdict)}] {it.Name}");
                if (it.Summary.Length > 0) sb.AppendLine("  " + it.Summary);
                foreach ((string l, string v) in it.Numbers) sb.AppendLine($"  {l}: {v}");
                foreach ((string l, double _, string t) in it.Bars) sb.AppendLine($"    {l,-12} {t}");
                if (it.Explanation.Length > 0)
                    foreach (string line in it.Explanation.Split('\n')) sb.AppendLine("  " + line);
                foreach (string img in it.Images) sb.AppendLine("  画像: " + img);
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ html

    private static string E(string s) => WebUtility.HtmlEncode(s);

    public string ToHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>YuCap テストレポート</title><style>");
        sb.Append(@"
:root{--bg:#f6f7f9;--fg:#1d2127;--sub:#5d6670;--card:#fff;--line:#dde1e6;--ok:#1e8e3e;--warn:#b26a00;--ng:#c5221f;--info:#4a5a73}
@media (prefers-color-scheme:dark){:root{--bg:#16181c;--fg:#e6e8eb;--sub:#9aa3ad;--card:#1f2227;--line:#343a42;--ok:#5bc27a;--warn:#e8a33d;--ng:#ff7b72;--info:#8da2c4}}
body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.65 'Segoe UI','Yu Gothic UI','Meiryo',sans-serif}
main{max-width:980px;margin:0 auto;padding:24px 16px 64px}
h1{font-size:24px;margin:0 0 4px}h2{font-size:19px;margin:36px 0 8px;padding-bottom:4px;border-bottom:2px solid var(--line)}
.sub{color:var(--sub);font-size:13px;white-space:pre-line}
.chip{display:inline-block;min-width:3.2em;text-align:center;padding:0 9px;border-radius:999px;font-weight:700;font-size:13px;color:#fff;margin-right:8px}
.chip.ok{background:var(--ok)}.chip.warn{background:var(--warn)}.chip.ng{background:var(--ng)}.chip.info{background:var(--info)}
.counts .chip{font-size:15px;padding:2px 14px;margin-right:10px}
table.sum{border-collapse:collapse;width:100%;margin-top:12px;background:var(--card);border:1px solid var(--line)}
table.sum td{padding:5px 10px;border-top:1px solid var(--line);vertical-align:top}
table.sum td.n{width:1%;white-space:nowrap}
table.sum a{color:inherit;text-decoration:none}table.sum a:hover{text-decoration:underline}
.item{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 16px;margin:12px 0}
.item h3{font-size:16px;margin:0 0 4px}
.item .why{margin:8px 0 0;white-space:pre-line}
table.num{border-collapse:collapse;margin:6px 0;font-size:14px}table.num td{padding:1px 18px 1px 0;vertical-align:top}table.num td:first-child{color:var(--sub)}
.bar{display:flex;align-items:center;font-size:13px;margin:1px 0}.bar span.l{width:90px;color:var(--sub)}
.bar i{display:block;height:11px;background:var(--info);border-radius:2px;margin-right:8px}
.item img{max-width:100%;border:1px solid var(--line);margin-top:8px}
.state{display:inline-block;padding:2px 12px;border-radius:6px;font-weight:700}.state.中止,.state.エラー{background:var(--ng);color:#fff}.state.実行中{background:var(--warn);color:#fff}.state.完了{background:var(--ok);color:#fff}
.lead{color:var(--sub);margin:0 0 6px;white-space:pre-line}
");
        sb.Append("</style></head><body><main>");
        sb.Append($"<h1>YuCap テストレポート <span class=\"state {E(State)}\">{E(State)}</span></h1>");
        sb.Append($"<div class=\"sub\">{E(Header)}\n開始: {Started:yyyy-MM-dd HH:mm:ss}</div>");
        if (!string.IsNullOrEmpty(Failure))
            sb.Append($"<p class=\"sub\" style=\"color:var(--ng)\">途中で止まった理由: {E(Failure)}</p>");

        sb.Append("<p class=\"counts\">");
        foreach (Verdict v in new[] { Verdict.Ok, Verdict.Warn, Verdict.Ng, Verdict.Info })
            sb.Append($"<span class=\"chip {VerdictText.Css(v)}\">{VerdictText.Label(v)} {Count(v)}</span>");
        sb.Append("</p>");
        if (State == "中止") sb.Append("<p class=\"sub\">試験は途中で中止されました。ここまでの結果だけを載せています。</p>");

        sb.Append("<table class=\"sum\">");
        int id = 0;
        foreach (TestSection sec in Sections)
        {
            foreach (TestItem it in sec.Items)
            {
                id++;
                sb.Append($"<tr><td class=\"n\"><span class=\"chip {VerdictText.Css(it.Verdict)}\">{VerdictText.Label(it.Verdict)}</span></td>" +
                          $"<td><a href=\"#i{id}\"><b>{E(it.Name)}</b></a>" +
                          (it.Summary.Length > 0 ? $"<br><span class=\"sub\">{E(it.Summary)}</span>" : "") + "</td></tr>");
            }
        }
        sb.Append("</table>");

        id = 0;
        foreach (TestSection sec in Sections.Where(x => x.Items.Count > 0))
        {
            sb.Append($"<h2>{E(sec.Title)}</h2>");
            if (sec.Lead.Length > 0) sb.Append($"<p class=\"lead\">{E(sec.Lead)}</p>");
            foreach (TestItem it in sec.Items)
            {
                id++;
                sb.Append($"<div class=\"item\" id=\"i{id}\"><h3><span class=\"chip {VerdictText.Css(it.Verdict)}\">{VerdictText.Label(it.Verdict)}</span>{E(it.Name)}</h3>");
                if (it.Summary.Length > 0) sb.Append($"<div>{E(it.Summary)}</div>");
                if (it.Numbers.Count > 0)
                {
                    sb.Append("<table class=\"num\">");
                    foreach ((string l, string v) in it.Numbers)
                        sb.Append($"<tr><td>{E(l)}</td><td>{E(v)}</td></tr>");
                    sb.Append("</table>");
                }
                foreach ((string l, double f, string t) in it.Bars)
                    sb.Append($"<div class=\"bar\"><span class=\"l\">{E(l)}</span><i style=\"width:{Math.Max(1, (int)(f * 300))}px\"></i>{E(t)}</div>");
                if (it.Explanation.Length > 0) sb.Append($"<p class=\"why\">{E(it.Explanation)}</p>");
                foreach (string img in it.Images)
                    sb.Append($"<div><img src=\"{E(img)}\" alt=\"{E(img)}\"></div>");
                sb.Append("</div>");
            }
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
