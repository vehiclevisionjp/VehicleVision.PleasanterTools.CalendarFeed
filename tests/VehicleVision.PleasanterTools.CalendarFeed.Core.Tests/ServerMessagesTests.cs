using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>サーバが管理画面へ返す文言（7 言語。Issue #58）。</summary>
public sealed class ServerMessagesTests
{
    private static readonly string[] AllLanguages = ["ja", "en", "zh", "de", "ko", "es", "vi"];

    [Fact]
    public void 対応する言語は_7つ()
    {
        Assert.Equal(AllLanguages.Order(), ServerMessages.Languages.Order());
    }

    [Fact]
    public void すべての鍵が_7言語すべてにある()
    {
        var missing = from key in ServerMessageKeys.All
                      from language in AllLanguages
                      where !ServerMessages.Has(key, language)
                      select $"{language}:{key}";

        Assert.Empty(missing);
    }

    [Fact]
    public void 言語ごとに_埋め込みの数が日本語と同じ()
    {
        // {0} の抜け・余計な {1} を検出する（翻訳で埋め込みを落とすと、値が画面に出ない）
        foreach (var key in ServerMessageKeys.All)
        {
            var expected = Placeholders(ServerMessages.Get(key, "ja"));
            foreach (var language in AllLanguages)
            {
                Assert.True(expected.SetEquals(Placeholders(ServerMessages.Get(key, language))), $"{language}:{key} の埋め込みが日本語と違う");
            }
        }
    }

    [Fact]
    public void 非日本語の言語の文言に_日本語の文字が混ざらない()
    {
        // 中国語は漢字を使うので除く。かな（ひらがな・カタカナ）は、日本語以外に出ない
        foreach (var key in ServerMessageKeys.All)
        {
            foreach (var language in AllLanguages.Where(l => l != "ja"))
            {
                Assert.DoesNotMatch("[぀-ヿ]", ServerMessages.Get(key, language));
            }
        }
    }

    [Theory]
    [InlineData("ja", "ログインが必要です。")]
    [InlineData("en", "You need to sign in.")]
    [InlineData("en-US", "You need to sign in.")]
    [InlineData("EN_gb", "You need to sign in.")]
    public void 言語を決めて返す_地域は見ない(string language, string expected)
    {
        Assert.Equal(expected, ServerMessages.Get(ServerMessageKeys.LoginRequired, language));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("xx-YY")]
    public void 言語が無い_対応外なら_英語へ落とす(string? language)
    {
        Assert.Equal("You need to sign in.", ServerMessages.Get(ServerMessageKeys.LoginRequired, language));
    }

    [Fact]
    public void 知らない鍵は_鍵そのものを返す_画面を壊さない()
    {
        Assert.Equal("noSuchKey", ServerMessages.Get("noSuchKey", "ja"));
    }

    [Fact]
    public void 埋め込みを埋める()
    {
        Assert.Equal("パスワードは 12 文字以上にしてください。", ServerMessages.Get(ServerMessageKeys.PasswordTooShort, "ja", 12));
        Assert.Equal("The password must be at least 12 characters.", ServerMessages.Get(ServerMessageKeys.PasswordTooShort, "en", 12));
    }

    [Fact]
    public void パスワードの条件の既定の文言も_言語ごとに返る()
    {
        var policy = new AdminPasswordPolicy(12, AllowSameAsLoginId: false);

        Assert.Equal("パスワードは 12 文字以上にしてください。", policy.Validate("alice", "short", "ja"));
        Assert.Equal("The password must be at least 12 characters.", policy.Validate("alice", "short", "en"));
        Assert.Equal("The password cannot be the same as the login ID.", policy.Validate("alice-password-1", "alice-password-1", "en"));
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value)];
}
