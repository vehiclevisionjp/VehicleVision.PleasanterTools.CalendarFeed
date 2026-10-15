using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>フィードに出す予定の期間（Issue #67）。時計は 2026-10-13 09:00（UTC）。</summary>
public sealed class FeedRangeEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public FeedRangeEndpointTests()
    {
        // 古い予定（2024 年）・範囲の中（2026-10）・遠い未来（2027-06）の 3 件
        _app.Pleasanter.Record = Record(1, "古い予定", "2024-01-10T09:00:00", "2024-01-10T10:00:00") + ","
            + Record(2, "いまの予定", "2026-10-20T09:00:00", "2026-10-20T10:00:00") + ","
            + Record(3, "遠い未来の予定", "2027-06-10T09:00:00", "2027-06-10T10:00:00");
    }

    public void Dispose() => _app.Dispose();

    private static string Record(int id, string title, string start, string end) =>
        $$"""{ "IssueId": {{id}}, "Ver": 1, "Title": "{{title}}", "StartTime": "{{start}}", "CompletionTime": "{{end}}" }""";

    private async Task<(string Path, JsonElement Feed)> CreateAsync(object body)
    {
        var response = await _app.AdminClient().PostAsJsonAsync("/admin/api/feeds", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (new Uri(json.GetProperty("url").GetString()!).AbsolutePath, json.GetProperty("feed"));
    }

    private async Task<string> IcsAsync(string path)
    {
        var response = await _app.Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 作成のとき期間を省くと_既定は過去365日_未来は無制限()
    {
        var (path, feed) = await CreateAsync(new { name = "共有", siteId = 1 });

        Assert.Equal(365, feed.GetProperty("range").GetProperty("pastDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, feed.GetProperty("range").GetProperty("futureDays").ValueKind);

        var ics = await IcsAsync(path);
        Assert.DoesNotContain("古い予定", ics);       // 1 年より前
        Assert.Contains("いまの予定", ics);
        Assert.Contains("遠い未来の予定", ics);       // 未来は開区間
    }

    [Fact]
    public async Task Pleasanterへは_日付の列の粗い絞り込みを足して取りに行く()
    {
        var (path, _) = await CreateAsync(new { name = "共有", siteId = 1, pastDays = 90, futureDays = 30 });

        await IcsAsync(path);

        var body = JsonDocument.Parse(_app.Pleasanter.Bodies[0]).RootElement.GetProperty("View").GetProperty("ColumnFilterHash");
        // 今日 2026-10-13: 90 日前は 2026-07-15、30 日後（11-12）の翌日 0 時は 2026-11-13
        Assert.Equal(["2026/07/15 00:00:00,"], JsonSerializer.Deserialize<string[]>(body.GetProperty("CompletionTime").GetString()!)!);
        Assert.Equal([",2026/11/13 00:00:00"], JsonSerializer.Deserialize<string[]>(body.GetProperty("StartTime").GetString()!)!);
    }

    [Fact]
    public async Task 取得した後で_期間と重ならない予定を外す_両側を絞るとき()
    {
        var (path, _) = await CreateAsync(new { name = "共有", siteId = 1, pastDays = 30, futureDays = 90 });

        var ics = await IcsAsync(path);

        Assert.Contains("いまの予定", ics);
        Assert.DoesNotContain("古い予定", ics);
        Assert.DoesNotContain("遠い未来の予定", ics);   // 90 日先（2027-01）より後
    }

    [Fact]
    public async Task 両側を無制限にすると_絞り込まず_すべて出す_開区間()
    {
        var (path, feed) = await CreateAsync(new { name = "共有", siteId = 1, pastDays = (int?)null, futureDays = (int?)null });

        Assert.Equal(JsonValueKind.Null, feed.GetProperty("range").GetProperty("pastDays").ValueKind);
        var ics = await IcsAsync(path);

        Assert.Contains("古い予定", ics);
        Assert.Contains("いまの予定", ics);
        Assert.Contains("遠い未来の予定", ics);
        Assert.DoesNotContain("ColumnFilterHash", _app.Pleasanter.Bodies[0]);
    }

    [Fact]
    public async Task 編集で期間を変えられる_キャッシュは捨てられ_次の取得から効く()
    {
        var (path, feed) = await CreateAsync(new { name = "共有", siteId = 1 });
        Assert.DoesNotContain("古い予定", await IcsAsync(path));
        var id = feed.GetProperty("id").GetString();

        var put = await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}", new { name = "共有", siteId = 1, pastDays = (int?)null, futureDays = 100 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var view = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("range");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("pastDays").ValueKind);
        Assert.Equal(100, view.GetProperty("futureDays").GetInt32());
        var ics = await IcsAsync(path);
        Assert.Contains("古い予定", ics);
        Assert.DoesNotContain("遠い未来の予定", ics);   // 100 日先（2027-01）より後
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 36501)]
    public async Task 範囲外の値は断る_言語ごとの文言(int? past, int? future)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/feeds")
        {
            Content = JsonContent.Create(new { name = "共有", siteId = 1, pastDays = past, futureDays = future }),
        };
        request.Headers.Add("Accept-Language", "en");

        var response = await _app.AdminClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PastDays and FutureDays must be between 0 and 36500", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
