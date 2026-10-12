using System.Security.Cryptography;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

public sealed class SecretProtectorTests
{
    private static readonly string Key = SecretProtector.GenerateKey();

    [Theory]
    [InlineData("6ce6c0fd2f4aea3c093c3ebdd7d4ea3250a132b4867d68e7d1abe35c2499664a")]
    [InlineData("日本語🗓️のキー")]
    [InlineData("")]
    public void 暗号化して復号すると元に戻る(string plain)
    {
        var protector = new SecretProtector(Key);

        var cipher = protector.Protect(plain, "ctx");

        Assert.StartsWith("v1:", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain(plain.Length > 4 ? plain : "\u0000", cipher, StringComparison.Ordinal);
        Assert.Equal(plain, protector.Unprotect(cipher, "ctx"));
    }

    [Fact]
    public void 同じ平文でも毎回違う暗号文になる()
    {
        var protector = new SecretProtector(Key);

        Assert.NotEqual(protector.Protect("secret", "ctx"), protector.Protect("secret", "ctx"));
    }

    [Fact]
    public void 別の文脈では復号できない_別のフィードへの差し替えを防ぐ()
    {
        var protector = new SecretProtector(Key);
        var cipher = protector.Protect("secret", "calendarfeed:apikey:feed-A");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(cipher, "calendarfeed:apikey:feed-B"));
    }

    [Fact]
    public void 別の鍵では復号できない()
    {
        var cipher = new SecretProtector(Key).Protect("secret", "ctx");

        Assert.ThrowsAny<CryptographicException>(
            () => new SecretProtector(SecretProtector.GenerateKey()).Unprotect(cipher, "ctx"));
    }

    [Fact]
    public void 改ざんを検知する()
    {
        var protector = new SecretProtector(Key);
        var cipher = protector.Protect("secret", "ctx");
        var raw = Convert.FromBase64String(cipher[3..]);
        raw[^1] ^= 0x01;
        var tampered = "v1:" + Convert.ToBase64String(raw);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered, "ctx"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v2:AAAA")]
    [InlineData("v1:!!!not-base64!!!")]
    [InlineData("v1:AAAA")]
    [InlineData("plain-text")]
    public void 壊れた暗号文は暗号の例外で止まる(string broken)
    {
        var protector = new SecretProtector(Key);

        if (broken.Length == 0)
        {
            Assert.Throws<ArgumentException>(() => protector.Unprotect(broken, "ctx"));
        }
        else
        {
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(broken, "ctx"));
        }
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void 鍵の書式と長さを検証する(string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new SecretProtector(key));

    [Fact]
    public void 作った鍵は32バイトで毎回違う()
    {
        var a = SecretProtector.GenerateKey();
        var b = SecretProtector.GenerateKey();

        Assert.Equal(32, Convert.FromBase64String(a).Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 鍵が未設定なら使おうとしたときに設定名を挙げて止まる()
    {
        ISecretProtector protector = new UnconfiguredSecretProtector();

        Assert.False(protector.IsConfigured);
        var ex = Assert.Throws<InvalidOperationException>(() => protector.Protect("x", "ctx"));
        Assert.Contains("CALENDARFEED_SECRET_KEY", ex.Message);
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect("v1:AAAA", "ctx"));
    }
}

public sealed class FeedLanguagesTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("JA", "ja")]
    [InlineData("zh_CN", "zh")]
    [InlineData(" de ", "de")]
    [InlineData(null, "ja")]
    [InlineData("", "ja")]
    [InlineData("fr", "ja")]
    public void 言語を正規化し対応外は既定へ落とす(string? input, string expected) =>
        Assert.Equal(expected, FeedLanguages.Normalize(input));

    [Fact]
    public void 全言語に仮の件名がある()
    {
        Assert.Equal(7, FeedLanguages.Supported.Count);
        foreach (var language in FeedLanguages.Supported)
        {
            Assert.False(string.IsNullOrWhiteSpace(FeedLanguages.Untitled(language)));
        }

        Assert.Equal("(無題)", FeedLanguages.Untitled("ja"));
        Assert.Equal("(Untitled)", FeedLanguages.Untitled("en"));
    }
}
