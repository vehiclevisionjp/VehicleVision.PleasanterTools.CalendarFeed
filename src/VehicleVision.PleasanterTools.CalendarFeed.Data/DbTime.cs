using System.Globalization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>DB に置く日時（4 種の RDBMS で通じる固定幅の UTC の文字列）。</summary>
/// <remarks>
/// <b>固定幅にする理由。</b> SQL の文字列比較（<c>ExpiresAt &gt; @Now</c>）が時刻の前後と一致するのは、
/// 桁数が揃った UTC のときだけ。RDBMS ごとの日時型の差（精度・時間帯）を持ち込まないためでもある。
/// </remarks>
public static class DbTime
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>文字列にする（常に UTC・100 ナノ秒まで）。</summary>
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>文字列から戻す。<c>null</c> と空は <c>null</c>。</summary>
    public static DateTimeOffset? Parse(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : new DateTimeOffset(DateTime.ParseExact(
                value, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
}
