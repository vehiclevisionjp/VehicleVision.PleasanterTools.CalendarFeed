using System.Globalization;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.Calendar;

/// <summary>予定を iCalendar（RFC 5545）の本文にする。</summary>
/// <remarks>
/// <para>
/// **時間指定の予定は UTC（<c>Z</c> 付き）で出す。** VTIMEZONE を持たずに済み、購読側のタイムゾーン
/// 解釈の差に巻き込まれない。
/// </para>
/// <para>
/// **値は必ずエスケープする。** Pleasanter の本文に改行が入ると、そのまま出せば別のプロパティを
/// 差し込めてしまう。
/// </para>
/// </remarks>
public static class ICalWriter
{
    private const string ProductId = "-//VehicleVision//PleasanterTools.CalendarFeed//JA";
    private const int MaxLineOctets = 75;

    /// <summary>カレンダー全体を書く。</summary>
    /// <param name="calendarName">カレンダー名（X-WR-CALNAME）。</param>
    /// <param name="events">予定。</param>
    /// <param name="now">DTSTAMP に使う現在時刻。</param>
    /// <param name="refreshInterval">購読側が見直す間隔の目安。</param>
    public static string Write(
        string calendarName,
        IEnumerable<CalendarEvent> events,
        DateTimeOffset now,
        TimeSpan? refreshInterval = null)
    {
        var interval = refreshInterval ?? TimeSpan.FromHours(1);
        var duration = $"PT{(int)interval.TotalMinutes}M";
        var sb = new StringBuilder();

        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, $"PRODID:{ProductId}");
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, $"X-WR-CALNAME:{Escape(calendarName)}");
        Line(sb, $"REFRESH-INTERVAL;VALUE=DURATION:{duration}");
        Line(sb, $"X-PUBLISHED-TTL:{duration}");

        foreach (var e in events)
        {
            WriteEvent(sb, e, now);
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    private static void WriteEvent(StringBuilder sb, CalendarEvent e, DateTimeOffset now)
    {
        Line(sb, "BEGIN:VEVENT");
        Line(sb, $"UID:{Escape(e.Uid)}");
        Line(sb, $"DTSTAMP:{Utc(now)}");

        if (e.AllDay)
        {
            Line(sb, $"DTSTART;VALUE=DATE:{Date(e.Start)}");
            if (e.End is { } end)
            {
                Line(sb, $"DTEND;VALUE=DATE:{Date(end)}");
            }
        }
        else
        {
            Line(sb, $"DTSTART:{Utc(e.Start)}");
            if (e.End is { } end)
            {
                Line(sb, $"DTEND:{Utc(end)}");
            }
        }

        Line(sb, $"SUMMARY:{Escape(e.Summary)}");
        if (!string.IsNullOrEmpty(e.Description))
        {
            Line(sb, $"DESCRIPTION:{Escape(e.Description)}");
        }

        if (!string.IsNullOrEmpty(e.Location))
        {
            Line(sb, $"LOCATION:{Escape(e.Location)}");
        }

        if (!string.IsNullOrEmpty(e.Url))
        {
            // URI 値。TEXT のエスケープは掛けないが、行を壊す制御文字は落とす
            Line(sb, $"URL:{StripControl(e.Url)}");
        }

        if (e.LastModified is { } modified)
        {
            Line(sb, $"LAST-MODIFIED:{Utc(modified)}");
        }

        Line(sb, $"SEQUENCE:{e.Sequence.ToString(CultureInfo.InvariantCulture)}");
        Line(sb, "END:VEVENT");
    }

    /// <summary>TEXT 値のエスケープ（RFC 5545 3.3.11）。</summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ';': sb.Append("\\;"); break;
                case ',': sb.Append("\\,"); break;
                case '\r':
                    sb.Append("\\n");
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\n': sb.Append("\\n"); break;
                default:
                    if (!char.IsControl(c) || c == '\t')
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static string StripControl(string value) =>
        string.Concat(value.Where(c => !char.IsControl(c)));

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset value) =>
        value.DateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>1 行を 75 オクテットで折り返して書く（RFC 5545 3.1）。UTF-8 の文字は割らない。</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > MaxLineOctets)
            {
                sb.Append("\r\n ");
                octets = 1; // 継続行の先頭の空白
            }

            sb.Append(rune.ToString());
            octets += size;
        }

        sb.Append("\r\n");
    }
}
