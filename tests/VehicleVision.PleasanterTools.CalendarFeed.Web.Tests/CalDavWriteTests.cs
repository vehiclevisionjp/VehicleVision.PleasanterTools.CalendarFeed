using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>CalDAV の書き込み（PUT。作成・更新・競合。Issue #85）。</summary>
public sealed class CalDavWriteTests : IDisposable
{
    private const string UserKey = "alice-pleasanter-key";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static string Ics(string uid, string summary = "新しい会議", params string[] extra) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTART:20260310T090000Z\r\nDTEND:20260310T100000Z\r\nSUMMARY:{summary}\r\n"
        + string.Concat(extra.Select(l => l + "\r\n")) + "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static HttpRequestMessage Put(string path, string body, string? ifMatch = null, bool ifNoneMatchAny = false, string key = UserKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/calendar"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice:{key}")));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (ifNoneMatchAny)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        return request;
    }

    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice:{UserKey}")));
        return request;
    }

    private async Task<(Guid Id, string Calendar)> FeedAsync(object? request = null)
    {
        var (_, feed) = await _app.CreateFeedAsync(request);
        var id = feed.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}/caldav", new { enabled = true })).StatusCode);
        return (id, $"/caldav/calendars/{id}/");
    }

    /// <summary>既存の予定（レコード 5）の資源の名前・UID・ETag。</summary>
    private async Task<(string Name, string Uid, string ETag, string Body)> ExistingAsync(Guid id, string calendar)
    {
        var uid = $"{FakePleasanter.SiteId}-5@{id:N}.calendarfeed";
        var name = uid + ".ics";
        var response = await _app.Client().SendAsync(Get(calendar + Uri.EscapeDataString(name)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (name, uid, response.Headers.ETag!.Tag, await response.Content.ReadAsStringAsync());
    }

    private JsonObject LastWrite(string suffix) =>
        JsonNode.Parse(_app.Pleasanter.Requests.Last(r => r.Path.EndsWith(suffix, StringComparison.Ordinal)).Body)!.AsObject();

    // ---- 作成 ----------------------------------------------------------------------------------

    [Fact]
    public async Task 作成_201でレコードを作り_利用者のAPIキーで書き_UIDと名前を残す()
    {
        var (id, calendar) = await FeedAsync();

        var response = await _app.Client().SendAsync(Put(calendar + "mine.ics", Ics("client-uid-1", "新しい会議", "DESCRIPTION:議題"), ifNoneMatchAny: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.ETag); // 保存したものは整えた内容なので、ETag は返さない
        var create = _app.Pleasanter.Requests.Single(r => r.Path.EndsWith("/Create", StringComparison.Ordinal));
        Assert.Equal("/api/items/123/Create", create.Path);
        var body = JsonNode.Parse(create.Body)!;
        Assert.Equal(UserKey, (string?)body["ApiKey"]);
        Assert.DoesNotContain(TestApp.GlobalApiKey, create.Body);
        Assert.Equal("新しい会議", (string?)body["Title"]);
        Assert.Equal("議題", (string?)body["Body"]);
        Assert.Equal("2026-03-10T09:00:00", (string?)body["StartTime"]); // テストのタイムゾーンは UTC
        Assert.Equal("2026-03-10T10:00:00", (string?)body["CompletionTime"]);

        var stored = await _app.Service<ICalDavStore>().FindByUidAsync(id, "client-uid-1");
        Assert.Equal("mine.ics", stored!.Name);
        Assert.Equal(6, stored.RecordId);
    }

    [Fact]
    public async Task 作成した予定は_カレンダーアプリが付けたUIDと名前で見える()
    {
        var (id, calendar) = await FeedAsync();
        await _app.Service<ICalDavStore>().TryAddAsync(new CalDavResource(id, "mine.ics", "client-uid-1", 5));

        var list = XDocument.Parse(await (await _app.Client().SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), calendar)
        {
            Headers = { { "Depth", "1" }, { "Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice:{UserKey}"))}" } },
        })).Content.ReadAsStringAsync());
        var hrefs = list.Root!.Elements(DavXml.D + "response").Select(r => r.Element(DavXml.D + "href")!.Value).ToList();

        Assert.Contains(calendar + "mine.ics", hrefs);
        Assert.DoesNotContain(hrefs, h => h.Contains(".calendarfeed", StringComparison.Ordinal));
        var got = await (await _app.Client().SendAsync(Get(calendar + "mine.ics"))).Content.ReadAsStringAsync();
        Assert.Contains("UID:client-uid-1", got);
    }

    [Fact]
    public async Task 作成_UIDが重なれば409でno_uid_conflict()
    {
        var (_, calendar) = await FeedAsync();
        await _app.Client().SendAsync(Put(calendar + "a.ics", Ics("same-uid"), ifNoneMatchAny: true));

        var response = await _app.Client().SendAsync(Put(calendar + "b.ics", Ics("same-uid"), ifNoneMatchAny: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(calendar + "a.ics", error.Descendants(DavXml.C + "no-uid-conflict").Single().Element(DavXml.D + "href")!.Value);
        Assert.Single(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Create", StringComparison.Ordinal)); // 2 件目は作らない
    }

    [Fact]
    public async Task 作成_存在しないものへのIf_Matchは412()
    {
        var (_, calendar) = await FeedAsync();

        var response = await _app.Client().SendAsync(Put(calendar + "x.ics", Ics("u"), ifMatch: "\"abc\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Create", StringComparison.Ordinal));
    }

    // ---- 更新 ----------------------------------------------------------------------------------

    [Fact]
    public async Task 更新_If_Matchが合えば204でレコードを更新する()
    {
        var (id, calendar) = await FeedAsync();
        var (name, uid, etag, _) = await ExistingAsync(id, calendar);

        var response = await _app.Client().SendAsync(Put(calendar + Uri.EscapeDataString(name), Ics(uid, "直した題名"), ifMatch: etag));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var update = _app.Pleasanter.Requests.Single(r => r.Path.EndsWith("/Update", StringComparison.Ordinal));
        Assert.Equal("/api/items/5/Update", update.Path);
        var body = LastWrite("/Update");
        Assert.Equal(UserKey, (string?)body["ApiKey"]);
        Assert.Equal("直した題名", (string?)body["Title"]);
        Assert.Null(body["Timestamp"]);
    }

    [Fact]
    public async Task 更新_If_Matchが無ければ428_違えば412で_更新しない()
    {
        var (id, calendar) = await FeedAsync();
        var (name, uid, _, _) = await ExistingAsync(id, calendar);
        var path = calendar + Uri.EscapeDataString(name);

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await _app.Client().SendAsync(Put(path, Ics(uid)))).StatusCode);
        var stale = await _app.Client().SendAsync(Put(path, Ics(uid), ifMatch: "\"stale\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await _app.Client().SendAsync(Put(path, Ics(uid), ifNoneMatchAny: true))).StatusCode);

        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 更新_Pleasanterの画面で直された後の古いETagは412()
    {
        var (id, calendar) = await FeedAsync();
        var (name, uid, etag, _) = await ExistingAsync(id, calendar);
        // 取得の後、Pleasanter の画面でタイトルが直された
        _app.Pleasanter.Record = _app.Pleasanter.Record.Replace("打合せ", "画面で直した題名", StringComparison.Ordinal);

        var response = await _app.Client().SendAsync(Put(calendar + Uri.EscapeDataString(name), Ics(uid, "自分の直し"), ifMatch: etag));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 更新_UIDを変える書き込みは403()
    {
        var (id, calendar) = await FeedAsync();
        var (name, _, etag, _) = await ExistingAsync(id, calendar);

        var response = await _app.Client().SendAsync(Put(calendar + Uri.EscapeDataString(name), Ics("another-uid"), ifMatch: etag));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("UID", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 更新_別のサイトのレコードは書かない()
    {
        var (id, calendar) = await FeedAsync();
        var (name, uid, etag, _) = await ExistingAsync(id, calendar);
        _app.Pleasanter.Record = _app.Pleasanter.Record.Replace("\"SiteId\": 123", "\"SiteId\": 999", StringComparison.Ordinal);

        var response = await _app.Client().SendAsync(Put(calendar + Uri.EscapeDataString(name), Ics(uid), ifMatch: etag));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 更新_作ったときの名前でも更新できる()
    {
        var (id, calendar) = await FeedAsync();
        await _app.Service<ICalDavStore>().TryAddAsync(new CalDavResource(id, "mine.ics", "client-uid-1", 5));
        var got = await _app.Client().SendAsync(Get(calendar + "mine.ics"));
        var etag = got.Headers.ETag!.Tag;

        var response = await _app.Client().SendAsync(Put(calendar + "mine.ics", Ics("client-uid-1", "名前そのままで更新"), ifMatch: etag));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("/api/items/5/Update", _app.Pleasanter.Requests.Single(r => r.Path.EndsWith("/Update", StringComparison.Ordinal)).Path);
    }

    [Fact]
    public async Task 更新_レコードが消えていたら_対応を捨てて412()
    {
        var (id, calendar) = await FeedAsync();
        await _app.Service<ICalDavStore>().TryAddAsync(new CalDavResource(id, "gone.ics", "gone-uid", 5));
        _app.Pleasanter.RecordStatusCode = HttpStatusCode.NotFound;

        var response = await _app.Client().SendAsync(Put(calendar + "gone.ics", Ics("gone-uid"), ifMatch: "\"x\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Null(await _app.Service<ICalDavStore>().FindByNameAsync(id, "gone.ics"));
    }

    // ---- 受け付けない内容 ----------------------------------------------------------------------

    [Theory]
    [InlineData("RRULE:FREQ=DAILY", "valid-calendar-object-resource")]
    [InlineData("EXDATE:20260311T090000Z", "valid-calendar-object-resource")]
    public async Task 繰り返しは403(string line, string condition)
    {
        var (_, calendar) = await FeedAsync();

        var response = await _app.Client().SendAsync(Put(calendar + "r.ics", Ics("rec", "繰り返し", line), ifNoneMatchAny: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(DavXml.C + condition));
        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TODOと_壊れたデータも403_理由の種類が違う()
    {
        var (_, calendar) = await FeedAsync();

        var todo = await _app.Client().SendAsync(Put(calendar + "t.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:t\r\nEND:VTODO\r\nEND:VCALENDAR\r\n", ifNoneMatchAny: true));
        var broken = await _app.Client().SendAsync(Put(calendar + "b.ics", "これは iCalendar ではない", ifNoneMatchAny: true));

        Assert.Equal(HttpStatusCode.Forbidden, todo.StatusCode);
        Assert.Contains("supported-calendar-component", await todo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, broken.StatusCode);
        Assert.Contains("valid-calendar-data", await broken.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 件名がテンプレートのフィードは_書き戻せないので403()
    {
        var (_, calendar) = await FeedAsync(new
        {
            name = "テンプレート",
            siteId = 123,
            mapping = new { summaryTemplate = "{Title}（{DateA:yyyy/MM/dd}）", formats = new Dictionary<string, string>() },
        });

        var response = await _app.Client().SendAsync(Put(calendar + "x.ics", Ics("u"), ifNoneMatchAny: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("template", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 大きすぎる本文は413()
    {
        var (_, calendar) = await FeedAsync();

        var response = await _app.Client().SendAsync(Put(calendar + "big.ics", Ics("u", new string('x', ICalReaderMax + 10)), ifNoneMatchAny: true));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private const int ICalReaderMax = 256 * 1024;

    // ---- 権限・認証・対象 ----------------------------------------------------------------------

    [Fact]
    public async Task 書く権限が無ければ403_APIキーが通らなければ401()
    {
        var (_, calendar) = await FeedAsync();

        _app.Pleasanter.WriteStatusCode = HttpStatusCode.Forbidden;
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.Client().SendAsync(Put(calendar + "a.ics", Ics("a"), ifNoneMatchAny: true))).StatusCode);

        _app.Pleasanter.WriteStatusCode = HttpStatusCode.Unauthorized;
        var denied = await _app.Client().SendAsync(Put(calendar + "b.ics", Ics("b"), ifNoneMatchAny: true));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.NotEmpty(denied.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task 認証なしと_無効のフィードと_予定以外のURLへのPUTは拒む()
    {
        var (_, calendar) = await FeedAsync();
        var (_, disabled) = await FeedAsync();
        var disabledId = disabled.Split('/')[^2];
        await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{disabledId}/caldav", new { enabled = false });

        var anonymous = new HttpRequestMessage(HttpMethod.Put, calendar + "a.ics") { Content = new StringContent(Ics("a")) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().SendAsync(anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().SendAsync(Put(disabled + "a.ics", Ics("a"), ifNoneMatchAny: true))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _app.Client().SendAsync(Put(calendar, Ics("a")))).StatusCode);
        Assert.Empty(_app.Pleasanter.Requests);
    }

    [Fact]
    public async Task 書いた後の読み取りは_キャッシュを使わず取り直す()
    {
        var (_, calendar) = await FeedAsync();
        var client = _app.Client();
        await client.SendAsync(Get(calendar + "none.ics")); // 一覧を取ってキャッシュさせる
        var before = _app.Pleasanter.Requests.Count(r => r.Path.EndsWith("/Get", StringComparison.Ordinal));

        await client.SendAsync(Put(calendar + "n.ics", Ics("n"), ifNoneMatchAny: true));
        await client.SendAsync(Get(calendar + "none.ics"));

        Assert.True(_app.Pleasanter.Requests.Count(r => r.Path.EndsWith("/Get", StringComparison.Ordinal)) > before);
    }

    // ---- 削除 ----------------------------------------------------------------------------------

    private static HttpRequestMessage Delete(string path, string? ifMatch = null, string key = UserKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"alice:{key}")));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    [Fact]
    public async Task 削除_If_Matchが合えば204_利用者のAPIキーでPleasanterへ削除を送る()
    {
        var (id, calendar) = await FeedAsync();
        var (name, _, etag, _) = await ExistingAsync(id, calendar);

        var response = await _app.Client().SendAsync(Delete(calendar + Uri.EscapeDataString(name), etag));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var request = _app.Pleasanter.Requests.Single(r => r.Path.EndsWith("/Delete", StringComparison.Ordinal));
        Assert.Equal("/api/items/5/Delete", request.Path);
        Assert.Equal(UserKey, (string?)JsonNode.Parse(request.Body)!["ApiKey"]);
    }

    [Fact]
    public async Task 削除_作ったときの名前でも消せて_対応表の行も消える()
    {
        var (id, calendar) = await FeedAsync();
        await _app.Service<ICalDavStore>().TryAddAsync(new CalDavResource(id, "mine.ics", "client-uid-1", 5));
        var etag = (await _app.Client().SendAsync(Get(calendar + "mine.ics"))).Headers.ETag!.Tag;

        var response = await _app.Client().SendAsync(Delete(calendar + "mine.ics", etag));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await _app.Service<ICalDavStore>().FindByNameAsync(id, "mine.ics"));
        Assert.Null(await _app.Service<ICalDavStore>().FindByUidAsync(id, "client-uid-1")); // 同じ UID で作り直せる
    }

    [Fact]
    public async Task 削除_If_Matchが古ければ412で消さない_付けなければ消す_ごみ箱へ移るので()
    {
        var (id, calendar) = await FeedAsync();
        var (name, _, etag, _) = await ExistingAsync(id, calendar);
        var path = calendar + Uri.EscapeDataString(name);
        _app.Pleasanter.Record = _app.Pleasanter.Record.Replace("打合せ", "画面で直した題名", StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await _app.Client().SendAsync(Delete(path, etag))).StatusCode); // 取得の後で直された
        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Delete", StringComparison.Ordinal));

        // If-Match を付けないクライアント（実機の Python caldav など）。削除は戻せるので、そのまま消す
        Assert.Equal(HttpStatusCode.NoContent, (await _app.Client().SendAsync(Delete(path))).StatusCode);
        Assert.Single(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 削除_無い資源は404_別のサイトのレコードは消さない()
    {
        var (id, calendar) = await FeedAsync();
        var (name, _, etag, _) = await ExistingAsync(id, calendar);

        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().SendAsync(Delete(calendar + "none.ics", "\"x\""))).StatusCode);

        _app.Pleasanter.Record = _app.Pleasanter.Record.Replace("\"SiteId\": 123", "\"SiteId\": 999", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.Client().SendAsync(Delete(calendar + Uri.EscapeDataString(name), etag))).StatusCode);
        Assert.DoesNotContain(_app.Pleasanter.Requests, r => r.Path.EndsWith("/Delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 削除_権限が無ければ403_レコードが既に無ければ412で対応を捨てる()
    {
        var (id, calendar) = await FeedAsync();
        var (name, _, etag, _) = await ExistingAsync(id, calendar);

        _app.Pleasanter.WriteStatusCode = HttpStatusCode.Forbidden;
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.Client().SendAsync(Delete(calendar + Uri.EscapeDataString(name), etag))).StatusCode);

        await _app.Service<ICalDavStore>().TryAddAsync(new CalDavResource(id, "gone.ics", "gone-uid", 5));
        _app.Pleasanter.RecordStatusCode = HttpStatusCode.NotFound;
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await _app.Client().SendAsync(Delete(calendar + "gone.ics", "\"x\""))).StatusCode);
        Assert.Null(await _app.Service<ICalDavStore>().FindByNameAsync(id, "gone.ics"));
    }

    [Fact]
    public async Task 削除_認証なしは401_カレンダーそのものの削除は405()
    {
        var (_, calendar) = await FeedAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().SendAsync(new HttpRequestMessage(HttpMethod.Delete, calendar + "a.ics"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _app.Client().SendAsync(Delete(calendar, "\"x\""))).StatusCode);
        Assert.Empty(_app.Pleasanter.Requests);
    }

    [Fact]
    public async Task 削除した予定は_フィードを失効させると対応表も消える()
    {
        var (id, _) = await FeedAsync();
        await _app.Service<ICalDavStore>().TryAddAsync(new CalDavResource(id, "mine.ics", "client-uid-1", 5));

        var response = await _app.AdminClient().DeleteAsync($"/admin/api/feeds/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await _app.Service<ICalDavStore>().ListAsync(id));
    }
}
