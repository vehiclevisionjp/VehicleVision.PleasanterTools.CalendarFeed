using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>秘密の鍵の生成（<c>--generate-secret-key</c> と <c>--write</c>。Issue #92）。環境変数を触るので直列に走らせ、必ず戻す。</summary>
public sealed class SecretKeyCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"calendarfeed-secretkey-{Guid.NewGuid():N}");

    public SecretKeyCommandTests() => Environment.SetEnvironmentVariable(AppSettings.SecretKey, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppSettings.SecretKey, null);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = SecretKeyCommand.Run([SecretKeyCommand.Argument, .. args], output, error, _dir);
        return (code, output.ToString(), error.ToString());
    }

    private string LocalFile => Path.Combine(_dir, "Security.local.json");

    private string? KeyInFile() => JsonNode.Parse(File.ReadAllText(LocalFile))!["SecretKey"]?.GetValue<string>();

    [Fact]
    public void 書き込まなければ_鍵を表示するだけ_使える形()
    {
        var result = Run();

        Assert.Equal(0, result.Code);
        _ = new SecretProtector(result.Out.Trim()); // 32 バイトの Base64 として受け付けられる
        Assert.False(File.Exists(LocalFile));
    }

    [Fact]
    public void write_は_Security_local_jsonへ書き_鍵を画面へ出さない()
    {
        var result = Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");

        Assert.Equal(0, result.Code);
        var key = KeyInFile();
        _ = new SecretProtector(key!);
        Assert.DoesNotContain(key!, result.Out);   // ログに残さない
        Assert.Contains("Back it up", result.Out); // 控えを促す（コンソールへ出す文字列は英語）
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void 書いた鍵は_起動時の設定として読める()
    {
        Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddParameterFiles(_dir).Build();

        Assert.Equal(KeyInFile(), config[AppSettings.SecretKey]);
    }

    [Fact]
    public void すでに鍵があるなら_上書きせずに断る_ファイル()
    {
        Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");
        var first = KeyInFile();

        var again = Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");

        Assert.Equal(1, again.Code);
        Assert.Contains("already set", again.Error);
        Assert.Equal(first, KeyInFile());
    }

    [Fact]
    public void すでに鍵があるなら_上書きせずに断る_Security_jsonと環境変数()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Security.json"), """{ "SecretKey": "from-security-json" }""");
        Assert.Equal(1, Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}").Code);
        Assert.False(File.Exists(LocalFile));

        File.Delete(Path.Combine(_dir, "Security.json"));
        Environment.SetEnvironmentVariable(AppSettings.SecretKey, "from-env");
        var result = Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");
        Assert.Equal(1, result.Code);
        Assert.Contains(AppSettings.SecretKey, result.Error);
    }

    [Fact]
    public void Security_local_jsonの他のキーは残す()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LocalFile, """{ "LockoutCount": 7, "SecretKey": null }""");

        var result = Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");

        Assert.Equal(0, result.Code);
        var json = JsonNode.Parse(File.ReadAllText(LocalFile))!;
        Assert.Equal(7, json["LockoutCount"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(json["SecretKey"]?.GetValue<string>()));
    }

    [Fact]
    public void 読めないファイルは_鍵があるか分からないので上書きしない()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LocalFile, "{ これは JSON ではない");

        var result = Run(SecretKeyCommand.WriteArgument, $"--directory={_dir}");

        Assert.Equal(1, result.Code);
        Assert.Contains("unreadable", result.Error);
        Assert.Equal("{ これは JSON ではない", File.ReadAllText(LocalFile));
    }
}
