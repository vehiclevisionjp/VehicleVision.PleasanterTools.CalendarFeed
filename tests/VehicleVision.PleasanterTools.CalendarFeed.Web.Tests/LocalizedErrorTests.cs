using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>サーバが返すエラー文を、要求の言語（<c>Accept-Language</c>）で返す（Issue #58）。</summary>
public sealed class LocalizedErrorTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private static HttpRequestMessage Login(string? acceptLanguage, string password = "wrong password!!")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/login")
        {
            Content = JsonContent.Create(new { loginId = TestApp.AdminLoginId, password }),
        };
        if (acceptLanguage is not null)
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        request.Headers.Add(RemoteIpStartupFilter.Header, "203.0.113.50");
        return request;
    }

    [Theory]
    [InlineData("ja", "ログイン ID またはパスワードが違います")]
    [InlineData("en", "The login ID or password is incorrect")]
    [InlineData("de", "Anmelde-ID")]
    [InlineData("en-US,ja;q=0.5", "The login ID or password is incorrect")]
    [InlineData("fr, ja;q=0.8", "ログイン ID またはパスワードが違います")]
    [InlineData(null, "The login ID or password is incorrect")]
    [InlineData("fr", "The login ID or password is incorrect")]
    public async Task ログインの失敗は_要求の言語で返る(string? acceptLanguage, string expectedContains)
    {
        _ = _app.AdminClient();

        var response = await _app.Client().SendAsync(Login(acceptLanguage));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(expectedContains, await ErrorOf(response));
    }

    [Fact]
    public async Task ログインの失敗の理由は_どの言語でも同じ文言_居ない_違う_で区別できない()
    {
        _ = _app.AdminClient();
        var client = _app.Client();

        var wrongPassword = await ErrorOf(await client.SendAsync(Login("en")));
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/login") { Content = JsonContent.Create(new { loginId = "nobody", password = "wrong password!!" }) };
        request.Headers.Add("Accept-Language", "en");
        request.Headers.Add(RemoteIpStartupFilter.Header, "203.0.113.50");
        var unknownUser = await ErrorOf(await client.SendAsync(request));

        Assert.Equal(wrongPassword, unknownUser);
    }

    [Fact]
    public async Task 権限が無いときの文言も_言語ごとに返る()
    {
        var bob = (await _app.Service<IAdminUserStore>().CreateAsync(
            "auditor@example.com", Security.PasswordHasher.Hash("correct horse battery staple"), Admin.AdminRoles.Auditor, "ja"))!;
        var client = _app.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/admin/api/login", new { loginId = bob.LoginId, password = "correct horse battery staple" })).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/settings/");
        request.Headers.Add("Accept-Language", "ko");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var message = await ErrorOf(response);
        Assert.Contains("settings.manage", message);
        Assert.DoesNotContain("権限", message);
    }

    [Fact]
    public async Task 入力の検証の文言は_言語ごと_パスワードの条件も()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/feeds") { Content = JsonContent.Create(new { name = "", siteId = 1 }) };
        request.Headers.Add("Accept-Language", "en");
        var response = await _app.AdminClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Name must be 1 to 200 characters.", await ErrorOf(response));

        using var fresh = new TestApp(setUpAdmin: false);
        var setup = new HttpRequestMessage(HttpMethod.Post, "/admin/api/setup") { Content = JsonContent.Create(new { loginId = "first", password = "short" }) };
        setup.Headers.Add("Accept-Language", "en");
        var tooShort = await fresh.Client().SendAsync(setup);

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("at least", await ErrorOf(tooShort));
    }

    [Theory]
    [InlineData("ja,en;q=0.5", "ja")]
    [InlineData("en-US,en;q=0.9,ja;q=0.8", "en")]
    [InlineData("fr,de;q=0.7", "de")]
    [InlineData("fr, es-MX", "es")]
    [InlineData("ja;q=0, en", "en")]
    [InlineData("fr", null)]
    [InlineData("", null)]
    public void Accept_Languageは_品質値の高い順に_対応する言語を選ぶ(string header, string? expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.AcceptLanguage = header;

        Assert.Equal(expected, RequestLanguage.Of(http));
    }
}
