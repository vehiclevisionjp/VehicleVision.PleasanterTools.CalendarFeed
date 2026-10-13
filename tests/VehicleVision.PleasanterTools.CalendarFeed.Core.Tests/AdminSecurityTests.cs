using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

public sealed class PasswordHasherTests
{
    // テストを速くするため反復回数は小さくする（本番は CurrentIterations）
    private const int Fast = 1000;

    [Fact]
    public void ハッシュは版と反復回数を持ち毎回違う塩で作られる()
    {
        var a = PasswordHasher.Hash("correct horse battery", Fast);
        var b = PasswordHasher.Hash("correct horse battery", Fast);

        Assert.StartsWith("v1$1000$", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("correct horse", a, StringComparison.Ordinal);
    }

    [Fact]
    public void 正しいパスワードだけが通る()
    {
        var hash = PasswordHasher.Hash("correct horse battery", Fast);

        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("correct horse batterY", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("v2$1000$AAAA$AAAA")]
    [InlineData("v1$abc$AAAA$AAAA")]
    [InlineData("v1$1000$!!!$!!!")]
    [InlineData("v1$1000$AAAA$AAAA")]
    public void 壊れた書式は例外にせず通さない(string stored) =>
        Assert.False(PasswordHasher.Verify("anything", stored));

    [Fact]
    public void 今より弱い反復回数は作り直しの対象()
    {
        Assert.True(PasswordHasher.NeedsRehash(PasswordHasher.Hash("x", Fast)));
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Dummy));
        Assert.True(PasswordHasher.NeedsRehash("壊れている"));
        Assert.Equal(210_000, PasswordHasher.CurrentIterations);
    }

    [Fact]
    public void 利用者が居ないときの照合用ハッシュはどのパスワードも通さない()
    {
        Assert.False(PasswordHasher.Verify("dummy", PasswordHasher.Dummy));
        Assert.False(PasswordHasher.Verify("", PasswordHasher.Dummy));
    }
}

public sealed class AdminPasswordPolicyTests
{
    private readonly AdminPasswordPolicy _policy = new();

    [Theory]
    [InlineData("short")]
    [InlineData("12345678901")]
    [InlineData("")]
    public void 短いパスワードは弾く(string password) => Assert.NotNull(_policy.Validate("admin", password));

    [Fact]
    public void 長さの上限を超えたら弾く() =>
        Assert.NotNull(_policy.Validate("admin", new string('a', AdminPasswordPolicy.MaximumLength + 1)));

    [Fact]
    public void ログインIDと同じは大文字小文字を区別せず弾き_許す設定なら通る()
    {
        Assert.NotNull(_policy.Validate("administrator-1", "ADMINISTRATOR-1"));
        Assert.Null(new AdminPasswordPolicy(12, AllowSameAsLoginId: true).Validate("administrator-1", "ADMINISTRATOR-1"));
    }

    [Fact]
    public void 条件を満たせば通り_最低の長さは設定で変えられる()
    {
        Assert.Null(_policy.Validate("admin", "correct horse battery"));
        Assert.NotNull(new AdminPasswordPolicy(30).Validate("admin", "correct horse battery"));
        Assert.Null(new AdminPasswordPolicy(8).Validate("admin", "12345678"));
    }
}

public sealed class TotpServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 作った共有鍵で計算した数字を確かめられ時間枠を返す()
    {
        var secret = TotpService.GenerateSecret();
        var code = TotpService.Compute(secret, Now);

        Assert.True(TotpService.Verify(secret, code, Now, out var step));
        Assert.Equal(new DateTimeOffset(Now).ToUnixTimeSeconds() / 30, step);
        Assert.Equal(6, code.Length);
    }

    [Fact]
    public void 前後1枠のずれは許し_それ以上は許さない()
    {
        var secret = TotpService.GenerateSecret();
        var code = TotpService.Compute(secret, Now);

        Assert.True(TotpService.Verify(secret, code, Now.AddSeconds(30), out _));
        Assert.True(TotpService.Verify(secret, code, Now.AddSeconds(-30), out _));
        Assert.False(TotpService.Verify(secret, code, Now.AddSeconds(90), out _));
        Assert.False(TotpService.Verify(secret, code, Now.AddSeconds(-90), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void 六桁の数字でなければ通らない(string code) =>
        Assert.False(TotpService.Verify(TotpService.GenerateSecret(), code, Now, out _));

    [Fact]
    public void 空白やハイフンが混ざっても数字だけを見る()
    {
        var secret = TotpService.GenerateSecret();
        var code = TotpService.Compute(secret, Now);

        Assert.True(TotpService.Verify(secret, $"{code[..3]} {code[3..]}", Now, out _));
    }

    [Fact]
    public void 別の共有鍵の数字は通らない() =>
        Assert.False(TotpService.Verify(
            TotpService.GenerateSecret(), TotpService.Compute(TotpService.GenerateSecret(), Now), Now, out _));

    [Fact]
    public void 登録用のURIに発行元と鍵が入る()
    {
        var uri = TotpService.BuildUri("Calendar Feed", "admin@example.com", "ABCDEFGH");

        Assert.StartsWith("otpauth://totp/Calendar%20Feed:admin%40example.com?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=ABCDEFGH", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Calendar%20Feed", uri, StringComparison.Ordinal);
    }
}

public sealed class RecoveryCodesTests
{
    [Fact]
    public void 十本の異なるコードを紛らわしい文字なしで作る()
    {
        var codes = RecoveryCodes.Generate();

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{5}-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{5}$", c));
    }

    [Fact]
    public void 大文字小文字とハイフンと空白の違いは同じコードとして扱う()
    {
        Assert.Equal(RecoveryCodes.Hash("ABCDE-FGHJK"), RecoveryCodes.Hash(" abcde fghjk "));
        Assert.Equal(RecoveryCodes.Hash("ABCDE-FGHJK"), RecoveryCodes.Hash("abcdefghjk"));
        Assert.NotEqual(RecoveryCodes.Hash("ABCDE-FGHJK"), RecoveryCodes.Hash("ABCDE-FGHJL"));
    }

    [Fact]
    public void 保管する形は現物ではなくSHA256()
    {
        var hash = RecoveryCodes.Hash("ABCDE-FGHJK");

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("ABCDE", hash, StringComparison.OrdinalIgnoreCase);
    }
}
