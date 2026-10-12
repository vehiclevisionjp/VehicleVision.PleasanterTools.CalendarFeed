using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>テスト用にアプリを起動する。Pleasanter は <see cref="FakePleasanter"/> が真似る。</summary>
internal sealed class TestApp : IDisposable
{
    public const string AdminKey = "test-admin-key";
    public const string GlobalApiKey = "global-api-key";

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"calendarfeed-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public TestApp(bool secretKey = true, bool globalApiKey = true)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // **ConfigureAppConfiguration で上書きする。** UseSetting は App_Data/Parameters の既定より弱い
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    [AppSettings.DbConnectionString] = $"Data Source={_db};Pooling=False",
                    [AppSettings.PleasanterBaseUrl] = "https://pleasanter.test",
                    [AppSettings.PleasanterTimeZone] = "UTC",
                    [AppSettings.AdminApiKey] = AdminKey,
                    [AppSettings.PublicBaseUrl] = "https://feed.test",
                };
                if (globalApiKey)
                {
                    settings[AppSettings.PleasanterApiKey] = GlobalApiKey;
                }

                if (secretKey)
                {
                    settings[AppSettings.SecretKey] = SecretProtector.GenerateKey();
                }

                config.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<Pleasanter.PleasanterClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => Pleasanter));
        });
    }

    public FakePleasanter Pleasanter { get; } = new();

    public HttpClient Client() => _factory.CreateClient();

    public HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminKey);
        return client;
    }

    /// <summary>フィードを作り、購読 URL のパスを返す。</summary>
    public async Task<(string Path, JsonElement Feed)> CreateFeedAsync(object? request = null)
    {
        var response = await AdminClient().PostAsJsonAsync("/admin/api/feeds", request ?? new { name = "共有予定", siteId = 123 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (new Uri(json.GetProperty("url").GetString()!).AbsolutePath, json.GetProperty("feed"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

/// <summary>Pleasanter の Get API の応答を真似る。</summary>
internal sealed class FakePleasanter : HttpMessageHandler
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public int CallCount { get; private set; }

    public string? LastRequestUri { get; private set; }

    public string? LastRequestBody { get; private set; }

    /// <summary>返すレコード 1 件（JSON）。</summary>
    public string Record { get; set; } = """
        { "IssueId": 5, "Ver": 1, "Title": "打合せ", "Body": "議題",
          "StartTime": "2026-03-01T09:00:00", "CompletionTime": "2026-03-01T10:00:00" }
        """;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri!.ToString();
        LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(
                $$"""{ "StatusCode": 200, "Response": { "Offset": 0, "PageSize": 200, "TotalCount": 1, "Data": [ {{Record}} ] } }""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
    }
}

public sealed class FeedEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 購読URLからicsが取れてAPIキーは本文に載せて送られる()
    {
        var (path, _) = await _app.CreateFeedAsync();

        var response = await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SUMMARY:打合せ", body);
        Assert.Contains("DTSTART:20260301T090000Z", body);
        Assert.DoesNotContain(TestApp.GlobalApiKey, body);

        Assert.Equal("https://pleasanter.test/api/items/123/Get", _app.Pleasanter.LastRequestUri);
        Assert.Contains($"\"ApiKey\":\"{TestApp.GlobalApiKey}\"", _app.Pleasanter.LastRequestBody);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task 同じ内容ならETagで304を返しキャッシュ中はPleasanterを叩かない()
    {
        var (path, _) = await _app.CreateFeedAsync();
        var client = _app.Client();

        var first = await client.GetAsync(path);
        var etag = first.Headers.ETag!.Tag;
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(1, _app.Pleasanter.CallCount);
    }

    [Fact]
    public async Task 不明なトークンと失効したトークンは404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().GetAsync("/feeds/unknown.ics")).StatusCode);

        var (path, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().DeleteAsync($"/admin/api/feeds/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Pleasanterが一時的に落ちていれば503()
    {
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.BadGateway;

        var response = await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task 管理キーが無い要求は401で一覧にトークンを含めない()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().GetAsync("/admin/api/feeds/")).StatusCode);

        await _app.CreateFeedAsync();
        var raw = await _app.AdminClient().GetStringAsync("/admin/api/feeds/");
        Assert.DoesNotContain("/feeds/", raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 不正な列名は400()
    {
        var response = await _app.AdminClient().PostAsJsonAsync(
            "/admin/api/feeds",
            new { name = "x", siteId = 1, mapping = new { summaryColumn = "Title; DROP" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>フィードごとの API キー（Issue #19）。</summary>
public sealed class FeedApiKeyEndpointTests : IDisposable
{
    private const string OwnKey = "6ce6c0fd2f4aea3c093c3ebdd7d4ea3250a132b4867d68e7d1abe35c2499664a";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task フィードごとのAPIキーが共通の既定より優先して送られる()
    {
        var (path, _) = await _app.CreateFeedAsync(new { name = "a", siteId = 1, apiKey = OwnKey });

        await _app.Client().GetAsync(path);

        Assert.Contains($"\"ApiKey\":\"{OwnKey}\"", _app.Pleasanter.LastRequestBody);
        Assert.DoesNotContain(TestApp.GlobalApiKey, _app.Pleasanter.LastRequestBody);
    }

    [Fact]
    public async Task 作成の応答にも一覧にもAPIキーは含まれず登録済みと末尾だけが出る()
    {
        var (_, feed) = await _app.CreateFeedAsync(new { name = "a", siteId = 1, apiKey = OwnKey });

        Assert.True(feed.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(OwnKey[^4..], feed.GetProperty("apiKeyHint").GetString());
        var list = await _app.AdminClient().GetStringAsync("/admin/api/feeds/");
        Assert.DoesNotContain(OwnKey, list);
        Assert.Contains("\"hasApiKey\":true", list);
    }

    [Fact]
    public async Task APIキーを差し替えると生成済みのicsを捨てて新しいキーで取り直す()
    {
        var (path, feed) = await _app.CreateFeedAsync(new { name = "a", siteId = 1, apiKey = OwnKey });
        await _app.Client().GetAsync(path);
        Assert.Equal(1, _app.Pleasanter.CallCount);

        var id = feed.GetProperty("id").GetGuid();
        var put = await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}/apikey", new { apiKey = "newkey-0123456789abcdef" });
        await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(2, _app.Pleasanter.CallCount);
        Assert.Contains("\"ApiKey\":\"newkey-0123456789abcdef\"", _app.Pleasanter.LastRequestBody);
    }

    [Fact]
    public async Task 存在しないフィードのAPIキー差し替えは404()
    {
        var put = await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{Guid.NewGuid()}/apikey", new { apiKey = "k" });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task フィードごとのタイムゾーンで日時を読む()
    {
        var (path, _) = await _app.CreateFeedAsync(new
        {
            name = "a",
            siteId = 1,
            apiKey = OwnKey,
            apiKeyUserTimeZoneId = "Asia/Tokyo",
        });

        var body = await _app.Client().GetStringAsync(path);

        // 09:00 を東京の壁時計として読む → 00:00Z
        Assert.Contains("DTSTART:20260301T000000Z", body);
    }

    [Fact]
    public async Task 件名が空ならフィードの言語の仮の件名になる()
    {
        _app.Pleasanter.Record = """{ "IssueId": 5, "Title": "", "StartTime": "2026-03-01T09:00:00" }""";
        var (path, _) = await _app.CreateFeedAsync(new { name = "a", siteId = 1, language = "en" });

        var body = await _app.Client().GetStringAsync(path);

        Assert.Contains("SUMMARY:(Untitled)", body);
    }

    [Theory]
    [InlineData("{\"name\":\"a\",\"siteId\":1,\"apiKeyUserTimeZoneId\":\"Mars/Olympus\"}")]
    [InlineData("{\"name\":\"a\",\"siteId\":1,\"language\":\"xx\"}")]
    [InlineData("{\"name\":\"a\",\"siteId\":1,\"apiKey\":\"\"}")]
    public async Task 不正な指定は400(string json)
    {
        var response = await _app.AdminClient().PostAsync(
            "/admin/api/feeds", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>秘密の鍵も共通の既定の API キーも無い構成。</summary>
public sealed class NoSecretKeyEndpointTests : IDisposable
{
    private readonly TestApp _app = new(secretKey: false, globalApiKey: false);

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 秘密の鍵が無ければAPIキーを保管できない()
    {
        var response = await _app.AdminClient().PostAsJsonAsync(
            "/admin/api/feeds", new { name = "a", siteId = 1, apiKey = "0123456789abcdef0123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(UnconfiguredSecretProtector.SettingName, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APIキーも共通の既定も無ければフィードを作れない()
    {
        var response = await _app.AdminClient().PostAsJsonAsync("/admin/api/feeds", new { name = "a", siteId = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
