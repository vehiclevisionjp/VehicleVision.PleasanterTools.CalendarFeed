using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>
/// DotNetClient の上に組んだ <see cref="PleasanterGateway"/> の確認。
/// 失敗の分類・本文の <c>StatusCode</c>・<c>BaseUrl</c> の検査・リダイレクトを辿らないこと・API キーと URL を出さないこと。
/// 成功時の取得（ページ送り・表示名の解決）は <see cref="FeedEndpointTests"/> が通して確かめている。
/// </summary>
public sealed class PleasanterGatewayTests
{
    private const string ApiKey = "test-api-key-not-a-real-secret";

    private sealed class Options(PleasanterOptions current) : IPleasanterOptionsSource
    {
        public PleasanterOptions Current { get; } = current;
    }

    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string OkBody =
        """{ "StatusCode": 200, "Response": { "Offset": 0, "PageSize": 200, "TotalCount": 1, "Data": [ { "IssueId": 1 } ] } }""";

    private static PleasanterGateway Create(HttpMessageHandler handler, string baseUrl = "https://pleasanter.test")
        => new(
            new HttpClient(handler),
            new Options(new PleasanterOptions { BaseUrl = baseUrl, ApiKeyUserTimeZoneId = "Asia/Tokyo" }));

    private static Task<PleasanterPage> GetAsync(PleasanterGateway gateway)
        => gateway.GetRecordsAsync(ApiKey, 123, 0, 200, null);

    private static async Task<PleasanterException> FailureOf(PleasanterGateway gateway)
        => await Assert.ThrowsAsync<PleasanterException>(() => GetAsync(gateway));

    [Fact]
    public async Task 成功すると_APIキーは本文にだけ載り_URLのパスは_api_items_サイトID_Get()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));

        var page = await GetAsync(Create(stub, "https://pleasanter.test/"));

        Assert.Equal(1, page.TotalCount);
        var (uri, body) = Assert.Single(stub.Requests);
        Assert.Equal("https://pleasanter.test/api/items/123/Get", uri.ToString());
        Assert.DoesNotContain(ApiKey, uri.ToString());
        Assert.Equal(ApiKey, JsonNode.Parse(body)!["ApiKey"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.NotFound, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.TooManyRequests, PleasanterErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.RequestTimeout, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.TemporaryRedirect, PleasanterErrorKind.Permanent)]
    public async Task HTTPの状態を失敗の種類へ分類する(HttpStatusCode status, PleasanterErrorKind expected)
    {
        var failure = await FailureOf(Create(new Stub((_, _) => Json(status, "{}"))));

        Assert.Equal(expected, failure.Kind);
        Assert.Equal((int)status, failure.StatusCode);
    }

    [Theory]
    [InlineData(401, PleasanterErrorKind.Unauthorized)]
    [InlineData(403, PleasanterErrorKind.Permanent)]
    [InlineData(404, PleasanterErrorKind.Permanent)]
    [InlineData(429, PleasanterErrorKind.RateLimited)]
    [InlineData(500, PleasanterErrorKind.Transient)]
    [InlineData(503, PleasanterErrorKind.Transient)]
    [InlineData(408, PleasanterErrorKind.Transient)]
    [InlineData(400, PleasanterErrorKind.Permanent)]
    [InlineData(302, PleasanterErrorKind.Permanent)]
    public async Task HTTPが200でも_本文のStatusCodeが200以外なら失敗にする(int bodyStatus, PleasanterErrorKind expected)
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, $$"""{ "StatusCode": {{bodyStatus}}, "Message": "secret detail" }"""));

        var failure = await FailureOf(Create(stub));

        Assert.Equal(expected, failure.Kind);
        Assert.Equal(bodyStatus, failure.StatusCode);
        Assert.DoesNotContain("secret detail", failure.Message);
    }

    [Fact]
    public async Task HTTPが200以外なら_本文のStatusCodeではなくHTTPの状態で分類する()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.InternalServerError, """{ "StatusCode": 404 }"""));

        var failure = await FailureOf(Create(stub));

        Assert.Equal(PleasanterErrorKind.Transient, failure.Kind);
        Assert.Equal(500, failure.StatusCode);
    }

    [Theory]
    [InlineData("""{ "StatusCode": "x", "Response": { "Data": [] } }""")]
    [InlineData("""{ "Response": { "Data": [] } }""")]
    public async Task 本文のStatusCodeが無い_または整数でないときは_失敗にしない(string body)
    {
        var page = await GetAsync(Create(new Stub((_, _) => Json(HttpStatusCode.OK, body))));

        Assert.Empty(page.Records);
    }

    private static PleasanterGateway CreateInsecure(HttpMessageHandler handler, string baseUrl)
        => new(
            new HttpClient(handler),
            new Options(new PleasanterOptions { BaseUrl = baseUrl, ApiKeyUserTimeZoneId = "Asia/Tokyo", AllowInsecureHttp = true }));

    [Theory]
    [InlineData("http://pleasanter:8080")]
    [InlineData("http://pleasanter.example.com")]
    public async Task AllowInsecureHttpを有効にすると_ループバック以外のhttpも使える(string baseUrl)
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));

        await GetAsync(CreateInsecure(stub, baseUrl));

        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData("ftp://pleasanter.example.com")]
    [InlineData("http://user:pass@pleasanter:8080")]
    [InlineData("http://pleasanter:8080?x=1")]
    [InlineData("http://pleasanter:8080#frag")]
    public async Task AllowInsecureHttpを有効にしても_資格情報_クエリ_フラグメント_他のスキームは拒否する(string baseUrl)
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));

        var failure = await FailureOf(CreateInsecure(stub, baseUrl));

        Assert.Equal(PleasanterErrorKind.Permanent, failure.Kind);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 環境変数_PLEASANTER_ALLOW_INSECURE_HTTP_は_true_のときだけ有効(string? value, bool expected)
    {
        Assert.Equal(expected, PleasanterOptions.ParseAllowInsecureHttp(value));
    }

    [Theory]
    [InlineData("<html>proxy error</html>")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public async Task 応答がJSONのオブジェクトでなければ_一時的な失敗(string body)
    {
        var failure = await FailureOf(Create(new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));

        Assert.Equal(PleasanterErrorKind.Transient, failure.Kind);
    }

    [Fact]
    public async Task Response_Dataが無ければ_恒久的な失敗()
    {
        var failure = await FailureOf(Create(new Stub((_, _) => Json(HttpStatusCode.OK, """{ "StatusCode": 200, "Response": {} }"""))));

        Assert.Equal(PleasanterErrorKind.Permanent, failure.Kind);
    }

    [Fact]
    public async Task 接続できなければ_一時的な失敗_URLもAPIキーも出さない()
    {
        var stub = new Stub((_, _) => throw new HttpRequestException("connect failed https://pleasanter.test/ " + ApiKey));

        var failure = await FailureOf(Create(stub));

        Assert.Equal(PleasanterErrorKind.Transient, failure.Kind);
        Assert.Null(failure.StatusCode);
        Assert.DoesNotContain("pleasanter.test", failure.Message);
        Assert.DoesNotContain(ApiKey, failure.Message);
    }

    [Fact]
    public async Task 呼び出し側の取り消しは_そのまま伝わる()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gateway = Create(new Stub((_, _) => Json(HttpStatusCode.OK, OkBody)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetRecordsAsync(ApiKey, 1, 0, 10, null, cts.Token));
    }

    [Fact]
    public async Task URLが空なら_通信せずに恒久的な失敗()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));

        var failure = await FailureOf(Create(stub, ""));

        Assert.Equal(PleasanterErrorKind.Permanent, failure.Kind);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("http://pleasanter.example.com")]
    [InlineData("http://pleasanter:8080")]
    [InlineData("ftp://pleasanter.example.com")]
    [InlineData("https://user:pass@pleasanter.example.com")]
    [InlineData("https://pleasanter.example.com?x=1")]
    [InlineData("https://pleasanter.example.com#frag")]
    [InlineData("pleasanter.example.com")]
    public async Task 使えないURLは_通信せずに恒久的な失敗_URLを出さない(string baseUrl)
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));

        var failure = await FailureOf(Create(stub, baseUrl));

        Assert.Equal(PleasanterErrorKind.Permanent, failure.Kind);
        Assert.Empty(stub.Requests);
        Assert.DoesNotContain("pass", failure.Message);
        Assert.DoesNotContain("example.com", failure.Message);
        Assert.DoesNotContain(ApiKey, failure.Message);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("https://pleasanter.example.com/pleasanter")]
    public async Task ループバックのhttpと_パス付きのhttpsは使える(string baseUrl)
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));

        await GetAsync(Create(stub, baseUrl));

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task 型付きの呼び出しをしないので_版の問い合わせ_GetLicenseInfo_は送らない()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));
        var gateway = Create(stub);

        await GetAsync(gateway);
        await GetAsync(gateway);

        Assert.All(stub.Requests, r => Assert.DoesNotContain("GetLicenseInfo", r.Uri.ToString()));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task 本番の経路は_リダイレクトを辿らず_APIキーを転送先へ再送しない()
    {
        // 転送先（別のホスト相当）が受けた要求を数える
        var targetHits = 0;
        await using var target = await StartAsync(_ => targetHits++);
        await using var origin = await StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = $"{target.Url}/api/items/123/Get";
        });

        using var client = new HttpClient(VehicleVision.PleasanterTools.DotNetClient.PleasanterHttp.CreateHandler());
        var gateway = new PleasanterGateway(
            client,
            new Options(new PleasanterOptions { BaseUrl = origin.Url, ApiKeyUserTimeZoneId = "Asia/Tokyo" }));

        var failure = await FailureOf(gateway);

        Assert.Equal(PleasanterErrorKind.Permanent, failure.Kind);
        Assert.Equal(307, failure.StatusCode);
        Assert.Equal(0, targetHits);
    }

    [Fact]
    public async Task レコードの取得_作成_更新_削除は_APIキーとApiVersionを本文に載せ_パスは_api_items_ID_操作()
    {
        var stub = new Stub((request, _) => Json(
            HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.EndsWith("/Get", StringComparison.Ordinal)
                ? """{ "StatusCode": 200, "Response": { "Data": [ { "IssueId": 5 } ] } }"""
                : """{ "Id": 6, "StatusCode": 200, "Message": "ok" }"""));
        var gateway = Create(stub);

        var record = await gateway.GetRecordAsync(ApiKey, 5);
        var id = await gateway.CreateRecordAsync(ApiKey, 123, new JsonObject { ["Title"] = "予定", ["Body"] = null });
        await gateway.UpdateRecordAsync(ApiKey, 5, new JsonObject { ["Title"] = "更新" });
        await gateway.DeleteRecordAsync(ApiKey, 5);

        Assert.Equal(5, record["IssueId"]!.GetValue<int>());
        Assert.Equal(6, id);
        Assert.Equal(
            ["/api/items/5/Get", "/api/items/123/Create", "/api/items/5/Update", "/api/items/5/Delete"],
            stub.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(stub.Requests, r =>
        {
            var body = JsonNode.Parse(r.Body)!.AsObject();
            Assert.Equal(ApiKey, body["ApiKey"]!.GetValue<string>());
            Assert.Equal(1.1m, body["ApiVersion"]!.GetValue<decimal>());
            Assert.DoesNotContain(ApiKey, r.Uri.ToString());
        });

        // null の項目（値を消す指定）も、そのまま送る
        Assert.True(JsonNode.Parse(stub.Requests[1].Body)!.AsObject().ContainsKey("Body"));
    }

    [Fact]
    public async Task レコードが無ければ_404の恒久的な失敗_作成の応答にIDが無ければ恒久的な失敗()
    {
        var empty = Create(new Stub((_, _) => Json(HttpStatusCode.OK, """{ "StatusCode": 200, "Response": { "Data": [] } }""")));
        var notFound = await Assert.ThrowsAsync<PleasanterException>(() => empty.GetRecordAsync(ApiKey, 5));
        Assert.Equal(PleasanterErrorKind.Permanent, notFound.Kind);
        Assert.Equal(404, notFound.StatusCode);

        var noId = Create(new Stub((_, _) => Json(HttpStatusCode.OK, """{ "StatusCode": 200 }""")));
        var failure = await Assert.ThrowsAsync<PleasanterException>(() => noId.CreateRecordAsync(ApiKey, 123, []));
        Assert.Equal(PleasanterErrorKind.Permanent, failure.Kind);
    }

    [Fact]
    public async Task 書き込みも_使えないURLは通信せずに失敗_本文のStatusCodeを見る()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, """{ "StatusCode": 403, "Message": "secret detail" }"""));

        var rejected = await Assert.ThrowsAsync<PleasanterException>(() => Create(stub).UpdateRecordAsync(ApiKey, 5, []));
        Assert.Equal(PleasanterErrorKind.Permanent, rejected.Kind);
        Assert.Equal(403, rejected.StatusCode);
        Assert.DoesNotContain("secret detail", rejected.Message);

        var insecure = new Stub((_, _) => Json(HttpStatusCode.OK, OkBody));
        var blocked = await Assert.ThrowsAsync<PleasanterException>(() => Create(insecure, "http://pleasanter:8080").DeleteRecordAsync(ApiKey, 5));
        Assert.Equal(PleasanterErrorKind.Permanent, blocked.Kind);
        Assert.Empty(insecure.Requests);
    }

    private static async Task<LoopbackServer> StartAsync(Action<Microsoft.AspNetCore.Http.HttpContext> handle)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context =>
        {
            handle(context);
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        return new LoopbackServer(app, url.TrimEnd('/'));
    }

    private sealed class LoopbackServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
