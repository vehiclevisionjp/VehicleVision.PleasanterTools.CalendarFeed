using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.Security;

/// <summary>管理者の招待トークン。</summary>
/// <remarks>
/// **推測できない 256 bit。DB には SHA-256 だけを保管する**（DB が漏れても、そこから有効な招待の URL は作れない）。
/// 現物は招待を発行した管理者に 1 度だけ見せる。
/// </remarks>
public static class InviteTokens
{
    /// <summary>新しいトークンを作る（URL に載せられる文字だけ）。</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>保管用のハッシュ（SHA-256 の 16 進・小文字）。</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>形が合うか（長さ・文字）。**DB を引く前に、明らかに違うものを落とす。**</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: >= 20 and <= 100 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
