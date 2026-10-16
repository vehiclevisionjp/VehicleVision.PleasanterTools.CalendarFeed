using System.Globalization;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.CalDav;

/// <summary>書き込まれた iCalendar を読んだ予定（Issue #85。設計方針 5 章）。</summary>
/// <param name="Uid">クライアントの UID。</param>
/// <param name="Summary">件名。</param>
/// <param name="Start">開始。<b>時間指定の予定は UTC の時刻</b>。終日の予定は日付の 0 時（<see cref="AllDay"/>）。</param>
/// <param name="End">終了。<b>終日の予定では RFC 5545 の DTEND のまま（その日を含まない）。</b>無ければ <c>null</c>。</param>
/// <param name="AllDay">終日か（<c>VALUE=DATE</c>）。</param>
/// <param name="Description">説明。</param>
/// <param name="Location">場所。</param>
/// <param name="Floating">開始が「浮動時間」（<c>Z</c> も <c>TZID</c> も無い）か。浮動時間は、利用者の Pleasanter のタイムゾーンの壁時計とみなす。</param>
public sealed record ParsedEvent(
    string Uid,
    string Summary,
    DateTime Start,
    DateTime? End,
    bool AllDay,
    string? Description,
    string? Location,
    bool Floating);

/// <summary>書き込まれた iCalendar を受け付けられない（403 にし、理由を返す）。</summary>
public sealed class ICalException(string reason, ICalProblem problem) : Exception(reason)
{
    /// <summary>問題の種類（応答の <c>CALDAV:</c> の事前条件名に使う）。</summary>
    public ICalProblem Problem { get; } = problem;
}

/// <summary>受け付けられない理由の種類。</summary>
public enum ICalProblem
{
    /// <summary>iCalendar として読めない（<c>valid-calendar-data</c>）。</summary>
    InvalidData,

    /// <summary>1 つの資源に VEVENT が 1 つではない・繰り返しがあるなど（<c>valid-calendar-object-resource</c>）。</summary>
    InvalidResource,

    /// <summary>VEVENT 以外の部品（<c>supported-calendar-component</c>）。</summary>
    UnsupportedComponent,
}

/// <summary>
/// 書き込まれた iCalendar（RFC 5545）から、<b>VEVENT を 1 つだけ</b>読む。
/// </summary>
/// <remarks>
/// <para>
/// <b>読むのは UID・SUMMARY・DTSTART・DTEND・DURATION・DESCRIPTION・LOCATION だけ。</b>アラーム（VALARM）・参加者などは無視する（設計方針 5 章。保存しない）。
/// <b>繰り返し（RRULE・RDATE・EXRULE・EXDATE・RECURRENCE-ID）は受け付けない</b>（データを壊さないため）。
/// </para>
/// <para>
/// <b>タイムゾーンは <c>TZID</c> の名前を OS のタイムゾーン（IANA・Windows の名前）で解決する。</b>解決できない名前は受け付けない
/// （VTIMEZONE の定義は読まない。設計方針 9 章）。
/// </para>
/// </remarks>
public static class ICalReader
{
    /// <summary>1 つの資源の上限（文字数）。</summary>
    public const int MaxLength = 256 * 1024;

    private static readonly string[] Recurrence = ["RRULE", "RDATE", "EXRULE", "EXDATE", "RECURRENCE-ID"];

    /// <summary>VEVENT 1 つを読む。</summary>
    public static ParsedEvent ParseEvent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            throw new ICalException("The calendar data is empty or too large.", ICalProblem.InvalidData);
        }

        var lines = Unfold(text);
        if (lines.Count < 2 || !lines[0].Is("BEGIN", "VCALENDAR") || !lines[^1].Is("END", "VCALENDAR"))
        {
            throw new ICalException("The data is not a VCALENDAR.", ICalProblem.InvalidData);
        }

        // 部品の入れ子を積んで読む。VEVENT の直下のプロパティだけを集める（VALARM の中などは読まない）
        var events = new List<List<Line>>();
        var stack = new Stack<string>();
        List<Line>? properties = null;
        foreach (var line in lines.Skip(1).Take(lines.Count - 2))
        {
            if (line.Name == "BEGIN")
            {
                var name = line.Value.Trim().ToUpperInvariant();
                if (stack.Count == 0)
                {
                    if (name == "VEVENT")
                    {
                        properties = [];
                        events.Add(properties);
                    }
                    else if (name != "VTIMEZONE")
                    {
                        throw new ICalException(
                            $"The component {line.Value} is not supported (only VEVENT).", ICalProblem.UnsupportedComponent);
                    }
                }

                stack.Push(name);
            }
            else if (line.Name == "END")
            {
                if (stack.Count == 0 || stack.Pop() != line.Value.Trim().ToUpperInvariant())
                {
                    throw new ICalException("The components are not nested correctly.", ICalProblem.InvalidData);
                }
            }
            else if (stack.Count == 1 && stack.Peek() == "VEVENT")
            {
                properties!.Add(line);
            }
        }

        if (stack.Count != 0)
        {
            throw new ICalException("A component is not closed.", ICalProblem.InvalidData);
        }

        return events.Count == 1
            ? FromEvent(events[0])
            : throw new ICalException("The resource must contain exactly one VEVENT.", ICalProblem.InvalidResource);
    }

    private static ParsedEvent FromEvent(List<Line> properties)
    {
        if (properties.FirstOrDefault(p => Recurrence.Contains(p.Name)) is { } recurring)
        {
            throw new ICalException(
                $"Recurring events are not supported ({recurring.Name}).", ICalProblem.InvalidResource);
        }

        var uid = properties.FirstOrDefault(p => p.Name == "UID")?.Text();
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 255 || uid.Any(char.IsControl))
        {
            throw new ICalException("The event needs a UID (up to 255 characters).", ICalProblem.InvalidResource);
        }

        var startProperty = properties.FirstOrDefault(p => p.Name == "DTSTART")
            ?? throw new ICalException("The event needs a DTSTART.", ICalProblem.InvalidResource);
        var (start, allDay, floating) = ReadTime(startProperty);

        DateTime? end = null;
        if (properties.FirstOrDefault(p => p.Name == "DTEND") is { } endProperty)
        {
            var (value, endAllDay, _) = ReadTime(endProperty);
            if (endAllDay != allDay)
            {
                throw new ICalException("DTSTART and DTEND must have the same value type.", ICalProblem.InvalidResource);
            }

            end = value;
        }
        else if (properties.FirstOrDefault(p => p.Name == "DURATION") is { } duration)
        {
            end = start + ReadDuration(duration.Value);
        }

        return new ParsedEvent(
            uid.Trim(),
            properties.FirstOrDefault(p => p.Name == "SUMMARY")?.Text() ?? string.Empty,
            start,
            end,
            allDay,
            properties.FirstOrDefault(p => p.Name == "DESCRIPTION")?.Text(),
            properties.FirstOrDefault(p => p.Name == "LOCATION")?.Text(),
            floating);
    }

    /// <summary>日時を読む。時間指定は UTC の時刻、終日は日付の 0 時、浮動時間は書かれた壁時計のまま。</summary>
    private static (DateTime Value, bool AllDay, bool Floating) ReadTime(Line line)
    {
        var value = line.Value.Trim();
        if (line.Param("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true || value.Length == 8)
        {
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? (DateTime.SpecifyKind(date, DateTimeKind.Unspecified), true, false)
                : throw new ICalException($"Invalid date value: {value}", ICalProblem.InvalidData);
        }

        var utc = value.EndsWith('Z');
        var text = utc ? value[..^1] : value;
        if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            throw new ICalException($"Invalid date-time value: {value}", ICalProblem.InvalidData);
        }

        if (utc)
        {
            return (DateTime.SpecifyKind(local, DateTimeKind.Utc), false, false);
        }

        if (line.Param("TZID") is { } tzid)
        {
            var zone = FindZone(tzid)
                ?? throw new ICalException($"Unknown time zone: {tzid}", ICalProblem.InvalidData);
            return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), false, false);
        }

        // 浮動時間: 利用者の Pleasanter のタイムゾーンの壁時計として扱う（呼び出し側で変換しない）
        return (DateTime.SpecifyKind(local, DateTimeKind.Unspecified), false, true);
    }

    private static TimeZoneInfo? FindZone(string tzid)
    {
        tzid = tzid.Trim('"');
        if (TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone))
        {
            return zone;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(tzid, out var iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out zone)
            ? zone
            : null;
    }

    /// <summary>期間（<c>P1D</c> <c>PT1H30M</c> <c>P1W</c>）を読む。</summary>
    private static TimeSpan ReadDuration(string value)
    {
        value = value.Trim();
        var negative = value.StartsWith('-');
        value = value.TrimStart('+', '-');
        if (!value.StartsWith('P'))
        {
            throw new ICalException($"Invalid duration: {value}", ICalProblem.InvalidData);
        }

        var total = TimeSpan.Zero;
        var number = new StringBuilder();
        var inTime = false;
        foreach (var c in value[1..])
        {
            if (c == 'T')
            {
                inTime = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                number.Append(c);
            }
            else
            {
                if (number.Length == 0 || !long.TryParse(number.ToString(), CultureInfo.InvariantCulture, out var n) || n > 100_000)
                {
                    throw new ICalException($"Invalid duration: {value}", ICalProblem.InvalidData);
                }

                number.Clear();
                total += c switch
                {
                    'W' when !inTime => TimeSpan.FromDays(7 * n),
                    'D' when !inTime => TimeSpan.FromDays(n),
                    'H' when inTime => TimeSpan.FromHours(n),
                    'M' when inTime => TimeSpan.FromMinutes(n),
                    'S' when inTime => TimeSpan.FromSeconds(n),
                    _ => throw new ICalException($"Invalid duration: {value}", ICalProblem.InvalidData),
                };
            }
        }

        return negative ? -total : total;
    }

    /// <summary>折り返し（RFC 5545 3.1。次の行が空白で始まる）を戻して、行ごとに分ける。</summary>
    private static List<Line> Unfold(string text)
    {
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var unfolded = new List<string>();
        foreach (var line in raw)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && unfolded.Count > 0)
            {
                unfolded[^1] += line[1..];
            }
            else if (line.Length > 0)
            {
                unfolded.Add(line);
            }
        }

        return unfolded.Select(Line.Parse).ToList();
    }

    /// <summary>内容行 1 つ（名前・パラメータ・値）。</summary>
    private sealed record Line(string Name, IReadOnlyDictionary<string, string> Params, string Value)
    {
        public static Line Parse(string raw)
        {
            // 名前;PARAM=値;PARAM="値:コロンを含む":値。引用符の外の最初の ':' が値の始まり
            var colon = -1;
            var quoted = false;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (raw[i] == ':' && !quoted)
                {
                    colon = i;
                    break;
                }
            }

            if (colon <= 0)
            {
                throw new ICalException("A content line has no name or value.", ICalProblem.InvalidData);
            }

            var head = raw[..colon].Split(';');
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in head.Skip(1))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    parameters[part[..eq]] = part[(eq + 1)..].Trim('"');
                }
            }

            return new Line(head[0].ToUpperInvariant(), parameters, raw[(colon + 1)..]);
        }

        public bool Is(string name, string value) =>
            Name == name && Value.Equals(value, StringComparison.OrdinalIgnoreCase);

        public string? Param(string name) => Params.GetValueOrDefault(name);

        /// <summary>TEXT 値のエスケープ（<c>\n</c> <c>\,</c> <c>\;</c> <c>\\</c>）を戻す。</summary>
        public string Text()
        {
            var sb = new StringBuilder(Value.Length);
            for (var i = 0; i < Value.Length; i++)
            {
                if (Value[i] == '\\' && i + 1 < Value.Length)
                {
                    i++;
                    sb.Append(Value[i] is 'n' or 'N' ? '\n' : Value[i]);
                }
                else
                {
                    sb.Append(Value[i]);
                }
            }

            return sb.ToString();
        }
    }
}
