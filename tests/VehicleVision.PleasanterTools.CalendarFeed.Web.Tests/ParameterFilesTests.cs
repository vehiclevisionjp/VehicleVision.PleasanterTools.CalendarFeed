using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>
/// 環境変数はプロセス全体のものなので、**このクラスのテストは 1 つずつ直列に走らせ、必ず元へ戻す**
/// （同じクラス内は xUnit が直列に実行する）。
/// </summary>
public sealed class ParameterFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"calendarfeed-params-{Guid.NewGuid():N}");

    public ParameterFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppSettings.PleasanterBaseUrl, null);
        Environment.SetEnvironmentVariable(AppSettings.PleasanterApiKey, null);
        Directory.Delete(_dir, recursive: true);
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, name), json);

    private IConfigurationRoot Build() => new ConfigurationBuilder().AddParameterFiles(_dir).Build();

    [Fact]
    public void ファイルのキーを環境変数と同じ名前へ写す()
    {
        Write("Pleasanter.json", """{ "BaseUrl": "https://a.test", "TimeoutSeconds": 12 }""");
        Write("Feed.json", """{ "CacheSeconds": 60 }""");

        var config = Build();

        Assert.Equal("https://a.test", config[AppSettings.PleasanterBaseUrl]);
        Assert.Equal("12", config[AppSettings.PleasanterTimeoutSeconds]);
        Assert.Equal("60", config[AppSettings.FeedCacheSeconds]);
    }

    [Fact]
    public void 失敗の通知の設定も_ファイルのキーを写す()
    {
        Write("Feed.json", """{ "FailureNotify": true, "FailureNotifyThreshold": 5, "FailureNotifyRepeatHours": 12 }""");

        var config = Build();

        Assert.Equal("True", config[AppSettingDefinitions.FeedFailureNotify]);
        Assert.Equal("5", config[AppSettingDefinitions.FeedFailureNotifyThreshold]);
        Assert.Equal("12", config[AppSettingDefinitions.FeedFailureNotifyRepeatHours]);
    }

    [Fact]
    public void SAMLとPleasanterのログインの設定は_環境変数と同じ正式名のキーをファイルへそのまま書ける()
    {
        // 別名の写しが無い設定（SAML・Pleasanter のログイン）は、正式名を書けばよい（README に書いてある）
        Write("Service.json", """{ "CALENDARFEED_SAML_ENABLED": "true", "CALENDARFEED_PLEASANTERSSO_LOGINURL": "/users/login" }""");

        var config = Build();

        Assert.Equal("true", config["CALENDARFEED_SAML_ENABLED"]);
        Assert.Equal("/users/login", config["CALENDARFEED_PLEASANTERSSO_LOGINURL"]);
    }

    [Fact]
    public void 優先順位はlocal_jsonが環境変数より強く環境変数がjsonより強い()
    {
        Write("Pleasanter.json", """{ "BaseUrl": "https://file.test" }""");
        Environment.SetEnvironmentVariable(AppSettings.PleasanterBaseUrl, "https://env.test");
        Assert.Equal("https://env.test", Build()[AppSettings.PleasanterBaseUrl]);

        Write("Pleasanter.local.json", """{ "BaseUrl": "https://local.test" }""");
        Assert.Equal("https://local.test", Build()[AppSettings.PleasanterBaseUrl]);
    }

    [Fact]
    public void nullと空の値は写さず環境変数のAPIキーを塗り潰さない()
    {
        Write("Pleasanter.json", """{ "ApiKey": null }""");
        Write("Pleasanter.local.json", """{ "ApiKey": "" }""");
        Environment.SetEnvironmentVariable(AppSettings.PleasanterApiKey, "from-env");

        Assert.Equal("from-env", Build()[AppSettings.PleasanterApiKey]);
    }

    [Fact]
    public void 読んだ置き場と見つかったファイルを残す()
    {
        Write("Service.json", "{}");
        Write("Security.local.json", "{}");

        var config = Build();

        Assert.Equal(_dir, config[ParameterFiles.DirectoryKey]);
        Assert.Equal("Service.json / Security.local.json", config[ParameterFiles.FoundFilesKey]);
    }

    [Fact]
    public void 置き場が無くても落ちず見つかったファイルは空()
    {
        var config = new ConfigurationBuilder().AddParameterFiles(Path.Combine(_dir, "none")).Build();

        Assert.Equal(string.Empty, config[ParameterFiles.FoundFilesKey]);
    }

    [Fact]
    public void 同梱の既定ファイルが読める()
    {
        var shipped = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
            "VehicleVision.PleasanterTools.CalendarFeed.Web", "App_Data", "Parameters"));
        var config = new ConfigurationBuilder().AddParameterFiles(shipped).Build();

        // **管理画面から編集できる設定は、同梱のファイルで固定しない**（値が入っていると外部設定として固定され、画面から変えられなくなる）。
        // 既定値はコードにある
        foreach (var definition in AppSettingDefinitions.All)
        {
            Assert.True(config[definition.Key] is null, $"同梱のファイルが {definition.Key} を固定している: {config[definition.Key]}");
        }

        Assert.Null(config[AppSettings.SecretKey]);
    }

    [Fact]
    public void Pleasanterの接続先が未設定でも起動できる_画面から設定できる()
    {
        var config = new ConfigurationBuilder().Build();

        var options = TestSettings.Build(config).Pleasanter;

        Assert.Equal(string.Empty, options.BaseUrl);
        Assert.Equal("Asia/Tokyo", options.ApiKeyUserTimeZoneId);
        Assert.Equal(1.1m, options.ApiVersion);
        Assert.Equal(30, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("CALENDARFEED_PLEASANTER_APIVERSION", "abc")]
    [InlineData("CALENDARFEED_PLEASANTER_TIMEOUTSECONDS", "abc")]
    [InlineData("CALENDARFEED_PLEASANTER_BASEURL", "ftp://a.test")]
    public void 読めない外部設定は起動時に落とす(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        Assert.Throws<AppSettingValidationException>(() => TestSettings.Build(config));
    }

    [Fact]
    public void 外部設定の値を読む()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppSettings.PleasanterBaseUrl] = "https://a.test/",
            [AppSettings.PleasanterApiKey] = "k",
            [AppSettings.TimeZoneDefault] = "Asia/Tokyo",
            [AppSettings.PleasanterApiVersion] = "1.0",
            [AppSettings.PleasanterTimeoutSeconds] = "45",
        }).Build();

        var options = TestSettings.Build(config).Pleasanter;

        Assert.Equal("https://a.test", options.BaseUrl);
        Assert.Equal("k", options.ApiKey);
        Assert.Equal(1.0m, options.ApiVersion);
        Assert.Equal(45, options.TimeoutSeconds);
    }
}
