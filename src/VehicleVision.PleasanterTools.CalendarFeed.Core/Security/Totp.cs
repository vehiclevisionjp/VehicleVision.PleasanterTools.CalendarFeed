using System.Security.Cryptography;
using System.Text;
using OtpNet;

namespace VehicleVision.PleasanterTools.CalendarFeed.Security;

/// <summary>2 要素（TOTP。RFC 6238）。</summary>
/// <remarks>
/// <para>
/// 計算は Otp.NET（MIT）に任せる。**自前で書かない。**
/// </para>
/// <para>
/// **同じ時間枠の数字は二度通さない。** 30 秒の間は同じ数字が有効なので、盗み見られた数字がそのまま使える。
/// <see cref="Verify"/> は一致した時間枠を返し、呼び出し側が「前回より新しい枠か」を DB で原子的に確かめる。
/// </para>
/// </remarks>
public static class TotpService
{
    /// <summary>共有鍵の長さ（バイト）。RFC 4226 の推奨（160 bit）。</summary>
    private const int SecretBytes = 20;

    /// <summary>前後 1 枠（±30 秒）の時計のずれを許す。</summary>
    private static readonly VerificationWindow Window = new(previous: 1, future: 1);

    /// <summary>新しい共有鍵を作る（Base32）。</summary>
    public static string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(SecretBytes));

    /// <summary>認証アプリへ登録する <c>otpauth://</c> の URI。</summary>
    public static string BuildUri(string issuer, string loginId, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(loginId)}"
        + $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";

    /// <summary>
    /// 数字を確かめる。一致したら、その時間枠を返す。
    /// **時刻は呼び出し側から受け取る**（注入した <see cref="TimeProvider"/> の時刻で確かめられるように）。
    /// </summary>
    public static bool Verify(string secret, string code, DateTime utcNow, out long timeStep)
    {
        timeStep = 0;
        var normalized = new string(code.Where(char.IsDigit).ToArray());
        if (normalized.Length != 6 || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(utcNow, normalized, out timeStep, Window);
    }

    /// <summary>ある時刻の数字を作る（テスト用）。</summary>
    public static string Compute(string secret, DateTime utcNow) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(utcNow);
}

/// <summary>復旧コード（端末を失ったときの使い切りの入口）。</summary>
public static class RecoveryCodes
{
    /// <summary>発行する本数。</summary>
    public const int Count = 10;

    // 紛らわしい文字（0/O、1/I）を除いた 32 文字（1 文字 5 bit）
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 10;

    /// <summary>復旧コードを作る（<c>XXXXX-XXXXX</c>）。**発行時に 1 度だけ見せる。**</summary>
    public static IReadOnlyList<string> Generate() =>
        Enumerable.Range(0, Count)
            .Select(_ =>
            {
                var chars = new char[Length];
                for (var i = 0; i < Length; i++)
                {
                    chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                }

                return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
            })
            .ToList();

    /// <summary>入力を正規化する（大文字・ハイフンと空白を除く）。</summary>
    public static string Normalize(string code) =>
        new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>DB に保管する形（SHA-256）。**現物は保管しない。**</summary>
    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
}
