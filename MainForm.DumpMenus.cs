using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace YuCap;

/// <summary>Hidden diagnostic (<c>--dump-menus 出力先</c>): writes the whole menu bar and context menu, in both languages, as a tree.
/// Used to check that a menu reorganisation lost no item and that every English entry exists.</summary>
public sealed partial class MainForm
{
    internal string DumpMenus()
    {
        var sb = new StringBuilder();
        bool keep = L.English;
        foreach (bool en in new[] { false, true })
        {
            L.English = en;
            RebuildMenus();
            sb.AppendLine(en ? "##### English" : "##### 日本語");
            sb.AppendLine("== メニューバー ==");
            DumpItems(sb, _menu.Items, 0);
            sb.AppendLine("== 右クリック ==");
            DumpItems(sb, _ctx.Items, 0);
            var texts = new SortedSet<string>(System.StringComparer.Ordinal);
            CollectTexts(_menu.Items, texts);
            CollectTexts(_ctx.Items, texts);
            sb.AppendLine("== 項目名の集合 ==");
            foreach (string t in texts) sb.AppendLine(t);
        }
        L.English = keep;
        return sb.ToString();
    }

    private static void DumpItems(StringBuilder sb, ToolStripItemCollection items, int depth)
    {
        string pad = new(' ', depth * 2);
        foreach (ToolStripItem it in items)
        {
            if (it is ToolStripSeparator) { sb.AppendLine(pad + "---"); continue; }
            string key = it is ToolStripMenuItem { ShortcutKeyDisplayString: { Length: > 0 } k } ? "  [" + k + "]" : string.Empty;
            sb.AppendLine(pad + it.Text + key);
            if (it is ToolStripMenuItem mi && mi.DropDownItems.Count > 0) DumpItems(sb, mi.DropDownItems, depth + 1);
        }
    }

    private static void CollectTexts(ToolStripItemCollection items, SortedSet<string> set)
    {
        foreach (ToolStripItem it in items)
        {
            if (it is ToolStripSeparator) continue;
            set.Add(it.Text ?? string.Empty);
            if (it is ToolStripMenuItem mi) CollectTexts(mi.DropDownItems, set);
        }
    }
}
