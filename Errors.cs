using System;

namespace YuCap;

/// <summary>
/// Turns exceptions — mostly COM/Media Foundation HRESULTs bubbling up from the
/// capture pipeline — into something a user can act on. Left alone, these
/// surface as bare text like "Exception from HRESULT: 0x80070005", which says
/// nothing about what happened or what to do about it. The raw HRESULT/type
/// belongs in error.log for us, not in a dialog for the user.
/// </summary>
internal static class Errors
{
    private const int ErrorAccessDenied = unchecked((int)0x80070005);
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const int ErrorGenFailure = unchecked((int)0x8007001F);
    private const int ErrorBusy = unchecked((int)0x800700AA);

    /// <summary>User-facing description of a failure.</summary>
    public static string Describe(Exception ex)
    {
        // Thrown by our own code with an already-readable message — pass through.
        if (ex is TimeoutException or InvalidOperationException) return ex.Message;

        switch (ex.HResult)
        {
            case ErrorAccessDenied:
                return L.T("アクセスが拒否されました。他のアプリがデバイスを使用中か、プライバシー設定で許可されていない可能性があります。");
            case ErrorNotFound:
                return L.T("デバイスが見つかりません。接続を確認してください。");
            case ErrorGenFailure:
                return L.T("デバイスが応答しません。USB を挿し直してください。");
            case ErrorBusy:
                return L.T("デバイスが使用中です。他のアプリを閉じてから再試行してください。");
        }

        // Media Foundation errors all live under facility 0xC00D — catch the
        // family even when the specific code isn't one we special-cased above.
        if (((uint)ex.HResult >> 16) == 0xC00D)
            return L.T("映像デバイスのエラーが発生しました。");

        return L.T("エラーが発生しました。");
    }

    /// <summary>Short technical tail for logs / a secondary dialog line.</summary>
    public static string Detail(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}";
}
