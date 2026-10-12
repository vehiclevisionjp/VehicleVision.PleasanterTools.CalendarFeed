using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

public sealed class FeedEndpointTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"calendarfeed-{Guid.NewGuid():N}.db");
    private readonly FakePleasanter _pleasanter = new();
    private readonly WebApplicationFactory<Program> _factory;

    public FeedEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:CalendarFeed", $"Data Source={_db};Pooling=False");
            builder.UseSetting("Pleasanter:BaseUrl", "https://pleasanter.test");
            builder.UseSetting("Pleasanter:ApiKey", "secret-api-key");
            builder.UseSetting("Pleasanter:ApiKeyUserTimeZoneId", "UTC");
            builder.UseSetting("Admin:ApiKey", AdminKey);
            builder.UseSetting("PublicBaseUrl", "https://feed.test");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<Pleasanter.PleasanterClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _pleasanter));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminKey);
        return client;
    }

    private async Task<string> CreateFeedAsync()
    {
        var response = await AdminClient().PostAsJsonAsync(
            "/admin/api/feeds", new { name = "共有予定", siteId = 123 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("url").GetString()!;
    }

    [Fact]
    public async Task 購読URLからicsが取れてAPIキーは本文に載せて送られる()
    {
        var url = await CreateFeedAsync();
        Assert.StartsWith("https://feed.test/feeds/", url);

        var response = await _factory.CreateClient().GetAsync(new Uri(url).AbsolutePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SUMMARY:打合せ", body);
        Assert.Contains("DTSTART:20260301T090000Z", body);
        Assert.DoesNotContain("secret-api-key", body);

        Assert.Equal("https://pleasanter.test/api/items/123/Get", _pleasanter.LastRequestUri);
        Assert.Contains("\"ApiKey\":\"secret-api-key\"", _pleasanter.LastRequestBody);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task 同じ内容ならETagで304を返しキャッシュ中はPleasanterを叩かない()
    {
        var path = new Uri(await CreateFeedAsync()).AbsolutePath;
        var client = _factory.CreateClient();

        var first = await client.GetAsync(path);
        var etag = first.Headers.ETag!.Tag;
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(1, _pleasanter.CallCount);
    }

    [Fact]
    public async Task 不明なトークンと失効したトークンは404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/feeds/unknown.ics")).StatusCode);

        var url = await CreateFeedAsync();
        var list = await AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/feeds/");
        var id = list[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await AdminClient().DeleteAsync($"/admin/api/feeds/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync(new Uri(url).AbsolutePath)).StatusCode);
    }

    [Fact]
    public async Task Pleasanterが一時的に落ちていれば503()
    {
        var path = new Uri(await CreateFeedAsync()).AbsolutePath;
        _pleasanter.StatusCode = HttpStatusCode.BadGateway;

        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task 管理キーが無い要求は401で一覧にトークンを含めない()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/admin/api/feeds/")).StatusCode);

        await CreateFeedAsync();
        var raw = await AdminClient().GetStringAsync("/admin/api/feeds/");
        Assert.DoesNotContain("/feeds/", raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 不正な列名は400()
    {
        var response = await AdminClient().PostAsJsonAsync(
            "/admin/api/feeds",
            new { name = "x", siteId = 1, mapping = new { summaryColumn = "Title; DROP" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Pleasanter の Get API の応答を真似る。</summary>
    private sealed class FakePleasanter : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public int CallCount { get; private set; }

        public string? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri!.ToString();
            LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(
                    """
                    { "StatusCode": 200, "Response": { "Offset": 0, "PageSize": 200, "TotalCount": 1,
                      "Data": [ { "IssueId": 5, "Ver": 1, "Title": "打合せ", "Body": "議題",
                                  "StartTime": "2026-03-01T09:00:00", "CompletionTime": "2026-03-01T10:00:00" } ] } }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
