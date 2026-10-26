using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Web;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>SAML 2.0 で管理画面へ入る（Issue #28。Questionnaire の SAML に則る）。IdP は <see cref="FakeIdp"/>。</summary>
public sealed class SamlTests : IDisposable
{
    private const string Prefix = "CALENDARFEED_SAML_";
    private readonly TestApp _app = new();
    private readonly FakeIdp _idp = new();

    public void Dispose()
    {
        _app.Dispose();
        _idp.Dispose();
    }

    /// <summary>手で扱う cookie の入れ物（Secure の cookie は http では自動で送られないので、自分で持つ）。</summary>
    private sealed class Jar
    {
        private static int _next;

        /// <summary>入れ物ごとに別の接続元にする（ログインのレート制限は IP ごと。多数の試行を 1 つの IP に集めない）。</summary>
        public string Ip { get; } = $"192.0.2.{Interlocked.Increment(ref _next) % 250 + 1}";

        public Dictionary<string, string> Cookies { get; } = [];

        public void Absorb(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var lines))
            {
                return;
            }

            foreach (var line in lines)
            {
                var pair = line.Split(';', 2)[0];
                var at = pair.IndexOf('=');
                var name = pair[..at];
                var value = pair[(at + 1)..];
                if (value.Length == 0 || line.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                {
                    Cookies.Remove(name);
                }
                else
                {
                    Cookies[name] = value;
                }
            }
        }

        public HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content = null, params string[] only)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.Add(RemoteIpStartupFilter.Header, Ip);
            var send = Cookies.Where(c => only.Length == 0 || only.Contains(c.Key)).Select(c => $"{c.Key}={c.Value}");
            if (send.Any())
            {
                request.Headers.Add("Cookie", string.Join("; ", send));
            }

            return request;
        }
    }

    private async Task EnableAsync(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            [Prefix + "ENABLED"] = "true",
            [Prefix + "ENTITYID"] = FakeIdp.SpEntityId,
            [Prefix + "IDPENTITYID"] = FakeIdp.EntityId,
            [Prefix + "SINGLESIGNONURL"] = FakeIdp.SingleSignOnUrl,
            [Prefix + "IDPCERTIFICATE"] = _idp.CertificatePem,
        };
        foreach (var (k, v) in overrides ?? [])
        {
            values[k] = v;
        }

        var response = await _app.AdminClient().PutAsJsonAsync("/admin/api/settings/", new { values });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string FormOf(string field, string value) => $"{field}={Uri.EscapeDataString(value)}";

    private static StringContent Form(params string[] pairs) =>
        new(string.Join('&', pairs), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

    /// <summary>ログインを始める。相関 cookie と、AuthnRequest の ID を返す。</summary>
    private async Task<(Jar Jar, string RequestId, HttpResponseMessage Response)> StartAsync(HttpClient client, string? returnUrl = null)
    {
        var jar = new Jar();
        var response = await client.SendAsync(jar.Request(HttpMethod.Get, "/admin/api/saml/login" + (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl))));
        jar.Absorb(response);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (jar, FakeIdp.RequestIdOf(response.Headers.Location!.OriginalString), response);
    }

    private static Task<HttpResponseMessage> PostAcsAsync(HttpClient client, Jar jar, string samlResponse) =>
        client.SendAsync(jar.Request(HttpMethod.Post, "/admin/api/saml/acs", Form(FormOf("SAMLResponse", samlResponse))));

    private static string LocationOf(HttpResponseMessage response) => response.Headers.Location!.OriginalString;

    /// <summary>ログイン済み（cookie を持っている）として /me を読む。</summary>
    private static async Task<HttpStatusCode> MeStatusAsync(HttpClient client, Jar jar) =>
        (await client.SendAsync(jar.Request(HttpMethod.Get, "/admin/api/me/"))).StatusCode;

    // ---- 無効・設定 ------------------------------------------------------------------

    [Fact]
    public async Task 既定では無効で_入口はどれも404()
    {
        var client = _app.RawClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/saml/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/saml/login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/api/saml/acs", Form("SAMLResponse=x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/api/saml/slo")).StatusCode);

        var status = await _app.Client().GetFromJsonAsync<JsonElement>("/admin/api/status");
        Assert.False(status.GetProperty("saml").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task 有効にするには必要な設定がそろっていること_足りない_読めない値は保存を断る()
    {
        var admin = _app.AdminClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [Prefix + "ENABLED"] = "true" } })).StatusCode);

        // 証明書が読めない
        var broken = await admin.PutAsJsonAsync("/admin/api/settings/", new
        {
            values = new Dictionary<string, string?>
            {
                [Prefix + "ENABLED"] = "true",
                [Prefix + "ENTITYID"] = FakeIdp.SpEntityId,
                [Prefix + "IDPENTITYID"] = FakeIdp.EntityId,
                [Prefix + "SINGLESIGNONURL"] = FakeIdp.SingleSignOnUrl,
                [Prefix + "IDPCERTIFICATE"] = "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----",
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("IDPCERTIFICATE", (await broken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // 属性から取るのに属性名が無い（有効にするときに検証される）・知らない役割・知らない値
        var good = new Dictionary<string, string?>
        {
            [Prefix + "ENABLED"] = "true",
            [Prefix + "ENTITYID"] = FakeIdp.SpEntityId,
            [Prefix + "IDPENTITYID"] = FakeIdp.EntityId,
            [Prefix + "SINGLESIGNONURL"] = FakeIdp.SingleSignOnUrl,
            [Prefix + "IDPCERTIFICATE"] = _idp.CertificatePem,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?>(good) { [Prefix + "LOGINIDSOURCE"] = "Claim" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [Prefix + "REGISTERROLE"] = "Root" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [Prefix + "UNKNOWNUSER"] = "Maybe" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/admin/api/settings/", new { values = new Dictionary<string, string?> { [Prefix + "SINGLESIGNONURL"] = "ftp://idp" } })).StatusCode);

        // 何も保存されていない
        Assert.False(_app.Service<RuntimeSettings>().Current.Saml.Enabled);
    }

    [Fact]
    public async Task 有効にするとメタデータが出て_ログイン画面の釦の情報が公開される()
    {
        await EnableAsync(new() { [Prefix + "BUTTONLABEL"] = "社内アカウントでログイン" });

        var metadata = await _app.RawClient().GetAsync("/admin/api/saml/metadata");

        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Contains("samlmetadata+xml", metadata.Content.Headers.ContentType!.MediaType);
        var xml = await metadata.Content.ReadAsStringAsync();
        Assert.Contains(FakeIdp.SpEntityId, xml);
        Assert.Contains("http://feed.test/admin/api/saml/acs", xml);
        Assert.Contains("HTTP-POST", xml);

        var status = (await _app.Client().GetFromJsonAsync<JsonElement>("/admin/api/status")).GetProperty("saml");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("社内アカウントでログイン", status.GetProperty("buttonLabel").GetString());
    }

    // ---- ログインの開始 ----------------------------------------------------------------

    [Fact]
    public async Task ログインはIdPへ要求を渡し_相関cookieはSameSite_None_Secure_HttpOnlyで入口の下だけ()
    {
        await EnableAsync();

        var (_, requestId, response) = await StartAsync(_app.RawClient());

        Assert.StartsWith(FakeIdp.SingleSignOnUrl, LocationOf(response));
        Assert.StartsWith("_", requestId);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("calendarfeed_saml=", StringComparison.Ordinal));
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/admin/api/saml", cookie, StringComparison.OrdinalIgnoreCase);
        // 要求の識別子を平文で置かない（暗号化している）
        Assert.DoesNotContain(requestId, cookie);
    }

    // ---- 成功 ------------------------------------------------------------------------

    [Fact]
    public async Task 登録済みの管理者はIdPの応答で入れ_管理画面へ戻り_セッションにSAMLの鍵が残る()
    {
        await EnableAsync();
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client, "/admin/users");

        var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, TestApp.AdminLoginId));

        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.Equal("/admin/users", LocationOf(acs));
        jar.Absorb(acs);
        Assert.False(jar.Cookies.ContainsKey("calendarfeed_saml"));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, jar));

        var sessionId = jar.Cookies[AdminSessionGuard.SessionCookie];
        var keys = await _app.Service<IAdminSessionStore>().GetSamlKeysAsync(sessionId);
        Assert.Equal(new SamlSessionKeys(TestApp.AdminLoginId, "session-1"), keys);

        var log = await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.LoginSucceeded });
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task 戻り先は管理画面の中の絶対パスだけ()
    {
        await EnableAsync();
        var client = _app.RawClient();

        foreach (var bad in new[] { "https://evil.test/", "//evil.test/", "/\\evil.test", "/other", "/admin/api/me/" })
        {
            var (jar, requestId, _) = await StartAsync(client, bad);
            var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, TestApp.AdminLoginId));
            Assert.Equal("/admin/", LocationOf(acs));
        }
    }

    [Fact]
    public async Task ログイン_IDを属性から取れる()
    {
        await EnableAsync(new() { [Prefix + "LOGINIDSOURCE"] = "Claim", [Prefix + "LOGINIDCLAIM"] = "urn:mail" });
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);

        var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, "opaque-name-id", extraClaims: [new System.Security.Claims.Claim("urn:mail", TestApp.AdminLoginId)]));
        jar.Absorb(acs);

        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, jar));
    }

    // ---- 本アプリに居ない・無効 ----------------------------------------------------------

    [Fact]
    public async Task 居ない利用者は既定では通さない_理由は画面へ伝え_監査ログへ残す()
    {
        await EnableAsync();
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);

        var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, "stranger@example.com"));
        jar.Absorb(acs);

        Assert.Equal("/admin/?samlError=unknown-user", LocationOf(acs));
        Assert.False(jar.Cookies.ContainsKey(AdminSessionGuard.SessionCookie));
        var failed = (await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.SamlLoginFailed })).Single();
        Assert.Equal("unknown-user", failed.DetailJson);
        Assert.Equal("stranger@example.com", failed.ActorLoginId);
        Assert.Single(await _app.Service<IAdminUserStore>().ListAsync());
    }

    [Fact]
    public async Task 設定で自動作成にすると_居ない利用者を設定の役割で作る_パスワードでは入れない()
    {
        await EnableAsync(new() { [Prefix + "UNKNOWNUSER"] = "Register", [Prefix + "REGISTERROLE"] = "Auditor" });
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);

        var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, "newbie@example.com"));
        jar.Absorb(acs);

        Assert.Equal("/admin/", LocationOf(acs));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, jar));
        var user = (await _app.Service<IAdminUserStore>().ListAsync()).Single(u => u.LoginId == "newbie@example.com");
        Assert.Equal(AdminRoles.Auditor, user.Role);
        var me = await (await client.SendAsync(jar.Request(HttpMethod.Get, "/admin/api/me/"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AdminRoles.Auditor, me.GetProperty("role").GetString());
        Assert.Contains(await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.SamlRegistered }), e => e.TargetId == user.Id.ToString("D"));

        // 誰も知らないパスワードなので、パスワードでは入れない（空・推測）
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/admin/api/login", new { loginId = "newbie@example.com", password = "" })).StatusCode);

        // 2 回目は作り直さない
        var (jar2, requestId2, _) = await StartAsync(client);
        await PostAcsAsync(client, jar2, _idp.Response(requestId2, "newbie@example.com"));
        Assert.Equal(2, (await _app.Service<IAdminUserStore>().ListAsync()).Count);
    }

    [Fact]
    public async Task 無効化された管理者は通さない()
    {
        await EnableAsync();
        var created = await (await _app.AdminClient().PostAsJsonAsync("/admin/api/users", new { loginId = "off@example.com", role = AdminRoles.Editor })).Content.ReadFromJsonAsync<JsonElement>();
        await _app.AdminClient().PostAsync($"/admin/api/users/{created.GetProperty("user").GetProperty("id").GetString()}/disable", null);
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);

        var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, "off@example.com"));
        jar.Absorb(acs);

        Assert.Equal("/admin/?samlError=disabled", LocationOf(acs));
        Assert.False(jar.Cookies.ContainsKey(AdminSessionGuard.SessionCookie));
    }

    // ---- 応答の検証 ------------------------------------------------------------------

    [Fact]
    public async Task 応答の署名_受信者_発行者_宛先_要求との対応_相関cookie_IdPのエラーのどれが違っても入れない()
    {
        await EnableAsync();
        var client = _app.RawClient();
        using var otherIdp = new FakeIdp("CN=other-idp");
        var admin = TestApp.AdminLoginId;

        async Task<string> AttemptAsync(Func<string, string> response, string name, bool dropRelay = false, string? relayFor = null)
        {
            var (jar, requestId, _) = await StartAsync(client);
            if (dropRelay)
            {
                jar.Cookies.Clear();
            }

            var acs = await PostAcsAsync(client, jar, response(relayFor ?? requestId));
            jar.Absorb(acs);
            Assert.False(jar.Cookies.ContainsKey(AdminSessionGuard.SessionCookie), $"セッションが張られた: {name}");
            return LocationOf(acs);
        }

        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, admin, signWith: otherIdp.Certificate), "別の IdP の署名"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, admin, audience: "https://someone-else.test/"), "受信者違い"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, admin, issuer: "https://evil-idp.test/"), "発行者違い"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, admin, destination: new Uri("http://feed.test/elsewhere")), "宛先違い"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response("_someone-elses-request", admin), "別の要求への応答"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, admin), "相関 cookie なし", dropRelay: true));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, admin, success: false), "IdP のエラー"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(_ => "not-base64-at-all!!", "壊れた応答"));
        Assert.Equal("/admin/?samlError=invalid", await AttemptAsync(id => _idp.Response(id, " "), "空の NameID"));
    }

    [Fact]
    public async Task 改ざんした応答は署名の検証で落ちる()
    {
        await EnableAsync();
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);
        var response = _idp.Response(requestId, "stranger@example.com");
        var xml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(response)).Replace("stranger@example.com", TestApp.AdminLoginId);

        var acs = await PostAcsAsync(client, jar, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml)));
        jar.Absorb(acs);

        Assert.Equal("/admin/?samlError=invalid", LocationOf(acs));
        Assert.False(jar.Cookies.ContainsKey(AdminSessionGuard.SessionCookie));
    }

    [Fact]
    public async Task 失敗した応答のたびに監査ログへ残す()
    {
        await EnableAsync();
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);

        await PostAcsAsync(client, jar, _idp.Response(requestId, TestApp.AdminLoginId, audience: "https://x.test/"));

        var failed = (await _app.Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Action = AuditActions.SamlLoginFailed })).Single();
        Assert.Equal("invalid-response", failed.DetailJson);
        Assert.False(failed.Succeeded);
    }

    // ---- 2 要素 ----------------------------------------------------------------------

    [Fact]
    public async Task 登録済みの2要素は_SAMLで来ても省かせない_数字を通すとセッションへSAMLの鍵が引き継がれる()
    {
        // 管理者 A（TestApp の管理者）に 2 要素を登録する
        var admin = _app.AdminClient();
        var begin = await (await admin.PostAsJsonAsync("/admin/api/me/totp/begin", new { password = TestApp.AdminPassword })).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) })).StatusCode);
        await EnableAsync();
        _app.Time.Advance(TimeSpan.FromMinutes(2));

        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);
        var acs = await PostAcsAsync(client, jar, _idp.Response(requestId, TestApp.AdminLoginId));
        jar.Absorb(acs);

        // まだ入れていない（途中の段階）
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client, jar));
        Assert.True(jar.Cookies.ContainsKey(AdminSessionGuard.PendingCookie));

        var totp = await client.SendAsync(jar.Request(HttpMethod.Post, "/admin/api/login/totp", JsonContent.Create(new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) })));
        jar.Absorb(totp);
        Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, jar));
        Assert.Equal(TestApp.AdminLoginId, (await _app.Service<IAdminSessionStore>().GetSamlKeysAsync(jar.Cookies[AdminSessionGuard.SessionCookie]))!.NameId);
    }

    [Fact]
    public async Task 必須の方針では_SAMLで来た人にも登録させる()
    {
        using var required = new TestApp(twoFactor: "required", setUpAdmin: false);
        var setup = required.Client();
        await setup.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        await setup.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
        var begin = await (await setup.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        await setup.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(begin.GetProperty("secret").GetString()!, required.Time.Now.UtcDateTime) });
        await setup.PutAsJsonAsync("/admin/api/settings/", new
        {
            values = new Dictionary<string, string?>
            {
                [Prefix + "ENABLED"] = "true",
                [Prefix + "ENTITYID"] = FakeIdp.SpEntityId,
                [Prefix + "IDPENTITYID"] = FakeIdp.EntityId,
                [Prefix + "SINGLESIGNONURL"] = FakeIdp.SingleSignOnUrl,
                [Prefix + "IDPCERTIFICATE"] = _idp.CertificatePem,
                [Prefix + "UNKNOWNUSER"] = "Register",
            },
        });

        // 新しく自動で作られる人は、2 要素が未登録。必須なので、登録の途中（enroll）になる
        var client = required.RawClient();
        var jar = new Jar();
        var start = await client.SendAsync(jar.Request(HttpMethod.Get, "/admin/api/saml/login"));
        jar.Absorb(start);
        var acs = await PostAcsAsync(client, jar, _idp.Response(FakeIdp.RequestIdOf(start.Headers.Location!.OriginalString), "newbie@example.com"));
        jar.Absorb(acs);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client, jar));
        Assert.True(jar.Cookies.ContainsKey(AdminSessionGuard.PendingCookie));
        var enroll = await client.SendAsync(jar.Request(HttpMethod.Post, "/admin/api/me/totp/begin", JsonContent.Create(new { })));
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
    }

    // ---- 単一ログアウト -----------------------------------------------------------------

    private static async Task<string> BeginReenrollAsync(HttpClient client, Jar jar)
    {
        var begin = await client.SendAsync(jar.Request(HttpMethod.Post, "/admin/api/me/totp/begin",
            JsonContent.Create(new { password = TestApp.AdminPassword })));
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        jar.Absorb(begin);
        return jar.Cookies[AdminSessionGuard.ReenrollCookie];
    }

    [Fact]
    public async Task 自分からのログアウトは_セッションを捨て_単一ログアウトが有効ならIdPへ頼む()
    {
        await EnableAsync(new() { [Prefix + "SINGLELOGOUTURL"] = FakeIdp.SingleLogoutUrl });
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);
        jar.Absorb(await PostAcsAsync(client, jar, _idp.Response(requestId, TestApp.AdminLoginId)));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, jar));
        var sessionId = jar.Cookies[AdminSessionGuard.SessionCookie];
        var reenrollId = await BeginReenrollAsync(client, jar);

        var logout = await client.SendAsync(jar.Request(HttpMethod.Get, "/admin/api/saml/logout"));
        jar.Absorb(logout);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.StartsWith(FakeIdp.SingleLogoutUrl, LocationOf(logout));
        Assert.Contains("SAMLRequest=", LocationOf(logout));
        Assert.Null(await _app.Service<IAdminSessionStore>().FindAsync(sessionId));
        Assert.Null(await _app.Service<IAdminSessionStore>().FindAsync(reenrollId));
        Assert.False(jar.Cookies.ContainsKey(AdminSessionGuard.ReenrollCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client, jar));
    }

    [Fact]
    public async Task 単一ログアウトが無ければ_こちらのセッションだけを捨てて画面へ戻る()
    {
        await EnableAsync();
        var client = _app.RawClient();
        var (jar, requestId, _) = await StartAsync(client);
        jar.Absorb(await PostAcsAsync(client, jar, _idp.Response(requestId, TestApp.AdminLoginId)));
        var reenrollId = await BeginReenrollAsync(client, jar);

        var logout = await client.SendAsync(jar.Request(HttpMethod.Get, "/admin/api/saml/logout"));
        jar.Absorb(logout);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/admin/", LocationOf(logout));
        Assert.Null(await _app.Service<IAdminSessionStore>().FindAsync(reenrollId));
        Assert.False(jar.Cookies.ContainsKey(AdminSessionGuard.ReenrollCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client, jar));
    }

    [Theory]
    [InlineData("s-1")]
    [InlineData(null)]
    public async Task IdPからの単一ログアウトは_cookieなしでも対応する再登録の途中を失効させる(string? sessionIndex)
    {
        await EnableAsync(new() { [Prefix + "SINGLELOGOUTURL"] = FakeIdp.SingleLogoutUrl });
        var client = _app.RawClient();
        var (first, id1, _) = await StartAsync(client);
        first.Absorb(await PostAcsAsync(client, first, _idp.Response(id1, TestApp.AdminLoginId, sessionIndex: "s-1")));
        var firstReenroll = await BeginReenrollAsync(client, first);
        var (second, id2, _) = await StartAsync(client);
        second.Absorb(await PostAcsAsync(client, second, _idp.Response(id2, TestApp.AdminLoginId, sessionIndex: "s-2")));
        var secondReenroll = await BeginReenrollAsync(client, second);
        var sessions = _app.Service<IAdminSessionStore>();

        using var other = new FakeIdp("CN=other-idp");
        await client.PostAsync("/admin/api/saml/slo", Form(FormOf("SAMLRequest", other.LogoutRequest(TestApp.AdminLoginId, sessionIndex))));
        Assert.NotNull(await sessions.FindAsync(firstReenroll));
        Assert.NotNull(await sessions.FindAsync(secondReenroll));

        var response = await client.PostAsync("/admin/api/saml/slo",
            Form(FormOf("SAMLRequest", _idp.LogoutRequest(TestApp.AdminLoginId, sessionIndex))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(FakeIdp.SingleLogoutUrl, LocationOf(response));
        Assert.Null(await sessions.FindAsync(first.Cookies[AdminSessionGuard.SessionCookie]));
        Assert.Null(await sessions.FindAsync(firstReenroll));
        if (sessionIndex is null)
        {
            Assert.Null(await sessions.FindAsync(second.Cookies[AdminSessionGuard.SessionCookie]));
            Assert.Null(await sessions.FindAsync(secondReenroll));
        }
        else
        {
            Assert.NotNull(await sessions.FindAsync(second.Cookies[AdminSessionGuard.SessionCookie]));
            Assert.NotNull(await sessions.FindAsync(secondReenroll));
        }
    }

    [Fact]
    public async Task IdPからの単一ログアウトは_署名を検証して_その人のセッションをすべて失効させる()
    {
        await EnableAsync(new() { [Prefix + "SINGLELOGOUTURL"] = FakeIdp.SingleLogoutUrl });
        var client = _app.RawClient();
        var (first, id1, _) = await StartAsync(client);
        first.Absorb(await PostAcsAsync(client, first, _idp.Response(id1, TestApp.AdminLoginId, sessionIndex: "s-1")));
        var (second, id2, _) = await StartAsync(client);
        second.Absorb(await PostAcsAsync(client, second, _idp.Response(id2, TestApp.AdminLoginId, sessionIndex: "s-2")));

        // 署名の無い・別の IdP の要求では何も起きない
        using var other = new FakeIdp("CN=other-idp");
        var forged = new FakeIdpLogoutRequest(other).Build(TestApp.AdminLoginId, "s-1");
        var rejected = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/admin/api/saml/slo") { Content = Form(FormOf("SAMLRequest", forged)) });
        Assert.StartsWith("/admin/", LocationOf(rejected));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, first));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, second));

        // 本物の IdP の要求。**cookie は付いていない**（他サイトからの POST）。セッション索引で、その 1 つだけが失効する
        var request = _idp.LogoutRequest(TestApp.AdminLoginId, "s-1");
        var slo = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/admin/api/saml/slo") { Content = Form(FormOf("SAMLRequest", request)) });

        Assert.StartsWith(FakeIdp.SingleLogoutUrl, LocationOf(slo));
        Assert.Contains("SAMLResponse=", LocationOf(slo));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client, first));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(client, second));

        // セッション索引が無ければ、その NameID のセッションをすべて失効させる
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/admin/api/saml/slo") { Content = Form(FormOf("SAMLRequest", _idp.LogoutRequest(TestApp.AdminLoginId, null))) });
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(client, second));
    }

    /// <summary>別の IdP（証明書が違う）が出した LogoutRequest を作る。</summary>
    private sealed class FakeIdpLogoutRequest(FakeIdp idp)
    {
        public string Build(string nameId, string? sessionIndex) => idp.LogoutRequest(nameId, sessionIndex);
    }
}
