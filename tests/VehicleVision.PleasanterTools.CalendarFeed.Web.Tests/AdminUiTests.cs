using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>管理画面（Svelte）の配信とセキュリティヘッダ、フィードの最後の取得結果（Issue #30）。</summary>
public sealed class AdminUiTests : IDisposable
{
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), $"calendarfeed-webroot-{Guid.NewGuid():N}");
    private readonly TestApp _app;

    public AdminUiTests()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "admin", "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "admin", "index.html"), "<!doctype html><title>admin</title>");
        File.WriteAllText(Path.Combine(_webRoot, "admin", "assets", "app.js"), "console.log('ok');");
        _app = new TestApp(webRoot: _webRoot);
    }

    public void Dispose()
    {
        _app.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/")]
    [InlineData("/admin/invite/some-token-value")]
    [InlineData("/admin/feeds")]
    public async Task 管理画面のパスは入口のHTMLを返す(string path)
    {
        var response = await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        // 組み立て済みの管理画面が wwwroot にある環境（静的 Web アセットの一覧に載る）では、そちらの入口が返る。どちらでも HTML の入口であればよい
        Assert.Contains("<!doctype html>", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 組み立て済みのファイルは配り_無いファイルとAPIの未定義のパスはHTMLにせず404()
    {
        var client = _app.Client();

        var script = await client.GetAsync("/admin/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType!.MediaType);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/assets/missing.js")).StatusCode);
        var unknownApi = await client.GetAsync("/admin/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
        Assert.NotEqual("text/html", unknownApi.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/admin/")]
    [InlineData("/admin/assets/app.js")]
    [InlineData("/admin/api/status")]
    public async Task 管理画面の応答にはセキュリティヘッダが付く(string path)
    {
        var response = await _app.Client().GetAsync(path);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("style-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task APIの応答はキャッシュさせない_購読フィードにはCSPを付けない()
    {
        var api = await _app.Client().GetAsync("/admin/api/status");
        Assert.Contains("no-store", api.Headers.CacheControl!.ToString());

        var feed = await _app.Client().GetAsync("/feeds/unknown.ics");
        Assert.False(feed.Headers.Contains("Content-Security-Policy"));
    }

    // ---- 最後の取得結果 --------------------------------------------------------------

    [Fact]
    public async Task 取りに行った結果が一覧へ出る_成功は件数_失敗は理由()
    {
        var (path, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString();
        var admin = _app.AdminClient();

        var before = (await admin.GetFromJsonAsync<JsonElement>("/admin/api/feeds/"))[0];
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastFetch").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await _app.Client().GetAsync(path)).StatusCode);
        var ok = (await admin.GetFromJsonAsync<JsonElement>("/admin/api/feeds/"))[0].GetProperty("lastFetch");
        Assert.Equal(FeedFetchStatus.Success, ok.GetProperty("status").GetString());
        Assert.Equal(1, ok.GetProperty("eventCount").GetInt32());

        // キャッシュに当たった要求は数えない（Pleasanter へ行っていない）。API キーを差し替えて取り直させる
        _app.Pleasanter.StatusCode = HttpStatusCode.Unauthorized;
        await admin.PutAsJsonAsync($"/admin/api/feeds/{id}/apikey", new { apiKey = "a-new-api-key-value" });
        Assert.Equal(HttpStatusCode.BadGateway, (await _app.Client().GetAsync(path)).StatusCode);

        var failed = (await admin.GetFromJsonAsync<JsonElement>("/admin/api/feeds/"))[0].GetProperty("lastFetch");
        Assert.Equal(FeedFetchStatus.Unauthorized, failed.GetProperty("status").GetString());
        Assert.Contains("401", failed.GetProperty("message").GetString());
        Assert.DoesNotContain("a-new-api-key-value", failed.ToString());
    }
}
