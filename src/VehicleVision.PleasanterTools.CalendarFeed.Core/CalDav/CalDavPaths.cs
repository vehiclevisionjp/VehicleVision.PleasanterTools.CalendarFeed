using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.CalDav;

/// <summary>/caldav/ 以下の URL の形（Issue #85。設計方針 4 章）。</summary>
public static class CalDavPaths
{
    /// <summary>入口。<c>current-user-principal</c> を返す。</summary>
    public const string Root = "/caldav/";

    /// <summary>カレンダーの置き場（<c>calendar-home-set</c>）。子がフィードごとのカレンダー。</summary>
    public const string Home = "/caldav/calendars/";

    /// <summary>プリンシパル（利用者）。ユーザー名はクライアントが Basic 認証で送ったもの（検証はしない。権限は Pleasanter の API キーで決まる）。</summary>
    public static string Principal(string user) => $"/caldav/principals/{Uri.EscapeDataString(user)}/";

    /// <summary>フィード 1 つのカレンダー。</summary>
    public static string Calendar(Guid feedId) => $"{Home}{feedId:D}/";

    /// <summary>予定 1 件の資源。UID から決まる名前（<c>.ics</c>）。</summary>
    public static string Event(Guid feedId, string uid) => Calendar(feedId) + FileName(uid);

    /// <summary>予定の資源の名前。UID を URL 用に直し、<c>.ics</c> を付ける。</summary>
    public static string FileName(string uid) => Uri.EscapeDataString(uid) + ".ics";

    /// <summary>資源の名前から UID へ戻す。<c>.ics</c> で終わらない・壊れた名前は <c>null</c>。</summary>
    public static string? UidOf(string fileName)
    {
        if (!fileName.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(fileName[..^4]);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>URL の形を読む。未知の形は <see cref="CalDavKind.Unknown"/>。</summary>
    public static CalDavTarget Parse(string path)
    {
        // **末尾のスラッシュの有無と、二重スラッシュは同じに扱う。** `..` は受けない（Unknown）
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Decode).ToArray();
        if (segments.Length == 0 || segments[0] != "caldav" || segments.Any(s => s is null or ".." or "." or ""))
        {
            return CalDavTarget.Unknown;
        }

        return segments.Length switch
        {
            1 => new CalDavTarget(CalDavKind.Root),
            2 when segments[1] == "calendars" => new CalDavTarget(CalDavKind.Home),
            3 when segments[1] == "principals" => new CalDavTarget(CalDavKind.Principal, User: segments[2]),
            3 when segments[1] == "calendars" && Guid.TryParse(segments[2], out var feed) =>
                new CalDavTarget(CalDavKind.Calendar, FeedId: feed),
            4 when segments[1] == "calendars" && Guid.TryParse(segments[2], out var feed) && segments[3]!.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) =>
                new CalDavTarget(CalDavKind.Event, FeedId: feed, FileName: segments[3]),
            _ => CalDavTarget.Unknown,
        };
    }

    private static string? Decode(string segment)
    {
        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>予定 1 件の ETag（資源の本文の SHA-256。<b>内容が同じなら同じ</b>）。二重引用符付き（RFC 7232 2.3）。</summary>
    public static string ETagOf(string resourceBody) =>
        "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceBody)))[..32].ToLowerInvariant() + "\"";

    /// <summary>カレンダー全体の更新の印（<c>getctag</c>）。<b>どれかの予定が変われば変わる</b>。</summary>
    public static string CTagOf(IEnumerable<string> etags) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', etags.Order(StringComparer.Ordinal)))))[..32]
            .ToLowerInvariant();
}

/// <summary>URL が指すものの種類。</summary>
public enum CalDavKind
{
    Unknown,
    Root,
    Principal,
    Home,
    Calendar,
    Event,
}

/// <summary>URL を読んだ結果。</summary>
public sealed record CalDavTarget(CalDavKind Kind, string? User = null, Guid? FeedId = null, string? FileName = null)
{
    /// <summary>読めない形。</summary>
    public static CalDavTarget Unknown { get; } = new(CalDavKind.Unknown);
}
