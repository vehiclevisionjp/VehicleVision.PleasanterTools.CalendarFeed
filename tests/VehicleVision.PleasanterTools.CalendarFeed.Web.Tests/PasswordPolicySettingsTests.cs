using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>パスワードの条件の設定（<c>PasswordPolicies</c>。Issue #29）。</summary>
public sealed class PasswordPolicySettingsTests
{
    private static IConfiguration Configure(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void 設定の条件を上から順に読み_文言を言語で選ぶ()
    {
        var options = AppSettings.ReadAdminAuth(Configure(
            ("PasswordPolicies:0:Regex", "[0-9]"),
            ("PasswordPolicies:0:Languages:0:Language", "ja"),
            ("PasswordPolicies:0:Languages:0:Body", "数字を含めてください。"),
            ("PasswordPolicies:0:Languages:1:Body", "Include a digit.")));

        Assert.Equal("数字を含めてください。", options.PasswordPolicy.Validate("admin", "abcdefghijkl", "ja"));
        Assert.Equal("Include a digit.", options.PasswordPolicy.Validate("admin", "abcdefghijkl", "en"));
        Assert.Null(options.PasswordPolicy.Validate("admin", "abcdefghijk1"));
    }

    [Fact]
    public void 設定が無ければ長さとログインIDだけを見る() =>
        Assert.Null(AppSettings.ReadAdminAuth(Configure()).PasswordPolicy.Validate("admin", "abcdefghijkl"));

    [Fact]
    public void 使えない正規表現は起動時に落ちる() =>
        Assert.Throws<InvalidOperationException>(
            () => AppSettings.ReadAdminAuth(Configure(("PasswordPolicies:0:Regex", "(?=a)"))));
}
