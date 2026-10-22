using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>固定のパラメータ（上限・時間。Issue #92）。</summary>
public sealed class FixedParametersTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void 未設定なら_これまでのコードの値と同じ()
    {
        var p = FixedParameters.Defaults;

        Assert.Equal(new RateLimitPolicy(10, TimeSpan.FromMinutes(5)), p.Policy(FixedParameters.AdminLogin));
        Assert.Equal(new RateLimitPolicy(300, TimeSpan.FromMinutes(5)), p.Policy(FixedParameters.PleasanterSso));
        Assert.Equal(new RateLimitPolicy(60, TimeSpan.FromMinutes(1)), p.Policy(FixedParameters.Feed));
        Assert.Equal(new RateLimitPolicy(600, TimeSpan.FromMinutes(5)), p.Policy(FixedParameters.CalDav));
        Assert.Equal(TimeSpan.FromHours(8), p.SessionRetention);
        Assert.Equal(5, p.LockoutCount);
        Assert.Equal(TimeSpan.FromMinutes(15), p.LockoutPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), p.PendingAuthenticationPeriod);
        Assert.Equal(TimeSpan.FromHours(48), p.InvitationPeriod);
        Assert.Equal(TimeSpan.FromDays(90), p.StaleAfter);
        Assert.Equal(TimeSpan.FromMinutes(5), p.MigrationLockTimeout);
        Assert.Equal(64 * 1024, p.CalDavMaxXmlBodyBytes);
        Assert.Equal(256 * 1024, p.CalDavMaxResourceBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), p.CalDavWriteLockLease);
        Assert.Equal(TimeSpan.FromSeconds(5), p.CalDavWriteLockWait);
    }

    [Fact]
    public void 同梱のファイルの値は_既定と同じ()
    {
        // 同梱の App_Data/Parameters を実際に読んで、既定とずれていないことを見る（ファイルとコードの二重管理のずれ止め）
        var directory = Path.Combine(AppContext.BaseDirectory, "App_Data", "Parameters");
        var config = new ConfigurationBuilder().AddParameterFiles(directory).Build();

        var fromFiles = FixedParameters.FromConfiguration(config);
        var defaults = FixedParameters.Defaults;

        Assert.Equal(defaults.Policies, fromFiles.Policies);
        Assert.Equal(defaults.SessionRetention, fromFiles.SessionRetention);
        Assert.Equal(defaults.LockoutCount, fromFiles.LockoutCount);
        Assert.Equal(defaults.LockoutPeriod, fromFiles.LockoutPeriod);
        Assert.Equal(defaults.PendingAuthenticationPeriod, fromFiles.PendingAuthenticationPeriod);
        Assert.Equal(defaults.InvitationPeriod, fromFiles.InvitationPeriod);
        Assert.Equal(defaults.StaleAfter, fromFiles.StaleAfter);
        Assert.Equal(defaults.MigrationLockTimeout, fromFiles.MigrationLockTimeout);
        Assert.Equal(defaults.CalDavMaxXmlBodyBytes, fromFiles.CalDavMaxXmlBodyBytes);
        Assert.Equal(defaults.CalDavMaxResourceBytes, fromFiles.CalDavMaxResourceBytes);
        Assert.Equal(defaults.CalDavWriteLockLease, fromFiles.CalDavWriteLockLease);
        Assert.Equal(defaults.CalDavWriteLockWait, fromFiles.CalDavWriteLockWait);
    }

    [Fact]
    public void 設定した値が効く_階層のキーと平らなキー()
    {
        var p = FixedParameters.FromConfiguration(Config(
            ("RateLimit:Policies:CalDav:Limit", "50"),
            ("RateLimit:Policies:CalDav:WindowSeconds", "60"),
            (FixedParameters.LockoutCountKey, "3"),
            (FixedParameters.SessionRetentionPeriod, "30"),
            ("CalDav:MaxResourceKB", "1024"),
            ("CalDav:WriteLockWaitSeconds", "2")));

        Assert.Equal(new RateLimitPolicy(50, TimeSpan.FromSeconds(60)), p.Policy(FixedParameters.CalDav));
        Assert.Equal(new RateLimitPolicy(10, TimeSpan.FromMinutes(5)), p.Policy(FixedParameters.AdminLogin)); // 他は既定のまま
        Assert.Equal(3, p.LockoutCount);
        Assert.Equal(TimeSpan.FromMinutes(30), p.SessionRetention);
        Assert.Equal(1024 * 1024, p.CalDavMaxResourceBytes);
        Assert.Equal(TimeSpan.FromSeconds(2), p.CalDavWriteLockWait);
    }

    [Theory]
    [InlineData("RateLimit:Policies:CalDav:Limit", "0")]
    [InlineData("RateLimit:Policies:AdminLogin:WindowSeconds", "99999999")]
    [InlineData("CALENDARFEED_LOCKOUT_COUNT", "abc")]
    [InlineData("CALENDARFEED_SESSION_RETENTIONPERIOD", "-5")]
    [InlineData("CALENDARFEED_INVITATION_PERIOD", "0")]
    [InlineData("CalDav:MaxXmlBodyKB", "0")]
    [InlineData("CalDav:MaxResourceKB", "10241")]
    [InlineData("CALENDARFEED_DB_MIGRATION_LOCK_TIMEOUT_SECONDS", "1.5")]
    public void 読めない値と範囲の外の値は_キーの名前つきで起動時に落とす(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FixedParameters.FromConfiguration(Config((key, value))));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void 排他を待つ長さが貸し出しの期間より長い設定は落とす()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FixedParameters.FromConfiguration(Config(
            ("CalDav:WriteLockLeaseSeconds", "10"), ("CalDav:WriteLockWaitSeconds", "20"))));

        Assert.Contains("WriteLockWaitSeconds", ex.Message);
    }
}

/// <summary>
/// <b>環境変数で指定できる設定は、ファイル（<c>App_Data/Parameters/*.json</c>）でも指定できる</b>（Pleasanter の Parameters の持ち方）。
/// 新しい設定を足したときに、ファイル側の写しを忘れないための突き合わせ。
/// </summary>
public sealed class FileBackedSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"calendarfeed-fixed-{Guid.NewGuid():N}");

    public FileBackedSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RateLimit__Policies__CalDav__Limit", null);
        Directory.Delete(_dir, recursive: true);
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, name), json);

    private IConfigurationRoot Build() => new ConfigurationBuilder().AddParameterFiles(_dir).Build();

    /// <summary>環境変数の名前（<c>CALENDARFEED_*</c>）として使われているものを集める。</summary>
    private static IEnumerable<string> EnvironmentSettingNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(AppSettings), typeof(AppSettingDefinitions), typeof(FixedParameters), typeof(MailSenderOptions) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field is { IsLiteral: true } && field.FieldType == typeof(string) && field.GetRawConstantValue() is string { } value
                    && value.StartsWith("CALENDARFEED_", StringComparison.Ordinal) && !value.EndsWith('_'))
                {
                    names.Add(value);
                }
            }
        }

        foreach (var definition in AppSettingDefinitions.All)
        {
            names.Add(definition.Key);
        }

        return names;
    }

    [Fact]
    public void 環境変数で指定できる設定は_すべてファイルでも指定できる()
    {
        // ファイルでは指定しない（置き場そのもの・読んだ結果の記録）
        string[] notFiles = [ParameterFiles.DirectoryKey, ParameterFiles.FoundFilesKey];
        var fileBacked = ParameterFiles.FileBackedSettings.ToHashSet(StringComparer.Ordinal);

        var missing = EnvironmentSettingNames().Except(notFiles).Where(n => !fileBacked.Contains(n)).Order().ToList();

        Assert.True(missing.Count == 0, "ファイルのキーへの写しが無い環境変数: " + string.Join(", ", missing));
    }

    [Fact]
    public void Authentication_jsonの階層のキーを_SAMLとPleasanterのログインの設定へ写す()
    {
        Write("Authentication.json", """
            {
              "SamlParameters": {
                "Enabled": true,
                "SPOptions": { "EntityId": "https://sp.test/saml" },
                "IdentityProviders": [ { "EntityId": "https://idp.test", "SignOnUrl": "https://idp.test/sso", "LogoutUrl": "https://idp.test/slo", "SigningCertificate": "MIIC" } ],
                "UnknownUser": "Register", "RegisterRole": "Editor", "LoginIdSource": "Claim", "LoginIdClaim": "mail", "ButtonLabel": "社内 SSO"
              },
              "PleasanterSsoParameters": {
                "Enabled": true, "InternalBaseUrl": "http://pleasanter:8080", "LoginUrl": "/users/login", "LogoutUrl": "/users/logout",
                "CookieNames": [ ".Pleasanter_Session", ".Pleasanter_Auth" ], "AllowedGroupIds": [ 1, 2 ], "RevalidateMinutes": 10, "TimeoutSeconds": 8
              }
            }
            """);

        var config = Build();

        Assert.Equal("True", config["CALENDARFEED_SAML_ENABLED"]);
        Assert.Equal("https://sp.test/saml", config["CALENDARFEED_SAML_ENTITYID"]);
        Assert.Equal("https://idp.test", config["CALENDARFEED_SAML_IDPENTITYID"]);
        Assert.Equal("https://idp.test/sso", config["CALENDARFEED_SAML_SINGLESIGNONURL"]);
        Assert.Equal("https://idp.test/slo", config["CALENDARFEED_SAML_SINGLELOGOUTURL"]);
        Assert.Equal("MIIC", config["CALENDARFEED_SAML_IDPCERTIFICATE"]);
        Assert.Equal("Register", config["CALENDARFEED_SAML_UNKNOWNUSER"]);
        Assert.Equal("社内 SSO", config["CALENDARFEED_SAML_BUTTONLABEL"]);
        Assert.Equal("True", config["CALENDARFEED_PLEASANTERSSO_ENABLED"]);
        Assert.Equal("http://pleasanter:8080", config["CALENDARFEED_PLEASANTERSSO_INTERNALBASEURL"]);
        Assert.Equal(".Pleasanter_Session,.Pleasanter_Auth", config["CALENDARFEED_PLEASANTERSSO_COOKIENAMES"]); // 配列はカンマ区切りへ
        Assert.Equal("1,2", config["CALENDARFEED_PLEASANTERSSO_ALLOWEDGROUPIDS"]);
        Assert.Equal("10", config["CALENDARFEED_PLEASANTERSSO_REVALIDATEMINUTES"]);
    }

    [Fact]
    public void Authentication_jsonのnullは写さない_環境変数を塗り潰さない()
    {
        Write("Authentication.json", """{ "SamlParameters": { "Enabled": null, "SPOptions": { "EntityId": null } } }""");
        Environment.SetEnvironmentVariable("CALENDARFEED_SAML_ENTITYID", "https://from-env.test");
        try
        {
            Assert.Equal("https://from-env.test", Build()["CALENDARFEED_SAML_ENTITYID"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CALENDARFEED_SAML_ENTITYID", null);
        }
    }

    [Fact]
    public void 階層のファイルは_ファイル名を節の名前にして読み_環境変数とlocal_jsonが上書きする()
    {
        Write("RateLimit.json", """{ "Policies": { "CalDav": { "Limit": 11, "WindowSeconds": 22 } } }""");
        var config = Build();
        Assert.Equal("11", config["RateLimit:Policies:CalDav:Limit"]);
        Assert.Equal("22", config["RateLimit:Policies:CalDav:WindowSeconds"]);
        Assert.Null(config["Policies:CalDav:Limit"]); // 他のファイルと混ざらないよう、根へはそのまま出さない

        Environment.SetEnvironmentVariable("RateLimit__Policies__CalDav__Limit", "33");
        Assert.Equal("33", Build()["RateLimit:Policies:CalDav:Limit"]);

        Write("RateLimit.local.json", """{ "Policies": { "CalDav": { "Limit": 44 } } }""");
        Assert.Equal("44", Build()["RateLimit:Policies:CalDav:Limit"]);
        Assert.Equal("22", Build()["RateLimit:Policies:CalDav:WindowSeconds"]);
    }

    [Fact]
    public void 平らなファイルの新しいキーも_正式な名前へ写す()
    {
        Write("Session.json", """{ "RetentionPeriod": 60 }""");
        Write("Security.json", """{ "LockoutCount": 7, "LockoutPeriod": 20, "PendingAuthenticationPeriod": 120, "InvitationPeriod": 24, "StaleAfterDays": 30 }""");
        Write("Rds.json", """{ "MigrationLockTimeoutSeconds": 99 }""");

        var p = FixedParameters.FromConfiguration(Build());

        Assert.Equal(TimeSpan.FromMinutes(60), p.SessionRetention);
        Assert.Equal(7, p.LockoutCount);
        Assert.Equal(TimeSpan.FromMinutes(20), p.LockoutPeriod);
        Assert.Equal(TimeSpan.FromSeconds(120), p.PendingAuthenticationPeriod);
        Assert.Equal(TimeSpan.FromHours(24), p.InvitationPeriod);
        Assert.Equal(TimeSpan.FromDays(30), p.StaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(99), p.MigrationLockTimeout);
    }
}

/// <summary>固定のパラメータが、実際の挙動に効くこと（Issue #92）。</summary>
public sealed class FixedParametersEffectTests : IDisposable
{
    private readonly TestApp _app;

    public FixedParametersEffectTests() => _app = new TestApp(extraSettings: new Dictionary<string, string?>
    {
        ["RateLimit:Policies:AdminLogin:Limit"] = "2",
        [FixedParameters.LockoutCountKey] = "9",
        [FixedParameters.LockoutPeriodKey] = "3",
        [FixedParameters.SessionRetentionPeriod] = "45",
        [FixedParameters.InvitationPeriodKey] = "12",
    });

    public void Dispose() => _app.Dispose();

    [Fact]
    public void 管理者認証の時間と回数が_設定どおりになる()
    {
        var auth = _app.Service<RuntimeSettings>().Current.AdminAuth;

        Assert.Equal(9, auth.LockoutThreshold);
        Assert.Equal(TimeSpan.FromMinutes(3), auth.Lockout);
        Assert.Equal(TimeSpan.FromMinutes(45), auth.SessionLifetime);
        Assert.Equal(TimeSpan.FromHours(12), auth.InvitationLifetime);
    }

    [Fact]
    public async Task ログインの試行の枠が_設定の回数で効く()
    {
        var client = _app.Client();
        async Task<HttpStatusCode> Attempt()
        {
            using var response = await client.PostAsJsonAsync("/admin/api/login", new { loginId = "nobody", password = "wrong password!!" });
            return response.StatusCode;
        }

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Attempt());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await Attempt());
        Assert.Equal(HttpStatusCode.TooManyRequests, await Attempt()); // 既定の 10 回ではなく、設定の 2 回
    }
}
