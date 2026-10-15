using System.Net;
using System.Net.Http.Json;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>同じ DB を向く 2 台で動かしたときの前提（Issue #49）。</summary>
public sealed class MultiInstanceWebTests : IDisposable
{
    private readonly TestApp _first = new();
    private readonly TestApp _second;

    public MultiInstanceWebTests()
    {
        // 1 台目が DB を作り・管理者を作ってから、同じ DB と同じ秘密の鍵で 2 台目を起こす
        _ = _first.AdminClient();
        _second = new TestApp(setUpAdmin: false, existingDb: _first.DbPath, secretKeyValue: _first.SecretKeyValue);
    }

    public void Dispose()
    {
        _second.Dispose();
        _first.Dispose();
    }

    private static async Task<HttpStatusCode> BadLoginAsync(TestApp app, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/login")
        {
            Content = JsonContent.Create(new { loginId = TestApp.AdminLoginId, password = "wrong password!!" }),
        };
        request.Headers.Add(RemoteIpStartupFilter.Header, ip);
        return (await app.Client().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task ログインの試行の枠は_台をまたいで同じ接続元を一緒に数える()
    {
        // 枠は 5 分に 10 回。2 台へ 5 回ずつ分けても、合わせて 10 回で使い切る（台ごとの数えなら 11 回目も通ってしまう）
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await BadLoginAsync(_first, "198.51.100.7"));
            Assert.Equal(HttpStatusCode.Unauthorized, await BadLoginAsync(_second, "198.51.100.7"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await BadLoginAsync(_first, "198.51.100.7"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await BadLoginAsync(_second, "198.51.100.7"));

        // 別の接続元は、別に数える
        Assert.Equal(HttpStatusCode.Unauthorized, await BadLoginAsync(_second, "198.51.100.8"));
    }

    [Fact]
    public async Task ログインは_ほかの台で作ったセッションでも通る_セッションは_DB_にある()
    {
        var login = await _first.Client().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("calendarfeed_admin=", StringComparison.Ordinal)).Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/me/");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await _second.Client().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task 設定の保存は_ほかの台へ30秒以内に届く_読み直しを呼べば反映される()
    {
        var saved = await _first.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { ["CALENDARFEED_FEED_CACHESECONDS"] = "77" } });
        Assert.True(saved.IsSuccessStatusCode);

        // 2 台目の定期処理（30 秒ごと）の 1 回分
        await _second.Service<Web.Services.AppSettingsMonitor>().RefreshAsync(CancellationToken.None);
        Assert.Equal(77, _second.Service<Web.Services.RuntimeSettings>().Current.Feed.CacheSeconds);
    }
}
