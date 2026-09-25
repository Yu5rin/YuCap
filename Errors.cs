using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace YuCap;

/// <summary>
/// Thrown by our own code when we already have a readable, localized message
/// for the user (e.g. Updater's own failures). Describe() passes this type's
/// Message through verbatim, unlike a framework exception whose Message is
/// developer-facing English text or a bare HRESULT string.
/// </summary>
internal sealed class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
}

/// <summary>
/// Turns exceptions — mostly COM/Media Foundation HRESULTs bubbling up from the
/// capture pipeline, but also file-system and network failures from snapshot
/// saving and update checks — into something a user can act on. Left alone,
/// these surface as bare text like "Exception from HRESULT: 0x80070005", which
/// says nothing about what happened or what to do about it. The raw
/// HRESULT/type belongs in error.log for us, not in a dialog for the user.
/// </summary>
internal static class Errors
{
    private const int ErrorAccessDenied = unchecked((int)0x80070005);
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const int ErrorGenFailure = unchecked((int)0x8007001F);
    private const int ErrorBusy = unchecked((int)0x800700AA);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int AudclntDeviceInUse = unchecked((int)0x8889000A);
    private const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntUnsupportedFormat = unchecked((int)0x88890008);

    /// <summary>User-facing description of a failure.</summary>
    public static string Describe(Exception ex)
    {
        // Thrown by our own code with an already-readable, localized message.
        if (ex is UserFacingException) return ex.Message;

        // Unwrap wrapper exceptions so the switch below sees the real failure —
        // otherwise an AggregateException or TargetInvocationException always
        // falls through to the generic "unknown error" branch.
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg)
            return Describe(agg.InnerException!);
        if (ex is TargetInvocationException { InnerException: { } inner })
            return Describe(inner);

        // File-system errors first, before any HRESULT mapping below — a
        // snapshot save failing with access denied must not be told "another
        // app is using the device" just because 0x80070005 is also
        // ERROR_ACCESS_DENIED for a capture device.
        switch (ex)
        {
            case UnauthorizedAccessException:
                return L.T("保存先に書き込む権限がありません。別のフォルダを選んでください。");
            case DirectoryNotFoundException:
                return L.T("保存先のフォルダが見つかりません。");
            case IOException io when io.HResult == ErrorDiskFull || io.HResult == ErrorHandleDiskFull:
                return L.T("ディスクの空き容量が足りません。");
            case PathTooLongException:
                return L.T("保存先のパスが長すぎます。");
        }

        // Network.
        switch (ex)
        {
            case HttpRequestException:
                return L.T("サーバーに接続できませんでした。ネットワーク接続を確認してください。");
            case TaskCanceledException:
            case TimeoutException:
                return L.T("応答がありませんでした（タイムアウト）。");
        }

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
            case AudclntDeviceInUse:
                return L.T("音声デバイスが他のアプリに占有されています。そのアプリを閉じてから再試行してください。");
            case AudclntDeviceInvalidated:
                return L.T("音声デバイスが取り外されたか、無効になりました。");
            case AudclntUnsupportedFormat:
                return L.T("音声デバイスの形式に対応していません。");
        }

        // Media Foundation errors all live under facility 0xC00D — catch the
        // family even when the specific code isn't one we special-cased above.
        // Pass the HResult through so the code survives into a bug report.
        if (((uint)ex.HResult >> 16) == 0xC00D)
            return L.F("映像デバイスのエラーが発生しました。（コード 0x{0:X8}）", ex.HResult);

        return L.F("エラーが発生しました。（コード 0x{0:X8}）", ex.HResult);
    }

    /// <summary>Short technical tail for logs / a secondary dialog line.</summary>
    public static string Detail(Exception ex) => $"{ex.GetType().Name} 0x{ex.HResult:X8}";
}
