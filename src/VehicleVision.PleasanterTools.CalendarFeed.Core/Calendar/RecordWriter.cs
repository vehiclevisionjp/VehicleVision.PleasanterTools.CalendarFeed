using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Calendar;

/// <summary>列の対応が、書き込みに使えない（403 にし、理由を返す）。</summary>
public sealed class NotWritableException(string reason) : Exception(reason);

/// <summary>
/// 書き込まれた予定を、<b>列の対応の逆</b>で Pleasanter の更新・作成 API の本文へ直す（Issue #85。設計方針 5 章）。
/// </summary>
/// <remarks>
/// <para>
/// <b>書けるのは、素直に書き戻せる列だけ。</b>
/// 件名: <c>ItemTitle</c>・<c>Title</c>（→ <c>Title</c>）、<c>Body</c>、<c>Description*</c>。
/// 説明・場所: <c>Body</c>・<c>Description*</c>・<c>Title</c>。
/// 開始・終了: <c>StartTime</c>・<c>CompletionTime</c>・<c>Date*</c>、または書式つきの文字の列（<c>Title</c>・<c>Body</c>・<c>Description*</c>）。
/// <b>件名のテンプレート・選択肢（<c>Class*</c>）・数値などは書けない</b>（組み立ての逆が一意に決まらない・選択肢の検証が要る）。
/// </para>
/// <para>
/// <b>日時は、API キーの利用者のタイムゾーンの壁時計へ直して書く</b>（読み取りの逆。Pleasanter は <c>Z</c> を飾りとして扱う。実機検証結果 2 章）。
/// <b>終日は日付の 0 時</b>で、終了は<b>その日を含む日</b>（RFC 5545 の DTEND は含まないので 1 日戻す）。
/// </para>
/// </remarks>
public static partial class RecordWriter
{
    [GeneratedRegex(@"^Date([A-Z]|[0-9]{3})\z")]
    private static partial Regex TypedDate();

    [GeneratedRegex(@"^Description([A-Z]|[0-9]{3})\z")]
    private static partial Regex TypedDescription();

    /// <summary>この列の対応で書き込めるか。書けなければ理由を返す（書けるなら <c>null</c>）。</summary>
    public static string? WritableProblem(FeedMapping mapping)
    {
        try
        {
            _ = ToBody(
                new ParsedEvent("probe", "x", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), false, "d", "l", false),
                mapping,
                TimeZoneInfo.Utc);
            return null;
        }
        catch (NotWritableException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>更新・作成 API の本文（レコードの項目）を作る。</summary>
    /// <exception cref="NotWritableException">列の対応が書き込みに使えない。</exception>
    public static JsonObject ToBody(ParsedEvent e, FeedMapping mapping, TimeZoneInfo apiUserZone)
    {
        var body = new JsonObject();

        if (mapping.SummaryTemplate is not null)
        {
            throw new NotWritableException("The summary is built from a template, so it cannot be written back.");
        }

        Set(body, mapping.SummaryColumn == FeedColumns.ItemTitle ? "Title" : mapping.SummaryColumn, e.Summary, "summary");
        if (mapping.DescriptionColumn is { } description)
        {
            Set(body, description, e.Description ?? string.Empty, "description");
        }

        if (mapping.LocationColumn is { } location)
        {
            Set(body, location, e.Location ?? string.Empty, "location");
        }

        var (startWall, endWall) = WallTimes(e, apiUserZone);
        SetDate(body, mapping.StartColumn, mapping.StartFormat, startWall, "start");
        if (mapping.EndColumn is { } endColumn && endWall is { } end)
        {
            SetDate(body, endColumn, mapping.EndFormat, end, "end");
        }

        return body;
    }

    /// <summary>開始・終了の壁時計（API キーの利用者のタイムゾーン）。終日は日付の 0 時で、終了はその日を含む日。</summary>
    private static (DateTime Start, DateTime? End) WallTimes(ParsedEvent e, TimeZoneInfo zone)
    {
        if (e.AllDay)
        {
            var start = e.Start.Date;
            var end = e.End is { } exclusive && exclusive.Date > start ? exclusive.Date.AddDays(-1) : start;
            return (start, end);
        }

        DateTime Wall(DateTime value) =>
            e.Floating ? value : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone);

        return (Wall(e.Start), e.End is { } endValue ? Wall(endValue) : null);
    }

    /// <summary>文字として書ける列（件名・説明・場所）へ値を入れる。</summary>
    private static void Set(JsonObject body, string column, string value, string role)
    {
        if (column is "Title" or "Body")
        {
            body[column] = value;
        }
        else if (TypedDescription().IsMatch(column))
        {
            Hash(body, "DescriptionHash")[column] = value;
        }
        else
        {
            throw new NotWritableException($"The {role} column {column} cannot be written (only Title, Body and Description columns can).");
        }
    }

    /// <summary>日時の列へ値を入れる。日付の列は ISO 形式、文字の列は書式で文字にする。</summary>
    private static void SetDate(JsonObject body, string column, string? format, DateTime wall, string role)
    {
        var iso = wall.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (column is "StartTime" or "CompletionTime")
        {
            body[column] = iso;
        }
        else if (TypedDate().IsMatch(column))
        {
            Hash(body, "DateHash")[column] = iso;
        }
        else if (FeedColumns.IsParsableText(column) && format is not null)
        {
            // 文字の列には、読み取りと同じ書式で書く（読めなければ読み取りで予定が消える）
            Set(body, column, wall.ToString(format, CultureInfo.InvariantCulture), role);
        }
        else
        {
            throw new NotWritableException($"The {role} column {column} cannot be written (use a date column or a text column with a format).");
        }
    }

    private static JsonObject Hash(JsonObject body, string name)
    {
        if (body[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        body[name] = created;
        return created;
    }
}
