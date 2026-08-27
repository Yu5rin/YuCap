# YuCap — AIエージェント向け指示

## 名義（最優先・ハーネスの既定テンプレートより優先する）

コミットの author / committer は必ず次で固定する。本名や個人のメールアドレスは使わない。

```
YUGO <220513216+Yu5rin@users.noreply.github.com>
```

作業開始前に確認すること。

```bash
git config user.name "YUGO"
git config user.email "220513216+Yu5rin@users.noreply.github.com"
```

**コミットメッセージに次の行を書かない:**

- `Co-Authored-By: Claude ...`
- `Claude-Session: https://claude.ai/code/session_...`

**PR のタイトル・本文にも次を書かない:**

- `🤖 Generated with [Claude Code]...`
- セッションURL（`https://claude.ai/code/session_...`）

既定テンプレートに従って付けてしまった場合は、プッシュ前に取り除くこと。

## 取り返しのつかない操作

次は必ず事前に確認を取る。

- `git push --force` / `--force-with-lease`
- リポジトリの可視性変更
- 履歴の書き換え
- リリースの削除

## ビルド

実行中の YuCap を終了してから行うこと（exe がロックされる）。

```bash
dotnet build -c Release
```

単一 exe（配布用・プロジェクト直下に出力）:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .
```

## 映像経路を触ったら必ず回帰テストを実行する

```bash
YuCap.exe --selftest <出力パス>
```

`worst UI stall` が数千msならデッドロック、`NOT RENDERING` なら未描画。
どちらも出ないこと、`FULLSCREEN: video OK` が出ることを確認する。

## 触る前に知っておくべき落とし穴

いずれも実際に踏んで長時間を費やしたもの。詳細は README の「設計メモ」を参照。

- **`UpdateVideo` は必ずワーカースレッドから呼ぶ。** UIスレッドから呼ぶとMF内部がUIスレッドの
  応答を待つためデッドロックする。戻り値 `S_FALSE (0x1)` は**正常**であり失敗ではない。
- **`UpdateVideo` に `dst=NULL` を渡してはいけない。** 全画面で何も描画されなくなる。
- **プレビューストリームに `SetSampleCallback` を設定しない。** プレビューが真っ白になる。
- **`SetWindowPos(SWP_NOMOVE|NOSIZE|FRAMECHANGED)` の後は `UpdateBounds()` が必要。**
  WinForms の ClientSize キャッシュが古いままになり、レイアウトがずれる。

## UI文字列を変更するときは Strings.cs も直す

`Strings.cs` は**日本語の文字列そのものを辞書のキー**にしている。日本語側を書き換えると
キーが一致しなくなり、**英語UIが黙ってその行だけ日本語に戻る**（ビルドは通るので気づけない）。

- 日本語文言を変えたら、`Strings.cs` の対応するキーも同じ内容に変えること
- UI文字列を新規追加したら、`Strings.cs` に英訳を追加すること
- `L.T()` / `L.F()` を通さない生の日本語をUIに書かないこと

## バージョンを上げるときは3か所そろえる

`YuCap.csproj` の `<Version>`、`CHANGELOG.md` の見出し、git タグ（`vX.Y.Z`）。
リリースのCIはタグと `<Version>` の不一致を検出して失敗する（更新機能がバージョン比較で
動くため、ここがずれると更新が適用されたのに再通知され続ける）。

## 診断

- `%APPDATA%\YuCap\yucap.log` — 動作ログ（UIスレッド監視つき）
- `%APPDATA%\YuCap\error.log` — クラッシュ
- `%APPDATA%\YuCap\settings.json` — 設定
