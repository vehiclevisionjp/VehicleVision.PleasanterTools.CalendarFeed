using System.Globalization;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Calendar;

/// <summary>Pleasanter のレコード（API 応答の JSON）を予定へ写す。</summary>
/// <remarks>
/// <para>
/// **日時は「API キーを持つ利用者のタイムゾーン」の壁時計として読む**
/// （Questionnaire の <c>実機検証結果.md</c> 4 章。Pleasanter 1.5.7.0 で確認された挙動。
/// 本リポジトリの対象版では未確認）。末尾の <c>Z</c> やオフセットは無視して、書かれている時刻をそのまま使う。
/// </para>
/// <para>
/// **未設定の日付は <c>1899-12-30</c> で返る**（同 5 章）。1900 年より前は未設定とみなす。
/// </para>
/// </remarks>
public static class EventMapper
{
    private static readonly string[] TypedHashes =
        ["Class", "Num", "Date", "Description", "Check"];

    /// <summary>レコード 1 件を予定にする。開始が読めないレコードは <c>null</c>（出さない）。</summary>
    /// <param name="record">Get の <c>Response.Data[]</c> の 1 要素。</param>
    /// <param name="feed">フィード定義。</param>
    /// <param name="apiUserTimeZone">API キーを持つ利用者のタイムゾーン。</param>
    /// <param name="linkBaseUrl">Pleasanter の公開 URL。指定すると各予定へレコードへのリンクを付ける。</param>
    /// <param name="resolved">
    /// Pleasanter に表示名へ解決させた文字（列名 → 表示名。選択肢・リンク・ユーザー・状況）。
    /// 無い列は生の値を使う（Issue #5）。
    /// </param>
    public static CalendarEvent? Map(
        JsonNode record,
        FeedDefinition feed,
        TimeZoneInfo apiUserTimeZone,
        string? linkBaseUrl = null,
        IReadOnlyDictionary<string, string>? resolved = null)
    {
        var mapping = feed.Mapping;

        var start = ReadDate(record, mapping.StartColumn, mapping.StartFormat);
        if (start is null)
        {
            return null;
        }

        var recordId = GetRecordId(record);
        if (recordId is null)
        {
            return null;
        }

        string? summary;
        if (mapping.SummaryTemplate is { } template)
        {
            // 列の値を書式・表示名を通して組み合わせる（Issue #5）
            summary = TextTemplate.Render(
                TextTemplate.Parse(template),
                (column, format) => ColumnText(record, column, format, mapping, resolved) ?? string.Empty);
        }
        else if (mapping.SummaryColumn == FeedColumns.ItemTitle)
        {
            // 組み上げ済みの ItemTitle が無い応答（想定外の形）では Title へ落とす
            summary = GetString(record, FeedColumns.ItemTitle) ?? GetString(record, "Title");
        }
        else
        {
            summary = ColumnText(record, mapping.SummaryColumn, null, mapping, resolved);
        }

        // 終了の文字が**空でないのに読めない**ときは予定を出さない（黙って終了を補うと、別の日付に見える）
        var end = mapping.EndColumn is null ? null : ReadDate(record, mapping.EndColumn, mapping.EndFormat);
        if (end is null && mapping.EndColumn is not null && mapping.EndFormat is not null
            && !string.IsNullOrWhiteSpace(GetString(record, mapping.EndColumn)))
        {
            return null;
        }


        DateTimeOffset startValue;
        DateTimeOffset? endValue;
        var allDay = mapping.AllDay switch
        {
            AllDayMode.Always => true,
            AllDayMode.Never => false,
            _ => start.Value.TimeOfDay == TimeSpan.Zero && (end is null || end.Value.TimeOfDay == TimeSpan.Zero),
        };
        if (allDay)
        {
            var startDate = start.Value.Date;
            // 終了の日を含めるため、DTEND は翌日にする。終了が無い・開始より前なら 1 日だけ
            var endDate = end is { } e && e.Date >= startDate ? e.Date : startDate;
            startValue = At(startDate, apiUserTimeZone);
            endValue = At(endDate.AddDays(1), apiUserTimeZone);
        }
        else
        {
            startValue = At(start.Value, apiUserTimeZone);
            endValue = end is { } e && At(e, apiUserTimeZone) > startValue
                ? At(e, apiUserTimeZone)
                : startValue.AddMinutes(mapping.DefaultDurationMinutes);
        }

        var updated = GetDateTime(record, "UpdatedTime");
        return new CalendarEvent
        {
            Uid = DerivedUid(feed, recordId.Value),
            RecordId = recordId,
            Summary = string.IsNullOrWhiteSpace(summary) ? FeedLanguages.Untitled(feed.Language) : summary,
            Start = startValue,
            End = endValue,
            AllDay = allDay,
            Description = mapping.DescriptionColumn is null
                ? null
                : ColumnText(record, mapping.DescriptionColumn, null, mapping, resolved),
            Location = mapping.LocationColumn is null
                ? null
                : ColumnText(record, mapping.LocationColumn, null, mapping, resolved),
            Url = linkBaseUrl is null ? null : $"{linkBaseUrl.TrimEnd('/')}/items/{recordId}",
            LastModified = updated is { } u ? At(u, apiUserTimeZone) : null,
            Sequence = Math.Max(0, (GetInt(record, "Ver") ?? 1) - 1),
        };
    }

    /// <summary>レコードから導く UID（<c>{サイト ID}-{レコード ID}@{フィード ID}.calendarfeed</c>）。レコードごとに変わらない。</summary>
    public static string DerivedUid(FeedDefinition feed, long recordId) => $"{feed.SiteId}-{recordId}@{feed.Id:N}.calendarfeed";

    /// <summary>
    /// 列の値を**表示用の文字**にする。
    /// <list type="bullet">
    /// <item>日付・数値の列は、生の値を **.NET の書式**（<paramref name="inlineFormat"/> ＞ <see cref="FeedMapping.Formats"/>）で整形する。
    /// 未設定（<c>1899-12-30</c>）の日付は空文字。インバリアントカルチャ</item>
    /// <item>選択肢・リンク・ユーザー・状況の列は、Pleasanter が解決した表示名（<paramref name="resolved"/>）。無ければ生の値</item>
    /// <item>それ以外は生の値</item>
    /// </list>
    /// </summary>
    internal static string? ColumnText(
        JsonNode record,
        string column,
        string? inlineFormat,
        FeedMapping mapping,
        IReadOnlyDictionary<string, string>? resolved)
    {
        if (FeedColumns.NeedsFormat(column))
        {
            var format = inlineFormat ?? mapping.Formats?.GetValueOrDefault(column);
            if (FeedColumns.IsDate(column))
            {
                return GetDateTime(record, column) is { } dt
                    ? dt.ToString(format ?? "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : string.Empty;
            }

            return GetDecimal(record, column) is { } number
                ? number.ToString(format, CultureInfo.InvariantCulture)
                : string.Empty;
        }

        if (resolved is not null && resolved.TryGetValue(column, out var text))
        {
            return text;
        }

        return GetString(record, column);
    }

    /// <summary>列の値を数値で読む。無い・読めなければ <c>null</c>。</summary>
    public static decimal? GetDecimal(JsonNode record, string column) =>
        decimal.TryParse(GetString(record, column), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    /// <summary>列の値を文字列で読む。無ければ <c>null</c>。</summary>
    public static string? GetString(JsonNode record, string column)
    {
        var node = FindColumn(record, column);
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString().Trim('"'),
        };
    }

    /// <summary>
    /// 開始・終了を読む。日付の列は ISO 形式の値、**文字の列は書式で読む**（<c>ParseExact</c>。インバリアントカルチャ・
    /// 前後の空白は無視）。無い・読めないなら <c>null</c>。
    /// </summary>
    internal static DateTime? ReadDate(JsonNode record, string column, string? format)
    {
        if (format is null || FeedColumns.IsDate(column))
        {
            return GetDateTime(record, column);
        }

        var text = GetString(record, column)?.Trim();
        return !string.IsNullOrEmpty(text)
            && DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : null;
    }

    /// <summary>列の値を日時で読む。無い・読めない・未設定（1900 年より前）なら <c>null</c>。</summary>
    public static DateTime? GetDateTime(JsonNode record, string column)
    {
        var text = GetString(record, column);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 書かれている時刻をそのまま使う（Z やオフセットで動かさない）
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return null;
        }

        var clock = parsed.DateTime;
        return clock.Year < 1900 ? null : DateTime.SpecifyKind(clock, DateTimeKind.Unspecified);
    }

    private static int? GetInt(JsonNode record, string column) =>
        int.TryParse(GetString(record, column), CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? GetRecordId(JsonNode record)
    {
        foreach (var key in new[] { "ResultId", "IssueId" })
        {
            if (long.TryParse(GetString(record, key), CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static JsonNode? FindColumn(JsonNode record, string column)
    {
        if (record[column] is { } direct)
        {
            return direct;
        }

        // ClassA → ClassHash.ClassA など
        foreach (var prefix in TypedHashes)
        {
            if (column.StartsWith(prefix, StringComparison.Ordinal)
                && record[$"{prefix}Hash"] is JsonObject hash
                && hash[column] is { } typed)
            {
                return typed;
            }
        }

        return null;
    }

    /// <summary>壁時計の時刻にタイムゾーンのオフセットを付ける。夏時間の欠落時刻でも例外にしない。</summary>
    private static DateTimeOffset At(DateTime clock, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(clock, DateTimeKind.Unspecified), zone.GetUtcOffset(clock));
}
