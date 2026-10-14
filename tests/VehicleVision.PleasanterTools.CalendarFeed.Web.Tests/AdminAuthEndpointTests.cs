using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>管理者認証（ログイン ID ＋ パスワード ＋ TOTP・サーバ側セッション。Issue #24）。</summary>
public sealed class AdminAuthEndpointTests : IDisposable
{
    private const string Password = TestApp.AdminPassword;
    private readonly TestApp _app = new(setUpAdmin: false);

    public void Dispose() => _app.Dispose();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body);

    private async Task SetUpAsync(HttpClient client) =>
        Assert.Equal(
            HttpStatusCode.Created,
            (await PostAsync(client, "/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = Password })).StatusCode);

    private static async Task<string?> StepOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("step").GetString();

    private Task<HttpResponseMessage> LoginAsync(HttpClient client, string password = Password) =>
        PostAsync(client, "/admin/api/login", new { loginId = TestApp.AdminLoginId, password });

    /// <summary>ログインして、2 要素を登録する。共有鍵と復旧コードを返す。</summary>
    private async Task<(string Secret, string[] Codes)> EnrollAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client)).StatusCode);
        var begin = await (await client.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        var complete = await PostAsync(client, "/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var codes = (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        return (secret, codes);
    }

    // ---- 最初の管理者 -----------------------------------------------------------------

    [Fact]
    public async Task 最初の管理者は管理者が居ないときだけ作れる()
    {
        var client = _app.Client();
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/admin/api/status")).GetProperty("setupRequired").GetBoolean());

        await SetUpAsync(client);

        Assert.False((await client.GetFromJsonAsync<JsonElement>("/admin/api/status")).GetProperty("setupRequired").GetBoolean());
        var again = await PostAsync(client, "/admin/api/setup", new { loginId = "second", password = "another long password" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("admin", "short")]
    [InlineData("admin", "ADMIN")]
    [InlineData("", "correct horse battery staple")]
    [InlineData("ad\nmin", "correct horse battery staple")]
    public async Task 最初の管理者のログインIDとパスワードは条件を満たす必要がある(string loginId, string password)
    {
        var response = await PostAsync(_app.Client(), "/admin/api/setup", new { loginId, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await _app.Client().GetFromJsonAsync<JsonElement>("/admin/api/status")).GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task ログインIDと同じパスワードは大文字小文字を区別せず弾く()
    {
        var response = await PostAsync(_app.Client(), "/admin/api/setup", new { loginId = "administrator-1", password = "ADMINISTRATOR-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- ログイン（2 要素なし） -------------------------------------------------------

    [Fact]
    public async Task ログインするとcookieが発行され管理APIを使える()
    {
        var client = _app.Client();
        await SetUpAsync(client);

        var login = await LoginAsync(client);

        Assert.Equal("done", await StepOfAsync(login));
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("calendarfeed_admin=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/admin", cookie, StringComparison.OrdinalIgnoreCase);
        var me = await client.GetFromJsonAsync<JsonElement>("/admin/api/me/");
        Assert.Equal(TestApp.AdminLoginId, me.GetProperty("loginId").GetString());
        Assert.Equal("Administrator", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/feeds/")).StatusCode);
    }

    [Fact]
    public async Task ログインしていなければ管理APIは401()
    {
        var anonymous = _app.Client();
        await SetUpAsync(anonymous);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/api/feeds/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, "/admin/api/feeds", new { name = "a", siteId = 1 })).StatusCode);
    }

    [Fact]
    public async Task ログイン_ID_は大文字小文字を区別せず_誤りは居ない場合と同じ応答()
    {
        var client = _app.Client();
        await SetUpAsync(client);

        var upper = await PostAsync(client, "/admin/api/login", new { loginId = "ADMIN", password = Password });
        var wrong = await PostAsync(_app.Client(), "/admin/api/login", new { loginId = TestApp.AdminLoginId, password = "wrong password!!" });
        var unknown = await PostAsync(_app.Client(), "/admin/api/login", new { loginId = "nobody", password = "wrong password!!" });

        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // **どの段階で外れたかを外へ伝えない**（居ない・パスワードが違うを区別して返すと、利用者名の総当たりに使える）
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 失敗が続くと締め出され_正しいパスワードでも入れず_時間が経てば入れる()
    {
        var client = _app.Client();
        await SetUpAsync(client);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_app.Client(), "wrong password!!")).StatusCode);
        }

        // 締め出し中は、正しいパスワードでも同じ応答
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_app.Client())).StatusCode);
        _app.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_app.Client())).StatusCode);

        // **恒久的には締め出さない**
        _app.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client)).StatusCode);
    }

    [Fact]
    public async Task ログアウトすると盗まれたcookieも使えない()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        var login = await LoginAsync(client);
        var session = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("calendarfeed_admin=", StringComparison.Ordinal)).Split(';')[0];

        // cookie を別の入れ物（別のブラウザ）へ写して使う
        var stolen = _app.Client();
        using (var before = new HttpRequestMessage(HttpMethod.Get, "/admin/api/me/"))
        {
            before.Headers.Add("Cookie", session);
            Assert.Equal(HttpStatusCode.OK, (await stolen.SendAsync(before)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/admin/api/logout", null)).StatusCode);

        // **失効はその場で効く**（サーバ側にセッションがあるので、cookie の期限を待たない）
        using var after = new HttpRequestMessage(HttpMethod.Get, "/admin/api/me/");
        after.Headers.Add("Cookie", session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.SendAsync(after)).StatusCode);
    }

    [Fact]
    public async Task セッションは8時間で切れる()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        await LoginAsync(client);

        _app.Time.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/me/")).StatusCode);
        _app.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api/me/")).StatusCode);
    }

    [Fact]
    public async Task ログインの試行は別枠のレート制限で429になる()
    {
        for (var i = 0; i < 10; i++)
        {
            await PostAsync(_app.Client(), "/admin/api/login", new { loginId = $"nobody{i}", password = "wrong password!!" });
        }

        var limited = await PostAsync(_app.Client(), "/admin/api/login", new { loginId = "nobody", password = "wrong password!!" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // ---- パスワードの変更 -------------------------------------------------------------

    [Fact]
    public async Task パスワードを変えると他の端末のセッションが失効しいまの端末は残る()
    {
        var first = _app.Client();
        await SetUpAsync(first);
        await LoginAsync(first);
        var second = _app.Client();
        await LoginAsync(second);

        var wrong = await PostAsync(first, "/admin/api/me/password", new { currentPassword = "not the password", newPassword = "a brand new long password" });
        var weak = await PostAsync(first, "/admin/api/me/password", new { currentPassword = Password, newPassword = "short" });
        var changed = await PostAsync(first, "/admin/api/me/password", new { currentPassword = Password, newPassword = "a brand new long password" });

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/admin/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_app.Client())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_app.Client(), "a brand new long password")).StatusCode);
    }

    // ---- 2 要素（任意） ---------------------------------------------------------------

    [Fact]
    public async Task 登録するとログインに数字が要る_復旧コードは10本()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        var (secret, codes) = await EnrollAsync(client);
        Assert.Equal(10, codes.Length);
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", codes[0]);

        var other = _app.Client();
        var login = await LoginAsync(other);

        Assert.Equal("totp", await StepOfAsync(login));
        // **途中（Pending）では何も操作させない**
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/admin/api/feeds/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/admin/api/me/")).StatusCode);

        _app.Time.Advance(TimeSpan.FromSeconds(30));
        var ok = await PostAsync(other, "/admin/api/login/totp", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });
        Assert.Equal("done", await StepOfAsync(ok));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/admin/api/feeds/")).StatusCode);
    }

    [Fact]
    public async Task 間違った数字は通らず_同じ時間枠の数字は二度通らない()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        var (secret, _) = await EnrollAsync(client);

        var wrong = _app.Client();
        await LoginAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(wrong, "/admin/api/login/totp", new { code = "000000" })).StatusCode);

        // 登録の完了で使った枠の数字は、そのままでは使えない（盗み見られた数字を使い回させない）
        var replay = _app.Client();
        await LoginAsync(replay);
        var sameStep = TotpService.Compute(secret, _app.Time.Now.UtcDateTime);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(replay, "/admin/api/login/totp", new { code = sameStep })).StatusCode);

        _app.Time.Advance(TimeSpan.FromSeconds(30));
        var fresh = TotpService.Compute(secret, _app.Time.Now.UtcDateTime);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(replay, "/admin/api/login/totp", new { code = fresh })).StatusCode);

        var again = _app.Client();
        await LoginAsync(again);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(again, "/admin/api/login/totp", new { code = fresh })).StatusCode);
    }

    [Fact]
    public async Task 数字の総当たりも締め出しに数える()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        var (secret, _) = await EnrollAsync(client);

        var attacker = _app.Client();
        await LoginAsync(attacker);
        for (var i = 0; i < 5; i++)
        {
            await PostAsync(attacker, "/admin/api/login/totp", new { code = "000000" });
        }

        // 締め出された後は、正しい数字でも通らない。途中のセッションも捨てられている
        _app.Time.Advance(TimeSpan.FromSeconds(30));
        var right = TotpService.Compute(secret, _app.Time.Now.UtcDateTime);
        // （ログインの試行は別枠のレート制限があるため、回数はその範囲に収めている。ここは 10 回目）
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(attacker, "/admin/api/login/totp", new { code = right })).StatusCode);
    }

    [Fact]
    public async Task 復旧コードは使い切りで入れる()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        var (_, codes) = await EnrollAsync(client);

        var first = _app.Client();
        await LoginAsync(first);
        var ok = await PostAsync(first, "/admin/api/login/recovery", new { code = codes[0].ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(9, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodesLeft").GetInt32());

        var second = _app.Client();
        await LoginAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(second, "/admin/api/login/recovery", new { code = codes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(second, "/admin/api/login/recovery", new { code = codes[1] })).StatusCode);
    }

    [Fact]
    public async Task 登録し直すと前の復旧コードは消える()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        var (_, oldCodes) = await EnrollAsync(client);

        // ログイン済みの端末から登録し直す
        var begin = await (await client.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        _app.Time.Advance(TimeSpan.FromSeconds(30));
        var complete = await PostAsync(client, "/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var other = _app.Client();
        await LoginAsync(other);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(other, "/admin/api/login/recovery", new { code = oldCodes[0] })).StatusCode);
    }

    [Fact]
    public async Task 登録を完了しなければ有効にならない()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        await LoginAsync(client);

        await client.PostAsync("/admin/api/me/totp/begin", null);
        var wrong = await PostAsync(client, "/admin/api/me/totp/complete", new { code = "123456" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("done", await StepOfAsync(await LoginAsync(_app.Client())));
    }

    [Fact]
    public async Task 任意では自分で解除できる_パスワードの再確認つき()
    {
        var client = _app.Client();
        await SetUpAsync(client);
        await EnrollAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(client, "/admin/api/me/totp/disable", new { password = "wrong password!!" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/admin/api/me/totp/disable", new { password = Password })).StatusCode);
        Assert.Equal("done", await StepOfAsync(await LoginAsync(_app.Client())));
    }
}

/// <summary>秘密の鍵を失った・変えた状態（運用手順書「鍵を失ったとき」）。</summary>
public sealed class AdminLostSecretKeyTests
{
    [Fact]
    public async Task 鍵を変えるとTOTPでは入れず_500にもならず_復旧コードでは入れる()
    {
        string[] codes;
        string db;
        using (var before = new TestApp(setUpAdmin: false))
        {
            db = before.DbPath;
            before.KeepDb = true;
            var client = before.Client();
            await client.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
            await client.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
            var begin = await (await client.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
            var complete = await client.PostAsJsonAsync(
                "/admin/api/me/totp/complete",
                new { code = TotpService.Compute(begin.GetProperty("secret").GetString()!, before.Time.Now.UtcDateTime) });
            codes = (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        try
        {
            // 同じ DB を、別の鍵で開く
            using var after = new TestApp(setUpAdmin: false, existingDb: db);
            var other = after.Client();
            var login = await other.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = TestApp.AdminPassword });
            Assert.Equal("totp", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("step").GetString());

            var totp = await other.PostAsJsonAsync("/admin/api/login/totp", new { code = "123456" });
            Assert.Equal(HttpStatusCode.Unauthorized, totp.StatusCode);

            // 復旧コードは鍵に依存しないので、それで入れる
            var recovery = await other.PostAsJsonAsync("/admin/api/login/recovery", new { code = codes[0] });
            Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/admin/api/feeds/")).StatusCode);

            // 入れたら、2 要素を登録し直せる（古い共有鍵は復号できないまま、新しい鍵で上書きされる）
            var begin = await (await other.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
            var done = await other.PostAsJsonAsync(
                "/admin/api/me/totp/complete",
                new { code = TotpService.Compute(begin.GetProperty("secret").GetString()!, after.Time.Now.UtcDateTime) });
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }
}

/// <summary>状態を変える要求は、同じサイトからのものだけ（CSRF）。</summary>
public sealed class AdminCsrfTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    public async Task 別のサイトからの状態を変える要求は403(string header, string value)
    {
        var client = _app.AdminClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/feeds")
        {
            Content = JsonContent.Create(new { name = "a", siteId = 1 }),
        };
        request.Headers.Add(header, value);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task 同じサイトからの要求は通る()
    {
        var client = _app.AdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/feeds")
        {
            Content = JsonContent.Create(new { name = "a", siteId = 1 }),
        };
        request.Headers.Add("Origin", "http://feed.test");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ログインの入口にも別のサイトからの要求は通さない()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/login")
        {
            Content = JsonContent.Create(new { loginId = "a", password = "b" }),
        };
        request.Headers.Add("Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await _app.Client().SendAsync(request)).StatusCode);
    }

}

/// <summary>2 要素が必須の構成。</summary>
public sealed class AdminTwoFactorRequiredTests : IDisposable
{
    private const string Password = TestApp.AdminPassword;
    private readonly TestApp _app = new(twoFactor: "required", setUpAdmin: false);

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 未登録は登録するまでログインが完了せず_登録するとその場で入れる()
    {
        var client = _app.Client();
        await client.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = Password });

        var login = await client.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = Password });
        Assert.Equal("enroll", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("step").GetString());

        // 途中では何も操作できない
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api/feeds/")).StatusCode);

        // 登録だけはできる（途中の状態から）
        var begin = await (await client.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = begin.GetProperty("secret").GetString()!;
        var complete = await client.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(secret, _app.Time.Now.UtcDateTime) });

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/api/feeds/")).StatusCode);
    }

    [Fact]
    public async Task 必須では自分で解除できない()
    {
        var client = _app.Client();
        await client.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = Password });
        await client.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = Password });
        var begin = await (await client.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        await client.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(begin.GetProperty("secret").GetString()!, _app.Time.Now.UtcDateTime) });

        var disable = await client.PostAsJsonAsync("/admin/api/me/totp/disable", new { password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
    }

    [Fact]
    public async Task 登録済みの人の鍵は途中の状態から差し替えられない()
    {
        var owner = _app.Client();
        await owner.PostAsJsonAsync("/admin/api/setup", new { loginId = TestApp.AdminLoginId, password = Password });
        await owner.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = Password });
        var begin = await (await owner.PostAsync("/admin/api/me/totp/begin", null)).Content.ReadFromJsonAsync<JsonElement>();
        await owner.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = TotpService.Compute(begin.GetProperty("secret").GetString()!, _app.Time.Now.UtcDateTime) });

        // パスワードを知っているだけの人（2 要素の途中）は、鍵を作り直せない
        var attacker = _app.Client();
        var login = await attacker.PostAsJsonAsync("/admin/api/login", new { loginId = TestApp.AdminLoginId, password = Password });
        Assert.Equal("totp", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("step").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsync("/admin/api/me/totp/begin", null)).StatusCode);
    }
}

/// <summary>2 要素が無効の構成。</summary>
public sealed class AdminTwoFactorDisabledTests : IDisposable
{
    private readonly TestApp _app = new(twoFactor: "disabled");

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 無効では登録の口を閉じる()
    {
        var client = _app.AdminClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/admin/api/me/totp/begin", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/admin/api/me/totp/complete", new { code = "123456" })).StatusCode);
    }
}

/// <summary>設定の読み取り。</summary>
public sealed class AdminAuthSettingsTests
{
    [Fact]
    public void 知らない二要素の値は起動時に落とす()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [Services.AppSettings.AdminTwoFactor] = "mandatory" })
            .Build();

        var ex = Assert.Throws<Services.AppSettingValidationException>(() => TestSettings.Build(configuration));
        Assert.Contains("required / optional / disabled", ex.Message);
    }

    [Theory]
    [InlineData(null, Services.TwoFactorMode.Optional)]
    [InlineData("required", Services.TwoFactorMode.Required)]
    [InlineData(" Disabled ", Services.TwoFactorMode.Disabled)]
    public void 二要素の方針を読む(string? value, Services.TwoFactorMode expected)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [Services.AppSettings.AdminTwoFactor] = value })
            .Build();

        Assert.Equal(expected, TestSettings.Build(configuration).AdminAuth.TwoFactor);
    }

    [Fact]
    public void パスワードの条件を読む()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [Services.AppSettings.PasswordMinimumLength] = "20",
                [Services.AppSettings.PasswordAllowSameAsLoginId] = "true",
            })
            .Build();

        var policy = TestSettings.Build(configuration).AdminAuth.PasswordPolicy;

        Assert.Equal(20, policy.MinimumLength);
        Assert.True(policy.AllowSameAsLoginId);
    }
}
