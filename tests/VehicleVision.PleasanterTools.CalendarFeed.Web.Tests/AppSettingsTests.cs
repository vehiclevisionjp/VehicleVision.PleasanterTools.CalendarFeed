using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>アプリケーション設定を管理画面から編集する（Issue #40）。Questionnaire の「アプリケーション設定」に則る。</summary>
public sealed class AppSettingsTests : IDisposable
{
    private const string Cache = "CALENDARFEED_FEED_CACHESECONDS";
    private const string MaxEvents = "CALENDARFEED_FEED_MAXEVENTS";
    private readonly TestApp _app = new(globalApiKey: false);

    public void Dispose() => _app.Dispose();

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, object values) =>
        client.PutAsJsonAsync("/admin/api/settings/", new { values });

    private static JsonElement Setting(JsonElement body, string key) =>
        body.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    private async Task<JsonElement> GetAsync() =>
        await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/settings/");

    // ---- 読み取り --------------------------------------------------------------------

    [Fact]
    public async Task 一覧は全項目を返し_外部設定で固定されたものを示し_秘密の値は返さない()
    {
        await PutAsync(_app.AdminClient(), new Dictionary<string, string?> { ["CALENDARFEED_PLEASANTER_APIKEY"] = "super-secret-global-key" });

        var body = await GetAsync();

        Assert.Equal(AppSettingDefinitions.All.Select(d => d.Key), body.GetProperty("settings").EnumerateArray().Select(s => s.GetProperty("key").GetString()));
        // テストの起動設定（外部設定）で固定されているもの
        Assert.True(Setting(body, "CALENDARFEED_PLEASANTER_BASEURL").GetProperty("isFixed").GetBoolean());
        Assert.True(Setting(body, "CALENDARFEED_PUBLIC_BASEURL").GetProperty("isFixed").GetBoolean());
        Assert.False(Setting(body, Cache).GetProperty("isFixed").GetBoolean());
        Assert.Equal("300", Setting(body, Cache).GetProperty("value").GetString());
        Assert.Equal("300", Setting(body, Cache).GetProperty("defaultValue").GetString());

        // 秘密: 登録済みかどうかだけ
        var secret = Setting(body, "CALENDARFEED_PLEASANTER_APIKEY");
        Assert.True(secret.GetProperty("isSecret").GetBoolean());
        Assert.True(secret.GetProperty("hasValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, secret.GetProperty("value").ValueKind);
        Assert.DoesNotContain("super-secret-global-key", body.GetRawText());
    }

    [Fact]
    public async Task 設定の編集はAdministratorだけ()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().GetAsync("/admin/api/settings/")).StatusCode);

        foreach (var role in new[] { AdminRoles.FeedAdministrator, AdminRoles.UserAdministrator, AdminRoles.Editor, AdminRoles.Auditor })
        {
            var created = await (await _app.AdminClient().PostAsJsonAsync("/admin/api/users", new { loginId = $"u-{role}", role })).Content.ReadFromJsonAsync<JsonElement>();
            var client = _app.Client();
            await client.PostAsJsonAsync("/admin/api/invitations/accept", new { token = created.GetProperty("invitation").GetProperty("invitationToken").GetString(), password = "a brand new long password" });

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/api/settings/")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(client, new Dictionary<string, string?> { [Cache] = "60" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/admin/api/settings/mail/test", null)).StatusCode);
        }
    }

    // ---- 保存 ------------------------------------------------------------------------

    [Fact]
    public async Task 保存した設定はその場で実行中の値へ反映される()
    {
        var response = await PutAsync(_app.AdminClient(), new Dictionary<string, string?> { [Cache] = "60", [MaxEvents] = "100" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("60", Setting(await response.Content.ReadFromJsonAsync<JsonElement>(), Cache).GetProperty("value").GetString());
        var runtime = _app.Service<RuntimeSettings>().Current;
        Assert.Equal(60, runtime.Feed.CacheSeconds);
        Assert.Equal(100, runtime.Feed.MaxEvents);
    }

    [Fact]
    public async Task 不正な値は断り_1つでも不正なら何も保存しない()
    {
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { [Cache] = "0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { [Cache] = "abc" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["UNKNOWN_KEY"] = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_ADMIN_TWOFACTOR"] = "mandatory" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_TIMEZONE_DEFAULT"] = "Nowhere/None" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_PLEASANTER_BASEURL"] = "ftp://x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?>() })).StatusCode);

        // 片方が正しくても、もう片方が不正なら、どちらも保存しない
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { [MaxEvents] = "100", [Cache] = "0" })).StatusCode);
        Assert.Equal("5000", Setting(await GetAsync(), MaxEvents).GetProperty("value").GetString());
        Assert.Equal(5000, _app.Service<RuntimeSettings>().Current.Feed.MaxEvents);
    }

    [Fact]
    public async Task 外部設定で固定された設定は変えられない_同じ値なら通る()
    {
        var admin = _app.AdminClient();

        var changed = await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_PUBLIC_BASEURL"] = "https://other.test" });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Contains("fixed by external configuration", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_PUBLIC_BASEURL"] = "http://feed.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_PUBLIC_BASEURL"] = null })).StatusCode);
    }

    [Fact]
    public async Task nullで既定へ戻す()
    {
        var admin = _app.AdminClient();
        await PutAsync(admin, new Dictionary<string, string?> { [Cache] = "60" });

        var response = await PutAsync(admin, new Dictionary<string, string?> { [Cache] = null });

        Assert.Equal("300", Setting(await response.Content.ReadFromJsonAsync<JsonElement>(), Cache).GetProperty("value").GetString());
        Assert.Equal(300, _app.Service<RuntimeSettings>().Current.Feed.CacheSeconds);
        Assert.DoesNotContain(await _app.Service<IAppSettingStore>().ListAsync(), r => r.SettingKey == Cache);
    }

    [Fact]
    public async Task メールを有効にするには差出人_公開URL_送信経路の設定が要る()
    {
        // テストの起動設定は公開 URL を固定している。差出人と SMTP サーバーが無ければ断る
        var admin = _app.AdminClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_MAIL_ENABLED"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_MAIL_ENABLED"] = "true", ["CALENDARFEED_MAIL_FROM_ADDRESS"] = "noreply@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(admin, new Dictionary<string, string?> { ["CALENDARFEED_MAIL_FROM_ADDRESS"] = "not-an-address" })).StatusCode);

        var ok = await PutAsync(admin, new Dictionary<string, string?>
        {
            ["CALENDARFEED_MAIL_ENABLED"] = "true",
            ["CALENDARFEED_MAIL_FROM_ADDRESS"] = "noreply@example.com",
            ["CALENDARFEED_MAIL_SMTP_HOST"] = "smtp.example.com",
        });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(_app.Service<RuntimeSettings>().Current.Mail.IsReady);
    }

    // ---- 秘密 ------------------------------------------------------------------------

    [Fact]
    public async Task 秘密は暗号化して保管し_空なら変えず_nullで消す()
    {
        var admin = _app.AdminClient();
        const string Key = "CALENDARFEED_PLEASANTER_APIKEY";

        await PutAsync(admin, new Dictionary<string, string?> { [Key] = "global-secret-key-123" });

        var record = (await _app.Service<IAppSettingStore>().ListAsync()).Single(r => r.SettingKey == Key);
        Assert.True(record.IsSecret);
        Assert.DoesNotContain("global-secret-key-123", record.Value);
        Assert.Equal("global-secret-key-123", _app.Service<RuntimeSettings>().Current.Pleasanter.ApiKey);

        // 空は「変えない」
        await PutAsync(admin, new Dictionary<string, string?> { [Key] = "", [Cache] = "60" });
        Assert.Equal("global-secret-key-123", _app.Service<RuntimeSettings>().Current.Pleasanter.ApiKey);

        // null は消す
        await PutAsync(admin, new Dictionary<string, string?> { [Key] = null });
        Assert.Null(_app.Service<RuntimeSettings>().Current.Pleasanter.ApiKey);
        Assert.False(Setting(await GetAsync(), Key).GetProperty("hasValue").GetBoolean());
    }

    [Fact]
    public async Task 共通のAPIキーを設定すると_APIキーの無いフィードがそれで動く()
    {
        using var app = new TestApp(globalApiKey: false);
        await app.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { ["CALENDARFEED_PLEASANTER_APIKEY"] = "from-settings-key" } });

        var (path, _) = await app.CreateFeedAsync();
        var response = await app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ApiKey\":\"from-settings-key\"", app.Pleasanter.LastRequestBody);
    }

    [Fact]
    public async Task 秘密の鍵が無ければ秘密は保存できない()
    {
        using var app = new TestApp(secretKey: false);

        var response = await app.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { ["CALENDARFEED_MAIL_SMTP_PASSWORD"] = "pw" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CALENDARFEED_SECRET_KEY", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // ---- 永続・他のインスタンス ---------------------------------------------------------

    [Fact]
    public async Task 再起動しても保存した設定が使われる()
    {
        string db;
        string key;
        using (var first = new TestApp(globalApiKey: false))
        {
            first.KeepDb = true;
            db = first.DbPath;
            key = first.SecretKeyValue;
            await first.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [Cache] = "77", ["CALENDARFEED_PLEASANTER_APIKEY"] = "persisted-key" } });
        }

        try
        {
            using var second = new TestApp(globalApiKey: false, existingDb: db, secretKeyValue: key, setUpAdmin: false);
            var runtime = second.Service<RuntimeSettings>().Current;

            Assert.Equal(77, runtime.Feed.CacheSeconds);
            Assert.Equal("persisted-key", runtime.Pleasanter.ApiKey);
            Assert.Equal("77", Setting(await second.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/settings/"), Cache).GetProperty("value").GetString());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public async Task 他のインスタンスの保存は読み直しで取り込む()
    {
        // 別のインスタンスが DB へ直接書いたことを模す
        await _app.Service<IAppSettingStore>().SaveAsync(Cache, "45", false, null, DateTimeOffset.UtcNow);
        Assert.Equal(300, _app.Service<RuntimeSettings>().Current.Feed.CacheSeconds);

        await _app.Service<AppSettingsMonitor>().RefreshAsync(CancellationToken.None);

        Assert.Equal(45, _app.Service<RuntimeSettings>().Current.Feed.CacheSeconds);
    }

    [Fact]
    public async Task 復号できない秘密は起動を止めず_空として扱い_画面で再入力を求める()
    {
        string db;
        using (var first = new TestApp(globalApiKey: false))
        {
            first.KeepDb = true;
            db = first.DbPath;
            await first.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { ["CALENDARFEED_PLEASANTER_APIKEY"] = "lost-key" } });
        }

        try
        {
            // 別の鍵で開き直す（鍵を失った・変えた）
            using var second = new TestApp(globalApiKey: false, existingDb: db, secretKeyValue: SecretProtector.GenerateKey(), setUpAdmin: false);
            Assert.Null(second.Service<RuntimeSettings>().Current.Pleasanter.ApiKey);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    // ---- 監査 ------------------------------------------------------------------------

    [Fact]
    public async Task 変えた設定の名前だけを監査ログへ残し_値と秘密は残さない()
    {
        var admin = _app.AdminClient();
        await PutAsync(admin, new Dictionary<string, string?> { [Cache] = "61", ["CALENDARFEED_PLEASANTER_APIKEY"] = "audit-secret-key" });
        await PutAsync(admin, new Dictionary<string, string?> { [Cache] = "61" });

        var log = (await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.SettingsUpdated })).ToList();

        Assert.Equal(2, log.Count);
        Assert.Contains(log, e => e.Detail!.Contains(Cache) && e.Detail.Contains("CALENDARFEED_PLEASANTER_APIKEY"));
        Assert.Contains(log, e => e.Detail == "no-change");
        Assert.DoesNotContain(log, e => e.Detail!.Contains("61") && !e.Detail.Contains(Cache) || e.Detail!.Contains("audit-secret-key"));
    }

    // ---- メール ----------------------------------------------------------------------

    [Fact]
    public async Task 画面で有効にしたメール設定で招待メールが送られ_試し送信もできる()
    {
        var admin = _app.AdminClient();
        var test = await admin.PostAsync("/admin/api/settings/mail/test", null);
        Assert.Equal("mail-disabled", (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        await PutAsync(admin, new Dictionary<string, string?>
        {
            ["CALENDARFEED_MAIL_ENABLED"] = "true",
            ["CALENDARFEED_MAIL_FROM_ADDRESS"] = "noreply@example.com",
            ["CALENDARFEED_MAIL_SMTP_HOST"] = "smtp.example.com",
            ["CALENDARFEED_MAIL_SMTP_PASSWORD"] = "smtp-secret",
        });

        // 自分のログイン ID がメールアドレスでなければ、試し送信は積めない
        Assert.Equal("login-id-not-email", (await (await admin.PostAsync("/admin/api/settings/mail/test", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        var invited = await (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "bob@example.com", role = AdminRoles.Editor })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(invited.GetProperty("invitation").GetProperty("mailSent").GetBoolean());

        Assert.Equal(SendOutcome.Sent, await _app.Service<MailSender>().SendOnceAsync());
        Assert.Equal("bob@example.com", Assert.Single(_app.MailTransport.Sent).ToAddress);
    }
}
