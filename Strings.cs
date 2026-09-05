using System.Collections.Generic;

namespace YuCap;

/// <summary>
/// Minimal UI localization: the Japanese string IS the key, and is also the
/// fallback when no English entry exists. That keeps every call site trivial
/// (no resource ids to invent) at one cost worth knowing about:
///
///     CHANGING A JAPANESE STRING SILENTLY BREAKS ITS ENGLISH TRANSLATION.
///
/// The lookup simply misses and the English UI shows that one line in Japanese.
/// The build still succeeds, so nothing warns you. Whenever Japanese UI text is
/// edited, the matching key here must be edited to match. `--selftest` cannot
/// catch this — it is a data mismatch, not a code fault.
///
/// MainForm.RebuildMenus() re-runs the menu builders, so switching language
/// takes effect immediately; it no longer needs a restart.
/// </summary>
internal static class L
{
    public static bool English;

    public static string T(string ja) =>
        English && Map.TryGetValue(ja, out string? en) ? en : ja;

    public static string F(string jaFormat, params object?[] args) =>
        string.Format(T(jaFormat), args);

    private static readonly Dictionary<string, string> Map = new()
    {
        // ---- Application ----
        ["YuCap - キャプチャビューア"] = "YuCap - Capture Viewer",
        ["YuCap は既に起動しています。"] = "YuCap is already running.",
        ["エラー"] = "Error",
        ["更新確認"] = "Check for updates",

        // ---- Menus ----
        ["ファイル(&F)"] = "&File",
        ["スナップショットを保存"] = "Save snapshot",
        ["スナップショットをコピー"] = "Copy snapshot",
        ["保存先フォルダを開く"] = "Open snapshot folder",
        ["スナップショット設定..."] = "Snapshot settings...",
        ["連写スナップショット..."] = "Burst snapshots...",
        ["Windows起動時に自動実行"] = "Run at Windows startup",
        ["グローバルホットキーを有効化"] = "Enable global hotkeys",
        ["ホットキー設定..."] = "Hotkey settings...",
        ["カーソルを自動的に隠す"] = "Auto-hide mouse cursor",
        ["隠すまでの時間"] = "Hide after",
        ["{0}秒"] = "{0} sec",
        ["カーソル自動非表示: {0}秒"] = "Cursor auto-hide: {0}s",
        ["カーソル自動非表示: オフ"] = "Cursor auto-hide: off",
        ["ホットキー設定"] = "Hotkey settings",
        ["スナップショット:"] = "Snapshot:",
        ["ミュート:"] = "Mute:",
        ["PiP切替:"] = "Toggle PiP:",
        ["欄をクリックしてキーを押してください。Esc で無効化できます。"] =
            "Click a field and press a key combo. Esc disables it.",
        ["同じキーが複数の操作に割り当てられています。"] =
            "The same key is assigned to more than one action.",
        ["終了"] = "Exit",
        ["デバイス(&D)"] = "&Device",
        ["映像デバイス"] = "Video device",
        ["音声デバイス"] = "Audio device",
        ["（開くと更新）"] = "(refreshes on open)",
        ["映像モード (解像度/FPS)"] = "Video mode (resolution/FPS)",
        ["音声バッファ設定..."] = "Audio buffer settings...",
        ["入力レベルを最大にする"] = "Maximize input level",
        ["入力レベルを元に戻す"] = "Restore input level",
        ["このデバイスは入力レベルを変更できません"] = "This device has no input level control",
        ["入力レベルは既に最大です"] = "Input level is already at maximum",
        ["入力レベル {0}% → 100%"] = "Input level {0}% → 100%",
        ["入力レベルを {0}% に戻しました"] = "Input level restored to {0}%",
        ["入力レベルを最大にしますか？\n\nWindows の録音デバイスの音量を 100% に変更します。\nこの設定は他のアプリにも影響し、YuCap を終了しても元に戻りません。\n（メニューの「入力レベルを元に戻す」で戻せます）"] =
            "Maximize the input level?\n\nThis sets the Windows recording level for this device to 100%.\nIt affects other applications and stays that way after YuCap exits.\n(Use \"Restore input level\" in the menu to undo it.)",
        ["優先デバイス設定..."] = "Preferred device...",
        ["ミュート"] = "Mute",
        ["表示(&V)"] = "&View",
        ["アスペクト比 / 表示モード"] = "Aspect ratio / display mode",
        ["アスペクト比を保持"] = "Keep aspect ratio",
        ["ウィンドウに引き伸ばし"] = "Stretch to window",
        ["原寸表示"] = "Actual size (1:1)",
        ["整数倍表示 (くっきり)"] = "Integer scaling (sharp)",
        ["ウィンドウ比率を映像に固定"] = "Lock window ratio to video",
        ["回転 / 反転"] = "Rotation / flip",
        ["回転なし"] = "No rotation",
        ["{0}° 回転"] = "Rotate {0}°",
        ["左右反転"] = "Flip horizontally",
        ["ズームをリセット"] = "Reset zoom",
        ["ウィンドウサイズ"] = "Window size",
        ["全画面表示"] = "Fullscreen",
        ["一時停止 / 再開"] = "Pause / resume",
        ["ピクチャインピクチャ"] = "Picture-in-picture",
        ["PiP設定"] = "PiP settings",
        ["不透明度（通常時）"] = "Opacity (idle)",
        ["不透明度（マウスオーバー時）"] = "Opacity (hover)",
        ["サイズ（映像原寸比）"] = "Size (% of source)",
        ["表示位置"] = "Position",
        ["右下"] = "Bottom-right",
        ["左下"] = "Bottom-left",
        ["右上"] = "Top-right",
        ["左上"] = "Top-left",
        ["クリックスルー"] = "Click-through",
        ["オプション(&O)"] = "&Options",
        ["ウィンドウ枠を非表示"] = "Hide window frame",
        ["常に前面に表示"] = "Always on top",
        ["メニューバーを表示"] = "Show menu bar",
        ["ステータスバーを表示"] = "Show status bar",
        ["ヘルプ(&H)"] = "&Help",
        ["言語 / Language"] = "言語 / Language",
        ["バージョン情報..."] = "About...",
        ["更新を確認..."] = "Check for updates...",

        // ---- Update ----
        ["保存しました: {0}（クリックで開く）"] = "Saved: {0} (click to open)",
        ["連写を停止 ({0}/{1})"] = "Stop burst ({0}/{1})",
        ["既に起動しています"] = "Already running",
        ["バージョン {0} に更新しました"] = "Updated to version {0}",
        ["{0} をスキップします"] = "Skipping {0}",
        ["ホットキー使用中のため無効: {0}（オプションで変更できます）"] =
            "Hotkey in use by another app: {0} (change it in Options)",
        ["クリックスルー: オン（{0} で解除）"] = "Click-through: on ({0} to exit)",
        ["クリックスルーには PiP切替 のホットキーが必要です。\nオプション → ホットキー設定 で割り当ててください。"] =
            "Click-through needs a Toggle PiP hotkey.\nAssign one under Options → Hotkey settings.",
        ["PiP のホットキーが未設定のため、クリックスルーを解除しました"] =
            "Click-through was turned off because no PiP hotkey is assigned",
        ["更新すると YuCap は自動的に再起動します。"] = "YuCap will restart automatically after updating.",
        ["リリースノートを見る"] = "View release notes",
        ["今すぐ更新"] = "Update now",
        ["後で"] = "Later",
        ["この版をスキップ"] = "Skip this version",
        ["起動時に更新を確認"] = "Check for updates at startup",
        ["起動時の更新確認: オン"] = "Startup update check: on",
        ["起動時の更新確認: オフ"] = "Startup update check: off",
        ["更新を確認しています..."] = "Checking for updates...",
        ["現在のバージョンは {0} です。\n更新はありません。"] =
            "You are running {0}.\nNo updates available.",
        ["新しいバージョン {0} があります（現在 {1}）。"] =
            "Version {0} is available (you have {1}).",
        ["ダウンロードサイズ: 約 {0} MB"] = "Download size: about {0} MB",
        ["更新の確認に失敗しました。\nネットワーク接続を確認してください。\n\n{0}"] =
            "Could not check for updates.\nPlease check your network connection.\n\n{0}",
        ["更新の確認先が不正です。"] = "The update endpoint is not a valid one.",
        ["最新リリースの情報を解釈できませんでした。"] = "The latest release information could not be read.",
        ["インストール先に書き込めないため、自動更新できません。\nリリースページを開きますか？"] =
            "Cannot write to the install folder, so the update cannot be applied.\nOpen the releases page instead?",
        ["更新をダウンロード中"] = "Downloading update",
        ["{0} をダウンロードしています..."] = "Downloading {0}...",
        ["更新のダウンロードに失敗しました。\n\n{0}"] = "Failed to download the update.\n\n{0}",
        ["更新の適用に失敗しました。元の状態に戻しました。\n\n{0}"] =
            "Failed to apply the update; the previous version was restored.\n\n{0}",


        // ---- Device lists ----
        ["（デバイスなし）"] = "(no devices)",
        ["自動 (最大解像度)"] = "Auto (max resolution)",
        ["（利用可能なモードなし）"] = "(no modes available)",

        // ---- OSD ----
        ["音量 {0}%"] = "Volume {0}%",
        ["ミュート解除 ({0}%)"] = "Unmuted ({0}%)",
        ["音声がありません"] = "No audio",
        ["映像がありません"] = "No video",
        ["映像なし"] = "No video",
        ["接続しています..."] = "Connecting...",
        ["キャプチャデバイスを接続すると自動的に表示されます"] =
            "Connect a capture device and it appears automatically",
        ["設定ファイルを読み込めませんでした。既定値で起動しています。"] =
            "The settings file could not be read; started with defaults.",
        ["ズーム {0}%"] = "Zoom {0}%",
        ["一時停止"] = "Paused",
        ["再開"] = "Resumed",
        ["保存に失敗しました"] = "Save failed",
        ["保存に失敗しました。\n\n{0}"] = "Failed to save the snapshot.\n\n{0}",
        ["保存先フォルダを使えないため、既定のフォルダに保存しました。"] =
            "The chosen folder is unusable; saved to the default folder instead.",
        ["クリップボードにコピーしました"] = "Copied to clipboard",
        ["コピーに失敗しました"] = "Copy failed",
        ["連写 {0}/{1}"] = "Burst {0}/{1}",
        ["連写を停止しました"] = "Burst stopped",
        ["連写を中止しました（保存に失敗）"] = "Burst stopped (a save failed)",
        ["連写を開始します ({0}枚 / {1}秒間隔)"] = "Burst started ({0} shots / every {1}s)",
        ["解像度が未確定です"] = "Resolution not ready",
        ["画面に収まるサイズに調整しました"] = "Adjusted to fit the screen",
        ["映像デバイスが切断されました"] = "Video device disconnected",
        ["映像を再接続しました: {0}"] = "Video reconnected: {0}",
        ["音声を再接続しました: {0}"] = "Audio reconnected: {0}",
        ["回転 {0}°"] = "Rotated {0}°",
        ["この環境では回転に対応していません"] = "Rotation not supported here",
        ["この環境では反転に対応していません"] = "Flip not supported here",
        ["左右反転: オン"] = "Flip: on",
        ["左右反転: オフ"] = "Flip: off",
        ["常に前面: オン"] = "Always on top: on",
        ["常に前面: オフ"] = "Always on top: off",
        ["音声バッファ {0}ms"] = "Audio buffer {0}ms",
        ["スナップショット: {0}"] = "Snapshots: {0}",
        ["映像: {0}"] = "Video: {0}",
        ["音声: {0}"] = "Audio: {0}",
        ["映像モード: 自動"] = "Video mode: auto",
        ["PiP: オン"] = "PiP: on",
        ["PiP: オフ"] = "PiP: off",
        ["クリックスルー: オフ"] = "Click-through: off",

        // ---- Status bar ----
        ["映像"] = "Video",
        ["音声"] = "Audio",
        ["音量"] = "Volume",
        ["なし"] = "none",
        ["遅延"] = "delay",

        // ---- Dialogs ----
        ["音声バッファ設定"] = "Audio buffer settings",
        ["バッファ長 (ms):"] = "Buffer (ms):",
        ["プリセット:"] = "Presets:",
        ["低遅延 60"] = "Low 60",
        ["標準 120"] = "Normal 120",
        ["安定 250"] = "Stable 250",
        ["この値が実際の音声遅延の目安になります。\n小さいほど低遅延ですが、音切れが出たら上げてください。\n実測値はステータスバーの「遅延」に表示されます。"] =
            "This value is the audio delay the app now holds.\nLower is snappier; raise it if you hear dropouts.\nThe measured value is shown in the status bar.",
        ["キャンセル"] = "Cancel",
        ["スナップショット設定"] = "Snapshot settings",
        ["保存先:"] = "Folder:",
        ["参照..."] = "Browse...",
        ["形式:"] = "Format:",
        ["PNG (無劣化)"] = "PNG (lossless)",
        ["JPEG (高画質)"] = "JPEG (high quality)",
        ["既定に戻す"] = "Reset to default",
        ["スナップショットの保存先フォルダ"] = "Snapshot destination folder",
        ["連写スナップショット"] = "Burst snapshots",
        ["間隔 (秒):"] = "Interval (s):",
        ["枚数:"] = "Count:",
        ["開始"] = "Start",
        ["優先デバイス設定"] = "Preferred device",
        ["キーワード:"] = "Keyword:",
        ["デバイス名にこの語を含む機器を起動時に自動選択します。\n（既定: JVA14）"] =
            "Devices whose name contains this word are\nauto-selected at startup. (default: JVA14)",
        ["バージョン情報"] = "About",
        ["バージョン {0}"] = "Version {0}",
        ["使用ライブラリ:"] = "Libraries:",

        // ---- Errors shown to the user ----
        // Raw HRESULT text ("Exception from HRESULT: 0x80070005") tells nobody
        // what to do; these are the actionable translations.
        ["アクセスが拒否されました。他のアプリがデバイスを使用中か、プライバシー設定で許可されていない可能性があります。"] =
            "Access was denied. Another application may be using the device, or Windows privacy settings may be blocking it.",
        ["デバイスが見つかりません。接続を確認してください。"] =
            "The device was not found. Check the connection.",
        ["デバイスが応答しません。USB を挿し直してください。"] =
            "The device is not responding. Reconnect the USB cable.",
        ["デバイスが使用中です。他のアプリを閉じてから再試行してください。"] =
            "The device is busy. Close other applications and try again.",
        ["映像デバイスのエラーが発生しました。"] = "A video device error occurred.",
        ["エラーが発生しました。"] = "An error occurred.",
        ["予期しないエラーが発生しました。\n\n{0}\n\n詳細は error.log に記録しました。"] =
            "An unexpected error occurred.\n\n{0}\n\nDetails were written to error.log.",
        ["映像デバイスが見つかりません。"] = "Video device not found.",
        ["表示ウィンドウが設定されていません。"] = "No render window has been set.",
        ["キャプチャエンジンの初期化がタイムアウトしました。"] = "Capture engine initialization timed out.",
        ["プレビュー開始がタイムアウトしました。"] = "Starting the preview timed out.",
        ["ダウンロードしたファイルの検証に失敗しました。"] = "The downloaded file failed verification.",
        ["実行ファイルの場所を特定できません。"] = "Could not determine the executable's location.",
        ["映像デバイスの切り替えに失敗しました。\n\n{0}"] = "Failed to switch video device.\n\n{0}",
        ["音声デバイスの切り替えに失敗しました。\n\n{0}"] = "Failed to switch audio device.\n\n{0}",
        ["映像モードの変更に失敗しました。\n\n{0}"] = "Failed to change video mode.\n\n{0}",
        ["音声の再初期化に失敗しました。\n\n{0}"] = "Failed to reinitialize audio.\n\n{0}",

        // ---- Command line ----
        ["YuCap コマンドラインオプション:\n\n" +
         "  --fullscreen        全画面で起動\n" +
         "  --borderless        ウィンドウ枠なしで起動\n" +
         "  --topmost           常に前面で起動\n" +
         "  --muted             ミュートで起動\n" +
         "  --volume <0-500>    音量を指定\n" +
         "  --mode <指定>       映像モード指定\n" +
         "                      例: 1080p120 / 1440p60 / 1920x1080@120\n" +
         "  --list-formats [出力先]   対応フォーマットを書き出して終了\n" +
         "  --selftest [出力先]       自己診断を実行して終了\n" +
         "  --check-update      更新の有無を確認して終了"] =
            "YuCap command line options:\n\n" +
            "  --fullscreen        start in fullscreen\n" +
            "  --borderless        start without a window frame\n" +
            "  --topmost           start always on top\n" +
            "  --muted             start muted\n" +
            "  --volume <0-500>    set the volume\n" +
            "  --mode <spec>       select the video mode\n" +
            "                      e.g. 1080p120 / 1440p60 / 1920x1080@120\n" +
            "  --list-formats [path]   write the supported formats and exit\n" +
            "  --selftest [path]       run the self-test and exit\n" +
            "  --check-update      check for an update and exit",
        ["現在: {0}\n更新はありません。\n\nエンドポイント:\n{1}"] =
            "Current: {0}\nNo updates available.\n\nEndpoint:\n{1}",
        ["現在: {0}\n更新の確認に失敗しました。\n\n{1}\n\nエンドポイント:\n{2}"] =
            "Current: {0}\nThe update check failed.\n\n{1}\n\nEndpoint:\n{2}",
        ["現在: {0}\n最新: {1} ({2})\n\n{3}  {4} bytes\nSHA256: {5}\n{6}"] =
            "Current: {0}\nLatest: {1} ({2})\n\n{3}  {4} bytes\nSHA256: {5}\n{6}",
    };
}
