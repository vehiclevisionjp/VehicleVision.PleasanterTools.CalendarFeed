using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>管理者の管理（招待・無効化・役割と権限・端末。Issue #26）。方式は Questionnaire に則る。</summary>
public sealed class AdminUserManagementTests : IDisposable
{
    private const string NewPassword = "a brand new long password";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static JsonElement InvitationOf(JsonElement response) => response.GetProperty("invitation");

    /// <summary>管理者 A が招待する。トークンと、作られた管理者の ID を返す。</summary>
    private async Task<(string Token, string Id)> InviteAsync(string loginId, string role = AdminRoles.Editor, TestApp? app = null)
    {
        var response = await (app ?? _app).AdminClient().PostAsJsonAsync("/admin/api/users", new { loginId, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (InvitationOf(json).GetProperty("invitationToken").GetString()!, json.GetProperty("user").GetProperty("id").GetString()!);
    }

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string token, string password = NewPassword) =>
        client.PostAsJsonAsync("/admin/api/invitations/accept", new { token, password });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string loginId, string password = NewPassword) =>
        client.PostAsJsonAsync("/admin/api/login", new { loginId, password });

    /// <summary>招待を受け取った（そのまま入れる設定では、受け取った時点でログイン済み）管理者。</summary>
    private async Task<(HttpClient Client, string Id)> NewMemberAsync(string loginId, string role)
    {
        var (token, id) = await InviteAsync(loginId, role);
        var client = _app.Client();
        var accepted = await AcceptAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("done", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("next").GetString());
        return (client, id);
    }

    private async Task<JsonElement> UsersAsync(string query = "") =>
        await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/users/" + query);

    private async Task<string> SelfIdAsync() =>
        (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == TestApp.AdminLoginId).GetProperty("id").GetString()!;

    // ---- 招待 ------------------------------------------------------------------------

    [Fact]
    public async Task 招待を受け取るまでログインできず_パスワードを決めるとそのまま入れる()
    {
        var (token, _) = await InviteAsync("bob");

        // 受け取る前は、誰も知らないパスワードしか無いのでログインできない
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_app.Client(), "bob", "")).StatusCode);

        var (bob, _) = await NewMemberAsync("carol", AdminRoles.Editor);
        var me = await bob.GetFromJsonAsync<JsonElement>("/admin/api/me/");
        Assert.Equal(AdminRoles.Editor, me.GetProperty("role").GetString());
        Assert.NotEmpty(me.GetProperty("lastLoginAt").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_app.Client(), "carol")).StatusCode);
        _ = token;
    }

    [Fact]
    public async Task 招待は使い切りで_条件を通らないパスワードでは招待が死なない()
    {
        var (token, _) = await InviteAsync("bob");

        // 条件に合わないパスワードは、招待を使い切る前に断る（使い切ってから断ると、受け取れなくなる）
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(_app.Client(), token, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(_app.Client(), token, "BOB")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(_app.Client(), token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(_app.Client(), token, "another long password")).StatusCode);
    }

    [Theory]
    [InlineData("nonexistent-token-nonexistent-token")]
    [InlineData("short")]
    [InlineData("bad!chars!bad!chars!bad!chars!")]
    [InlineData("")]
    public async Task 知らないトークンは形が違っても同じ404(string token) =>
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(_app.Client(), token)).StatusCode);

    [Fact]
    public async Task 招待を出し直すと前の招待は死に_受け取り済みの人にも出し直せてパスワードの決め直しになる()
    {
        var (oldToken, id) = await InviteAsync("bob");
        var admin = _app.AdminClient();

        var reissued = await (await admin.PostAsync($"/admin/api/users/{id}/invitation", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newToken = InvitationOf(reissued).GetProperty("invitationToken").GetString()!;

        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(_app.Client(), oldToken)).StatusCode);
        var bob = _app.Client();
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(bob, newToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/admin/api/me/")).StatusCode);

        // 受け取り済みでも出し直せる。**受け取ると、以前のパスワードで張られたセッションはすべて失効する**
        var again = await (await admin.PostAsync($"/admin/api/users/{id}/invitation", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(_app.Client(), InvitationOf(again).GetProperty("invitationToken").GetString()!, "yet another long password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_app.Client(), "bob", "yet another long password")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/admin/api/users/{Guid.NewGuid()}/invitation", null)).StatusCode);
    }

    [Fact]
    public async Task 期限は48時間で_切れた招待は使えず出し直せる()
    {
        var (token, id) = await InviteAsync("bob");

        _app.Time.Advance(TimeSpan.FromHours(47));
        await _app.AdminClient().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        var still = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob");
        Assert.True(still.GetProperty("invitationPending").GetBoolean());

        _app.Time.Advance(TimeSpan.FromHours(2));
        await _app.AdminClient().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(_app.Client(), token)).StatusCode);
        Assert.False((await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("invitationPending").GetBoolean());

        var again = await (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/invitation", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(_app.Client(), InvitationOf(again).GetProperty("invitationToken").GetString()!)).StatusCode);
    }

    [Fact]
    public async Task 同じログインIDと知らない役割は断る()
    {
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = TestApp.AdminLoginId.ToUpperInvariant(), role = AdminRoles.Editor })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "bob", role = "Root" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "bob" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "  ", role = AdminRoles.Editor })).StatusCode);
    }

    [Fact]
    public async Task 招待のトークンの現物はDBに残らない()
    {
        var (token, _) = await InviteAsync("bob");

        var store = _app.Service<IAdminInvitationStore>();
        Assert.NotNull(await store.FindByTokenHashAsync(InviteTokens.Hash(token)));
        Assert.Null(await store.FindByTokenHashAsync(token));
    }

    [Fact]
    public async Task 受け取った後の2要素は方針に従う_必須ならその場で登録へ_登録済みならまず2要素()
    {
        using var required = new TestApp(twoFactor: "required", setUpAdmin: false);
        var admin = required.Client();
        await admin.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        Assert.Equal("enroll", (await (await LoginAsync(admin, TestApp.AdminLoginId, TestApp.AdminPassword)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("step").GetString());
        var begin = await (await admin.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(begin.GetProperty("secret").GetString()!, required.Time.Now.UtcDateTime) })).StatusCode);

        var created = await (await admin.PostAsJsonAsync("/admin/api/users", new { loginId = "bob", role = AdminRoles.Editor })).Content.ReadFromJsonAsync<JsonElement>();
        var accepted = await AcceptAsync(required.Client(), InvitationOf(created).GetProperty("invitationToken").GetString()!);

        Assert.Equal("enroll", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("next").GetString());
    }

    // ---- 権限 ------------------------------------------------------------------------

    [Theory]
    [InlineData(AdminRoles.Administrator, "feeds.read,feeds.write,feeds.delete,outbox.read,audit.read,users.read,users.write,users.resetTwoFactor")]
    [InlineData(AdminRoles.FeedAdministrator, "feeds.read,feeds.write,feeds.delete,outbox.read")]
    [InlineData(AdminRoles.UserAdministrator, "audit.read,users.read,users.write,users.resetTwoFactor")]
    [InlineData(AdminRoles.Editor, "feeds.read,feeds.write")]
    [InlineData(AdminRoles.Auditor, "audit.read,outbox.read")]
    [InlineData("Unknown", "")]
    public void 役割ごとの権限(string role, string expected) =>
        Assert.Equal(
            expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(),
            AdminPermissions.Of(role).Order());

    [Fact]
    public async Task 役割ごとに入口が分かれる()
    {
        await _app.CreateFeedAsync();
        var feedId = Guid.NewGuid();

        async Task<Dictionary<string, HttpStatusCode>> ProbeAsync(HttpClient c) => new()
        {
            ["feeds.read"] = (await c.GetAsync("/admin/api/feeds/")).StatusCode,
            ["feeds.write"] = (await c.PostAsync($"/admin/api/feeds/{feedId}/rotate", null)).StatusCode,
            ["feeds.delete"] = (await c.DeleteAsync($"/admin/api/feeds/{feedId}")).StatusCode,
            ["audit.read"] = (await c.GetAsync("/admin/api/audit-logs/")).StatusCode,
            ["users.read"] = (await c.GetAsync("/admin/api/users/")).StatusCode,
            ["users.write"] = (await c.PostAsJsonAsync("/admin/api/users", new { loginId = "x", role = "Bad" })).StatusCode,
            ["outbox.read"] = (await c.GetAsync("/admin/api/mail/status")).StatusCode,
        };

        // 権限があれば 403 にならない（中身の判定で 404・400 になる）。無ければ 403
        var matrix = new Dictionary<string, string[]>
        {
            [AdminRoles.FeedAdministrator] = ["feeds.read", "feeds.write", "feeds.delete", "outbox.read"],
            [AdminRoles.UserAdministrator] = ["audit.read", "users.read", "users.write"],
            [AdminRoles.Editor] = ["feeds.read", "feeds.write"],
            [AdminRoles.Auditor] = ["audit.read", "outbox.read"],
        };

        foreach (var (role, allowed) in matrix)
        {
            var (client, _) = await NewMemberAsync($"u-{role}", role);
            foreach (var (permission, status) in await ProbeAsync(client))
            {
                Assert.True(
                    allowed.Contains(permission) ? status != HttpStatusCode.Forbidden : status == HttpStatusCode.Forbidden,
                    $"{role} / {permission}: {status}");
            }

            // 自分の端末と /me は、役割を問わず
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/me/sessions/")).StatusCode);
            var permissions = (await client.GetFromJsonAsync<JsonElement>("/admin/api/me/")).GetProperty("permissions").EnumerateArray().Select(p => p.GetString());
            Assert.Equal(AdminPermissions.Of(role).Order(), permissions.Order());
        }

        // 特権管理者は、すべて 403 にならない
        Assert.All(await ProbeAsync(_app.AdminClient()), kv => Assert.NotEqual(HttpStatusCode.Forbidden, kv.Value));
    }

    [Fact]
    public async Task 役割を切り替えると権限がその場で変わる_自分の役割は変えられない()
    {
        var (editor, id) = await NewMemberAsync("editor", AdminRoles.Editor);
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/admin/api/users/")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/admin/api/users/{id}/role", new { role = AdminRoles.UserAdministrator })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync("/admin/api/users/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/admin/api/feeds/")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/users/{id}/role", new { role = "Root" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/admin/api/users/{await SelfIdAsync()}/role", new { role = AdminRoles.Editor })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/admin/api/users/{Guid.NewGuid()}/role", new { role = AdminRoles.Editor })).StatusCode);
    }

    // ---- 無効化 ----------------------------------------------------------------------

    [Fact]
    public async Task 無効化するとその場で使えなくなり_有効化すると戻る_自分は無効化できない()
    {
        var (bob, id) = await NewMemberAsync("bob", AdminRoles.Editor);
        var admin = _app.AdminClient();
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/admin/api/me/")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/admin/api/users/{id}/disable", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_app.Client(), "bob")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/admin/api/users/{id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_app.Client(), "bob")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/admin/api/users/{await SelfIdAsync()}/disable", null)).StatusCode);
    }

    [Fact]
    public async Task 止めた相手宛ての未使用の招待は取り消す_止められた人には招待を出せない()
    {
        var (token, id) = await InviteAsync("bob");
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/admin/api/users/{id}/disable", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(_app.Client(), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/admin/api/users/{id}/invitation", null)).StatusCode);
    }

    [Fact]
    public async Task 一度もログインしていないAdministratorは頭数に入れず_最後の1人を外せない()
    {
        var store = _app.Service<IAdminUserStore>();
        var only = (await store.ListAsync()).Count == 0 ? null : (await store.ListAsync()).Single();
        _ = _app.AdminClient();
        only = (await store.ListAsync()).Single();

        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetDisabledAsync(only.Id, true));
        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetRoleAsync(only.Id, AdminRoles.Editor));

        // 招待しただけ（一度もログインしていない）の Administrator は、頼りにしない
        var (token, secondId) = await InviteAsync("second", AdminRoles.Administrator);
        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetDisabledAsync(only.Id, true));

        // 受け取って入れば（ログインとして記録される）、片方は外せる
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(_app.Client(), token)).StatusCode);
        Assert.Equal(AdminChangeResult.Updated, await store.SetDisabledAsync(only.Id, true));
        Assert.Equal(AdminChangeResult.LastAdministrator, await store.SetDisabledAsync(secondId, true));
    }

    [Fact]
    public async Task UserAdministratorでも最後の特権管理者は止められない()
    {
        var (token, _) = await InviteAsync("second", AdminRoles.Administrator);
        await AcceptAsync(_app.Client(), token);
        var (userAdmin, _) = await NewMemberAsync("uadmin", AdminRoles.UserAdministrator);
        var adminId = await SelfIdAsync();
        var secondId = (await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "second").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await userAdmin.PostAsync($"/admin/api/users/{adminId}/disable", null)).StatusCode);

        // 残った特権管理者は 1 人。誰がやっても断る
        Assert.Equal(HttpStatusCode.Conflict, (await userAdmin.PostAsync($"/admin/api/users/{secondId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await userAdmin.PutAsJsonAsync($"/admin/api/users/{secondId}/role", new { role = AdminRoles.Editor })).StatusCode);
    }

    // ---- 2 要素の解除・端末 ----------------------------------------------------------

    [Fact]
    public async Task 他人の2要素を解除でき_その人の端末も失効する_自分の分はこの口からは外せない()
    {
        var (bob, id) = await NewMemberAsync("bob", AdminRoles.Editor);
        var begin = await (await bob.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) })).StatusCode);
        Assert.True((await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("totpEnabled").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/totp/reset", null)).StatusCode);

        Assert.False((await UsersAsync()).EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_app.Client(), "bob")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await _app.AdminClient().PostAsync($"/admin/api/users/{await SelfIdAsync()}/totp/reset", null)).StatusCode);
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
        var (bob, id) = await NewMemberAsync("bob", AdminRoles.Editor);

        Assert.Equal(HttpStatusCode.NoContent, (await _app.AdminClient().DeleteAsync($"/admin/api/users/{id}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.AdminClient().DeleteAsync($"/admin/api/users/{await SelfIdAsync()}/sessions")).StatusCode);
    }

    // ---- 棚卸し・監査 ----------------------------------------------------------------

    [Fact]
    public async Task 長い間ログインしていない管理者が分かる_招待中と無効は対象外()
    {
        var (_, bobId) = await NewMemberAsync("bob", AdminRoles.Editor);
        var (_, expiredId) = await InviteAsync("expired");
        var (_, disabledId) = await NewMemberAsync("gone", AdminRoles.Editor);
        await _app.AdminClient().PostAsync($"/admin/api/users/{disabledId}/disable", null);

        _app.Time.Advance(TimeSpan.FromDays(91));
        // A だけ、ログインし直す（最終ログインを新しくする）
        Assert.Equal(HttpStatusCode.OK, (await _app.AdminClient().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword })).StatusCode);
        // いま出した招待は招待中（対象外）。期限が切れたままの招待は、使われていないだけなので対象
        await InviteAsync("fresh");

        var stale = (await UsersAsync("?staleOnly=true")).EnumerateArray().ToList();

        Assert.Equal(new[] { bobId, expiredId }.Order(), stale.Select(u => u.GetProperty("id").GetString()!).Order());
        var all = (await UsersAsync()).EnumerateArray().ToList();
        Assert.NotEqual(JsonValueKind.Null, all.Single(u => u.GetProperty("loginId").GetString() == "bob").GetProperty("lastLoginAt").ValueKind);
    }

    [Fact]
    public async Task 管理者の管理の操作を監査ログへ残し_招待のトークンは残さない()
    {
        var (token, id) = await InviteAsync("bob", AdminRoles.Editor);
        var reissued = await (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/invitation", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newToken = InvitationOf(reissued).GetProperty("invitationToken").GetString()!;
        await AcceptAsync(_app.Client(), newToken);
        var admin = _app.AdminClient();
        await admin.PutAsJsonAsync($"/admin/api/users/{id}/role", new { role = AdminRoles.FeedAdministrator });
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

        var everything = JsonSerializer.Serialize(log);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(newToken, everything);
        Assert.DoesNotContain(NewPassword, everything);
    }
}

/// <summary>招待のメール（Issue #26。Questionnaire の Issue #189 に則る）。</summary>
public sealed class AdminInvitationMailTests : IDisposable
{
    private readonly TestApp _app = new(mail: true);

    public void Dispose() => _app.Dispose();

    private async Task<(JsonElement Invitation, string Token)> InviteAsync(string loginId, TestApp? app = null)
    {
        var response = await (app ?? _app).AdminClient().PostAsJsonAsync("/admin/api/users", new { loginId, role = AdminRoles.Editor });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invitation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation");
        return (invitation, invitation.GetProperty("invitationToken").GetString()!);
    }

    private async Task<string> RawOutboxAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_app.DbPath};Pooling=False");
        await connection.OpenAsync();
        return string.Join('|', await connection.QueryAsync<string>("SELECT PayloadProtected FROM MailOutbox"));
    }

    [Fact]
    public async Task メールアドレスのログインIDには招待を積み_送ると行は消える_URLは画面にも返す()
    {
        var (invitation, token) = await InviteAsync("bob@example.com");

        Assert.True(invitation.GetProperty("mailSent").GetBoolean());
        Assert.Contains(token, invitation.GetProperty("url").GetString());

        // 送信待ちの行には、宛先も本文も URL も平文で入っていない
        var raw = await RawOutboxAsync();
        Assert.NotEmpty(raw);
        Assert.DoesNotContain("bob@example.com", raw);
        Assert.DoesNotContain(token, raw);

        Assert.Equal(SendOutcome.Sent, await _app.Service<MailSender>().SendOnceAsync());

        var mail = Assert.Single(_app.MailTransport.Sent);
        Assert.Equal("bob@example.com", mail.ToAddress);
        // リンクは設定の公開 URL から作る（要求の Host ではない）
        Assert.Contains($"http://feed.test/admin/invitations/accept?token={Uri.EscapeDataString(token)}", mail.Body);
        Assert.Contains("(UTC)", mail.Body);
        Assert.Empty(await RawOutboxAsync());
        Assert.Equal(SendOutcome.Idle, await _app.Service<MailSender>().SendOnceAsync());
    }

    [Fact]
    public async Task メールアドレスでないログインIDには積まず_画面のURLで渡せる()
    {
        var (invitation, token) = await InviteAsync("bob");

        Assert.False(invitation.GetProperty("mailSent").GetBoolean());
        Assert.Contains(token, invitation.GetProperty("url").GetString());
        Assert.Equal(SendOutcome.Idle, await _app.Service<MailSender>().SendOnceAsync());
    }

    [Fact]
    public async Task メールを有効にしていなければ積まない()
    {
        using var disabled = new TestApp();

        var (invitation, _) = await InviteAsync("bob@example.com", disabled);

        Assert.False(invitation.GetProperty("mailSent").GetBoolean());
        Assert.Empty(disabled.MailTransport.Sent);
    }

    [Fact]
    public async Task 招待の出し直しは新しい1通を積む_件名と本文は招いた側の言語()
    {
        var (_, _) = await InviteAsync("bob@example.com");
        var users = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/users/");
        var id = users.EnumerateArray().Single(u => u.GetProperty("loginId").GetString() == "bob@example.com").GetProperty("id").GetString();

        var reissued = (await (await _app.AdminClient().PostAsync($"/admin/api/users/{id}/invitation", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation");

        Assert.True(reissued.GetProperty("mailSent").GetBoolean());
        var mailer = _app.Service<MailSender>();
        Assert.Equal(SendOutcome.Sent, await mailer.SendOnceAsync());
        Assert.Equal(SendOutcome.Sent, await mailer.SendOnceAsync());
        Assert.Equal(2, _app.MailTransport.Sent.Count);
        Assert.Equal(InvitationMailTexts.For("ja").Subject, _app.MailTransport.Sent[0].Subject);
    }

    [Fact]
    public async Task 一時的な失敗は再送待ちへ_恒久的な失敗はデッドレターへ回し_件数が分かる()
    {
        await InviteAsync("bob@example.com");
        _app.MailTransport.Failure = new MailDeliveryException("一時的に送れない", isTransient: true);
        Assert.Equal(SendOutcome.Rescheduled, await _app.Service<MailSender>().SendOnceAsync());

        var status = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/mail/status");
        Assert.True(status.GetProperty("ready").GetBoolean());
        Assert.Equal(1, status.GetProperty("pendingCount").GetInt32());
        Assert.Equal(0, status.GetProperty("deadLetterCount").GetInt32());

        // 再送の時刻まで進めて、今度は恒久的な失敗
        _app.Time.Advance(TimeSpan.FromHours(2));
        _app.MailTransport.Failure = new MailDeliveryException("宛先が無い", isTransient: false);
        Assert.Equal(SendOutcome.DeadLettered, await _app.Service<MailSender>().SendOnceAsync());

        await _app.AdminClient().PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        status = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/mail/status");
        Assert.Equal(0, status.GetProperty("pendingCount").GetInt32());
        Assert.Equal(1, status.GetProperty("deadLetterCount").GetInt32());
    }

    [Fact]
    public async Task 鍵が変わって復号できない行はデッドレターへ回す()
    {
        await InviteAsync("bob@example.com");
        // 別の鍵で積んだ行を模す: 中身を壊す
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_app.DbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("UPDATE MailOutbox SET PayloadProtected = 'broken'");
        }

        Assert.Equal(SendOutcome.DeadLettered, await _app.Service<MailSender>().SendOnceAsync());
        Assert.Empty(_app.MailTransport.Sent);
    }
}
