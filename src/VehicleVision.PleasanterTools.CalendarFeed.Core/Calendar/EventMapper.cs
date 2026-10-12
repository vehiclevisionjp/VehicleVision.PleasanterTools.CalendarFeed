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
    public static CalendarEvent? Map(
        JsonNode record,
        FeedDefinition feed,
        TimeZoneInfo apiUserTimeZone,
        string? linkBaseUrl = null)
    {
        var mapping = feed.Mapping;

        var start = GetDateTime(record, mapping.StartColumn);
        if (start is null)
        {
            return null;
        }

        var recordId = GetRecordId(record);
        if (recordId is null)
        {
            return null;
        }

        // 組み上げ済みの ItemTitle が無い応答（想定外の形）では Title へ落とす
        var summary = GetString(record, mapping.SummaryColumn)
            ?? (mapping.SummaryColumn == FeedColumns.ItemTitle ? GetString(record, "Title") : null);
        var end = mapping.EndColumn is null ? null : GetDateTime(record, mapping.EndColumn);

        DateTimeOffset startValue;
        DateTimeOffset? endValue;
        if (mapping.AllDay)
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
            Uid = $"{feed.SiteId}-{recordId}@{feed.Id:N}.calendarfeed",
            Summary = string.IsNullOrWhiteSpace(summary) ? "(無題)" : summary,
            Start = startValue,
            End = endValue,
            AllDay = mapping.AllDay,
            Description = mapping.DescriptionColumn is null ? null : GetString(record, mapping.DescriptionColumn),
            Location = mapping.LocationColumn is null ? null : GetString(record, mapping.LocationColumn),
            Url = linkBaseUrl is null ? null : $"{linkBaseUrl.TrimEnd('/')}/items/{recordId}",
            LastModified = updated is { } u ? At(u, apiUserTimeZone) : null,
            Sequence = Math.Max(0, (GetInt(record, "Ver") ?? 1) - 1),
        };
    }

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
