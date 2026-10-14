using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>管理操作の記録（監査ログ。Issue #27）。</summary>
public sealed class AuditLogTests : IDisposable
{
    private const string WrongPassword = "definitely-the-wrong-password";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private Task<IReadOnlyList<AuditEntry>> ListAsync(AuditLogQuery? query = null) =>
        _app.Service<IAuditLogStore>().ListAsync(query ?? new AuditLogQuery { Limit = 500 });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string loginId, string password) =>
        client.PostAsJsonAsync("/admin/api/login", new { loginId, password });

    [Fact]
    public async Task 最初の管理者とログインの成功を残す()
    {
        using var app = new TestApp(setUpAdmin: false);
        var client = app.Client();
        await client.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        await LoginAsync(client, TestApp.AdminLoginId, TestApp.AdminPassword);

        var log = await app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery());

        Assert.Contains(log, e => e.Action == AuditActions.AdminSetup && e.Succeeded && e.LoginId == TestApp.AdminLoginId);
        Assert.Contains(log, e => e.Action == AuditActions.LoginSucceeded && e.Succeeded && e.AdminUserId is not null);
    }

    [Fact]
    public async Task ログインの失敗は理由つきで残し_外へは同じ応答にする()
    {
        var admin = _app.AdminClient();
        _ = admin;
        var client = _app.Client();

        var unknown = await LoginAsync(client, "nobody", WrongPassword);
        var bad = await LoginAsync(client, TestApp.AdminLoginId, WrongPassword);

        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await bad.Content.ReadAsStringAsync());
        var failed = (await ListAsync(new AuditLogQuery { FailedOnly = true })).Where(e => e.Action == AuditActions.LoginFailed).ToList();
        Assert.Contains(failed, e => e.Detail == "unknown-user" && e.LoginId == "nobody" && e.AdminUserId is null);
        Assert.Contains(failed, e => e.Detail == "bad-password" && e.LoginId == TestApp.AdminLoginId && e.AdminUserId is not null);
    }

    [Fact]
    public async Task 失敗が続いて締め出したことを残す()
    {
        _ = _app.AdminClient();
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(_app.Client(), TestApp.AdminLoginId, WrongPassword);
        }

        Assert.Contains(await ListAsync(), e => e.Action == AuditActions.LoginLocked);
    }

    [Fact]
    public async Task パスワード変更_ログアウトを残す()
    {
        var admin = _app.AdminClient();
        await admin.PostAsJsonAsync("/admin/api/me/password", new { currentPassword = WrongPassword, newPassword = "another long password 1" });
        await admin.PostAsJsonAsync("/admin/api/me/password", new { currentPassword = TestApp.AdminPassword, newPassword = "another long password 1" });
        await admin.PostAsync("/admin/api/logout", null);

        var actions = (await ListAsync()).Select(e => e.Action).ToList();

        Assert.Contains(AuditActions.PasswordChangeFailed, actions);
        Assert.Contains(AuditActions.PasswordChanged, actions);
        Assert.Contains(AuditActions.Logout, actions);
    }

    [Fact]
    public async Task フィードの操作を残し_APIキーやトークンは残さない()
    {
        const string ApiKey = "super-secret-feed-api-key";
        var (path, feed) = await _app.CreateFeedAsync(new { name = "共有予定", siteId = 123, apiKey = ApiKey });
        var id = feed.GetProperty("id").GetString()!;
        var token = path.Split('/').Last().Replace(".ics", "", StringComparison.Ordinal);
        var admin = _app.AdminClient();
        var rotated = await (await admin.PostAsync($"/admin/api/feeds/{id}/rotate", null)).Content.ReadAsStringAsync();
        await admin.PutAsJsonAsync($"/admin/api/feeds/{id}/apikey", new { apiKey = "another-secret-key" });
        await admin.DeleteAsync($"/admin/api/feeds/{id}");

        var log = await ListAsync();
        var feedActions = log.Where(e => e.TargetType == "Feed").ToList();

        // 同じ時刻の記録は順序が決まらないので、集合で見る
        Assert.Equal(
            new[] { AuditActions.FeedApiKeySet, AuditActions.FeedCreated, AuditActions.FeedRevoked, AuditActions.FeedTokenRotated },
            feedActions.Select(e => e.Action).Order());
        Assert.All(feedActions, e => Assert.Equal(id, e.TargetId));

        // 資格情報・トークン・パスワードが、記録のどこにも入っていない
        var everything = JsonSerializer.Serialize(log);
        foreach (var secret in new[] { ApiKey, "another-secret-key", token, TestApp.AdminPassword })
        {
            Assert.DoesNotContain(secret, everything);
        }

        Assert.DoesNotContain(rotated.Split('/').Last().Replace(".ics\"}", "", StringComparison.Ordinal), everything);
    }

    [Fact]
    public async Task 記録を読むAPIはログインが要り_新しい順に絞り込める()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().GetAsync("/admin/api/audit-logs/")).StatusCode);

        var admin = _app.AdminClient();
        await LoginAsync(_app.Client(), "nobody", WrongPassword);
        _app.Time.Advance(TimeSpan.FromMinutes(1));
        await _app.CreateFeedAsync();

        var all = await admin.GetFromJsonAsync<JsonElement>("/admin/api/audit-logs/");
        Assert.Equal(AuditActions.FeedCreated, all[0].GetProperty("action").GetString());

        var failed = await admin.GetFromJsonAsync<JsonElement>("/admin/api/audit-logs/?failed=true");
        Assert.All(failed.EnumerateArray(), e => Assert.False(e.GetProperty("succeeded").GetBoolean()));
        Assert.NotEmpty(failed.EnumerateArray());

        var one = await admin.GetFromJsonAsync<JsonElement>($"/admin/api/audit-logs/?action={AuditActions.FeedCreated}&limit=1");
        Assert.Single(one.EnumerateArray());

        var before = all[0].GetProperty("occurredAt").GetString();
        var older = await admin.GetFromJsonAsync<JsonElement>($"/admin/api/audit-logs/?before={Uri.EscapeDataString(before!)}");
        Assert.DoesNotContain(older.EnumerateArray(), e => e.GetProperty("action").GetString() == AuditActions.FeedCreated);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/admin/api/audit-logs/?before=not-a-date")).StatusCode);
    }

    [Fact]
    public async Task 保存期間より古い記録だけを掃除し_0なら消さない()
    {
        var store = _app.Service<IAuditLogStore>();
        var now = _app.Time.Now;
        await store.WriteAsync(new AuditEntry(now.AddDays(-400), "old", true));
        await store.WriteAsync(new AuditEntry(now.AddDays(-10), "recent", true));

        var deleted = await _app.Service<AuditLogRetentionService>().SweepAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(["recent"], (await ListAsync()).Select(e => e.Action));
    }

    [Fact]
    public async Task 保存日数が0なら掃除しない()
    {
        using var app = new TestApp(retentionDays: "0");
        var store = app.Service<IAuditLogStore>();
        await store.WriteAsync(new AuditEntry(app.Time.Now.AddDays(-4000), "ancient", true));

        Assert.Equal(0, await app.Service<AuditLogRetentionService>().SweepAsync(CancellationToken.None));
        Assert.Single(await store.ListAsync(new AuditLogQuery()));
    }

    [Fact]
    public async Task 長い値と制御文字は切り詰めて落とす()
    {
        var store = _app.Service<IAuditLogStore>();
        await store.WriteAsync(new AuditEntry(_app.Time.Now, "x\r\ninjected", true, LoginId: new string('a', 500) + "\nfake line", Detail: new string('d', 900)));

        var entry = Assert.Single(await ListAsync());

        Assert.DoesNotContain('\n', entry.Action);
        Assert.Equal(100, entry.LoginId!.Length);
        Assert.Equal(500, entry.Detail!.Length);
    }
}

public sealed class AuditTextTests
{
    [Fact]
    public void 制御文字を落として長さを切る()
    {
        Assert.Null(AuditText.Safe(null, 10));
        Assert.Null(AuditText.Safe("", 10));
        Assert.Equal("ab", AuditText.Safe("a\nb\t\u0000", 10));
        Assert.Equal("abc", AuditText.Safe("abcdef", 3));
    }
}
