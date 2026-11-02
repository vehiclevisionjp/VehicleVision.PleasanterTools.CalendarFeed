using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>条件付き GET の判定（RFC 9110 §13）。</summary>
public static class ConditionalGet
{
    /// <summary>
    /// 304 を返してよいか。<c>If-None-Match</c> があればそれだけで決める（<c>If-Modified-Since</c> は無視。§13.1.3）。
    /// </summary>
    /// <param name="requestHeaders">リクエストのヘッダ。</param>
    /// <param name="etag">現在の ETag（引用符つき）。</param>
    /// <param name="lastModified">現在の最終更新。<c>null</c> なら日時の条件は使わない。</param>
    public static bool IsNotModified(IHeaderDictionary requestHeaders, string etag, DateTimeOffset? lastModified)
    {
        var ifNoneMatch = requestHeaders.IfNoneMatch;
        if (ifNoneMatch.Count > 0)
        {
            return MatchesAny(ifNoneMatch, etag);
        }

        if (lastModified is { } modified
            && requestHeaders.IfModifiedSince.Count == 1
            && DateTimeOffset.TryParseExact(
                requestHeaders.IfModifiedSince[0], "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
        {
            // HTTP の日時は秒単位。最終更新が since 以前なら変わっていない
            return TruncateToSeconds(modified) <= since;
        }

        return false;
    }

    /// <summary><c>Last-Modified</c> 用に秒へ切り捨てる（HTTP の日時は秒単位）。</summary>
    public static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    /// <summary>弱い比較（<c>W/</c> を無視）で、リストのどれかが一致するか。<c>*</c> は常に一致。</summary>
    private static bool MatchesAny(StringValues headerValues, string etag)
    {
        var current = StripWeak(etag);
        foreach (var line in headerValues)
        {
            foreach (var part in (line ?? string.Empty).Split(','))
            {
                var candidate = part.Trim();
                if (candidate == "*" || StripWeak(candidate) == current)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string StripWeak(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
