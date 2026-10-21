using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.CalendarFeed.Web;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>
/// 値をファイルから読む（<c>CALENDARFEED_X_FILE</c>。Docker の secrets・Kubernetes の Secret のボリューム。Issue #94）。
/// 環境変数はプロセス全体のものなので、直列に走らせ、必ず元へ戻す。
/// </summary>
public sealed class SecretFilesTests : IDisposable
{
    private const string SecretKeyFile = AppSettings.SecretKey + ParameterFiles.FileSuffix;
    private const string ApiKeyFile = AppSettings.PleasanterApiKey + ParameterFiles.FileSuffix;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"calendarfeed-secrets-{Guid.NewGuid():N}");

    public SecretFilesTests()
    {
        Directory.CreateDirectory(_dir);
        Clear();
    }

    public void Dispose()
    {
        Clear();
        Directory.Delete(_dir, recursive: true);
    }

    private static void Clear()
    {
        foreach (var name in new[] { AppSettings.SecretKey, SecretKeyFile, AppSettings.PleasanterApiKey, ApiKeyFile, "CALENDARFEED_SAML_IDPCERTIFICATE_FILE", "CALENDARFEED_SAML_IDPCERTIFICATE" })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private string WriteSecret(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private IConfigurationRoot Build() => new ConfigurationBuilder().AddParameterFiles(_dir).Build();

    [Fact]
    public void ファイルの中身が_設定の値になる_末尾の改行は取る()
    {
        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("secret_key", "abc123==\n"));
        Environment.SetEnvironmentVariable(ApiKeyFile, WriteSecret("api_key", "k-1\r\n"));

        var config = Build();

        Assert.Equal("abc123==", config[AppSettings.SecretKey]);
        Assert.Equal("k-1", config[AppSettings.PleasanterApiKey]);
    }

    [Fact]
    public void 途中の改行は残す_複数行の証明書()
    {
        const string pem = "-----BEGIN CERTIFICATE-----\nMIIC\nAAAA\n-----END CERTIFICATE-----";
        Environment.SetEnvironmentVariable("CALENDARFEED_SAML_IDPCERTIFICATE_FILE", WriteSecret("cert.pem", pem + "\n"));

        Assert.Equal(pem, Build()["CALENDARFEED_SAML_IDPCERTIFICATE"]);
    }

    [Fact]
    public void 読んだ設定の名前だけを記録し_値は載せない()
    {
        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("secret_key", "very-secret-value"));

        var config = Build();

        Assert.Equal(AppSettings.SecretKey, config[ParameterFiles.SecretFilesKey]);
        Assert.DoesNotContain("very-secret-value", config[ParameterFiles.SecretFilesKey]);
    }

    [Fact]
    public void 何も指定しなければ_記録は空()
    {
        Assert.True(string.IsNullOrEmpty(Build()[ParameterFiles.SecretFilesKey]));
    }

    [Fact]
    public void 優先順位は_環境変数と同じ強さ_local_jsonより弱い()
    {
        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("secret_key", "from-file"));
        Assert.Equal("from-file", Build()[AppSettings.SecretKey]);

        File.WriteAllText(Path.Combine(_dir, "Security.local.json"), """{ "SecretKey": "from-local-json" }""");
        Assert.Equal("from-local-json", Build()[AppSettings.SecretKey]);
    }

    [Fact]
    public void 本体の環境変数とファイルの指定が両方あると_起動時に落とす_値は出さない()
    {
        Environment.SetEnvironmentVariable(AppSettings.SecretKey, "plain-secret");
        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("secret_key", "file-secret"));

        var ex = Assert.Throws<InvalidOperationException>(Build);

        Assert.Contains(AppSettings.SecretKey, ex.Message);
        Assert.Contains(SecretKeyFile, ex.Message);
        Assert.DoesNotContain("plain-secret", ex.Message);
        Assert.DoesNotContain("file-secret", ex.Message);
    }

    [Fact]
    public void ファイルが無い_空_大きすぎるときは_起動時に落とす()
    {
        Environment.SetEnvironmentVariable(SecretKeyFile, Path.Combine(_dir, "none"));
        Assert.Contains("ありません", Assert.Throws<InvalidOperationException>(Build).Message);

        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("empty", "\n"));
        Assert.Contains("空", Assert.Throws<InvalidOperationException>(Build).Message);

        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("big", new string('x', 70 * 1024)));
        Assert.Contains("大きすぎ", Assert.Throws<InvalidOperationException>(Build).Message);
    }

    [Fact]
    public void 設定の名前でない_FILEは無視する()
    {
        Environment.SetEnvironmentVariable("CALENDARFEED_NOT_A_SETTING_FILE", Path.Combine(_dir, "none"));
        try
        {
            Assert.True(string.IsNullOrEmpty(Build()[ParameterFiles.SecretFilesKey]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CALENDARFEED_NOT_A_SETTING_FILE", null);
        }
    }

    [Fact]
    public void 鍵の生成は_ファイルで鍵が指定されているときも_書かずに断る()
    {
        Environment.SetEnvironmentVariable(SecretKeyFile, WriteSecret("secret_key", "x"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = SecretKeyCommand.Run([SecretKeyCommand.Argument, SecretKeyCommand.WriteArgument, $"--directory={_dir}"], output, error, _dir);

        Assert.Equal(1, code);
        Assert.Contains(SecretKeyFile, error.ToString());
        Assert.False(File.Exists(Path.Combine(_dir, "Security.local.json")));
    }
}
