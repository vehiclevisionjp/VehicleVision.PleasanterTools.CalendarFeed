using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>フィードの編集（Issue #40）。購読 URL（トークン）は変えない。</summary>
public sealed class FeedUpdateTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static object Update(string name = "編集後の名前", long siteId = 456) => new
    {
        name,
        siteId,
        mapping = new { summaryColumn = "Title", startColumn = "StartTime", endColumn = "CompletionTime", locationColumn = "ClassA" },
        filters = new Dictionary<string, string> { ["Status"] = "[\"100\"]" },
        apiKeyUserTimeZoneId = "Asia/Tokyo",
        language = "en",
    };

    [Fact]
    public async Task 名前_サイト_列の対応_絞り込み_言語を更新でき_購読URLは変わらない()
    {
        var (path, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString();
        var admin = _app.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await _app.Client().GetAsync(path)).StatusCode);

        var response = await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", Update());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("編集後の名前", updated.GetProperty("name").GetString());
        Assert.Equal(456, updated.GetProperty("siteId").GetInt64());
        Assert.Equal("en", updated.GetProperty("language").GetString());
        Assert.Equal("Asia/Tokyo", updated.GetProperty("apiKeyUserTimeZoneId").GetString());
        Assert.Equal("ClassA", updated.GetProperty("mapping").GetProperty("locationColumn").GetString());
        Assert.Equal("[\"100\"]", updated.GetProperty("filters").GetProperty("Status").GetString());

        // 一覧にも出る。購読 URL（トークン）は同じまま使える。API キーも変わらない
        var listed = (await admin.GetFromJsonAsync<JsonElement>("/admin/api/feeds/"))[0];
        Assert.Equal("編集後の名前", listed.GetProperty("name").GetString());
        Assert.True(listed.GetProperty("hasApiKey").GetBoolean() || !listed.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _app.Client().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task 更新すると生成済みの_ics_を捨てて新しい定義で取り直す()
    {
        var (path, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString();
        await _app.Client().GetAsync(path);
        var before = _app.Pleasanter.CallCount;
        await _app.Client().GetAsync(path);
        Assert.Equal(before, _app.Pleasanter.CallCount); // キャッシュに当たる

        await _app.AdminClient().PutAsJsonAsync($"/admin/api/feeds/{id}", Update(siteId: 789));
        var body = await (await _app.Client().GetAsync(path)).Content.ReadAsStringAsync();

        Assert.True(_app.Pleasanter.CallCount > before);
        Assert.Contains("/api/items/789/Get", _app.Pleasanter.LastRequestUri);
        Assert.Contains("X-WR-CALNAME:編集後の名前", body);
    }

    [Fact]
    public async Task 入力が不正なら400_無いフィードは404_失効済みは404()
    {
        var (_, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString();
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", Update(name: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", Update(siteId: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", new { name = "x", siteId = 1, language = "xx" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", new { name = "x", siteId = 1, apiKeyUserTimeZoneId = "Nowhere/None" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/admin/api/feeds/{Guid.NewGuid()}", Update())).StatusCode);

        await admin.DeleteAsync($"/admin/api/feeds/{id}");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", Update())).StatusCode);
    }

    [Fact]
    public async Task 編集できるのはfeeds_writeを持つ役割だけ()
    {
        var (_, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString();

        foreach (var (role, allowed) in new[] { (AdminRoles.Editor, true), (AdminRoles.FeedAdministrator, true), (AdminRoles.Auditor, false), (AdminRoles.UserAdministrator, false) })
        {
            var created = await (await _app.AdminClient().PostAsJsonAsync("/admin/api/users", new { loginId = $"u-{role}", role })).Content.ReadFromJsonAsync<JsonElement>();
            var token = created.GetProperty("invitation").GetProperty("invitationToken").GetString();
            var client = _app.Client();
            await client.PostAsJsonAsync("/admin/api/invitations/accept", new { token, password = "a brand new long password" });

            var status = (await client.PutAsJsonAsync($"/admin/api/feeds/{id}", Update(name: $"by {role}"))).StatusCode;

            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, status);
        }
    }

    [Fact]
    public async Task 変えた項目の名前だけを監査ログへ残し_値は残さない()
    {
        var (_, feed) = await _app.CreateFeedAsync();
        var id = feed.GetProperty("id").GetString()!;
        var admin = _app.AdminClient();

        await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", Update(name: "秘密っぽくない名前"));
        await admin.PutAsJsonAsync($"/admin/api/feeds/{id}", Update(name: "秘密っぽくない名前"));

        var log = (await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.FeedUpdated, Limit = 10 })).ToList();
        Assert.Equal(2, log.Count);
        Assert.Contains(log, e => e.DetailJson!.StartsWith("changed=", StringComparison.Ordinal) && e.DetailJson.Contains("name") && e.DetailJson.Contains("siteId"));
        Assert.Contains(log, e => e.DetailJson == "no-change");
        Assert.DoesNotContain(log, e => e.DetailJson!.Contains("秘密っぽくない名前") || e.DetailJson.Contains("456"));
        Assert.All(log, e => Assert.Equal(id, e.TargetId));
    }
}
