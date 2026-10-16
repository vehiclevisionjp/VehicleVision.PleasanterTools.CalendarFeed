using System.Xml.Linq;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>CalDAV の要求の読み取り・URL・応答の組み立て（Issue #85）。</summary>
public sealed class CalDavTests
{
    private const string PropfindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/">
          <d:prop><d:resourcetype/><d:displayname/><cs:getctag/></d:prop>
        </d:propfind>
        """;

    [Fact]
    public void PROPFINDの本文が空なら_全プロパティ()
    {
        var request = PropfindRequest.Parse("");

        Assert.True(request.AllProp);
    }

    [Fact]
    public void PROPFINDは_求められたプロパティを名前空間つきで読む()
    {
        var request = PropfindRequest.Parse(PropfindBody);

        Assert.False(request.AllProp);
        Assert.Equal(
            [DavXml.D + "resourcetype", DavXml.D + "displayname", DavXml.CS + "getctag"],
            request.Props);
    }

    [Fact]
    public void PROPFINDの_propname_と_allprop_を見分ける()
    {
        Assert.True(PropfindRequest.Parse("<d:propfind xmlns:d=\"DAV:\"><d:propname/></d:propfind>").PropNameOnly);
        Assert.True(PropfindRequest.Parse("<d:propfind xmlns:d=\"DAV:\"><d:allprop/></d:propfind>").AllProp);
    }

    [Fact]
    public void DTDを含む本文は拒む_外部実体を読まない()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>"
            + "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:displayname>&e;</d:displayname></d:prop></d:propfind>";

        Assert.Throws<DavXmlException>(() => PropfindRequest.Parse(xxe));
    }

    [Fact]
    public void 壊れた本文と_違う根の要素は拒む()
    {
        Assert.Throws<DavXmlException>(() => PropfindRequest.Parse("<d:propfind"));
        Assert.Throws<DavXmlException>(() => PropfindRequest.Parse("<x/>"));
    }

    [Fact]
    public void 大きすぎる本文は拒む()
    {
        Assert.Throws<DavXmlException>(() => DavXml.Parse(new string('a', DavXml.MaxBodyBytes + 1)));
    }

    [Fact]
    public void calendar_multiget_は_hrefを読む()
    {
        var request = ReportRequest.Parse("""
            <c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data/></d:prop>
              <d:href>/caldav/calendars/a/x.ics</d:href><d:href>/caldav/calendars/a/y.ics</d:href>
            </c:calendar-multiget>
            """);

        Assert.Equal(ReportKind.CalendarMultiget, request.Kind);
        Assert.Equal(["/caldav/calendars/a/x.ics", "/caldav/calendars/a/y.ics"], request.Hrefs);
        Assert.Contains(DavXml.C + "calendar-data", request.Props);
    }

    [Fact]
    public void calendar_query_は_time_rangeをUTCで読む_無ければ全期間()
    {
        var query = ReportRequest.Parse("""
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/></d:prop>
              <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT">
                <c:time-range start="20260301T000000Z" end="20260401T000000Z"/>
              </c:comp-filter></c:comp-filter></c:filter>
            </c:calendar-query>
            """);
        var all = ReportRequest.Parse("<c:calendar-query xmlns:c=\"urn:ietf:params:xml:ns:caldav\"/>");

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), query.Start);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), query.End);
        Assert.Null(all.Start);
        Assert.Null(all.End);
    }

    [Fact]
    public void 対応しないREPORTは拒む()
    {
        Assert.Throws<DavXmlException>(() => ReportRequest.Parse("<d:sync-collection xmlns:d=\"DAV:\"/>"));
        Assert.Throws<DavXmlException>(() => ReportRequest.Parse(""));
    }

    [Fact]
    public void MultiStatusは_見つかったものと無いものを分けて書く()
    {
        var xml = new MultiStatus()
            .Add(
                "/caldav/calendars/a/",
                [new XElement(DavXml.D + "displayname", "共有予定")],
                [DavXml.CS + "getctag"])
            .AddStatus("/caldav/calendars/a/gone.ics", "HTTP/1.1 404 Not Found")
            .ToXml();

        var doc = XDocument.Parse(xml);
        var responses = doc.Root!.Elements(DavXml.D + "response").ToList();
        Assert.Equal(2, responses.Count);
        Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Equal(
            ["HTTP/1.1 200 OK", "HTTP/1.1 404 Not Found"],
            responses[0].Elements(DavXml.D + "propstat").Select(p => p.Element(DavXml.D + "status")!.Value));
        Assert.Equal("HTTP/1.1 404 Not Found", responses[1].Element(DavXml.D + "status")!.Value);
    }

    [Fact]
    public void URLの形を読む()
    {
        var feed = Guid.NewGuid();

        Assert.Equal(CalDavKind.Root, CalDavPaths.Parse("/caldav").Kind);
        Assert.Equal(CalDavKind.Root, CalDavPaths.Parse("/caldav/").Kind);
        Assert.Equal(CalDavKind.Home, CalDavPaths.Parse("/caldav/calendars/").Kind);
        Assert.Equal("alice smith", CalDavPaths.Parse("/caldav/principals/alice%20smith/").User);
        Assert.Equal(feed, CalDavPaths.Parse($"/caldav/calendars/{feed:D}/").FeedId);
        var ev = CalDavPaths.Parse($"/caldav/calendars/{feed:D}/1-5%40x.ics");
        Assert.Equal(CalDavKind.Event, ev.Kind);
        Assert.Equal("1-5@x.ics", ev.FileName);   // デコード済みの名前
        Assert.Equal("1-5@x", CalDavPaths.UidOf(ev.FileName!));
    }

    [Theory]
    [InlineData("/other/")]
    [InlineData("/caldav/calendars/not-a-guid/")]
    [InlineData("/caldav/calendars/../x/")]
    [InlineData("/caldav/principals/a/b/")]
    [InlineData("/caldav/calendars/00000000-0000-0000-0000-000000000000/x.txt")]
    public void 知らない形や_上位への参照は_Unknown(string path)
    {
        Assert.Equal(CalDavKind.Unknown, CalDavPaths.Parse(path).Kind);
    }

    [Fact]
    public void UIDと資源の名前は_往復でき_URLでは符号化される()
    {
        const string uid = "123-5@feed.calendarfeed";
        var feed = Guid.NewGuid();

        Assert.Equal(uid, CalDavPaths.UidOf(CalDavPaths.ResourceName(uid)));
        Assert.Null(CalDavPaths.UidOf("x.txt"));
        Assert.Null(CalDavPaths.UidOf(".ics"));
        Assert.EndsWith("/a%20b%2F%40.ics", CalDavPaths.Event(feed, "a b/@.ics"));
        // URL に載せた名前は、読み直すと元に戻る
        Assert.Equal("a b@.ics", CalDavPaths.Parse(CalDavPaths.Event(feed, "a b@.ics")).FileName);
    }

    private static CalendarEvent Event(string summary = "打合せ") => new()
    {
        Uid = "1-5@f.calendarfeed",
        Summary = summary,
        Start = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
        LastModified = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void 資源の本文は_METHODを持たず_同じ内容なら同じ本文とETag()
    {
        var a = ICalWriter.WriteResource(Event());
        var b = ICalWriter.WriteResource(Event());

        Assert.DoesNotContain("METHOD", a);
        Assert.Contains("BEGIN:VEVENT", a);
        Assert.Contains("DTSTAMP:20260201T000000Z", a);
        Assert.Equal(a, b);
        Assert.Equal(CalDavPaths.ETagOf(a), CalDavPaths.ETagOf(b));
        Assert.NotEqual(CalDavPaths.ETagOf(a), CalDavPaths.ETagOf(ICalWriter.WriteResource(Event("別の題名"))));
        Assert.StartsWith("\"", CalDavPaths.ETagOf(a));
    }

    [Fact]
    public void CTagは_並びによらず_どれかのETagが変われば変わる()
    {
        Assert.Equal(CalDavPaths.CTagOf(["\"a\"", "\"b\""]), CalDavPaths.CTagOf(["\"b\"", "\"a\""]));
        Assert.NotEqual(CalDavPaths.CTagOf(["\"a\"", "\"b\""]), CalDavPaths.CTagOf(["\"a\"", "\"c\""]));
    }
}
