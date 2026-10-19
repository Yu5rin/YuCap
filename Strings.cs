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

    // Punctuation that differs between the languages (full-width brackets and middle dots read as
    // broken text in the English UI): use these instead of writing （ ） ・ ／ into a string that is not translated.
    /// <summary>Parenthesised remark: "（x）" in Japanese, " (x)" in English.</summary>
    public static string Par(string inner) => English ? " (" + inner + ")" : "（" + inner + "）";
    /// <summary>List separator inside a label: "・" / ", ".</summary>
    public static string Sep => English ? ", " : "・";
    /// <summary>Separator between alternatives (key hints): " ／ " / " / ".</summary>
    public static string Or => English ? " / " : " ／ ";

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
        ["表示の文字の大きさ"] = "Overlay text size",
        ["小（75%）"] = "Small (75%)",
        ["大（125%）"] = "Large (125%)",
        ["特大（150%）"] = "Extra large (150%)",
        ["表示の文字の大きさ: 自動"] = "Overlay text size: auto",
        ["表示の文字の大きさ: {0}%"] = "Overlay text size: {0}%",
        ["通知と情報表示の文字の大きさ"] = "Size of notice and info text",
        ["ホットキー設定"] = "Hotkey settings",
        ["スナップショット:"] = "Snapshot:",
        ["ミュート:"] = "Mute:",
        ["PiP切替:"] = "Toggle PiP:",
        ["同じキーが複数の操作に割り当てられています。"] =
            "The same key is assigned to more than one action.",
        ["{0} は Windows の基本操作と重なるため使えません。"] =
            "{0} can't be used because it conflicts with a basic Windows shortcut.",
        ["欄を選んでキーの組み合わせを押してください。［解除］または Backspace で割り当てを外せます。"] =
            "Select a field and press a key combination. Use [Clear] or Backspace to unassign.",
        ["解除"] = "Clear",
        ["閉じる"] = "Close",
        ["（グローバルホットキーは無効です）"] = "(Global hotkeys are disabled)",
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
        ["映像(&V)"] = "&Video",
        ["音声(&A)"] = "&Audio",
        ["表示(&W)"] = "Vie&w",
        ["アスペクト比 / 表示モード"] = "Aspect ratio / display mode",
        ["アスペクト比を保持"] = "Keep aspect ratio",
        ["ウィンドウに引き伸ばし"] = "Stretch to window",
        ["原寸表示"] = "Actual size (1:1)",
        ["整数倍表示 (くっきり)"] = "Integer scaling (sharp)",
        ["拡大の画質"] = "Upscaling quality",
        ["標準"] = "Standard",
        ["高画質（FSR）"] = "High quality (FSR)",
        ["くっきり（アニメ・イラスト向け）"] = "Crisp (for anime and illustrations)",
        ["ニューラルネット（Anime4K）で線や文字を細くくっきり描き直します。ゲーム機の絵やイラスト向けで、GPU を多く使います。"] = "Redraws lines and text thin and crisp with a neural network (Anime4K). Made for console graphics and illustrations; uses a lot of GPU.",
        ["くっきり"] = "Crisp",
        ["くっきり（Anime4K）"] = "Crisp (Anime4K)",
        ["シャープさ"] = "Sharpness",
        ["Direct3D 描画のときに使えます（映像 → 描画方式）"] = "Available with Direct3D rendering (Video > Renderer)",
        ["整数倍表示では使いません"] = "Not used with integer scaling",
        ["描画方式"] = "Renderer",
        ["標準（Media Foundation）"] = "Standard (Media Foundation)",
        ["Direct3D（高画質拡大・試験版）"] = "Direct3D (high-quality upscaling, experimental)",
        ["描画方式: {0}"] = "Renderer: {0}",
        ["拡大の画質: {0}"] = "Upscaling quality: {0}",
        ["シャープさ: {0}%"] = "Sharpness: {0}%",
        ["元の細かさに合わせて拡大"] = "Match original detail",
        ["元の細かさに合わせた拡大"] = "Match-original-detail upscaling",
        ["自動"] = "Auto",
        ["元の細かさに合わせて拡大: {0}"] = "Match original detail: {0}",
        ["元の細かさ"] = "Original detail",
        ["{0}p 相当（{0}p から拡大）"] = "{0}p equivalent (upscaled from {0}p)",
        ["判定中"] = "Analysing",
        ["{0}p のまま（縮めません）"] = "{0}p as is (not reduced)",
        ["{0}p 相当（今は使っていません）"] = "{0}p equivalent (not in use now)",
        ["映像の調整..."] = "Picture adjustments...",
        ["映像の調整"] = "Picture adjustments",
        ["明るさ"] = "Brightness",
        ["コントラスト"] = "Contrast",
        ["彩度"] = "Saturation",
        ["ガンマ"] = "Gamma",
        ["色調整"] = "Adjusted",
        ["音ズレ補正..."] = "Audio sync...",
        ["音ズレ補正"] = "Audio sync",
        ["音声を早める"] = "Audio earlier",
        ["音声を遅らせる"] = "Audio later",
        ["補正なし"] = "No offset",
        ["音声を {0}ms 遅らせる"] = "Delay audio by {0}ms",
        ["音声を {0}ms 早める"] = "Advance audio by {0}ms",
        ["映像の動きと音が合う位置にしてください。手をたたく動画などで合わせると分かりやすいです。"] =
            "Set it where the motion on screen and the sound line up. A video of hands clapping makes it easy to judge.",
        ["音声を早める補正は Direct3D 描画のときに使えます。標準の描画では 0 として扱います。"] =
            "Playing the audio earlier needs Direct3D rendering. With the standard renderer this is treated as 0.",
        ["Direct3D 描画を開始できなかったため、標準の描画に戻しました"] = "Direct3D rendering could not be started, so the standard renderer was restored",
        ["入力 {0:0}fps / 表示 {1:0}fps / 欠落 {2}"] = "In {0:0}fps / shown {1:0}fps / dropped {2}",
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
        ["操作一覧..."] = "Controls...",
        ["バージョン情報..."] = "About...",
        ["更新を確認..."] = "Check for updates...",

        // ---- Update ----
        ["保存しました: {0}（クリックで開く）"] = "Saved: {0} (click to open)",
        ["連写を停止 ({0}/{1})"] = "Stop burst ({0}/{1})",
        ["既に起動しています"] = "Already running",
        ["{0} をスキップします"] = "Skipping {0}",
        ["バージョン {0} に更新しました"] = "Updated to version {0}",
        ["新しいバージョン {0} がありますが、自動で更新できるファイルが見つかりません。リリースページから入手してください。"] =
            "Version {0} is available, but no file for automatic updating was found. Please get it from the release page.",
        ["このリリースには検証用のハッシュ（SHA256）がないため、アプリ内では更新できません。リリースページから手動で更新してください。"] =
            "This release has no verification hash (SHA256), so it cannot be installed from within the app. Please update manually from the release page.",
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
        ["自動で更新する"] = "Update automatically",
        ["自動更新: オン"] = "Automatic updates: on",
        ["自動更新: オフ"] = "Automatic updates: off",
        ["再起動して更新: {0}..."] = "Restart to update: {0}...",
        ["v{0} の準備ができました。YuCap を閉じると更新されます（クリックで今すぐ更新）"] =
            "v{0} is ready. YuCap updates when you close it (click to update now)",
        ["今すぐ YuCap を再起動して v{0} に更新しますか？\n\n「いいえ」を選んだ場合は、YuCap を閉じたときに更新されます。"] =
            "Restart YuCap now to update to v{0}?\n\nIf you choose No, the update is applied when you close YuCap.",
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
        ["更新があります"] = "Update available",
        ["更新があります: {0}..."] = "Update available: {0}...",
        ["新しいバージョン {0} があります（クリックで詳細）"] =
            "Version {0} is available (click for details)",


        // ---- Device lists ----
        ["（デバイスなし）"] = "(no devices)",
        ["自動（機器の既定）"] = "Auto (device default)",
        ["（利用可能なモードなし）"] = "(no modes available)",

        // ---- OSD ----
        ["音量 {0}%"] = "Volume {0}%",
        ["音量 {0}%（一時停止中）"] = "Volume {0}% (paused)",
        ["! = 音量が大きすぎるため、音割れを防ぐ処理が働いています"] =
            "! = the volume is so high that clipping protection is active",
        ["ミュート解除 ({0}%)"] = "Unmuted ({0}%)",
        ["音声がありません"] = "No audio",
        ["映像がありません"] = "No video",
        ["映像が入力されていません"] = "No video input",
        ["接続しています..."] = "Connecting...",
        ["接続しています: {0}"] = "Connecting: {0}",
        ["映像を準備しています…"] = "Preparing the video…",
        ["映像を準備中です"] = "The video is getting ready",
        ["優先デバイス: {0}"] = "Preferred device: {0}",
        ["優先デバイスを「{0}」に設定しました"] = "Preferred device set to \"{0}\"",
        ["キャプチャデバイスを接続すると自動的に表示されます"] =
            "Connect a capture device and it appears automatically",
        ["設定ファイルを読み込めませんでした。既定値で起動しています。"] =
            "The settings file could not be read; started with defaults.",
        ["ズーム {0}%"] = "Zoom {0}%",
        ["一時停止"] = "Paused",
        ["一時停止中"] = "Paused",
        ["再開"] = "Resumed",
        ["再開時にミュート: オン"] = "Mute on resume: on",
        ["再開時にミュート: オフ"] = "Mute on resume: off",
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
        ["音量表示をクリック"] = "Click the volume display",
        ["クリックでミュート切替 ／ 映像の上でホイールまたは ↑↓ で音量"] =
            "Click to toggle mute / wheel or ↑↓ over the video for volume",
        ["PiP"] = "PiP",

        // ---- Dialogs ----
        ["音声バッファ設定"] = "Audio buffer settings",
        ["安全のための余裕 (ms):"] = "Safety margin (ms):",
        ["プリセット:"] = "Presets:",
        ["低遅延 20"] = "Low 20",
        ["標準 40"] = "Normal 40",
        ["安定 100"] = "Stable 100",
        ["音を出す前に YuCap がためておく量です。\n小さいほど音の遅れが減りますが、小さすぎると音が途切れます。\n途切れるときは大きくしてください（既定は 40）。\n実際の遅れは、ステータスバーの「遅延」や情報表示で確かめられます。"] =
            "How much audio YuCap holds back before playing it.\nSmaller means less delay, but too small makes the sound break up.\nIf it breaks up, raise it (default 40).\nThe actual delay shows in the status bar and the info overlay.",
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
        ["ウィンドウの大きさに関係なく、入力された映像の解像度で保存します。"] =
            "Saves at the source video's resolution, regardless of the window size.",

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
        ["映像デバイスのエラーが発生しました。（コード 0x{0:X8}）"] =
            "A video device error occurred. (code 0x{0:X8})",
        ["エラーが発生しました。（コード 0x{0:X8}）"] = "An error occurred. (code 0x{0:X8})",
        ["サーバーに接続できませんでした。ネットワーク接続を確認してください。"] =
            "Could not connect to the server. Check your network connection.",
        ["応答がありませんでした（タイムアウト）。"] = "There was no response (timed out).",
        ["ディスクの空き容量が足りません。"] = "There is not enough free disk space.",
        ["保存先に書き込む権限がありません。別のフォルダを選んでください。"] =
            "You don't have permission to write to that folder. Choose a different one.",
        ["保存先のパスが長すぎます。"] = "The destination path is too long.",
        ["保存先のフォルダが見つかりません。"] = "The destination folder was not found.",
        ["最小化中は画面から取得できません。"] = "Can't capture the screen while minimized.",
        ["他のウィンドウに隠れているため、画面から取得できません。YuCap を前面に出してください。"] =
            "Can't capture the screen because another window is covering it. Bring YuCap to the front.",
        ["音声デバイスが他のアプリに占有されています。そのアプリを閉じてから再試行してください。"] =
            "The audio device is in use by another application. Close that application and try again.",
        ["音声デバイスが取り外されたか、無効になりました。"] = "The audio device was removed or disabled.",
        ["音声デバイスの形式に対応していません。"] = "The audio device's format is not supported.",
        ["予期しないエラーが発生しました。\n\n{0}\n\n詳細は error.log に記録しました。"] =
            "An unexpected error occurred.\n\n{0}\n\nDetails were written to error.log.",
        ["Direct3D 11 を初期化できません。"] = "Could not initialize Direct3D 11.",
        ["このウィンドウは別の描画方式で使われています。映像ウィンドウを作り直す必要があります。"] = "This window is in use by the other renderer. The video window has to be recreated.",
        ["映像の開始がタイムアウトしました。"] = "Starting the video timed out.",
        ["映像デバイスの形式を取得できません。"] = "Could not read the video device's format.",
        ["映像形式を NV12 に変換できません。"] = "Could not convert the video format to NV12.",
        ["映像デバイスが見つかりません。"] = "Video device not found.",
        ["表示ウィンドウが設定されていません。"] = "No render window has been set.",
        ["キャプチャエンジンの初期化がタイムアウトしました。"] = "Capture engine initialization timed out.",
        ["プレビュー開始がタイムアウトしました。"] = "Starting the preview timed out.",
        ["ダウンロードしたファイルの検証に失敗しました。"] = "The downloaded file failed verification.",
        ["実行ファイルの場所を特定できません。"] = "Could not determine the executable's location.",
        ["映像デバイスの切り替えに失敗しました。\n\n{0}"] = "Failed to switch video device.\n\n{0}",
        ["音声デバイスの切り替えに失敗しました。\n\n{0}"] = "Failed to switch audio device.\n\n{0}",
        ["音声の出力先の切り替えに失敗しました。\n\n{0}"] = "Failed to switch the audio output.\n\n{0}",
        ["音声の出力先"] = "Audio output",
        ["音声の出力先: {0}"] = "Audio output: {0}",
        ["Windows の既定"] = "Windows default",
        ["Windows の既定（{0}）"] = "Windows default ({0})",
        ["（Windows の既定）"] = "(Windows default)",
        ["（選んだ出力先は接続されていません）"] = "(the chosen output is not connected)",
        ["出力先: {0}"] = "Output: {0}",
        ["音量を上げる"] = "Volume up",
        ["音量を下げる"] = "Volume down",
        ["音量を上げる:"] = "Volume up:",
        ["音量を下げる:"] = "Volume down:",
        ["遅延（YuCap 内）"] = "Latency (inside YuCap)",
        ["映像 {0} ／ 音 {1}"] = "video {0} / audio {1}",
        ["約 {0:0} ms"] = "~{0:0} ms",
        ["いまの遅れ（YuCap の中だけ）: 映像 {0} ／ 音 {1}"] = "Current delay (inside YuCap only): video {0} / audio {1}",
        ["→ 測定中、または測定できません"] = "-> measuring, or not measurable",
        ["→ 映像と音は、ほぼ揃っています"] = "-> video and audio are almost in step",
        ["→ 音が {0:0} ms 遅れています"] = "-> the audio is {0:0} ms behind",
        ["→ 映像が {0:0} ms 遅れています"] = "-> the video is {0:0} ms behind",
        ["キャプチャーボードやテレビの中の遅れは含みません。目安として使ってください。"] = "Delays inside the capture card and the TV are not included. Use this as a guide.",
        ["目安に合わせる"] = "Match to the guide",
        ["遅延の確認"] = "Checking latency",
        ["映像モードの変更に失敗しました。\n\n{0}"] = "Failed to change video mode.\n\n{0}",
        ["音声の再初期化に失敗しました。\n\n{0}"] = "Failed to reinitialize audio.\n\n{0}",

        // ---- Fullscreen monitor / GPU load ----
        ["全画面にするモニター"] = "Fullscreen monitor",
        ["ウィンドウがあるモニター"] = "Monitor with the window",
        ["メイン"] = "Primary",
        ["指定のモニターが見つからないため、今のモニターで全画面にしました"] =
            "The chosen monitor was not found, so fullscreen uses the current one",
        ["{0:0.00}ms（最大 {1:0.00}ms）/ 1コマ {2:0.0}ms"] = "{0:0.00}ms (max {1:0.00}ms) / {2:0.0}ms per frame",
        ["（余裕なし）"] = "(no headroom)",

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
         "  --gpu-bench [出力先]      4K 拡大の GPU 負荷を計測して終了\n" +
         "  --hdr-probe [出力先]      HDR 入力の調査結果を書き出して終了\n" +
         "  --vblank-probe [出力先]   各モニターの垂直同期と表示レートを調べて終了\n" +
         "  --restart-stress [出力先] [回数] [--fullscreen] [--fast]   映像の再起動を繰り返し、毎回映像が戻るか調べて終了\n" +
         "  --interp-probe [出力先] [--mode <指定>] [--stills <フォルダ>]   フレーム補間を実機の映像で調べて終了\n" +
         "  --interp-synth [出力先] --in <画像> [--scale <倍率>] [--stills <フォルダ>]   既知の動きでフレーム補間を調べて終了\n" +
         "  --test-mode [--display <番号>] [--quick]   自動試験を実行してレポートを作り、終了\n" +
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
            "  --gpu-bench [path]      measure the GPU cost of 4K upscaling and exit\n" +
            "  --hdr-probe [path]      write an HDR input report and exit\n" +
            "  --vblank-probe [path]   measure each display's vertical blank and present rate and exit\n" +
            "  --restart-stress [path] [count] [--fullscreen] [--fast]   restart the video repeatedly, check it returns each time, and exit\n" +
            "  --interp-probe [path] [--mode <spec>] [--stills <dir>]   test frame interpolation on the live video and exit\n" +
            "  --interp-synth [path] --in <picture> [--scale <n>] [--stills <dir>]   test frame interpolation on a known motion and exit\n" +
            "  --test-mode [--display <n>] [--quick]   run the automatic test, write the report and exit\n" +
            "  --check-update      check for an update and exit",
        ["現在: {0}\n更新はありません。\n\nエンドポイント:\n{1}"] =
            "Current: {0}\nNo updates available.\n\nEndpoint:\n{1}",
        ["現在: {0}\n更新の確認に失敗しました。\n\n{1}\n\nエンドポイント:\n{2}"] =
            "Current: {0}\nThe update check failed.\n\n{1}\n\nEndpoint:\n{2}",
        ["現在: {0}\n最新: {1} ({2})\n\n{3}  {4} bytes\nSHA256: {5}\n{6}"] =
            "Current: {0}\nLatest: {1} ({2})\n\n{3}  {4} bytes\nSHA256: {5}\n{6}",

        // ---- Shortcuts dialog ----
        ["操作一覧"] = "Controls",
        ["操作"] = "Action",
        ["キー・マウス"] = "Key / mouse",
        ["YuCap の画面で"] = "In the YuCap window",
        ["どこからでも（グローバルホットキー）"] = "Anywhere (global hotkeys)",
        ["全画面表示の切替"] = "Toggle fullscreen",
        ["ダブルクリック"] = "Double-click",
        ["全画面・PiP を終了"] = "Exit fullscreen / PiP",
        ["中クリック"] = "Middle-click",
        ["ホイール"] = "Wheel",
        ["ズーム"] = "Zoom",
        ["ズーム中の表示位置"] = "Pan while zoomed",
        ["ドラッグ"] = "Drag",
        ["ウィンドウの移動"] = "Move window",
        ["映像をドラッグ"] = "Drag the video",
        ["メニューバーの表示"] = "Show menu bar",
        ["メニュー"] = "Menu",
        ["右クリック"] = "Right-click",
        ["PiP の切替"] = "Toggle PiP",
        ["情報表示の切替（オフ → FPS だけ → 詳しく）"] = "Info display (off → FPS only → detailed)",
        ["FSR・くっきりと標準の拡大を見比べる"] = "Compare FSR / Crisp with standard scaling",
        ["Q（押している間）"] = "Q (hold)",
        ["標準の拡大で表示中（Q を離すと元に戻ります）"] = "Showing standard scaling (release Q to go back)",
        ["見比べは Direct3D 描画で高画質（FSR）かくっきりのときに使えます"] = "Comparison works with Direct3D rendering and FSR or Crisp quality",
        ["入力"] = "Input",
        ["入力 {0:0} / 表示 {1:0} / 欠落 {2}"] = "in {0:0} / shown {1:0} / dropped {2}",
        ["（ゲーム {0:0} fps）"] = " (game {0:0} fps)",
        ["（静止）"] = " (still)",
        ["画面"] = "Display",
        ["拡大"] = "Scaling",
        ["整数倍"] = "Integer",
        ["等倍"] = "1:1",
        ["縮小"] = "Downscale",
        ["映像 {0:0}ms / 音声 {1:0}ms"] = "video {0:0}ms / audio {1:0}ms",
        ["オン"] = "On",
        ["オフ"] = "Off",

        // ---- Profiles / crop / low latency / save at display size ----
        ["プロファイル(&P)"] = "&Profiles",
        ["（まだありません。現在の設定を保存して作成します）"] = "(None yet. Save the current settings to create one)",
        ["現在の設定を新しいプロファイルとして保存..."] = "Save current settings as a new profile...",
        ["「{0}」を現在の設定で上書き"] = "Overwrite \"{0}\" with the current settings",
        ["名前を変更..."] = "Rename...",
        ["削除..."] = "Delete...",
        ["プロファイルは最大{0}個までです"] = "Up to {0} profiles can be saved",
        ["プロファイル {0} はありません"] = "There is no profile {0}",
        ["プロファイル: {0}"] = "Profile: {0}",
        ["{0}（変更あり）"] = "{0} (modified)",
        ["プロファイルを適用できなかったため、元の設定に戻しました"] = "Could not apply the profile; the previous settings were restored",
        ["プロファイルを保存しました: {0}"] = "Profile saved: {0}",
        ["プロファイルを上書きしました: {0}"] = "Profile overwritten: {0}",
        ["プロファイル名を変更しました: {0}"] = "Profile renamed: {0}",
        ["プロファイルを削除しました: {0}"] = "Profile deleted: {0}",
        ["プロファイル「{0}」を削除しますか？"] = "Delete the profile \"{0}\"?",
        ["新しいプロファイル"] = "New profile",
        ["名前を変更"] = "Rename",
        ["プロファイル名:"] = "Profile name:",
        ["名前を入力してください"] = "Enter a name",
        ["同じ名前のプロファイルがあります"] = "A profile with this name already exists",
        ["プロファイルを切り替える"] = "Switch profile",
        ["黒い縁の切り取り..."] = "Crop black borders...",
        ["黒い縁の切り取り"] = "Crop black borders",
        ["左"] = "Left",
        ["上"] = "Top",
        ["右"] = "Right",
        ["下"] = "Bottom",
        ["自動で検出"] = "Detect automatically",
        ["黒い縁は見つかりませんでした"] = "No black borders were found",
        ["切り取り後の大きさ: {0}x{1}"] = "Size after cropping: {0}x{1}",
        ["切り取り"] = "Crop",
        ["低遅延モード（試験版）"] = "Low-latency mode (experimental)",
        ["低遅延モード: オン"] = "Low-latency mode: on",
        ["低遅延モード: オフ"] = "Low-latency mode: off",
        ["低遅延モードを切り替えられなかったため、元に戻しました"] = "Could not switch low-latency mode; reverted",

        // ---- Frame interpolation ----
        ["フレーム補間"] = "Frame interpolation",
        ["画面の更新が映像のコマ数より十分速いときだけ、間のコマを作って滑らかにします。映像が 1 コマぶん遅れます。"] =
            "Only when the display refreshes clearly faster than the video, makes in-between frames to look smoother. Adds one frame of delay.",
        ["フレーム補間: オフ"] = "Frame interpolation: off",
        ["フレーム補間: オン"] = "Frame interpolation: on",
        ["フレーム補間: Direct3D 描画で使えます"] = "Frame interpolation: available with Direct3D rendering",
        ["フレーム補間: オン（映像が始まると働きます）"] = "Frame interpolation: on (starts once video is running)",
        ["フレーム補間: オン（{0:0}→{1:0} fps）"] = "Frame interpolation: on ({0:0}→{1:0} fps)",
        ["フレーム補間: オン（低遅延モード中は使えません）"] = "Frame interpolation: on (not available in low-latency mode)",
        ["フレーム補間: オン（画面 {0:0}Hz が映像 {1:0}fps より十分高くないため今は働きません）"] =
            "Frame interpolation: on (idle: the {0:0} Hz display is not clearly faster than the {1:0} fps video)",
        ["フレーム補間: この環境では使えません"] = "Frame interpolation: not available on this system",
        ["補間"] = "Interpolation",
        ["補間 {0}"] = "Interp. {0}",
        ["くっきり {0}"] = "Crisp {0}",
        ["くっきり度"] = "Crispness",
        ["{0}p 相当"] = "{0}p equiv.",
        ["フレーム補間: 映像 {0:0.#} fps を、画面に実際に出ている {1:0.#} fps にしています（画面は {2:0.#} Hz）"] =
            "Frame interpolation: turning {0:0.#} fps video into the {1:0.#} fps actually shown (display {2:0.#} Hz)",
        ["フレーム補間は今は働いていません: {0}"] = "Frame interpolation is not running right now: {0}",
        ["拡大: 元の絵の細かさ {0} から、画面に出す大きさ {1}x{2} へ（{3}）。キャプチャの大きさは {4}x{5} です。"] =
            "Scaling: from a picture detail of {0} to {1}x{2} on screen ({3}). The capture size is {4}x{5}.",
        ["くっきり度: 今の拡大処理が、ふつうの拡大（バイリニア）より絵の輪郭をどれだけくっきりさせているか。数秒ごとに、画面の一部分を両方の方法で描いて比べています。"] =
            "Crispness: how much crisper the current scaling makes the edges than plain bilinear scaling. Every few seconds a small part of the picture is drawn both ways and compared.",
        ["待機（低遅延モード）"] = "idle (low-latency mode)",
        ["待機（画面 {0:0}Hz）"] = "idle (display {0:0} Hz)",
        ["待機（映像待ち）"] = "idle (waiting for video)",
        ["使えません"] = "unavailable",
        ["低遅延"] = "Low latency",
        ["表示どおりの大きさで保存する（拡大の画質を反映。Direct3D 描画のとき）"] = "Save at the displayed size (includes the scaling quality; Direct3D rendering only)",
        ["表示方法"] = "Present",

        // ---- Test mode (automatic test) ----
        ["テストモード（自動試験）..."] = "Test mode (automatic test)...",
        ["テストモード（自動試験）"] = "Test mode (automatic test)",
        ["試験するモニター:"] = "Monitor to test:",
        ["所要時間の目安: 約 {0} 分"] = "Estimated time: about {0} min",
        ["試験中は画面が何度も切り替わります。Esc でいつでも中止できます。終了すると設定は元に戻ります。"] =
            "The screen switches many times during the test. Press Esc to stop at any time. Your settings are restored when it ends.",
        ["レポートは「ピクチャ」フォルダの YuCap\\テストレポート に保存されます。"] =
            "The report is saved in the Pictures folder under YuCap\\テストレポート.",
        ["テストモード {0}/{1}：{2}"] = "Test mode {0}/{1}: {2}",
        ["テストモード：準備中"] = "Test mode: preparing",
        ["Esc で中止"] = "Esc to stop",
        ["前回のテストモードが途中で終了したため、設定を元に戻しました"] =
            "The previous test mode ended unexpectedly, so your settings were restored",
        ["環境の確認"] = "Checking the environment",
        ["取り込み: {0}"] = "Capture: {0}",
        ["表示の計測: {0}"] = "Display measurement: {0}",
        ["通知の表示: {0}"] = "Overlay visibility: {0}",
        ["FSR の見え方: {0}"] = "FSR comparison: {0}",
        ["HDR の確認"] = "Checking HDR",
        ["機能の確認: {0}"] = "Feature check: {0}",
        ["音声の確認"] = "Checking audio",
        ["ダイアログの確認"] = "Checking dialogs",
        ["メニューの確認"] = "Checking menus",
        ["まとめ"] = "Summary",
        ["フレーム補間の画質（既知の動き）"] = "Interpolation quality (known motion)",
        ["フレーム補間の画質（実際の映像）"] = "Interpolation quality (live frames)",
        ["ゲームのコマ数に合わせた補間"] = "Interpolation matched to the game's frame rate",
        ["重ねた表示による更新の低下"] = "Refresh drop caused by overlays",
        ["映像の実際の細かさ"] = "Actual picture detail",
        ["低遅延オン"] = "low latency on",
        ["低遅延オフ"] = "low latency off",
        ["通知"] = "notice",
        ["一時停止の画像"] = "pause image",
        ["情報表示"] = "Info display",
        ["情報表示（FPS だけ）"] = "info display (FPS only)",
        ["情報表示（FPS だけ）の余白"] = "info display (FPS only) margins",
        ["FPS だけ"] = "FPS only",
        ["詳しく"] = "Detailed",
        ["情報表示: {0}"] = "Info display: {0}",
        ["ゲーム {0:0}"] = "Game {0:0}",
        ["入力 {0:0}"] = "Input {0:0}",
        ["補間待機 {0:0}Hz"] = "Interp. idle {0:0}Hz",
        ["補間待機 低遅延"] = "Interp. idle: low latency",
        ["補間 使えません"] = "Interp. unavailable",
        ["静止"] = "Still",
        ["プロファイル"] = "Profiles",
        ["ウィンドウ"] = "Window",
        ["色の調整"] = "color adjustment",
        ["映像の遅延"] = "video delay",
        ["表示どおりの保存"] = "save as displayed",
        ["回転と反転"] = "rotation and mirror",
        ["テスト表示"] = "Test overlay",

        // ---- Device choice and automatic reconnection ----
        ["キャプチャーボードが見つかりません"] = "No capture card found",
        ["キャプチャーボードが見つかりません。「デバイス」メニューから映像デバイスを選んでください"] = "No capture card found. Choose the video device from the Device menu.",
        ["キャプチャーボードを接続すると自動的に表示されます"] = "Connect the capture card and the picture appears automatically.",
        ["使う機器が決まっていません。「デバイス」メニューの「映像デバイス」から選んでください（接続すると自動的に表示されます）"] = "No device has been chosen yet. Pick one under Device > Video device (the picture appears automatically once the card is connected).",
        ["映像が止まったため、つなぎ直しています"] = "The video stopped - reconnecting",
        ["映像をつなぎ直しました"] = "Video reconnected",
        ["映像をつなぎ直せませんでした"] = "Could not reconnect the video",
        ["映像が止まり、つなぎ直せませんでした"] = "The video stopped and could not be reconnected",
        ["ケーブルとキャプチャーボードを確かめてください。抜き差しすると自動で再開します。「デバイス」メニューから選び直すこともできます"] = "Check the cable and the capture card. Unplugging and re-plugging restarts it automatically; you can also choose the device again from the Device menu.",
        ["音声が止まったため、つなぎ直しています"] = "The audio stopped - reconnecting",
        ["音声をつなぎ直せませんでした（抜き差しすると自動で再開します）"] = "Could not reconnect the audio (it restarts automatically when the device is re-plugged)",
        ["映像が停止中"] = "Video stopped",
        ["映像をつなぎ直し中"] = "Reconnecting video",
        ["音声が停止中"] = "Audio stopped",
        ["音声をつなぎ直し中"] = "Reconnecting audio",
        ["入力レベルを変更できませんでした"] = "Could not change the input level",
        ["入力レベルを元に戻せませんでした"] = "Could not restore the input level",
        ["更新ファイルの入れ替えは終わりましたが、新しい版を起動できませんでした。YuCap をいったん閉じて、もう一度起動してください。\n\n{0}"] = "The update was installed, but the new version could not be started. Close YuCap and start it again.\n\n{0}",
        ["{0}\n\nリリースページを開きますか？"] = "{0}\n\nOpen the release page?",
    };
}
