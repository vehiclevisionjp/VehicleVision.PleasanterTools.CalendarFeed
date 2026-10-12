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

        Assert.Equal("Asia/Tokyo", config[AppSettings.TimeZoneDefault]);
        Assert.Equal("300", config[AppSettings.FeedCacheSeconds]);
        Assert.Null(config[AppSettings.PleasanterApiKey]);
        Assert.Null(config[AppSettings.AdminApiKey]);
    }

    [Fact]
    public void 必須の設定が無ければ設定名を挙げて止める()
    {
        var config = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => AppSettings.ReadPleasanter(config));
        Assert.Contains(AppSettings.PleasanterBaseUrl, ex.Message);
    }

    [Fact]
    public void 読めない数値は既定へ落とす()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppSettings.PleasanterBaseUrl] = "https://a.test",
            [AppSettings.PleasanterApiKey] = "k",
            [AppSettings.TimeZoneDefault] = "Asia/Tokyo",
            [AppSettings.PleasanterApiVersion] = "abc",
            [AppSettings.PleasanterTimeoutSeconds] = "-5",
        }).Build();

        var options = AppSettings.ReadPleasanter(config);

        Assert.Equal(1.1m, options.ApiVersion);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal("Asia/Tokyo", options.ApiKeyUserTimeZoneId);
    }
}
