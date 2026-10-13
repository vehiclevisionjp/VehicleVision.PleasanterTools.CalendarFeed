using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.Security;

/// <summary>管理者のパスワードのハッシュ（PBKDF2-HMAC-SHA512）。</summary>
/// <remarks>
/// <para>
/// **書式に版と反復回数を持たせる**（<c>v1$210000$塩$ハッシュ</c>）。反復回数を将来上げても、既存の利用者を締め出さずに
/// 次のログインで移行できる（<see cref="NeedsRehash"/>）。
/// </para>
/// <para>
/// **平文・可逆暗号にしない。** 塩は毎回新しく作る。比較は定数時間。
/// </para>
/// </remarks>
public static class PasswordHasher
{
    private const string Version = "v1";
    private const int SaltBytes = 16;
    private const int HashBytes = 64;

    /// <summary>反復回数。OWASP の PBKDF2-HMAC-SHA512 の推奨（210,000 回）。</summary>
    public const int CurrentIterations = 210_000;

    /// <summary>ハッシュを作る。</summary>
    public static string Hash(string password, int iterations = CurrentIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>照合する。書式が壊れていれば <c>false</c>（例外にしない）。</summary>
    public static bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!TryParse(stored, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), expected);
    }

    /// <summary>今の反復回数より弱いか。ログインに成功したときに作り直す。</summary>
    public static bool NeedsRehash(string stored) =>
        !TryParse(stored, out var iterations, out _, out _) || iterations < CurrentIterations;

    /// <summary>
    /// 利用者が居ないときの照合用。**居なくても同じ時間をかけて照合する**（応答の速さで実在が分からないように）。
    /// </summary>
    public static readonly string Dummy = Hash("dummy-password-for-unknown-users");

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, HashBytes);

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Version
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length == HashBytes;
    }
}

/// <summary>パスワードに求める条件。</summary>
/// <param name="MinimumLength">最低の長さ。</param>
/// <param name="AllowSameAsLoginId">ログイン ID と同じパスワードを許すか（大文字小文字は区別しない）。</param>
public sealed record AdminPasswordPolicy(int MinimumLength = 12, bool AllowSameAsLoginId = false)
{
    /// <summary>上限。極端に長い入力で照合の負荷を上げさせない。</summary>
    public const int MaximumLength = 256;

    /// <summary>条件を満たさなければ理由を返す。**最初の管理者・パスワードの変更のどちらもここを通す**（片方だけ緩いと、そこが一番弱い所になる）。</summary>
    public string? Validate(string loginId, string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            return $"パスワードは {MinimumLength} 文字以上にしてください。";
        }

        if (password.Length > MaximumLength)
        {
            return $"パスワードは {MaximumLength} 文字以下にしてください。";
        }

        return !AllowSameAsLoginId && string.Equals(password, loginId, StringComparison.OrdinalIgnoreCase)
            ? "パスワードにログイン ID と同じものは使えません。"
            : null;
    }
}
