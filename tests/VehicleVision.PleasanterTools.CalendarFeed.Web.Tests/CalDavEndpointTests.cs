using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>CalDAV の入口（読み取りのみ。Issue #85）。</summary>
public sealed class CalDavEndpointTests : IDisposable
{
    private const string UserKey = "alice-pleasanter-key";
    private const string AllProp = "";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static string Basic(string user, string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{key}"));

    private static HttpRequestMessage Request(string method, string path, string? body = null, string? depth = null, string? key = UserKey)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Basic("alice", key));
        }

        if (depth is not null)
        {
            request.Headers.Add("Depth", depth);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        }

        return request;
    }

    private async Task<(string Id, string Calendar)> EnabledFeedAsync(bool enable = true)
    {
        var (_, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString()!;
        if (enable)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}/caldav", new { enabled = true })).StatusCode);
        }

        return (id, $"/caldav/calendars/{id}/");
    }

    private static async Task<XDocument> XmlAsync(HttpResponseMessage response) => XDocument.Parse(await response.Content.ReadAsStringAsync());

    private static IEnumerable<string> Hrefs(XDocument doc) =>
        doc.Root!.Elements(DavXml.D + "response").Select(r => r.Element(DavXml.D + "href")!.Value);

    private const string EventHref = "123-5@";

    [Fact]
    public async Task OPTIONSは認証なしで_DAVヘッダを返す()
    {
        var response = await _app.Client().SendAsync(Request("OPTIONS", "/caldav/", key: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("calendar-access", response.Headers.GetValues("DAV").Single());
        var allow = string.Join(",", response.Content.Headers.Allow);
        Assert.Contains("PUT", allow);
        Assert.DoesNotContain("DELETE", allow);
    }

    [Fact]
    public async Task 認証なしは401でBasicを求める()
    {
        var response = await _app.Client().SendAsync(Request("PROPFIND", "/caldav/", depth: "0", key: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task well_knownは_caldavへ転送する()
    {
        var response = await _app.RawClient().GetAsync("/.well-known/caldav");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/caldav/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task 探索_ルートからプリンシパル_ホームへたどれる()
    {
        var root = await XmlAsync(await _app.Client().SendAsync(Request("PROPFIND", "/caldav/", AllProp, "0")));
        var principal = root.Descendants(DavXml.D + "current-user-principal").Single().Element(DavXml.D + "href")!.Value;
        Assert.Equal("/caldav/principals/alice/", principal);

        var response = await _app.Client().SendAsync(Request(
            "PROPFIND", principal, "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-home-set/></d:prop></d:propfind>", "0"));
        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var home = (await XmlAsync(response)).Descendants(DavXml.C + "calendar-home-set").Single().Element(DavXml.D + "href")!.Value;
        Assert.Equal(CalDavPaths.Home, home);
    }

    [Fact]
    public async Task ホームの一覧は_CalDAVを有効にしたフィードだけ()
    {
        var (_, calendar) = await EnabledFeedAsync();
        var (_, disabled) = await EnabledFeedAsync(enable: false);

        var doc = await XmlAsync(await _app.Client().SendAsync(Request("PROPFIND", CalDavPaths.Home, AllProp, "1")));

        var hrefs = Hrefs(doc).ToList();
        Assert.Contains(calendar, hrefs);
        Assert.DoesNotContain(disabled, hrefs);
        Assert.Contains(doc.Descendants(DavXml.D + "resourcetype"), r => r.Element(DavXml.C + "calendar") is not null);
    }

    [Fact]
    public async Task カレンダーは_名前と対応する部品とctagと読み取り権限を返す()
    {
        var (_, calendar) = await EnabledFeedAsync();

        var response = await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "0"));

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var doc = await XmlAsync(response);
        Assert.Equal("共有予定", doc.Descendants(DavXml.D + "displayname").Single().Value);
        Assert.Equal("VEVENT", doc.Descendants(DavXml.C + "comp").Single().Attribute("name")!.Value);
        Assert.NotEmpty(doc.Descendants(DavXml.CS + "getctag").Single().Value);
        var privileges = doc.Descendants(DavXml.D + "current-user-privilege-set").Single().Descendants().Select(e => e.Name.LocalName).ToList();
        Assert.Contains("read", privileges);
        Assert.Contains("write-content", privileges);
        Assert.Contains("bind", privileges);
        Assert.DoesNotContain("unbind", privileges); // 削除は未対応
    }

    [Fact]
    public async Task カレンダーの子は予定_etagと種類つき()
    {
        var (_, calendar) = await EnabledFeedAsync();

        var doc = await XmlAsync(await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "1")));

        var eventResponse = doc.Root!.Elements(DavXml.D + "response").Single(r => r.Element(DavXml.D + "href")!.Value.EndsWith(".ics", StringComparison.Ordinal));
        Assert.StartsWith("\"", eventResponse.Descendants(DavXml.D + "getetag").Single().Value);
        Assert.Contains("text/calendar", eventResponse.Descendants(DavXml.D + "getcontenttype").Single().Value);
        Assert.Contains(EventHref, Uri.UnescapeDataString(eventResponse.Element(DavXml.D + "href")!.Value));
    }

    [Fact]
    public async Task calendar_query_は予定の本文を返し_METHODを含まない()
    {
        var (_, calendar) = await EnabledFeedAsync();
        const string body = """
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data/></d:prop>
              <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"/></c:comp-filter></c:filter>
            </c:calendar-query>
            """;

        var response = await _app.Client().SendAsync(Request("REPORT", calendar, body, "1"));

        Assert.Equal((HttpStatusCode)207, response.StatusCode);
        var data = (await XmlAsync(response)).Descendants(DavXml.C + "calendar-data").Single().Value;
        Assert.Contains("SUMMARY:打合せ", data);
        Assert.DoesNotContain("METHOD", data);
    }

    [Fact]
    public async Task calendar_query_の期間の外の予定は返さない()
    {
        var (_, calendar) = await EnabledFeedAsync();
        const string body = """
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/></d:prop>
              <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT">
                <c:time-range start="20270101T000000Z" end="20270201T000000Z"/>
              </c:comp-filter></c:comp-filter></c:filter>
            </c:calendar-query>
            """;

        var doc = await XmlAsync(await _app.Client().SendAsync(Request("REPORT", calendar, body, "1")));

        Assert.Empty(doc.Root!.Elements(DavXml.D + "response"));
    }

    [Fact]
    public async Task calendar_multiget_は_見つからないhrefを404で返す()
    {
        var (id, calendar) = await EnabledFeedAsync();
        var list = await XmlAsync(await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "1")));
        var eventHref = Hrefs(list).Single(h => h.EndsWith(".ics", StringComparison.Ordinal));
        var body = $"""
            <c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop><d:getetag/><c:calendar-data/></d:prop>
              <d:href>{eventHref}</d:href><d:href>/caldav/calendars/{id}/none.ics</d:href>
            </c:calendar-multiget>
            """;

        var doc = await XmlAsync(await _app.Client().SendAsync(Request("REPORT", calendar, body)));

        var responses = doc.Root!.Elements(DavXml.D + "response").ToList();
        Assert.Equal(2, responses.Count);
        Assert.Contains("SUMMARY:打合せ", responses[0].Descendants(DavXml.C + "calendar-data").Single().Value);
        Assert.Equal("HTTP/1.1 404 Not Found", responses[1].Element(DavXml.D + "status")!.Value);
    }

    [Fact]
    public async Task GETは_予定1件のicsとETagを返す()
    {
        var (_, calendar) = await EnabledFeedAsync();
        var list = await XmlAsync(await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "1")));
        var eventHref = Hrefs(list).Single(h => h.EndsWith(".ics", StringComparison.Ordinal));

        var response = await _app.Client().SendAsync(Request("GET", eventHref));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("\"", response.Headers.ETag!.Tag);
        Assert.Contains("SUMMARY:打合せ", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().SendAsync(Request("GET", calendar + "none.ics"))).StatusCode);
    }

    [Fact]
    public async Task 取得には利用者のAPIキーを使い_フィードの共通キーは使わない()
    {
        var (_, calendar) = await EnabledFeedAsync();

        await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "0"));

        Assert.Contains($"\"ApiKey\":\"{UserKey}\"", _app.Pleasanter.LastRequestBody);
        Assert.DoesNotContain(TestApp.GlobalApiKey, _app.Pleasanter.LastRequestBody);
    }

    [Fact]
    public async Task 同じ利用者の続けての要求は_Pleasanterを叩き直さない_別の鍵なら叩く()
    {
        var (_, calendar) = await EnabledFeedAsync();
        var client = _app.Client();

        await client.SendAsync(Request("PROPFIND", calendar, AllProp, "1"));
        var calls = _app.Pleasanter.CallCount;
        await client.SendAsync(Request("PROPFIND", calendar, AllProp, "1"));
        Assert.Equal(calls, _app.Pleasanter.CallCount);

        await client.SendAsync(Request("PROPFIND", calendar, AllProp, "1", key: "bob-key"));
        Assert.True(_app.Pleasanter.CallCount > calls);
    }

    [Fact]
    public async Task PleasanterがAPIキーを拒んだら_401で鍵の入れ直しを求める()
    {
        var (_, calendar) = await EnabledFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.Unauthorized;

        var response = await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEmpty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task ホームの一覧で_すべてのフィードがAPIキーを拒めば_401()
    {
        await EnabledFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.Unauthorized;

        var response = await _app.Client().SendAsync(Request("PROPFIND", CalDavPaths.Home, AllProp, "1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 権限が無ければ403_上限や一時的な失敗は503()
    {
        var (_, calendar) = await EnabledFeedAsync();

        _app.Pleasanter.StatusCode = HttpStatusCode.Forbidden;
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "0", key: "k1"))).StatusCode);

        _app.Pleasanter.StatusCode = HttpStatusCode.TooManyRequests;
        var limited = await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "0", key: "k2"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task 無効のフィードと知らないフィードは404()
    {
        var (_, disabled) = await EnabledFeedAsync(enable: false);

        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().SendAsync(Request("PROPFIND", disabled, AllProp, "0"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().SendAsync(Request("PROPFIND", $"/caldav/calendars/{Guid.NewGuid()}/", AllProp, "0"))).StatusCode);
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("MKCALENDAR")]
    [InlineData("PROPPATCH")]
    public async Task 削除など未対応の書き込み系は405(string method)
    {
        var (_, calendar) = await EnabledFeedAsync();

        var response = await _app.Client().SendAsync(Request(method, calendar + "x.ics", "<x/>"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Depthがinfinityは403_壊れた本文とDTDつきは400()
    {
        var (_, calendar) = await EnabledFeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "infinity"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.Client().SendAsync(Request("PROPFIND", calendar, "<d:propfind", "0"))).StatusCode);
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><d:propfind xmlns:d=\"DAV:\"/>";
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.Client().SendAsync(Request("PROPFIND", calendar, xxe, "0"))).StatusCode);
    }

    [Fact]
    public async Task 対応しないREPORTは403_supported_report()
    {
        var (_, calendar) = await EnabledFeedAsync();

        var response = await _app.Client().SendAsync(Request("REPORT", calendar, "<d:sync-collection xmlns:d=\"DAV:\"/>"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("supported-report", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 応答にAPIキーを載せない_キャッシュさせない()
    {
        var (_, calendar) = await EnabledFeedAsync();

        var response = await _app.Client().SendAsync(Request("PROPFIND", calendar, AllProp, "1"));

        Assert.DoesNotContain(UserKey, await response.Content.ReadAsStringAsync());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }
}
