using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>Pleasanter の API の回数の上限（HTTP 429）に当たったとき（Issue #71。実機検証結果 13 章）。時計は 2026-10-13 09:00（UTC）。</summary>
public sealed class RateLimitedFeedTests : IDisposable
{
    private readonly TestApp _app = new(mail: true);

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 上限に達したら_503で_Retry_Afterは日付が変わるまで()
    {
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.TooManyRequests;

        var response = await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // 09:00 から翌日の 0 時までは 15 時間。1 分の余裕を足す
        Assert.Equal((15 * 3600 + 60).ToString(), response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString("0"));
    }

    [Fact]
    public async Task 日付が変わる直前は_残り30秒に余裕の1分を足した90秒()
    {
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.TooManyRequests;
        _app.Time.Advance(TimeSpan.FromHours(14) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(30)); // 23:59:30

        var response = await _app.Client().GetAsync(path);

        Assert.Equal(90, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
    }

    [Fact]
    public async Task 一時的な失敗は従来どおり60秒()
    {
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.InternalServerError;

        var response = await _app.Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(60, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
    }

    [Fact]
    public async Task 最後の取得結果は_RateLimited_で_管理画面に出る()
    {
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.TooManyRequests;
        await _app.Client().GetAsync(path);

        var feeds = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/feeds");

        Assert.Equal("RateLimited", feeds[0].GetProperty("lastFetch").GetProperty("status").GetString());
    }

    [Fact]
    public async Task 失敗の通知のメールに_上限に達したことと対処が出る()
    {
        await _app.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { ["CALENDARFEED_FEED_FAILURENOTIFY"] = "true" } });
        await _app.Service<IAdminUserStore>().CreateAsync("editor@example.com", PasswordHasher.Hash("x-unknowable-password"), AdminRoles.FeedAdministrator, "en", _app.Time.Now);
        var (path, _) = await _app.CreateFeedAsync();
        _app.Pleasanter.StatusCode = HttpStatusCode.TooManyRequests;

        for (var i = 0; i < 3; i++)
        {
            await _app.Client().GetAsync(path);
        }

        var sender = _app.Service<MailSender>();
        while (await sender.SendOnceAsync() == SendOutcome.Sent)
        {
        }

        var mail = Assert.Single(_app.MailTransport.Sent);
        Assert.Contains("API limit", mail.Body);
        Assert.Contains("cache seconds", mail.Body);
    }
}
