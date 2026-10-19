using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuCap;

/// <summary>Test mode (7. 機能): the snapshot list and the snapshot options. Everything is saved into a temporary
/// folder that the save folder is pointed at for the duration; the user's folder, settings and clipboard are put back.</summary>
public sealed partial class MainForm
{
    private static Bitmap MakeTestPicture(int n)
    {
        var bmp = new Bitmap(1280, 720);
        using var g = Graphics.FromImage(bmp);
        Color c1 = Color.FromArgb(40 + n * 60 % 200, 90, 200 - n * 40 % 150), c2 = Color.FromArgb(230, 60 + n * 50 % 180, 70);
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, bmp.Width, bmp.Height), c1, c2, 35f)) g.FillRectangle(br, 0, 0, bmp.Width, bmp.Height);
        using var f = new Font("Segoe UI", 120f, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("YuCap " + (n + 1), f, Brushes.White, 60, 260);
        return bmp;
    }

    /// <summary>What was on the clipboard (text, image, files), to put back after the test. Other formats cannot be kept.</summary>
    private sealed class ClipboardBackup
    {
        public string? Text;
        public Bitmap? Image;
        public string[]? Files;
        public bool Unrestorable;     // something else was on it
        public bool Failed;

        public static ClipboardBackup Capture()
        {
            var b = new ClipboardBackup();
            try
            {
                if (Clipboard.ContainsText()) b.Text = Clipboard.GetText();
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) b.Image = new Bitmap(img);
                if (Clipboard.ContainsFileDropList()) b.Files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (b.Text == null && b.Image == null && b.Files == null)
                {
                    IDataObject? d = Clipboard.GetDataObject();
                    b.Unrestorable = d != null && d.GetFormats().Length > 0;
                }
            }
            catch (Exception) { b.Failed = true; }
            return b;
        }

        public void Restore()
        {
            if (Failed || Unrestorable) return;
            try
            {
                if (Text == null && Image == null && Files == null) { Clipboard.Clear(); return; }
                var d = new DataObject();
                if (Text != null) d.SetText(Text);
                if (Image != null) d.SetImage(Image);
                if (Files != null) { var sc = new StringCollection(); sc.AddRange(Files); d.SetFileDropList(sc); }
                Clipboard.SetDataObject(d, true, 5, 100);
            }
            catch (Exception ex) { Log.Info("test mode: clipboard not restored - " + ex.Message); }
            finally { Image?.Dispose(); Image = null; }
        }
    }

    /// <summary>Looks for the file in the Recycle Bin of its drive (by the original path kept in the "$I" record).
    /// Returns the record's path when found, "" when the bin can be read but does not hold it, null when it cannot be checked.</summary>
    private static string? FindInRecycleBin(string originalPath)
    {
        try
        {
            string? drive = Path.GetPathRoot(originalPath);
            string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (drive == null || sid == null) return null;
            string bin = Path.Combine(drive, "$Recycle.Bin", sid);
            if (!Directory.Exists(bin)) return null;
            foreach (string f in Directory.EnumerateFiles(bin, "$I*"))
            {
                byte[] b = File.ReadAllBytes(f);
                if (b.Length <= 28) continue;
                string text = Encoding.Unicode.GetString(b, 24, b.Length - 24);
                if (text.Contains(originalPath, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return string.Empty;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Reads the picture on the clipboard from another process (what an editor pasting it would get), as "WxH".
    /// Null when that process could not be run; "null" text when it found no picture.</summary>
    private static async Task<string?> ClipboardImageFromOtherProcess()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; $i=[System.Windows.Forms.Clipboard]::GetImage(); if($i){'{0}x{1}' -f $i.Width,$i.Height}else{'null'}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            string o = await p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } return null; }
            return o.Trim();
        }
        catch (Exception) { return null; }
    }

    private async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Wait(40);
        }
        return cond();
    }

    private async Task FeatureGallery(TestSection sec)
    {
        var item = new TestItem { Name = "スナップショット一覧" };
        var problems = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), "YuCapGalleryTest_" + Guid.NewGuid().ToString("N")[..8]);
        string? dir0 = _settings.SnapshotDir, prof0 = _settings.ActiveProfile;
        bool byProf0 = _settings.SnapshotByProfile, copy0 = _settings.SnapshotAlsoCopy;
        string fmt0 = _settings.SnapshotFormat;
        string lastDir0 = _lastSaveDir ?? string.Empty;
        ClipboardBackup clip = ClipboardBackup.Capture();
        SnapshotGalleryForm? g = null;
        string? recycledRecord = null;
        try
        {
            Directory.CreateDirectory(tmp);
            _settings.SnapshotDir = tmp;
            _settings.SnapshotByProfile = false;
            _settings.SnapshotAlsoCopy = false;
            _settings.SnapshotFormat = "png";

            // ---- 1. three saves: unique, time-sortable names ----
            var saved = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                using Bitmap pic = MakeTestPicture(i);
                if (!SaveBitmapAsSnapshot(pic, out _, out string? err, false)) { problems.Add("保存に失敗しました: " + err); break; }
                saved.Add(_lastSavedPath);
                await Wait(30);
            }
            var names = saved.Select(p => Path.GetFileName(p)).ToList();
            bool unique = names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count;
            bool sortable = names.SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal));
            DateTime fixedNow = new(2026, 1, 2, 3, 4, 5, 678);
            string c1 = UniqueSnapshotPath(tmp, "png", fixedNow);
            File.WriteAllBytes(c1, new byte[] { 0 });
            string c2 = UniqueSnapshotPath(tmp, "png", fixedNow);
            File.Delete(c1);
            bool collisionOk = !string.Equals(c1, c2, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(c1) == "Capture_20260102_030405_678.png";
            item.Num("保存した枚数", $"{saved.Count} 枚（{(unique ? "名前は重なりません" : "名前が重なりました")}・{(sortable ? "時刻順に並びます" : "時刻順になりません")}）");
            item.Num("同じミリ秒に保存したとき", collisionOk ? "後の方に _2 が付いて上書きされません" : "名前が重なります");
            if (!unique || !sortable || !collisionOk) problems.Add("ファイル名が一意・時刻順になっていません");

            // ---- 2. open the list (invisibly, no focus) ----
            g = new SnapshotGalleryForm(tmp) { Icon = Icon, Silent = true, Opacity = 0, StartPosition = FormStartPosition.Manual };
            Rectangle tb = _topt.Target.Bounds;
            g.Location = new Point(tb.X + Math.Max(0, (tb.Width - g.Width) / 2), tb.Y + Math.Max(0, (tb.Height - g.Height) / 2));
            g.Show(this);
            bool scanned = await WaitUntil(() => g.ScanCount >= 1, 5000);
            var expectedOrder = saved.AsEnumerable().Reverse().ToList();
            var listed = g.Listed.Select(e => e.Path).ToList();
            bool orderOk = scanned && listed.SequenceEqual(expectedOrder, StringComparer.OrdinalIgnoreCase)
                && g.ViewEntries.Select(e => e.Path).SequenceEqual(expectedOrder, StringComparer.OrdinalIgnoreCase);
            item.Num("一覧に出た枚数", scanned ? $"{listed.Count} 枚（新しい順: {(orderOk ? "正しい" : "違う")}）" : "読み込めませんでした");
            if (!orderOk) problems.Add("一覧が新しい順に並んでいません");

            // ---- 3. thumbnails ----
            g.LoadAllThumbs();
            bool thumbs = await WaitUntil(() => g.CachedThumbs() >= saved.Count, 8000);
            item.Num("サムネイル", thumbs ? $"{g.CachedThumbs()} 枚とも読み込めました（画面の外で）" : $"{g.CachedThumbs()} / {saved.Count} 枚しか読み込めませんでした");
            if (!thumbs) problems.Add("サムネイルを読み込めません");

            // ---- 4. selection, preview, copy ----
            g.SelectPaths(saved[2]);
            bool preview = await WaitUntil(() => g.PreviewHasImage, 4000);
            item.Num("プレビュー", preview ? "選んだ画像が大きく出ます" : "出ません");
            if (!preview) problems.Add("プレビューが出ません");

            if (clip.Failed) item.Num("コピー", "クリップボードを読めなかったため、確認を省きました（他のアプリが使用中の可能性）");
            else
            {
                try
                {
                    Clipboard.Clear();
                    bool c = g.CopySelected();
                    string? other = await ClipboardImageFromOtherProcess();
                    Size cs = Size.Empty;
                    if (other != null && other.Contains('x') && other != "null") { var parts = other.Split('x'); if (parts.Length == 2 && int.TryParse(parts[0], out int cw) && int.TryParse(parts[1], out int ch)) cs = new Size(cw, ch); }
                    bool img = cs.Width > 0, files = Clipboard.ContainsFileDropList()
                        && Clipboard.GetFileDropList().Cast<string>().SequenceEqual(new[] { saved[2] }, StringComparer.OrdinalIgnoreCase);
                    item.Num("コピー（1 枚）", c && img && files ? $"画像（{cs.Width}x{cs.Height}）とファイルの両方が入りました" : $"失敗（画像 {img}・ファイル {files}）");
                    if (!(c && img && files)) problems.Add("コピーで画像が入りません");

                    g.SelectPaths(saved[0], saved[1]);
                    Clipboard.Clear();
                    bool c2m = g.CopySelected();
                    bool two = Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Count == 2;
                    item.Num("コピー（2 枚）", c2m && two ? "2 つのファイルが入りました" : "失敗");
                    if (!(c2m && two)) problems.Add("複数枚のコピーが入りません");

                    // 保存と同時にコピー
                    Clipboard.Clear();
                    _settings.SnapshotAlsoCopy = true;
                    using (Bitmap pic = MakeTestPicture(3))
                    {
                        bool ok = SaveBitmapAsSnapshot(pic, out _, out string? err, true);
                        saved.Add(_lastSavedPath);
                        string? other2 = await ClipboardImageFromOtherProcess();
                        bool alsoOk = ok && _lastSaveCopied && other2 is { Length: > 0 } && other2 != "null";
                        item.Num("保存と同時にコピー", alsoOk ? "保存して、クリップボードにも入りました" : $"失敗（{err}）");
                        if (!alsoOk) problems.Add("保存と同時のコピーが働きません");
                    }
                    _settings.SnapshotAlsoCopy = false;
                }
                catch (Exception ex) { problems.Add("コピーの確認中にエラー: " + ex.Message); }
            }
            if (saved.Count < 4)
            {
                // the clipboard part was skipped: still save the fourth picture, for the auto-refresh check
                using Bitmap pic = MakeTestPicture(3);
                SaveBitmapAsSnapshot(pic, out _, out _, false);
                saved.Add(_lastSavedPath);
            }

            // ---- 5. auto refresh (nothing is told to the list) ----
            string fourth = saved[3];
            bool auto = await WaitUntil(() => g.Listed.Any(e => string.Equals(e.Path, fourth, StringComparison.OrdinalIgnoreCase)), 5000);
            item.Num("保存すると一覧が自動で増える", auto ? "増えました" : "増えませんでした");
            if (!auto) problems.Add("新しく保存した画像が一覧に出ません");

            // ---- 6. delete -> Recycle Bin ----
            g.SelectPaths(fourth);
            int deleted = g.DeleteSelected(false);
            bool gone = !File.Exists(fourth);
            string? rec = FindInRecycleBin(fourth);
            recycledRecord = string.IsNullOrEmpty(rec) ? null : rec;
            bool listGone = await WaitUntil(() => !g.Listed.Any(e => string.Equals(e.Path, fourth, StringComparison.OrdinalIgnoreCase)), 5000);
            string binText = rec == null ? "ごみ箱の中身は確認できませんでした" : rec.Length == 0 ? "ごみ箱に見つかりません（完全に削除された可能性）" : "ごみ箱に入りました";
            item.Num("削除", $"{deleted} 枚・元の場所から{(gone ? "消えました" : "消えていません")}・{binText}・一覧から{(listGone ? "消えました" : "消えていません")}");
            if (deleted != 1 || !gone) problems.Add("削除できません");
            else if (rec != null && rec.Length == 0) problems.Add("削除したファイルがごみ箱に入っていません");
            else if (!listGone) problems.Add("削除した画像が一覧に残っています");

            // ---- 7. rename ----
            GalleryEntry? target = g.Listed.FirstOrDefault(e => string.Equals(e.Path, saved[0], StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                bool badRefused = !g.RenameFile(target, "a/b", out _) && !g.RenameFile(target, "", out _);
                bool renamed = g.RenameFile(target, "renamed_test", out string? rmsg);
                string np = Path.Combine(tmp, "renamed_test.png");
                bool fileOk = renamed && File.Exists(np) && !File.Exists(saved[0]);
                bool inList = fileOk && await WaitUntil(() => g.Listed.Any(e => string.Equals(e.Path, np, StringComparison.OrdinalIgnoreCase)), 5000);
                item.Num("名前の変更", fileOk ? $"変更できました・使えない名前は{(badRefused ? "断られました" : "断られませんでした")}・一覧に{(inList ? "反映されました" : "反映されません")}" : $"失敗（{rmsg}）");
                if (!fileOk || !badRefused || !inList) problems.Add("名前の変更が働きません");
                if (fileOk) saved[0] = np;
            }

            // ---- 8. per-profile folder ----
            _settings.SnapshotByProfile = true;
            _settings.ActiveProfile = "テスト/用:1";
            string expectSub = Path.Combine(tmp, "テスト_用_1");
            using (Bitmap pic = MakeTestPicture(4))
            {
                bool ok = SaveBitmapAsSnapshot(pic, out _, out string? err, false);
                string p = _lastSavedPath;
                bool inSub = ok && string.Equals(Path.GetDirectoryName(p), expectSub, StringComparison.OrdinalIgnoreCase) && File.Exists(p);
                bool listed2 = inSub && await WaitUntil(() => g.Listed.Any(e => string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase) && e.Folder == "テスト_用_1"), 5000);
                item.Num("プロファイルごとのフォルダ", inSub ? $"「テスト_用_1」フォルダに保存・一覧では{(listed2 ? "そのフォルダの組に入りました" : "出ませんでした")}" : $"失敗（{err}）");
                if (!inSub || !listed2) problems.Add("プロファイルごとのフォルダに保存されません");
            }
            _settings.ActiveProfile = null;
            using (Bitmap pic = MakeTestPicture(5))
            {
                bool ok = SaveBitmapAsSnapshot(pic, out _, out _, false);
                bool root = ok && string.Equals(Path.GetDirectoryName(_lastSavedPath), tmp, StringComparison.OrdinalIgnoreCase);
                item.Num("プロファイルなしのとき", root ? "保存先の直下に保存" : "直下に保存されません");
                if (!root) problems.Add("プロファイルなしのときに直下へ保存されません");
            }
            bool names2 = SafeFolderName("CON") == "_CON" && SafeFolderName("a:b*c") == "a_b_c" && SafeFolderName("x. ") == "x"
                && SafeFolderName("") == "" && SafeFolderName(null) == "" && SafeFolderName(new string('あ', 80)).Length == 48;
            item.Num("フォルダ名の整え方", names2 ? "使えない文字・予約名・長さを直せます" : "整え方が想定と違います");
            if (!names2) problems.Add("プロファイル名からフォルダ名を作れません");

            // ---- 9. the second scan of the folder: grouped in the list ----
            bool grouped = await WaitUntil(() => g.Listed.Select(e => e.Folder).Distinct().Count() >= 2, 3000);
            item.Num("フォルダの組", grouped ? "保存先の直下とサブフォルダが別の組になります" : "組が分かれていません");

            item.Verdict = problems.Count == 0 ? Verdict.Ok : Verdict.Ng;
            item.Summary = problems.Count == 0 ? "スナップショット一覧とその保存の設定が働きました" : $"{problems.Count} 件の問題があります";
            item.Explanation = problems.Count == 0
                ? "一時フォルダに保存した画像で試験しました（あなたの保存先フォルダは使っていません）。一覧は新しい順に並び、サムネイルとプレビューが出て、コピー・削除（ごみ箱）・名前の変更・保存時の自動更新が働きました。"
                : string.Join("\n", problems);
            if (clip.Unrestorable) item.Explanation += "\n試験の前のクリップボードには文字・画像・ファイル以外の形式が入っていたため、元には戻していません。";
            else if (!clip.Failed) item.Explanation += "\n試験の前のクリップボードの内容は元に戻しました。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Verdict = Verdict.Ng;
            item.Summary = "この項目は実行できませんでした。";
            item.Explanation = "理由: " + ex.Message;
            Log.Info("test mode: gallery step failed - " + ex);
        }
        finally
        {
            try { g?.Close(); g?.Dispose(); } catch (Exception) { }
            _settings.SnapshotDir = dir0;
            _settings.SnapshotByProfile = byProf0;
            _settings.SnapshotAlsoCopy = copy0;
            _settings.SnapshotFormat = fmt0;
            _settings.ActiveProfile = prof0;
            _lastSaveDir = lastDir0.Length == 0 ? null : lastDir0;
            clip.Restore();
            try
            {
                // the test's own files: the temporary folder, and the record of the picture it sent to the Recycle Bin
                if (recycledRecord != null && File.Exists(recycledRecord))
                {
                    string fn = Path.GetFileName(recycledRecord);
                    File.Delete(recycledRecord);
                    string r = Path.Combine(Path.GetDirectoryName(recycledRecord)!, "$R" + fn[2..]);
                    if (File.Exists(r)) File.Delete(r);
                }
                if (Directory.Exists(tmp) && Path.GetFileName(tmp).StartsWith("YuCapGalleryTest_", StringComparison.Ordinal))
                    Directory.Delete(tmp, true);
            }
            catch (Exception ex) { Log.Info("test mode: could not clean up - " + ex.Message); }
        }
        sec.Items.Add(item);
    }
}
