using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>購読 URL に載せる秘密トークン。</summary>
/// <remarks>
/// **トークンそのものは保管しない。** DB へ入れるのは SHA-256 だけで、発行時に 1 度だけ利用者へ見せる。
/// トークンは 256 bit の乱数なので、ソルトや鍵伸長は要らない。
/// </remarks>
public static class FeedTokens
{
    /// <summary>新しいトークンを作る。URL にそのまま載せられる（base64url、パディング無し）。</summary>
    public static string Generate() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>DB に保管する形（小文字 16 進の SHA-256）にする。</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
