using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>管理者の管理（招待・無効化・役割・端末。Issue #26）。</summary>
public sealed class AdminUserManagementTests : IDisposable
{
    private const string NewPassword = "a brand new long password";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static string TokenOf(JsonElement created) =>
        created.GetProperty("inviteUrl").GetString()!.Split('/').Last();

    /// <summary>管理者 A が招待する。招待のトークンと、作られた管理者の ID を返す。</summary>
    private async Task<(string Token, string Id)> InviteAsync(string loginId, string? role = null)
    {
        var response = await _app.AdminClient().PostAsJsonAsync("/admin/api/users", new { loginId, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (TokenOf(json), json.GetProperty("user").GetProperty("id").GetString()!);
    }

    private async Task<HttpClient> AcceptAndLoginAsync(string loginId, string token, string password = NewPassword)
    {
        var client = _app.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/admin/api/invite/{token}/accept", new { password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/admin/api/login", new { loginId, password })).StatusCode);
        return client;
    }

    /// <summary>招待して受け取り、ログインした管理者（役割つき）。</summary>
    private async Task<(HttpClient Client, string Id)> NewMemberAsync(string loginId, string role)
    {
        var (token, id) = await InviteAsync(loginId, role);
        return (await AcceptAndLoginAsync(loginId, token), id);
    }

    private async Task<JsonElement> UsersAsync(string query = "") =>
        await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/users/" + query);

    // ---- 招待 ------------------------------------------------------------------------

    [Fact]
    public async Task 招待を受け取るまでログインできず_パスワードを決めると入れる()
    {
        var (token, _) = await InviteAsync("bob", AdminRoles.Viewer);

        // 受け取る前はログインできない
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/admin/api/login", new { loginId = "bob", password = "" })).StatusCode);

        var info = await _app.Client().GetFromJsonAsync<JsonElement>($"/admin/api/invite/{token}");
        Assert.Equal("bob", info.GetProperty("loginId").GetString());

        var bob = await AcceptAndLoginAsync("bob", token);
        Assert.Equal(AdminRoles.Viewer, (await bob.GetFromJsonAsync<JsonElement>("/admin/api/me/")).GetProperty("role").GetString());
    }

    [Fact]
    public async Task 招待は使い切りで_パスワードの条件を通らなければ受け取れない()
    {
        var (token, _) = await InviteAsync("bob");

        Assert.Equal(HttpStatusCode.BadRequest, (await _app.Client().PostAsJsonAsync($"/admin/api/invite/{token}/accept", new { password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.Client().PostAsJsonAsync($"/admin/api/invite/{token}/accept", new { password = "BOB" })).StatusCode);

        await AcceptAndLoginAsync("bob", token);

        // 受け取った後は同じ招待で何もできない（情報も、2 回目の受け取りも 404）
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().GetAsync($"/admin/api/invite/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().PostAsJsonAsync($"/admin/api/invite/{token}/accept", new { password = "another long password" })).StatusCode);
    }

    [Theory]
    [InlineData("nonexistent-token-nonexistent-token")]
    [InlineData("short")]
    [InlineData("bad!chars!bad!chars!bad!chars!")]
    public async Task 知らないトークンは形が違っても同じ404(string token)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().GetAsync($"/admin/api/invite/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().PostAsJsonAsync($"/admin/api/invite/{token}/accept", new { password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task 招待を出し直すと前の招待は死に_受け取り済みの人には出し直せない()
    {
        var (oldToken, id) = await InviteAsync("bob");
        var admin = _app.AdminClient();

        var reissued = await (await admin.PostAsync($"/admin/api/users/{id}/invite", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newToken = TokenOf(reissued);

        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().GetAsync($"/admin/api/invite/{oldToken}")).StatusCode);
        await AcceptAndLoginAsync("bob", newToken);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/admin/api/users/{id}/invite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/admin/api/users/{Guid.NewGuid()}/invite", null)).StatusCode);
    }

    [Fact]
    public async Task 期限が切れた招待は使えず_出し直せる()
    {
        var (token, id) = await InviteAsync("bob");

        _app.Time.Advance(TimeSpan.FromDays(8));
        await _app.AdminClient().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });

        Assert.Equal(HttpStatusCode.NotFound, (await _app.Client().GetAsync($"/admin/api/invite/{token}")).StatusCode);
        var bob = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob");
        Assert.True(bob.GetProperty("inviteExpired").GetBoolean());

        var again = await (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/invite", null)).Content.ReadFromJsonAsync<JsonElement>();
        await AcceptAndLoginAsync("bob", TokenOf(again));
    }

    [Fact]
    public async Task 同じログインIDと知らない役割は断る()
    {
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = TestApp.AdminLoginId.ToUpperInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "bob", role = "Root" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "  " })).StatusCode);
    }

    [Fact]
    public async Task 招待のトークンの現物はDBに残らない()
    {
        var (token, _) = await InviteAsync("bob");

        var users = await _app.Service<IAdminUserStore>().ListAsync();
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(users));
        Assert.True(await _app.Service<IAdminUserStore>().FindByInviteAsync(InviteTokens.Hash(token), _app.Time.Now) is not null);
    }

    // ---- 役割 ------------------------------------------------------------------------

    [Fact]
    public async Task 閲覧のみはフィードの一覧だけ見られ_変更_監査ログ_管理者の管理はできない()
    {
        await _app.CreateFeedAsync();
        var (viewer, _) = await NewMemberAsync("viewer", AdminRoles.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/admin/api/feeds/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/admin/api/feeds", new { name = "x", siteId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/admin/api/feeds/{Guid.NewGuid()}/rotate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/admin/api/feeds/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/admin/api/feeds/{Guid.NewGuid()}/apikey", new { apiKey = "k" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/admin/api/audit-logs/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/admin/api/users/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/admin/api/users", new { loginId = "eve", role = "Administrator" })).StatusCode);

        // 自分の端末は、役割を問わず見られる
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/admin/api/me/sessions/")).StatusCode);
    }

    [Fact]
    public async Task 役割を切り替えると権限が変わる_自分の役割は変えられない()
    {
        var (viewer, id) = await NewMemberAsync("viewer", AdminRoles.Viewer);
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/admin/api/users/")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/admin/api/users/{id}/role", new { role = AdminRoles.Administrator })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/admin/api/users/")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/users/{id}/role", new { role = "Root" })).StatusCode);
        var self = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == TestApp.AdminLoginId).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/users/{self}/role", new { role = AdminRoles.Viewer })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/admin/api/users/{Guid.NewGuid()}/role", new { role = AdminRoles.Viewer })).StatusCode);
    }

    // ---- 無効化 ----------------------------------------------------------------------

    [Fact]
    public async Task 無効化するとその場で使えなくなり_有効化すると戻る_自分は無効化できない()
    {
        var (bob, id) = await NewMemberAsync("bob", AdminRoles.Viewer);
        var admin = _app.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/admin/api/me/")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/admin/api/users/{id}/disable", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/admin/api/login", new { loginId = "bob", password = NewPassword })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/admin/api/users/{id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.Client().PostAsJsonAsync("/admin/api/login", new { loginId = "bob", password = NewPassword })).StatusCode);

        var self = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == TestApp.AdminLoginId).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/admin/api/users/{self}/disable", null)).StatusCode);
    }

    [Fact]
    public async Task 最後の有効なAdministratorは無効化も降格もできない()
    {
        var store = _app.Service<IAdminUserStore>();
        _ = _app.AdminClient();
        var only = (await store.ListAsync()).Single();

        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetDisabledAsync(only.Id, true));
        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetRoleAsync(only.Id, AdminRoles.Viewer));
        Assert.Equal(AdminChangeResult.NotFound, await store.SetDisabledAsync(Guid.NewGuid().ToString(), true));

        // 招待中の管理者は「有効な Administrator」に数えない
        await InviteAsync("pending", AdminRoles.Administrator);
        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetDisabledAsync(only.Id, true));

        // もう 1 人が受け取れば、片方は外せる
        var (token, id) = await InviteAsync("second", AdminRoles.Administrator);
        await AcceptAndLoginAsync("second", token);
        Assert.Equal(AdminChangeResult.Updated, await store.SetRoleAsync(id, AdminRoles.Viewer));
        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetDisabledAsync(only.Id, true));
        Assert.Equal(AdminChangeResult.Updated, await store.SetRoleAsync(id, AdminRoles.Administrator));
        Assert.Equal(AdminChangeResult.Updated, await store.SetDisabledAsync(only.Id, true));
    }

    [Fact]
    public async Task APIでも最後の1人を外せず409になる()
    {
        // A が B（Administrator）を作り、B が A を無効化して、残った B を A が…という順で、最後の 1 人を外そうとする
        var (b, bId) = await NewMemberAsync("bee", AdminRoles.Administrator);
        var aId = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == TestApp.AdminLoginId).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await b.PostAsync($"/admin/api/users/{aId}/disable", null)).StatusCode);

        // A は無効になった。B は残り 1 人の Administrator で、自分は外せない（自己操作の拒否）。store でも 409 相当
        Assert.Equal(AdminChangeResult.LastAdministrator, await _app.Service<IAdminUserStore>().SetDisabledAsync(bId, true));
    }

    // ---- 2 要素の解除・端末 ----------------------------------------------------------

    [Fact]
    public async Task 管理者は他人の2要素を解除でき_その人の端末も失効する()
    {
        var (bob, id) = await NewMemberAsync("bob", AdminRoles.Viewer);
        var begin = await (await bob.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) })).StatusCode);
        Assert.True((await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("totpEnabled").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/totp/reset", null)).StatusCode);

        Assert.False((await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.Client().PostAsJsonAsync("/admin/api/login", new { loginId = "bob", password = NewPassword })).StatusCode);

        // 自分の分は、この口からは外せない
        var self = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == TestApp.AdminLoginId).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.AdminClient().PostAsync($"/admin/api/users/{self}/totp/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.AdminClient().PostAsync($"/admin/api/users/{Guid.NewGuid()}/totp/reset", null)).StatusCode);
    }

    [Fact]
    public async Task 自分の端末の一覧_個別失効_ほかをすべて終了()
    {
        var first = _app.AdminClient();
        var second = _app.Client();
        var third = _app.Client();
        foreach (var client in new[] { second, third })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword })).StatusCode);
        }

        var list = await first.GetFromJsonAsync<JsonElement>("/admin/api/me/sessions/");
        Assert.Equal(3, list.GetArrayLength());
        Assert.Single(list.EnumerateArray(), s => s.GetProperty("isCurrent").GetBoolean());

        // 他人の端末の識別子では消えない
        Assert.Equal(HttpStatusCode.NotFound, (await first.DeleteAsync("/admin/api/me/sessions/not-a-handle")).StatusCode);

        var other = list.EnumerateArray().First(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("handle").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/admin/api/me/sessions/{other}")).StatusCode);
        Assert.Equal(2, (await first.GetFromJsonAsync<JsonElement>("/admin/api/me/sessions/")).GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/admin/api/me/sessions/revoke-others", null)).StatusCode);
        var rest = await first.GetFromJsonAsync<JsonElement>("/admin/api/me/sessions/");
        Assert.Equal(1, rest.GetArrayLength());
        Assert.True(rest[0].GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task 他人の端末を失効させられる_自分の分は自分の画面から()
    {
        var (bob, id) = await NewMemberAsync("bob", AdminRoles.Viewer);

        Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().DeleteAsync($"/admin/api/users/{id}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);

        var self = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == TestApp.AdminLoginId).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.AdminClient().DeleteAsync($"/admin/api/users/{self}/sessions")).StatusCode);
    }

    // ---- 棚卸し・監査 ----------------------------------------------------------------

    [Fact]
    public async Task 長い間ログインしていない管理者が分かる_招待中と無効は対象外()
    {
        var (_, bobId) = await NewMemberAsync("bob", AdminRoles.Viewer);
        await InviteAsync("pending");
        var (_, disabledId) = await NewMemberAsync("gone", AdminRoles.Viewer);
        await _app.AdminClient().PostAsync($"/admin/api/users/{disabledId}/disable", null);

        _app.Time.Advance(TimeSpan.FromDays(91));
        // A だけ、ログインし直す（最終ログインを新しくする）
        Assert.Equal(HttpStatusCode.OK, (await _app.AdminClient().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword })).StatusCode);

        var stale = (await UsersAsync("?staleOnly=true")).EnumerateArray().ToList();

        Assert.Equal([bobId], stale.Select(u => u.GetProperty("id").GetString()));
        var all = (await UsersAsync()).EnumerateArray().ToList();
        Assert.True(all.Single(u => u.GetProperty("loginId").GetString() == "pending").GetProperty("invitePending").GetBoolean());
        Assert.False(all.Single(u => u.GetProperty("loginId").GetString() == "pending").GetProperty("stale").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, all.Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("lastLoginAt").ValueKind);
    }

    [Fact]
    public async Task 管理者の管理の操作を監査ログへ残し_招待のトークンは残さない()
    {
        var (token, id) = await InviteAsync("bob", AdminRoles.Viewer);
        var reissued = await (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/invite", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newToken = TokenOf(reissued);
        await AcceptAndLoginAsync("bob", newToken);
        var admin = _app.AdminClient();
        await admin.PutAsJsonAsync($"/admin/api/users/{id}/role", new { role = AdminRoles.Administrator });
        await admin.PostAsync($"/admin/api/users/{id}/disable", null);
        await admin.PostAsync($"/admin/api/users/{id}/enable", null);
        await admin.PostAsync($"/admin/api/users/{id}/totp/reset", null);
        await admin.DeleteAsync($"/admin/api/users/{id}/sessions");
        await admin.PostAsync("/admin/api/me/sessions/revoke-others", null);

        var log = await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Limit = 500 });
        var actions = log.Select(e => e.Action).ToHashSet();

        foreach (var expected in new[]
        {
            AuditActions.UserInvited, AuditActions.UserInviteReissued, AuditActions.UserInviteAccepted, AuditActions.UserRoleChanged,
            AuditActions.UserDisabled, AuditActions.UserEnabled, AuditActions.UserTotpReset, AuditActions.UserSessionsRevoked,
            AuditActions.SessionsRevokedOthers,
        })
        {
            Assert.Contains(expected, actions);
        }

        var everything = System.Text.Json.JsonSerializer.Serialize(log);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(newToken, everything);
        Assert.DoesNotContain(NewPassword, everything);
    }
}
