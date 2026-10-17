using System.Xml.Linq;

namespace VehicleVision.PleasanterTools.CalendarFeed.CalDav;

/// <summary>PROPFIND の要求。</summary>
/// <param name="AllProp">全プロパティ（<c>allprop</c>、または本文が空）。</param>
/// <param name="PropNameOnly">プロパティ名だけ（<c>propname</c>）。</param>
/// <param name="Props">求められたプロパティ（<c>prop</c>）。</param>
public sealed record PropfindRequest(bool AllProp, bool PropNameOnly, IReadOnlyList<XName> Props)
{
    /// <summary>本文を読む。空の本文は <c>allprop</c>（RFC 4918 9.1）。</summary>
    public static PropfindRequest Parse(string body, int maxBytes = DavXml.MaxBodyBytes)
    {
        var doc = DavXml.Parse(body, maxBytes);
        if (doc is null)
        {
            return new PropfindRequest(true, false, []);
        }

        var root = doc.Root;
        if (root is null || root.Name != DavXml.D + "propfind")
        {
            throw new DavXmlException("The request body is not a DAV:propfind element.");
        }

        if (root.Element(DavXml.D + "propname") is not null)
        {
            return new PropfindRequest(false, true, []);
        }

        var prop = root.Element(DavXml.D + "prop");
        if (prop is not null)
        {
            return new PropfindRequest(false, false, prop.Elements().Select(e => e.Name).Distinct().ToList());
        }

        // allprop（include があっても、本アプリは実装済みのプロパティをすべて返す）
        return new PropfindRequest(true, false, []);
    }
}

/// <summary>REPORT の種類。</summary>
public enum ReportKind
{
    /// <summary><c>CALDAV:calendar-query</c>（RFC 4791 7.8）。</summary>
    CalendarQuery,

    /// <summary><c>CALDAV:calendar-multiget</c>（RFC 4791 7.9）。</summary>
    CalendarMultiget,
}

/// <summary>REPORT の要求。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Props">求められたプロパティ。</param>
/// <param name="Hrefs"><c>calendar-multiget</c> の対象。</param>
/// <param name="Start"><c>calendar-query</c> の期間の始まり（UTC。無ければ <c>null</c>）。</param>
/// <param name="End"><c>calendar-query</c> の期間の終わり（UTC。無ければ <c>null</c>）。</param>
public sealed record ReportRequest(
    ReportKind Kind,
    IReadOnlyList<XName> Props,
    IReadOnlyList<string> Hrefs,
    DateTimeOffset? Start,
    DateTimeOffset? End)
{
    /// <summary>本文を読む。対応していない REPORT は <see cref="DavXmlException"/>（呼び出し側が 403 supported-report にする）。</summary>
    public static ReportRequest Parse(string body, int maxBytes = DavXml.MaxBodyBytes)
    {
        var doc = DavXml.Parse(body, maxBytes) ?? throw new DavXmlException("The REPORT request needs a body.");
        var root = doc.Root!;
        var props = root.Element(DavXml.D + "prop")?.Elements().Select(e => e.Name).Distinct().ToList() ?? [];

        if (root.Name == DavXml.C + "calendar-multiget")
        {
            var hrefs = root.Elements(DavXml.D + "href").Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
            return new ReportRequest(ReportKind.CalendarMultiget, props, hrefs, null, null);
        }

        if (root.Name == DavXml.C + "calendar-query")
        {
            var (start, end) = ReadTimeRange(root);
            return new ReportRequest(ReportKind.CalendarQuery, props, [], start, end);
        }

        throw new DavXmlException("Unsupported REPORT: " + root.Name.LocalName);
    }

    /// <summary>フィルタの中の最初の <c>time-range</c> を読む（<c>start</c>・<c>end</c> は UTC の iCalendar 日時）。</summary>
    private static (DateTimeOffset? Start, DateTimeOffset? End) ReadTimeRange(XElement root)
    {
        var range = root.Descendants(DavXml.C + "time-range").FirstOrDefault();
        if (range is null)
        {
            return (null, null);
        }

        return (ParseUtc(range.Attribute("start")?.Value), ParseUtc(range.Attribute("end")?.Value));
    }

    private static DateTimeOffset? ParseUtc(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            value,
            ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : throw new DavXmlException("Invalid time-range value: " + value);
    }
}
