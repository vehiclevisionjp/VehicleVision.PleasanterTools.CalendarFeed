using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>フィードごとの CalDAV の有効・無効（Issue #85。既定は無効）。</summary>
public sealed class FeedCalDavSwitchTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<bool> EnabledAsync()
    {
        var feeds = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/feeds/");
        return feeds[0].GetProperty("calDavEnabled").GetBoolean();
    }

    [Fact]
    public async Task 新しいフィードは_CalDAVが無効()
    {
        await _app.CreateFeedAsync();

        Assert.False(await EnabledAsync());
    }

    [Fact]
    public async Task 有効にして無効に戻せる_監査ログに残る()
    {
        var (_, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString();

        var on = await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}/caldav", new { enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        Assert.True(await EnabledAsync());

        var off = await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}/caldav", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False(await EnabledAsync());

        var audit = await _app.AdminClient().GetStringAsync("/admin/api/audit-logs/?action=feed.caldav_changed");
        Assert.Contains("feed.caldav_changed", audit);
    }

    [Fact]
    public async Task 無いフィードは404()
    {
        var response = await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{Guid.NewGuid()}/caldav", new { enabled = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
