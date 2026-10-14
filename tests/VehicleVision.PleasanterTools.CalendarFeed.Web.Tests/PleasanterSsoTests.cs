using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>Pleasanter のログインで管理画面へ入る（Issue #28。Questionnaire の Pleasanter SSO に則る）。Pleasanter は <see cref="FakePleasanterSso"/>。</summary>
public sealed class PleasanterSsoTests : IDisposable
{
    private const string Prefix = "CALENDARFEED_PLEASANTERSSO_";
    private const string Check = "/admin/api/pleasanter-sso/check";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private FakePleasanterSso Pleasanter => _app.PleasanterSso;

    private static int _next;

    /// <summary>入れ物ごとに別の接続元にする（確認の入口にも IP ごとの枠がある）。</summary>
    private sealed class Browser(HttpClient client)
    {
        private readonly string _ip = $"198.51.100.{Interlocked.Increment(ref _next) % 250 + 1}";
        public Dictionary<string, string> Cookies { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? json = null, Dictionary<string, string>? headers = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = json is null ? null : JsonContent.Create(json) };
            request.Headers.Add(RemoteIpStartupFilter.Header, _ip);
            if (Cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            foreach (var (k, v) in headers ?? [])
            {
                request.Headers.Add(k, v);
            }

            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var lines))
            {
                foreach (var line in lines)
                {
                    var pair = line.Split(';', 2)[0];
                    var at = pair.IndexOf('=');
                    if (pair[(at + 1)..].Length == 0 || line.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                    {
                        Cookies.Remove(pair[..at]);
                    }
                    else
                    {
                        Cookies[pair[..at]] = pair[(at + 1)..];
                    }
                }
            }

            return response;
        }

        public Task<HttpResponseMessage> CheckAsync(Dictionary<string, string>? headers = null) =>
            SendAsync(HttpMethod.Post, Check, new { }, headers);

        public async Task<HttpStatusCode> MeAsync() => (await SendAsync(HttpMethod.Get, "/admin/api/me/")).StatusCode;

        /// <summary>Pleasanter の cookie を持たせる（ブラウザが Pleasanter にログインした状態）。</summary>
        public Browser WithPleasanterCookie(string value)
        {
            Cookies[FakePleasanterSso.AuthCookie] = value;
            return this;
        }
    }

    private Browser NewBrowser() => new(_app.RawClient());

    private async Task EnableAsync(Dictionary<string, string?>? overrides = null, TestApp? app = null)
    {
        var values = new Dictionary<string, string?>
        {
            [Prefix + "ENABLED"] = "true",
            [Prefix + "INTERNALBASEURL"] = "https://pleasanter.internal",
            [Prefix + "LOGINURL"] = "/users/login",
        };
        foreach (var (k, v) in overrides ?? [])
        {
            values[k] = v;
        }

        var response = await (app ?? _app).AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<AdminUser> AddUserAsync(string loginId, string role = AdminRoles.Editor, bool disabled = false)
    {
        var users = _app.Service<IAdminUserStore>();
        var user = (await users.CreateAsync(loginId, PasswordHasher.Hash("x-unknowable-password"), role, "ja", _app.Time.Now))!;
        if (disabled)
        {
            await users.SetDisabledAsync(user.Id, true);
        }

        return user;
    }

    private static string CodeOf(JsonElement json) => json.GetProperty("code").GetString()!;

    // ---- 無効・設定 -----------------------------------------------------------------

    [Fact]
    public async Task 既定では無効で_入口は404_状態にも出ない()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await NewBrowser().WithPleasanterCookie("a").CheckAsync()).StatusCode);

        var status = await _app.Client().GetFromJsonAsync<JsonElement>("/admin/api/status");
        Assert.False(status.GetProperty("pleasanterSso").GetProperty("enabled").GetBoolean());
        Assert.Empty(Pleasanter.Calls);
    }

    [Fact]
    public async Task 有効にするには内部URLとログインURLが要る_書き間違いは保存を断る()
    {
        var admin = _app.AdminClient();
        async Task<HttpStatusCode> Put(Dictionary<string, string?> values) =>
            (await admin.PutAsJsonAsync("/admin/api/settings/", new { values })).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "ENABLED"] = "true" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "ENABLED"] = "true", [Prefix + "INTERNALBASEURL"] = "https://p.internal" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "LOGINURL"] = "javascript:alert(1)" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "LOGINURL"] = "//evil.example/" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "REGISTERROLE"] = "Root" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "ALLOWEDDEPTIDS"] = "1,abc" }));

        // 本アプリの cookie と重なる指定は、Pleasanter へ本アプリのセッションを送ってしまうので断る
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "COOKIENAMES"] = "calendarfeed_admin" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(new() { [Prefix + "COOKIENAMES"] = "calendar" }));
        Assert.Equal(HttpStatusCode.OK, await Put(new() { [Prefix + "COOKIENAMES"] = ".AspNetCore.Cookies, MyCookie" }));
        Assert.False(_app.Service<RuntimeSettings>().Current.PleasanterSso.Enabled);
    }

    [Fact]
    public async Task 有効にすると状態にログインURLと釦の文字が出る()
    {
        await EnableAsync(new() { [Prefix + "BUTTONLABEL"] = "Pleasanter でログイン", [Prefix + "LOGOUTURL"] = "/users/logout" });

        var sso = (await _app.Client().GetFromJsonAsync<JsonElement>("/admin/api/status")).GetProperty("pleasanterSso");
        Assert.True(sso.GetProperty("enabled").GetBoolean());
        Assert.Equal("/users/login", sso.GetProperty("loginUrl").GetString());
        Assert.Equal("/users/logout", sso.GetProperty("logoutUrl").GetString());
        Assert.Equal("Pleasanter でログイン", sso.GetProperty("buttonLabel").GetString());
    }

    // ---- ログイン -------------------------------------------------------------------

    [Fact]
    public async Task Pleasanterにログインしていなければ_まだと答える()
    {
        await EnableAsync();
        var browser = NewBrowser();

        // cookie が無ければ、Pleasanter へ問い合わせもしない
        var none = await browser.CheckAsync();
        Assert.Equal("unauthenticated", (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Empty(Pleasanter.Calls);

        // cookie はあるが、Pleasanter が知らない（ログアウト済み）
        var stale = await browser.WithPleasanterCookie("stale").CheckAsync();
        Assert.Equal("unauthenticated", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());
    }

    [Fact]
    public async Task Pleasanterのログインで入れる_ログイン_ID_で本アプリの管理者へ結び付く()
    {
        await EnableAsync();
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理 太郎"));
        var browser = NewBrowser().WithPleasanterCookie("c1");

        var response = await browser.CheckAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("signedIn", json.GetProperty("status").GetString());
        Assert.Equal("done", json.GetProperty("next").GetString());

        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());
        var me = await (await browser.SendAsync(HttpMethod.Get, "/admin/api/me/")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("viaPleasanterSso").GetBoolean());
        Assert.False(me.GetProperty("viaSaml").GetBoolean());

        // セッションへ、Pleasanter のテナント・利用者・確かめた時刻が載る
        var sso = await _app.Service<IAdminSessionStore>().GetSsoAsync(browser.Cookies[AdminSessionGuard.SessionCookie]);
        Assert.Equal(new PleasanterSsoKeys(1, 10, _app.Time.Now), sso!.Pleasanter);
    }

    [Fact]
    public async Task 転送するのは設定した名前の_cookie_だけ_本アプリのセッションと_API_キーは送らない()
    {
        await EnableAsync(new() { [Prefix + "COOKIENAMES"] = ".AspNetCore.Cookies" });
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理"));
        var browser = NewBrowser().WithPleasanterCookie("c1");
        browser.Cookies[AdminSessionGuard.SessionCookie] = "mine-secret";
        browser.Cookies["Other"] = "x";

        await browser.CheckAsync();

        var call = Assert.Single(Pleasanter.Calls);
        Assert.Equal("/api/users/get", call.Path);
        Assert.Equal($"{FakePleasanterSso.AuthCookie}=c1", call.Cookie);
        Assert.DoesNotContain("ApiKey", call.Body);
        Assert.DoesNotContain("mine-secret", call.Cookie);
    }

    [Fact]
    public async Task 本アプリに居ない利用者は通さない_既定は拒否()
    {
        await EnableAsync();
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 11, "stranger", "他人"));

        var response = await NewBrowser().WithPleasanterCookie("c1").CheckAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("unknown-user", CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>()));
        var failed = (await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.PleasanterSsoFailed })).Single();
        Assert.Equal("unknown-user", failed.Detail);
        Assert.Equal("stranger", failed.LoginId);
    }

    [Fact]
    public async Task 無効化された利用者は通さない()
    {
        await EnableAsync();
        await AddUserAsync("bob", disabled: true);
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 12, "bob", "ボブ"));

        var response = await NewBrowser().WithPleasanterCookie("c1").CheckAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("disabled", CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task 最初の管理者をPleasanterから作らせない()
    {
        // 管理者が 0 人のときは設定を画面から変えられないので、外部設定で有効にする
        using var fresh = new TestApp(setUpAdmin: false, extraSettings: new()
        {
            [Prefix + "ENABLED"] = "true",
            [Prefix + "INTERNALBASEURL"] = "https://pleasanter.internal",
            [Prefix + "LOGINURL"] = "/users/login",
            [Prefix + "UNKNOWNUSER"] = "Register",
            [Prefix + "ALLOWEDDEPTIDS"] = "1",
        });
        fresh.PleasanterSso.Login("c1", new FakePleasanterSso.User(1, 10, "first", "最初"));

        var response = await new Browser(fresh.RawClient()).WithPleasanterCookie("c1").CheckAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("setup-required", CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(0, await fresh.Service<IAdminUserStore>().CountAsync());
    }

    // ---- 自動登録（JIT）と所属 ---------------------------------------------------------

    [Fact]
    public async Task 自動登録は許可する所属を決めたときだけ_決めていなければ作らない()
    {
        await EnableAsync(new() { [Prefix + "UNKNOWNUSER"] = "Register", [Prefix + "REGISTERROLE"] = AdminRoles.Auditor });
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 13, "newcomer", "新人", DeptId: 5));

        var response = await NewBrowser().WithPleasanterCookie("c1").CheckAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not-allowed", CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Null(await _app.Service<IAdminUserStore>().FindForLoginAsync("newcomer"));
    }

    [Fact]
    public async Task 許可した組織の利用者は_設定の役割で自動登録されて入れる()
    {
        await EnableAsync(new() { [Prefix + "UNKNOWNUSER"] = "Register", [Prefix + "REGISTERROLE"] = AdminRoles.Auditor, [Prefix + "ALLOWEDDEPTIDS"] = "5, 6" });
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 13, "newcomer", "新人", DeptId: 5));
        var browser = NewBrowser().WithPleasanterCookie("c1");

        var response = await browser.CheckAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await _app.Service<IAdminUserStore>().FindForLoginAsync("newcomer"))!.Value.User;
        Assert.Equal(AdminRoles.Auditor, created.Role);
        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());
        Assert.Contains(await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.PleasanterSsoRegistered }), e => e.TargetId == created.Id);
    }

    [Fact]
    public async Task 許可した所属に居ない利用者は断る()
    {
        await EnableAsync(new() { [Prefix + "UNKNOWNUSER"] = "Register", [Prefix + "ALLOWEDDEPTIDS"] = "5" });
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 14, "outsider", "部外者", DeptId: 9));

        var response = await NewBrowser().WithPleasanterCookie("c1").CheckAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not-allowed", CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Null(await _app.Service<IAdminUserStore>().FindForLoginAsync("outsider"));
    }

    [Fact]
    public async Task グループの所属_子グループも含む_は_API_キーで確かめる()
    {
        await EnableAsync(new() { [Prefix + "UNKNOWNUSER"] = "Register", [Prefix + "ALLOWEDGROUPIDS"] = "100" });
        Pleasanter.Groups[100] = new(100, false, [], ["Group,200,"]);
        Pleasanter.Groups[200] = new(200, false, ["User,15,false"], []);
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 15, "member", "所属者", DeptId: 9));
        Pleasanter.Login("c2", new FakePleasanterSso.User(1, 16, "other", "他", DeptId: 9));

        Assert.Equal(HttpStatusCode.OK, (await NewBrowser().WithPleasanterCookie("c1").CheckAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await NewBrowser().WithPleasanterCookie("c2").CheckAsync()).StatusCode);

        // API キーを載せた問い合わせに、cookie は載っていない
        Assert.All(Pleasanter.Calls.Where(c => c.Body.Contains("ApiKey")), c => Assert.Null(c.Cookie));
    }

    [Fact]
    public async Task 無効なグループの所属は認めない()
    {
        await EnableAsync(new() { [Prefix + "UNKNOWNUSER"] = "Register", [Prefix + "ALLOWEDGROUPIDS"] = "100" });
        Pleasanter.Groups[100] = new(100, true, ["User,15,false"], []);
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 15, "member", "所属者", DeptId: 9));

        Assert.Equal(HttpStatusCode.Forbidden, (await NewBrowser().WithPleasanterCookie("c1").CheckAsync()).StatusCode);
    }

    // ---- 2 要素 ----------------------------------------------------------------------

    [Fact]
    public async Task 登録済みの2要素は_Pleasanter経由でも省かせない()
    {
        var admin = _app.AdminClient();
        var begin = await (await admin.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) })).StatusCode);
        await EnableAsync();
        _app.Time.Advance(TimeSpan.FromMinutes(2));
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理"));
        var browser = NewBrowser().WithPleasanterCookie("c1");

        var json = await (await browser.CheckAsync()).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("totp", json.GetProperty("next").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());

        var totp = await browser.SendAsync(HttpMethod.Post, "/admin/api/login/totp", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());

        // 途中の段階から、Pleasanter の鍵が引き継がれる（再検証が効く）
        var sso = await _app.Service<IAdminSessionStore>().GetSsoAsync(browser.Cookies[AdminSessionGuard.SessionCookie]);
        Assert.Equal(10, sso!.Pleasanter!.UserId);
    }

    // ---- 失敗の扱い --------------------------------------------------------------------

    [Theory]
    [InlineData("500")]
    [InlineData("302")]
    [InlineData("html")]
    public async Task Pleasanterの応答が成り立たなければ_成功と扱わず503(string failure)
    {
        await EnableAsync();
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理"));
        switch (failure)
        {
            case "500": Pleasanter.Fail = HttpStatusCode.InternalServerError; break;
            case "302": Pleasanter.Fail = HttpStatusCode.Found; break;
            default: Pleasanter.ReturnHtml = true; break;
        }

        var browser = NewBrowser().WithPleasanterCookie("c1");
        var response = await browser.CheckAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upstream-error", CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());
    }

    [Fact]
    public async Task 利用者のAPI利用が禁止されていても_本アプリのAPIキーで引き直して入れる()
    {
        await EnableAsync();
        Pleasanter.ApiForbidden = true;
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理"));
        var browser = NewBrowser().WithPleasanterCookie("c1");

        Assert.Equal(HttpStatusCode.OK, (await browser.CheckAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());

        // cookie を送る問い合わせに API キーは載らず、API キーの問い合わせに cookie は載らない
        Assert.All(Pleasanter.Calls, c => Assert.Equal(c.Body.Contains("ApiKey"), c.Cookie is null));
    }

    [Fact]
    public async Task 確認の入口は別のサイトからの要求を断る()
    {
        await EnableAsync();
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理"));
        var browser = NewBrowser().WithPleasanterCookie("c1");

        var response = await browser.CheckAsync(new() { ["Sec-Fetch-Site"] = "cross-site" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Pleasanter.Calls);
    }

    // ---- 再検証 -----------------------------------------------------------------------

    private async Task<Browser> SignedInAsync(string cookie = "c1", int userId = 10)
    {
        await EnableAsync(new() { [Prefix + "REVALIDATEMINUTES"] = "5" });
        Pleasanter.Login(cookie, new FakePleasanterSso.User(1, userId, TestApp.AdminLoginId, "管理"));
        var browser = NewBrowser().WithPleasanterCookie(cookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.CheckAsync()).StatusCode);
        Pleasanter.Calls.Clear();
        return browser;
    }

    [Fact]
    public async Task 間隔の内は問い合わせず_過ぎたら確かめ直して続けられる()
    {
        var browser = await SignedInAsync();

        _app.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());
        Assert.Empty(Pleasanter.Calls);

        _app.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());
        Assert.Single(Pleasanter.Calls);

        // 確かめた時刻が進んでいる（すぐにはまた聞かない）
        Assert.Equal(HttpStatusCode.OK, await browser.MeAsync());
        Assert.Single(Pleasanter.Calls);
    }

    [Fact]
    public async Task Pleasanterでログアウトしたら_次の確認で本アプリからも落ちる()
    {
        var browser = await SignedInAsync();
        Pleasanter.Sessions.Clear();

        _app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());
        Assert.False(browser.Cookies.ContainsKey(AdminSessionGuard.SessionCookie));
    }

    [Fact]
    public async Task Pleasanterで別の人がログインし直したら_落ちる()
    {
        var browser = await SignedInAsync();
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 99, TestApp.AdminLoginId, "別人"));

        _app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());
    }

    [Fact]
    public async Task Pleasanterに問い合わせられなければ_セッションを延ばさず落として503()
    {
        var browser = await SignedInAsync();
        Pleasanter.Fail = HttpStatusCode.BadGateway;

        _app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await browser.MeAsync());
        Pleasanter.Fail = null;
        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());
    }

    [Fact]
    public async Task 機能を無効にすると_それで入ったセッションは次の要求で落ちる()
    {
        var browser = await SignedInAsync();
        var off = await _app.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [Prefix + "ENABLED"] = "false" } });
        Assert.True(off.IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await browser.MeAsync());
    }

    [Fact]
    public async Task パスワードで入ったセッションは_再検証の対象にならない()
    {
        await EnableAsync();
        var admin = _app.AdminClient();

        _app.Time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Empty(Pleasanter.Calls);
    }

    // ---- 接続の試験 -------------------------------------------------------------------

    [Fact]
    public async Task 接続の試験は_設定の権限を持つ人だけ_cookie_で本人が返る()
    {
        // 有効にしていなくても試せる（有効にする前に確かめたい）
        await EnableAsync(new() { [Prefix + "ENABLED"] = "false" });
        Pleasanter.Login("c1", new FakePleasanterSso.User(1, 10, TestApp.AdminLoginId, "管理 太郎"));
        var admin = _app.AdminClient();
        admin.DefaultRequestHeaders.Add("Cookie", $"{FakePleasanterSso.AuthCookie}=c1");

        var response = await admin.PostAsJsonAsync("/admin/api/pleasanter-sso/test", new { });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Authenticated", json.GetProperty("status").GetString());
        Assert.Equal(TestApp.AdminLoginId, json.GetProperty("loginId").GetString());
        Assert.True(json.GetProperty("registered").GetBoolean());

        // ログインしていない人は使えない
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/admin/api/pleasanter-sso/test", new { })).StatusCode);
    }
}
