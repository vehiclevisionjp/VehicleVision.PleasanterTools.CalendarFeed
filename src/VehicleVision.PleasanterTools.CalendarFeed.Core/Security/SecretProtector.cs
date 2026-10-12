using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.Security;

/// <summary>DB に置く秘密の暗号化と復号。</summary>
public interface ISecretProtector
{
    /// <summary>鍵が設定されているか。</summary>
    bool IsConfigured { get; }

    /// <summary>暗号化する。<paramref name="context"/> は暗号文を持ち主に束縛する（復号時に同じ値が要る）。</summary>
    string Protect(string plainText, string context);

    /// <summary>復号する。改ざん・別の <paramref name="context"/>・別の鍵なら <see cref="CryptographicException"/>。</summary>
    string Unprotect(string protectedText, string context);
}

/// <summary>鍵が未設定のときの実装。**使おうとしたときに、設定名を挙げて止まる。**</summary>
public sealed class UnconfiguredSecretProtector : ISecretProtector
{
    public const string SettingName = "CALENDARFEED_SECRET_KEY";

    public bool IsConfigured => false;

    public string Protect(string plainText, string context) => throw Missing();

    public string Unprotect(string protectedText, string context) => throw Missing();

    private static InvalidOperationException Missing() => new(
        $"秘密の鍵（{SettingName}）が設定されていません。API キーを保管・使用できません。"
        + "`--generate-secret-key` で鍵を作れます。");
}

/// <summary>AES-256-GCM で秘密を暗号化する。</summary>
/// <remarks>
/// <para>
/// **Data Protection の鍵束を使わない。** App Service では鍵の保存先が既定で一時領域になり、
/// 再起動で鍵を失うと保管した API キーが全部使えなくなる。運用者が持つ 1 本の鍵を設定から受け取る形にして、
/// この事故を無くす（Questionnaire の <c>SecretProtector</c> と同じ方針）。
/// </para>
/// <para>
/// **暗号文は <c>context</c>（フィード ID）に束縛する**（GCM の追加認証データ）。別のフィードの行へ暗号文を
/// 差し替えても復号できず、他のフィードの API キーを自分のフィードで使わせることができない。
/// </para>
/// <para>
/// **鍵を失うと復号できない。** その場合は API キーを登録し直す。
/// </para>
/// </remarks>
public sealed class SecretProtector : ISecretProtector
{
    /// <summary>書式の版。**変えるときは <see cref="Unprotect"/> に分岐を足す。**</summary>
    private const string Version = "v1";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly byte[] _key;

    /// <param name="base64Key">256 ビットの鍵を Base64 にしたもの。</param>
    public SecretProtector(string base64Key) => _key = DecodeKey(base64Key);

    public bool IsConfigured => true;

    /// <summary>新しい鍵を作る（Base64）。設定の <c>CALENDARFEED_SECRET_KEY</c> へ書く。</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>設定から受け取った鍵を読む。**書式と長さをここ 1 か所で見る。**</summary>
    public static byte[] DecodeKey(string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64Key);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"秘密の鍵は Base64 で与えてください（{UnconfiguredSecretProtector.SettingName}）", nameof(base64Key), exception);
        }

        return decoded.Length == KeyBytes
            ? decoded
            : throw new ArgumentException(
                $"秘密の鍵は {KeyBytes} バイト（256 ビット）で与えてください。実際は {decoded.Length} バイトです",
                nameof(base64Key));
    }

    public string Protect(string plainText, string context)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentException.ThrowIfNullOrEmpty(context);

        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));

        return $"{Version}:{Convert.ToBase64String([.. nonce, .. tag, .. cipher])}";
    }

    public string Unprotect(string protectedText, string context)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedText);
        ArgumentException.ThrowIfNullOrEmpty(context);

        var separator = protectedText.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0 || protectedText[..separator] != Version)
        {
            throw new CryptographicException("暗号文の書式（版）が不明です。");
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(protectedText[(separator + 1)..]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("暗号文が壊れています。", exception);
        }

        if (raw.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("暗号文が短すぎます。");
        }

        var nonce = raw.AsSpan(0, NonceBytes);
        var tag = raw.AsSpan(NonceBytes, TagBytes);
        var cipher = raw.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(plain);
    }
}
