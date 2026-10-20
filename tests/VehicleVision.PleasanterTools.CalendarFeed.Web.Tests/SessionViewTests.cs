using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>端末の一覧の「入った方式」と、管理者による利用者の端末の個別の失効（Issue #47）。</summary>
public sealed class SessionViewTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<AdminUser> AddUserAsync(string loginId = "bob") =>
        (await _app.Service<IAdminUserStore>().CreateAsync(loginId, PasswordHasher.Hash("x-unknowable-password"), AdminRoles.Editor, "ja"))!;

    private Task<string> SessionOfAsync(AdminUser user, SessionSso? sso = null) =>
        _app.Service<IAdminSessionStore>().CreateAsync(user.Id, AdminSessionKinds.Session, TimeSpan.FromHours(8), default, "203.0.113.5", "TestAgent", sso);

    private async Task<JsonElement> ListAsync(AdminUser user) =>
        await _app.AdminClient().GetFromJsonAsync<JsonElement>($"/admin/api/users/{user.Id}/sessions");

    [Fact]
    public async Task 自分の端末の一覧に_入った方式が出る()
    {
        var sessions = await _app.AdminClient().GetFromJsonAsync<JsonElement>("/admin/api/me/sessions/");

        Assert.Equal("password", sessions[0].GetProperty("via").GetString());
    }

    [Fact]
    public async Task 管理者が見る端末の一覧は_方式つきで_セッションの現物は出さない()
    {
        var bob = await AddUserAsync();
        var password = await SessionOfAsync(bob);
        await SessionOfAsync(bob, new SessionSso(Saml: new SamlSessionKeys("bob", "idx")));
        await SessionOfAsync(bob, new SessionSso(Pleasanter: new PleasanterSsoKeys(1, 7, _app.Time.Now)));

        var list = await ListAsync(bob);

        Assert.Equal(["password", "pleasanter", "saml"], list.EnumerateArray().Select(s => s.GetProperty("via").GetString()!).Order().ToArray());
        Assert.DoesNotContain(password, list.GetRawText());
    }

    [Fact]
    public async Task 端末を1つだけ失効させられる_ほかの端末は残り_監査ログに残る()
    {
        var bob = await AddUserAsync();
        await SessionOfAsync(bob);
        await SessionOfAsync(bob, new SessionSso(Saml: new SamlSessionKeys("bob", null)));
        var handle = (await ListAsync(bob))[0].GetProperty("handle").GetString()!;

        var response = await _app.AdminClient().DeleteAsync($"/admin/api/users/{bob.Id}/sessions/{handle}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, (await ListAsync(bob)).GetArrayLength());
        Assert.Contains(
            await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.UserSessionRevoked }),
            e => e.TargetId == bob.Id.ToString("D"));
    }

    [Fact]
    public async Task 知らない端末_知らない利用者は404_自分の端末はここからは失効させられない()
    {
        var bob = await AddUserAsync();
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/admin/api/users/{bob.Id}/sessions/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/api/users/nope/sessions")).StatusCode);

        var me = await admin.GetFromJsonAsync<JsonElement>("/admin/api/me/");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/admin/api/users/{me.GetProperty("id").GetString()}/sessions/x")).StatusCode);
    }

    [Fact]
    public async Task ログインしていない人は見られない()
    {
        var bob = await AddUserAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().GetAsync($"/admin/api/users/{bob.Id}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().DeleteAsync($"/admin/api/users/{bob.Id}/sessions/x")).StatusCode);
    }
}
