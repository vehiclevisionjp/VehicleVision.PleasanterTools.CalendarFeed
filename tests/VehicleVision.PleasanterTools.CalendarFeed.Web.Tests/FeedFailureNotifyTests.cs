using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>フィードの取得の失敗が続いたときの、メールでの通知（Issue #48）。</summary>
public sealed class FeedFailureNotifyTests : IDisposable
{
    private readonly TestApp _app = new(mail: true);

    public void Dispose() => _app.Dispose();

    private async Task EnableAsync(bool notify = true)
    {
        var response = await _app.AdminClient().PutAsJsonAsync("/admin/api/settings/", new
        {
            values = new Dictionary<string, string?>
            {
                ["CALENDARFEED_FEED_FAILURENOTIFY"] = notify ? "true" : "false",
                ["CALENDARFEED_FEED_FAILURENOTIFYTHRESHOLD"] = "3",
            },
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<AdminUser> AddUserAsync(string loginId, string role, string language = "ja", bool disabled = false)
    {
        var users = _app.Service<IAdminUserStore>();
        var user = (await users.CreateAsync(loginId, PasswordHasher.Hash("x-unknowable-password"), role, language, _app.Time.Now))!;
        if (disabled)
        {
            await users.SetDisabledAsync(user.Id, true);
        }

        return user;
    }

    /// <summary>フィードを取得する（Pleasanter がどう答えるかは呼ぶ前に決める）。キャッシュを避けるため、毎回別のフィードの取得になるものは使わない。</summary>
    private async Task FetchAsync(string path) => await _app.Client().GetAsync(path);

    private async Task<List<VehicleVision.PleasanterTools.Core.Mail.OutgoingMail>> DrainAsync()
    {
        var sender = _app.Service<MailSender>();
        while (await sender.SendOnceAsync() == SendOutcome.Sent)
        {
        }

        return [.. _app.MailTransport.Sent];
    }

    [Fact]
    public async Task 閾値に届いたとき1通だけ知らせ_続く間は再通知しない_間隔が過ぎたらもう一度知らせる()
    {
        await EnableAsync();
        await AddUserAsync("editor@example.com", AdminRoles.FeedAdministrator);
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;

        await FetchAsync(path);
        await FetchAsync(path);
        Assert.Empty(await DrainAsync());

        await FetchAsync(path);
        var first = Assert.Single(await DrainAsync());
        Assert.Equal("editor@example.com", first.ToAddress);
        Assert.Contains("共有予定", first.Subject);
        Assert.Contains("3 回続けて", first.Body);

        // 続く間は、間隔の内は再通知しない
        await FetchAsync(path);
        await FetchAsync(path);
        Assert.Single(await DrainAsync());

        // 間隔（既定 24 時間）が過ぎたら、もう一度
        _app.Time.Advance(TimeSpan.FromHours(25));
        await FetchAsync(path);
        Assert.Equal(2, (await DrainAsync()).Count);
    }

    [Fact]
    public async Task 回復したら1通だけ知らせ_失敗を知らせていなければ知らせない()
    {
        await EnableAsync();
        await AddUserAsync("editor@example.com", AdminRoles.FeedAdministrator, "en");
        var (path, feed) = await _app.CreateFeedAsync();

        // 1 回だけ失敗して回復しても、失敗を知らせていないので回復も知らせない
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;
        await FetchAsync(path);
        _app.Pleasanter.StatusCode = HttpStatusCode.OK;
        await FetchAsync(path);
        Assert.Empty(await DrainAsync());

        // 成功した .ics はキャッシュされるので、次の取得が Pleasanter へ届くよう捨てる
        _app.Service<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Remove(Guid.Parse(feed.GetProperty("id").GetString()!));

        // 閾値まで失敗 → 通知 → 回復 → 回復の通知（利用者の言語）
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;
        for (var i = 0; i < 3; i++)
        {
            await FetchAsync(path);
        }

        Assert.Single(await DrainAsync());
        _app.Pleasanter.StatusCode = HttpStatusCode.OK;
        await FetchAsync(path);
        var sent = await DrainAsync();
        Assert.Equal(2, sent.Count);
        Assert.Contains("recovered", sent[1].Subject);

        // 回復の通知は 1 回だけ
        await FetchAsync(path);
        Assert.Equal(2, (await DrainAsync()).Count);
    }

    [Fact]
    public async Task 既定では知らせない()
    {
        await AddUserAsync("editor@example.com", AdminRoles.FeedAdministrator);
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;

        for (var i = 0; i < 5; i++)
        {
            await FetchAsync(path);
        }

        Assert.Empty(await DrainAsync());
    }

    [Fact]
    public async Task 宛先は_フィードを編集できる有効な管理者のうちメールアドレスの人だけ()
    {
        await EnableAsync();
        await AddUserAsync("feedadmin@example.com", AdminRoles.FeedAdministrator);
        await AddUserAsync("editor@example.com", AdminRoles.Editor);
        await AddUserAsync("auditor@example.com", AdminRoles.Auditor);
        await AddUserAsync("gone@example.com", AdminRoles.FeedAdministrator, disabled: true);
        await AddUserAsync("not-an-email", AdminRoles.FeedAdministrator);
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;

        for (var i = 0; i < 3; i++)
        {
            await FetchAsync(path);
        }

        var to = (await DrainAsync()).Select(m => m.ToAddress).Order().ToArray();
        Assert.Equal(["editor@example.com", "feedadmin@example.com"], to);
    }

    [Fact]
    public async Task 本文にフィードのURL_APIキー_Pleasanterの文面を載せない()
    {
        await EnableAsync();
        await AddUserAsync("editor@example.com", AdminRoles.FeedAdministrator);
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.Unauthorized;

        for (var i = 0; i < 3; i++)
        {
            await FetchAsync(path);
        }

        var mail = Assert.Single(await DrainAsync());
        var token = path.Split('/').Last().Replace(".ics", "");
        Assert.DoesNotContain(token, mail.Body + mail.Subject);
        Assert.DoesNotContain(TestApp.GlobalApiKey, mail.Body);
        Assert.Contains("http://feed.test/admin/", mail.Body);
        Assert.Contains("API キー", mail.Body);
    }

    [Fact]
    public async Task 画面へ出す最後の取得結果に_連続の失敗の回数が出る()
    {
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;
        await FetchAsync(path);
        await FetchAsync(path);

        var feeds = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/feeds");
        Assert.Equal(2, feeds[0].GetProperty("lastFetch").GetProperty("consecutiveFailures").GetInt32());

        _app.Pleasanter.StatusCode = HttpStatusCode.OK;
        await FetchAsync(path);
        feeds = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/feeds");
        Assert.Equal(0, feeds[0].GetProperty("lastFetch").GetProperty("consecutiveFailures").GetInt32());
    }

    [Fact]
    public async Task 設定の検証_閾値と間隔の範囲()
    {
        var admin = _app.AdminClient();
        async Task<HttpStatusCode> Put(string key, string value) =>
            (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [key] = value } })).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Put("CALENDARFEED_FEED_FAILURENOTIFYTHRESHOLD", "0"));
        Assert.Equal(HttpStatusCode.BadRequest, await Put("CALENDARFEED_FEED_FAILURENOTIFYREPEATHOURS", "0"));
        Assert.Equal(HttpStatusCode.OK, await Put("CALENDARFEED_FEED_FAILURENOTIFYTHRESHOLD", "5"));
    }
}
