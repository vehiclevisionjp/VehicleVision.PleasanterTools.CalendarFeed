using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>テストで進められる時計。</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan span) => Now += span;
}

/// <summary>テスト用にアプリを起動する。Pleasanter は <see cref="FakePleasanter"/> が真似る。</summary>
internal sealed class TestApp : IDisposable
{
    public const string AdminLoginId = "admin";
    public const string AdminPassword = "correct horse battery staple";
    public const string GlobalApiKey = "global-api-key";

    private readonly string _db;
    private readonly bool _ownsDb;
    private readonly WebApplicationFactory<Program> _factory;
    private HttpClient? _admin;

    /// <param name="existingDb">
    /// すでにある DB のパス。**同じ DB を別の鍵で開く**ために使う（秘密の鍵を失った・変えた状態の再現）。
    /// その DB は呼び出し側が消す。
    /// </param>
    /// <param name="secretKeyValue">秘密の鍵そのもの。省略すると、毎回新しい鍵を作る。</param>
    public TestApp(
        bool secretKey = true,
        bool globalApiKey = true,
        string? twoFactor = null,
        bool setUpAdmin = true,
        string? existingDb = null,
        string? secretKeyValue = null,
        string? retentionDays = null,
        bool mail = false,
        string? forwardedNetworks = null)
    {
        _ownsDb = existingDb is null;
        _db = existingDb ?? Path.Combine(Path.GetTempPath(), $"calendarfeed-{Guid.NewGuid():N}.db");
        DbPath = _db;
        SecretKeyValue = secretKeyValue ?? SecretProtector.GenerateKey();

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
                    // cookie の Secure を付けない（テストの HttpClient は http で話す）。HTTPS の判定は AdminAuthEndpointTests で見る
                    [AppSettings.PublicBaseUrl] = "http://feed.test",
                };
                if (globalApiKey)
                {
                    settings[AppSettings.PleasanterApiKey] = GlobalApiKey;
                }

                if (secretKey)
                {
                    settings[AppSettings.SecretKey] = SecretKeyValue;
                }

                // **送信ワーカーは動かさない**（テストが 1 通ずつ SendOnceAsync で進める）。メールは設定したときだけ有効にする
                settings[MailSenderOptions.WorkerKey] = "false";
                if (mail)
                {
                    settings["CALENDARFEED_MAIL_ENABLED"] = "true";
                    settings["CALENDARFEED_MAIL_SMTP_HOST"] = "smtp.test";
                    settings["CALENDARFEED_MAIL_FROM_ADDRESS"] = "noreply@feed.test";
                }

                if (retentionDays is not null)
                {
                    settings[AppSettings.AuditLogRetentionDays] = retentionDays;
                }

                if (forwardedNetworks is not null)
                {
                    settings[AppSettings.ForwardedProxyNetworks] = forwardedNetworks;
                }

                if (twoFactor is not null)
                {
                    settings[AppSettings.AdminTwoFactor] = twoFactor;
                }

                config.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<Pleasanter.PleasanterClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => Pleasanter);
                services.AddSingleton<TimeProvider>(Time);
                services.AddSingleton<IMailTransport>(MailTransport);
                services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            });
        });

        SetUpAdmin = setUpAdmin;
    }

    public FakePleasanter Pleasanter { get; } = new();

    public ManualTime Time { get; } = new();

    /// <summary>送ったメールを受け取る（実際には送らない）。</summary>
    public FakeMailTransport MailTransport { get; } = new();

    /// <summary>Dispose で DB を消さない（同じ DB を別のアプリで開き直すテスト用）。</summary>
    public bool KeepDb { get; set; }

    /// <summary>DB のファイルのパス。</summary>
    public string DbPath { get; }

    /// <summary>秘密の鍵。</summary>
    public string SecretKeyValue { get; }

    private bool SetUpAdmin { get; }

    /// <summary>ログインしていない利用者（ブラウザ）。呼ぶたびに別の cookie の入れ物になる。</summary>
    public HttpClient Client() => _factory.CreateClient();

    /// <summary>アプリ内のサービス（監査ログの保管先などを直接読むため）。</summary>
    public T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    /// <summary>
    /// ログイン済みの管理者。**最初の管理者を作ってログインする**（2 要素が要らない構成）。同じ入れ物を使い回す。
    /// </summary>
    public HttpClient AdminClient()
    {
        if (_admin is not null)
        {
            return _admin;
        }

        var client = _factory.CreateClient();
        if (SetUpAdmin)
        {
            Assert.Equal(HttpStatusCode.Created, client.PostAsJsonAsync("/admin/api/setup", new { loginId = AdminLoginId, password = AdminPassword }).GetAwaiter().GetResult().StatusCode);
        }

        var login = client.PostAsJsonAsync("/admin/api/login", new { loginId = AdminLoginId, password = AdminPassword }).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return _admin = client;
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
        if (_ownsDb && !KeepDb)
        {
            File.Delete(_db);
        }
    }
}

/// <summary>
/// テストの接続元 IP を、ヘッダ <c>X-Test-Remote-Ip</c> で決める（TestServer は接続元を持たない）。
/// **パイプラインの先頭**に入るので、転送ヘッダの処理より前の <c>RemoteIpAddress</c> を作る。
/// </summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((http, nextMiddleware) =>
        {
            if (http.Request.Headers[Header].FirstOrDefault() is { } ip)
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            }

            return nextMiddleware(http);
        });
        next(app);
    };
}

/// <summary>メールの送信を真似る。送った中身を取っておき、<see cref="Failure"/> があればそれを投げる。</summary>
internal sealed class FakeMailTransport : IMailTransport
{
    public List<OutgoingMail> Sent { get; } = [];

    /// <summary>送るときに投げる失敗（<c>null</c> なら送れる）。</summary>
    public Exception? Failure { get; set; }

    public Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Sent.Add(mail);
        return Task.CompletedTask;
    }
}

/// <summary>Pleasanter の Get API の応答を真似る。</summary>
internal sealed class FakePleasanter : HttpMessageHandler
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public int CallCount { get; private set; }

    public string? LastRequestUri { get; private set; }

    public string? LastRequestBody { get; private set; }

    /// <summary>受けたリクエスト本文（順に）。</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>表示名への解決（KeyValues ＋ Text）の要求に返す行（JSON）。</summary>
    public string ResolvedRow { get; set; } = """{ "IssueId": "5", "ClassA": "会議", "ClassB": "第一会議室" }""";

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
        Bodies.Add(LastRequestBody);

        // 表示名への解決の要求（View.ApiDataType = KeyValues）には、解決済みの行を返す
        var row = LastRequestBody.Contains("\"ApiDataType\"", StringComparison.Ordinal) ? ResolvedRow : Record;

        return new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(
                $$"""{ "StatusCode": 200, "Response": { "Offset": 0, "PageSize": 200, "TotalCount": 1, "Data": [ {{row}} ] } }""",
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

/// <summary>件名のテンプレート・書式・表示名への解決（Issue #5）。</summary>
public sealed class FeedTemplateEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public FeedTemplateEndpointTests() =>
        _app.Pleasanter.Record = """
            { "IssueId": 5, "Ver": 1, "Title": "見積", "Body": "議題",
              "StartTime": "2026-03-01T09:00:00", "CompletionTime": "2026-03-01T10:00:00",
              "ClassHash": { "ClassA": "1", "ClassB": "2" },
              "DateHash": { "DateA": "2026-03-05T14:30:00" }, "NumHash": { "NumA": 1234.5 } }
            """;

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 選択肢の列を使うときだけ表示名への解決を別に取り件名と場所へ入る()
    {
        var (path, _) = await _app.CreateFeedAsync(new
        {
            name = "a",
            siteId = 1,
            mapping = new
            {
                summaryTemplate = "{ClassA}：{Title}（{DateA:yyyy/MM/dd HH:mm} {NumA:#,##0.00}）",
                locationColumn = "ClassB",
            },
        });

        var body = await _app.Client().GetStringAsync(path);

        Assert.Contains("SUMMARY:会議：見積（2026/03/05 14:30 1\\,234.50）", body);
        Assert.Contains("LOCATION:第一会議室", body);
        Assert.Equal(2, _app.Pleasanter.CallCount);

        // 2 回目は KeyValues ＋ Text で、必要な列と ID だけを求める。日付・数値は求めない
        var second = _app.Pleasanter.Bodies[1];
        Assert.Contains("\"ApiDataType\":1", second);
        Assert.Contains("\"ApiColumnValueDisplayType\":2", second);
        Assert.Contains("\"GridColumns\":[\"IssueId\",\"ClassA\",\"ClassB\"]", second);
        Assert.DoesNotContain("DateA", second);
    }

    [Fact]
    public async Task 解決が要る列が無ければ2回目の取得はしない()
    {
        var (path, _) = await _app.CreateFeedAsync(new
        {
            name = "a",
            siteId = 1,
            mapping = new { summaryTemplate = "{Title} {DateA:yyyy/MM/dd}" },
        });

        var body = await _app.Client().GetStringAsync(path);

        Assert.Contains("SUMMARY:見積 2026/03/05", body);
        Assert.Equal(1, _app.Pleasanter.CallCount);
    }

    [Fact]
    public async Task 既定の件名では2回目の取得はしない()
    {
        var (path, _) = await _app.CreateFeedAsync();

        await _app.Client().GetStringAsync(path);

        Assert.Equal(1, _app.Pleasanter.CallCount);
    }

    [Fact]
    public async Task 突き合わせられないレコードは生の値のまま出す()
    {
        _app.Pleasanter.ResolvedRow = """{ "IssueId": "999", "ClassA": "別のレコード" }""";
        var (path, _) = await _app.CreateFeedAsync(new
        {
            name = "a",
            siteId = 1,
            mapping = new { summaryTemplate = "{ClassA}：{Title}" },
        });

        var body = await _app.Client().GetStringAsync(path);

        Assert.Contains("SUMMARY:1：見積", body);
    }

    [Theory]
    [InlineData("{\"summaryTemplate\":\"{DateA}\"}")]
    [InlineData("{\"summaryTemplate\":\"{Title\"}")]
    [InlineData("{\"summaryTemplate\":\"固定\"}")]
    [InlineData("{\"locationColumn\":\"NumA\"}")]
    [InlineData("{\"formats\":{\"ClassA\":\"yyyy\"}}")]
    public async Task 書式やテンプレートが不正なら400(string mapping)
    {
        var response = await _app.AdminClient().PostAsync(
            "/admin/api/feeds",
            new StringContent(
                $"{{\"name\":\"a\",\"siteId\":1,\"mapping\":{mapping}}}",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>日付が文字の列に入っているときの開始・終了（Issue #14）。</summary>
public sealed class FeedTextDateEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public FeedTextDateEndpointTests() =>
        _app.Pleasanter.Record = """
            { "IssueId": 5, "Ver": 1, "Title": "出張",
              "DescriptionHash": { "DescriptionA": "2026/03/05", "DescriptionB": "2026/03/06" } }
            """;

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 文字の列の日付を書式で読んで終日の予定にする()
    {
        var (path, _) = await _app.CreateFeedAsync(new
        {
            name = "a",
            siteId = 1,
            mapping = new
            {
                startColumn = "DescriptionA",
                startFormat = "yyyy/MM/dd",
                endColumn = "DescriptionB",
                endFormat = "yyyy/MM/dd",
            },
        });

        var body = await _app.Client().GetStringAsync(path);

        Assert.Contains("DTSTART;VALUE=DATE:20260305", body);
        Assert.Contains("DTEND;VALUE=DATE:20260307", body);
    }

    [Theory]
    [InlineData("{\"startColumn\":\"DescriptionA\"}")]
    [InlineData("{\"startColumn\":\"DateA\",\"startFormat\":\"yyyy/MM/dd\"}")]
    [InlineData("{\"startColumn\":\"DescriptionA\",\"startFormat\":\"%\"}")]
    [InlineData("{\"startColumn\":\"NumA\",\"startFormat\":\"yyyy\"}")]
    public async Task 書式と列の組み合わせが不正なら400(string mapping)
    {
        var response = await _app.AdminClient().PostAsync(
            "/admin/api/feeds",
            new StringContent(
                $"{{\"name\":\"a\",\"siteId\":1,\"mapping\":{mapping}}}",
                System.Text.Encoding.UTF8,
                "application/json"));

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
