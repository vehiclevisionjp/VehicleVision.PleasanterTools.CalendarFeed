using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>U7・T3〜T5: 再登録の本人確認と、認証段階を取り違えないこと。</summary>
public sealed class AdminTotpReenrollTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static Task<HttpResponseMessage> BeginAsync(HttpClient client, string? password = TestApp.AdminPassword) =>
        client.PostAsJsonAsync("/admin/api/me/totp/begin", new { password });

    private Task<HttpResponseMessage> CompleteAsync(HttpClient client, string secret) =>
        client.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });

    private async Task<string> BeginSecretAsync(HttpClient client)
    {
        var response = await BeginAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
    }

    private async Task<HttpClient> EnrolledAsync()
    {
        var client = _app.AdminClient();
        var secret = await BeginSecretAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, secret)).StatusCode);
        _app.Time.Advance(TimeSpan.FromMinutes(6));
        return client;
    }

    private async Task<Guid> UserIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/admin/api/me/")).GetProperty("id").GetGuid();

    private static string CookieOf(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(name + "=", StringComparison.Ordinal))
            .Split(';')[0][(name.Length + 1)..];

    private async Task<HttpResponseMessage> RawPostAsync(string path, string cookies, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api" + path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        request.Headers.Add("Cookie", cookies);
        return await _app.RawClient().SendAsync(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong password")]
    public async Task 登録済みはパスワードの再確認なしに鍵を作り直せない(string? password)
    {
        var client = await EnrolledAsync();
        var userId = await UserIdAsync(client);
        var denied = await BeginAsync(client, password);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(ServerMessages.Get(ServerMessageKeys.CurrentPasswordRejected, "en"),
            (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(denied.Headers.Contains("Set-Cookie"));
        Assert.Null(await _app.Service<IAdminUserStore>().GetTotpPendingAsync(userId));
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(client, TotpService.GenerateSecret())).StatusCode);
    }

    [Fact]
    public async Task 本文なしも再確認を省略できず_未入力は失敗回数に数えない()
    {
        var client = await EnrolledAsync();
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/admin/api/me/totp/begin", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await BeginAsync(client)).StatusCode);
    }

    [Fact]
    public async Task 誤ったパスワードは5回目も401で_次から正解でも423になる()
    {
        var client = await EnrolledAsync();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await BeginAsync(client, "wrong password")).StatusCode);
        }

        Assert.Equal((HttpStatusCode)423, (await BeginAsync(client)).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await CompleteAsync(client, TotpService.GenerateSecret())).StatusCode);
        _app.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await BeginAsync(client)).StatusCode);
    }

    [Fact]
    public async Task 再登録はログインと同じ枠で10回までで_拒否時は鍵もセッションも発行しない()
    {
        var client = await EnrolledAsync();
        var userId = await UserIdAsync(client);
        var secret = await BeginSecretAsync(client);
        // 1 回目の begin と、別のログイン試行 9 回で共有枠を使い切る。
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await _app.Client().PostAsJsonAsync("/admin/api/login", new { loginId = "nobody", password = "wrong password" })).StatusCode);
        }

        var limited = await BeginAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.False(limited.Headers.Contains("Set-Cookie"));
        Assert.Equal(secret, await _app.Service<IAdminUserStore>().GetTotpPendingAsync(userId));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CompleteAsync(client, secret)).StatusCode);
    }

    [Fact]
    public async Task 再確認後に再登録でき_古い復旧コードは消え_新しいコードだけ使える()
    {
        var client = await EnrolledAsync();
        var firstSecret = await BeginSecretAsync(client);
        var firstComplete = await CompleteAsync(client, firstSecret);
        var oldCodes = (await firstComplete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
            .EnumerateArray().Select(c => c.GetString()!).ToArray();
        var begin = await BeginAsync(client);
        var reenrollId = CookieOf(begin, AdminSessionGuard.ReenrollCookie);
        var secret = (await begin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var session = await _app.Service<IAdminSessionStore>().FindAsync(reenrollId);
        Assert.Equal(AdminSessionGuard.ReenrollKind, session!.Kind);
        Assert.Equal(_app.Time.Now + TimeSpan.FromMinutes(5), session.ExpiresAt);
        var complete = await CompleteAsync(client, secret);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var newCodes = (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
            .EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(10, newCodes.Length);
        Assert.Empty(oldCodes.Intersect(newCodes));
        Assert.Null(await _app.Service<IAdminSessionStore>().FindAsync(reenrollId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(client, secret)).StatusCode);

        _app.Time.Advance(TimeSpan.FromMinutes(6));
        var other = _app.Client();
        await other.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await other.PostAsJsonAsync("/admin/api/login/recovery", new { code = oldCodes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await other.PostAsJsonAsync("/admin/api/login/recovery", new { code = newCodes[0] })).StatusCode);
    }

    [Fact]
    public async Task サーバに途中鍵が残っていても再確認のcookieなしには完了できない()
    {
        var client = await EnrolledAsync();
        var secret = await BeginSecretAsync(client);
        var sessionId = await _app.Service<IAdminSessionStore>().CreateAsync(
            await UserIdAsync(client), AdminSessionKinds.Session, TimeSpan.FromHours(8));
        var denied = await RawPostAsync("/me/totp/complete", $"{AdminSessionGuard.SessionCookie}={sessionId}",
            new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });

        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(ServerMessages.Get(ServerMessageKeys.EnrollmentRestartRequired, "en"),
            (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task 再登録のcookieは単独でも名前を変えてもログインやPendingに使えない()
    {
        var client = await EnrolledAsync();
        var begin = await BeginAsync(client);
        var id = CookieOf(begin, AdminSessionGuard.ReenrollCookie);
        foreach (var name in new[] { AdminSessionGuard.ReenrollCookie, AdminSessionGuard.PendingCookie, AdminSessionGuard.SessionCookie })
        {
            var cookies = $"{name}={id}";
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await RawPostAsync("/login/totp", cookies, new { code = "123456" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await RawPostAsync("/me/totp/complete", cookies, new { code = "123456" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await RawPostAsync("/me/totp/begin", cookies, new { password = TestApp.AdminPassword })).StatusCode);
        }
    }

    [Fact]
    public async Task Pendingや別人の再登録cookieでは途中鍵があっても完了できない()
    {
        var client = await EnrolledAsync();
        var userId = await UserIdAsync(client);
        var secret = await BeginSecretAsync(client);
        var sessions = _app.Service<IAdminSessionStore>();
        var sessionId = await sessions.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromHours(8));
        var invited = await client.PostAsJsonAsync("/admin/api/users", new { loginId = "other", role = "Editor" });
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var otherId = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("id").GetGuid();
        foreach (var (id, kind) in new[]
        {
            (userId, AdminSessionKinds.Pending),
            (otherId, AdminSessionGuard.ReenrollKind),
            (userId, AdminSessionKinds.Session),
        })
        {
            var invalid = await sessions.CreateAsync(id, kind, TimeSpan.FromMinutes(5));
            var denied = await RawPostAsync("/me/totp/complete",
                $"{AdminSessionGuard.SessionCookie}={sessionId}; {AdminSessionGuard.ReenrollCookie}={invalid}",
                new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        }
    }

    [Fact]
    public async Task 再登録は5分で切れ_通常セッションは残り_再開すると前の途中セッションは失効する()
    {
        var client = await EnrolledAsync();
        var first = CookieOf(await BeginAsync(client), AdminSessionGuard.ReenrollCookie);
        var second = await BeginAsync(client);
        var id = CookieOf(second, AdminSessionGuard.ReenrollCookie);
        var secret = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        Assert.Null(await _app.Service<IAdminSessionStore>().FindAsync(first));
        _app.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(client, secret)).StatusCode);
        Assert.Null(await _app.Service<IAdminSessionStore>().FindAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/me/")).StatusCode);
    }

    [Theory]
    [InlineData("/logout")]
    [InlineData("/saml/logout")]
    public async Task ログアウトで再登録の途中もサーバ側で失効する(string path)
    {
        var client = await EnrolledAsync();
        var begin = await BeginAsync(client);
        var id = CookieOf(begin, AdminSessionGuard.ReenrollCookie);
        var secret = (await begin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var sessions = _app.Service<IAdminSessionStore>();
        var userId = await UserIdAsync(client);
        var sessionId = await sessions.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromHours(8));
        var pendingId = await sessions.CreateAsync(userId, AdminSessionKinds.Pending, TimeSpan.FromMinutes(5));
        using var request = new HttpRequestMessage(path == "/saml/logout" ? HttpMethod.Get : HttpMethod.Post, "/admin/api" + path);
        request.Headers.Add("Cookie",
            $"{AdminSessionGuard.SessionCookie}={sessionId}; {AdminSessionGuard.PendingCookie}={pendingId}; {AdminSessionGuard.ReenrollCookie}={id}");
        var response = await _app.RawClient().SendAsync(request);
        if (path == "/saml/logout")
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/admin/", response.Headers.Location!.OriginalString);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        foreach (var name in new[] { AdminSessionGuard.SessionCookie, AdminSessionGuard.PendingCookie, AdminSessionGuard.ReenrollCookie })
        {
            Assert.Equal("", CookieOf(response, name));
        }

        Assert.Null(await sessions.FindAsync(sessionId));
        Assert.Null(await sessions.FindAsync(pendingId));
        Assert.Null(await sessions.FindAsync(id));
        var otherSession = await sessions.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromHours(8));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await RawPostAsync("/me/totp/complete",
                $"{AdminSessionGuard.SessionCookie}={otherSession}; {AdminSessionGuard.ReenrollCookie}={id}",
                new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) })).StatusCode);
    }
}
