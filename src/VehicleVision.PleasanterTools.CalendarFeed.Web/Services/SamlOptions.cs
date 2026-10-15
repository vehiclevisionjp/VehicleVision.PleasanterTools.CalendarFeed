using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using System.Collections.Immutable;
using System.IdentityModel.Selectors;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>本アプリに居ない利用者が IdP から来たときの扱い。</summary>
public enum SamlUnknownUserPolicy
{
    /// <summary>通さない。**既定。** 管理者は本アプリ側で先に作っておく。</summary>
    Reject = 0,

    /// <summary>その場で作って通す（JIT）。**役割は設定で決める。**</summary>
    Register = 1,
}

/// <summary>ログイン ID として使う値の取り出し先。</summary>
public enum SamlLoginIdSource
{
    /// <summary><c>NameID</c> をそのまま使う。**既定。** Google はここにメールアドレスを入れる。</summary>
    NameId = 0,

    /// <summary>属性から取る。<see cref="SamlOptions.LoginIdClaim"/> で名前を指定する。</summary>
    Claim = 1,
}

/// <summary>SAML 2.0 で管理画面へ入れるようにする設定（Issue #166）。</summary>
/// <remarks>
/// <para>
/// **既定は無効。** 何も設定しなければ、今までどおりログイン ID とパスワードだけになる。
/// </para>
/// <para>
/// ⚠️ **IdP の署名証明書は設定から与える。** メタデータの URL を渡して取りに行かせると、
/// その URL を差し替えられた時点で誰でも管理者になれる。**信頼の起点は設定に置く。**
/// </para>
/// <para>
/// ⚠️ **利用者の突き合わせはログイン ID で行う。** IdP から来た値
/// （既定は <c>NameID</c>）を本アプリのログイン ID として扱う。
/// **DB の列は増やしていない**（Issue #166 の決めごと）。
/// </para>
/// </remarks>
public sealed class SamlOptions
{
    public const string EnabledKey = "CALENDARFEED_SAML_ENABLED";
    public const string EntityIdKey = "CALENDARFEED_SAML_ENTITYID";
    public const string IdpEntityIdKey = "CALENDARFEED_SAML_IDPENTITYID";
    public const string SingleSignOnUrlKey = "CALENDARFEED_SAML_SINGLESIGNONURL";
    public const string IdpCertificateKey = "CALENDARFEED_SAML_IDPCERTIFICATE";
    public const string UnknownUserKey = "CALENDARFEED_SAML_UNKNOWNUSER";
    public const string RegisterRoleKey = "CALENDARFEED_SAML_REGISTERROLE";
    public const string LoginIdSourceKey = "CALENDARFEED_SAML_LOGINIDSOURCE";
    public const string LoginIdClaimKey = "CALENDARFEED_SAML_LOGINIDCLAIM";
    public const string ButtonLabelKey = "CALENDARFEED_SAML_BUTTONLABEL";

    /// <summary>IdP の単一ログアウトの窓口（Issue #191）。**未設定なら SLO は使わない。**</summary>
    public const string SingleLogoutUrlKey = "CALENDARFEED_SAML_SINGLELOGOUTURL";

    /// <summary>SAML でのログインを使うか。**既定は使わない。**</summary>
    public bool Enabled { get; init; }

    /// <summary>SP（本アプリ）の EntityID。**IdP へ登録する値と同じにする。**</summary>
    public string EntityId { get; init; } = string.Empty;

    /// <summary>IdP の EntityID。**これ以外が発行した応答は受け取らない。**</summary>
    public string IdpEntityId { get; init; } = string.Empty;

    /// <summary>IdP のログインの窓口（<c>AuthnRequest</c> の宛先）。</summary>
    public Uri? SingleSignOnUrl { get; init; }

    /// <summary>IdP の署名証明書。**入れ替えの最中は 2 枚とも並べられる。**</summary>
    public ImmutableArray<X509Certificate2> IdpCertificates { get; init; } = [];

    /// <summary>IdP の単一ログアウトの窓口（Issue #191）。</summary>
    /// <remarks>
    /// <para>
    /// **未設定なら単一ログアウトを使わない。** ログアウトは本アプリの cookie を
    /// 消すだけになる（これまでどおり）。**SLO に対応していない IdP でも動くこと。**
    /// </para>
    /// <para>
    /// ⚠️ **こちらからの要求に署名は付けない。** 署名を求める IdP と繋ぐには
    /// SP の秘密鍵を持つ必要があり、**鍵をもう 1 本増やすかどうかは別の判断**
    /// （検証用の Keycloak は <c>saml.client.signature = false</c> で求めていない）。
    /// **IdP から来る要求と応答の署名は必ず確かめる。**
    /// </para>
    /// </remarks>
    public Uri? SingleLogoutUrl { get; init; }

    /// <summary>単一ログアウトを使うか。</summary>
    public bool SingleLogoutEnabled => Enabled && SingleLogoutUrl is not null;

    /// <summary>本アプリに居ない利用者の扱い。**既定は通さない。**</summary>
    public SamlUnknownUserPolicy UnknownUser { get; init; } = SamlUnknownUserPolicy.Reject;

    /// <summary>
    /// <see cref="SamlUnknownUserPolicy.Register"/> で作る利用者の役割。
    /// **既定は <see cref="AdminRoles.Editor"/>。**
    /// </summary>
    /// <remarks>
    /// ⚠️ **ここを <see cref="AdminRoles.Administrator"/> にすると、
    /// IdP に居る全員が全権を持つ。** 組織全体へ配っている IdP では選ばないこと。
    /// </remarks>
    public string RegisterRole { get; init; } = AdminRoles.Editor;

    /// <summary>ログイン ID をどこから取るか。</summary>
    public SamlLoginIdSource LoginIdSource { get; init; } = SamlLoginIdSource.NameId;

    /// <summary>
    /// <see cref="SamlLoginIdSource.Claim"/> のときに読む属性の名前。
    /// </summary>
    public string LoginIdClaim { get; init; } = string.Empty;

    /// <summary>ログイン画面の釦に出す文字。**組織の IdP の名前を入れると分かりやすい。**</summary>
    public string ButtonLabel { get; init; } = string.Empty;

    /// <summary>鍵ごとの生値から設定を読む。</summary>
    /// <remarks>DB と外部設定を合成した後にも、従来と同じ検証を通すための入口。</remarks>
    public static SamlOptions FromValues(Func<string, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);

        if (!IsTrue(valueOf(EnabledKey)))
        {
            return new SamlOptions();
        }

        var entityId = Trim(valueOf(EntityIdKey));
        var idpEntityId = Trim(valueOf(IdpEntityIdKey));
        var singleSignOnUrl = Trim(valueOf(SingleSignOnUrlKey));
        var certificates = ReadCertificates(valueOf(IdpCertificateKey));

        Require(entityId, EntityIdKey);
        Require(idpEntityId, IdpEntityIdKey);
        Require(singleSignOnUrl, SingleSignOnUrlKey);

        if (certificates.IsEmpty)
        {
            throw new SettingException(ServerMessageKeys.SamlCertificateRequired, [IdpCertificateKey]);
        }

        if (!Uri.TryCreate(singleSignOnUrl, UriKind.Absolute, out var ssoUri)
            || (ssoUri.Scheme != Uri.UriSchemeHttps && ssoUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new SettingException(ServerMessageKeys.SettingAbsoluteUrlInvalid, [SingleSignOnUrlKey, singleSignOnUrl]);
        }

        // **単一ログアウトは任意**（Issue #191）。**未設定でも SAML は使える**
        var singleLogoutUrl = Trim(valueOf(SingleLogoutUrlKey));
        Uri? sloUri = null;

        if (singleLogoutUrl.Length > 0
            && (!Uri.TryCreate(singleLogoutUrl, UriKind.Absolute, out sloUri)
                || (sloUri.Scheme != Uri.UriSchemeHttps && sloUri.Scheme != Uri.UriSchemeHttp)))
        {
            throw new SettingException(ServerMessageKeys.SettingAbsoluteUrlInvalid, [SingleLogoutUrlKey, singleLogoutUrl]);
        }

        var loginIdSource = ParseEnum<SamlLoginIdSource>(valueOf(LoginIdSourceKey), LoginIdSourceKey)
            ?? SamlLoginIdSource.NameId;
        var loginIdClaim = Trim(valueOf(LoginIdClaimKey));

        if (loginIdSource == SamlLoginIdSource.Claim && loginIdClaim.Length == 0)
        {
            throw new SettingException(ServerMessageKeys.SamlLoginIdClaimRequired, [LoginIdSourceKey, LoginIdClaimKey]);
        }

        return new SamlOptions
        {
            Enabled = true,
            EntityId = entityId,
            IdpEntityId = idpEntityId,
            SingleSignOnUrl = ssoUri,
            SingleLogoutUrl = sloUri,
            IdpCertificates = certificates,
            UnknownUser = ParseEnum<SamlUnknownUserPolicy>(valueOf(UnknownUserKey), UnknownUserKey)
                ?? SamlUnknownUserPolicy.Reject,
            RegisterRole = ParseRole(valueOf(RegisterRoleKey)),
            LoginIdSource = loginIdSource,
            LoginIdClaim = loginIdClaim,
            ButtonLabel = Trim(valueOf(ButtonLabelKey)),
        };
    }

    /// <summary>照合に使う SAML の設定を作る。</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Saml2Configuration.CertificateValidationMode"/> を
    /// <see cref="X509CertificateValidationMode.None"/> にしているのは、
    /// **信頼の起点が設定で与えた証明書そのもの**だから。
    /// IdP の証明書は自己署名が普通で、連鎖を辿らせると必ず落ちる。
    /// </para>
    /// </remarks>
    public Saml2Configuration ToSaml2Configuration()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("SAML は無効です。");
        }

        var configuration = new Saml2Configuration
        {
            Issuer = EntityId,
            SingleSignOnDestination = SingleSignOnUrl,

            // **単一ログアウトの宛先**（Issue #191）。未設定なら null のまま
            SingleLogoutDestination = SingleLogoutUrl,

            // **発行者を固定する。** 別の IdP が署名した応答を受け取らない
            AllowedIssuer = IdpEntityId,

            // **宛先も固定する。** 他所の SP 向けの応答を持ち込まれても受け取らない
            AudienceRestricted = true,

            // **署名の無い応答は受け取らない**（既定のまま）
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        configuration.AllowedAudienceUris.Add(EntityId);
        configuration.SignatureValidationCertificates.AddRange(IdpCertificates);

        // **署名の種類は IdP 任せにしない。** SHA-1 は受け取らない
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha256Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha384Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha512Signature);

        return configuration;
    }

    private static void Require(string value, string key)
    {
        if (value.Length == 0)
        {
            throw new SettingException(ServerMessageKeys.SettingRequiredWhenEnabled, [EnabledKey, key]);
        }
    }

    /// <summary>役割。知らない値は例外にする（黙って既定へ落とさない）。空なら <see cref="AdminRoles.Editor"/>。</summary>
    private static string ParseRole(string? value)
    {
        var trimmed = Trim(value);
        if (trimmed.Length == 0)
        {
            return AdminRoles.Editor;
        }

        return AdminRoles.All.FirstOrDefault(r => string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new SettingException(ServerMessageKeys.SettingUnknownValue, [RegisterRoleKey, trimmed, string.Join(" / ", AdminRoles.All)]);
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsTrue(string? value) =>
        bool.TryParse(value?.Trim(), out var parsed) && parsed;

    /// <summary>知らない値は例外にする。**書き間違いを黙って既定へ落とさない。**</summary>
    private static TEnum? ParseEnum<TEnum>(string? value, string key)
        where TEnum : struct, Enum
    {
        var trimmed = Trim(value);
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!Enum.TryParse<TEnum>(trimmed, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new SettingException(ServerMessageKeys.SettingUnknownValue, [key, trimmed, string.Join(" / ", Enum.GetNames<TEnum>())]);
        }

        return parsed;
    }

    /// <summary>PEM でも base64 でも読む。**複数枚はカンマか改行で並べる。**</summary>
    private static ImmutableArray<X509Certificate2> ReadCertificates(string? raw)
    {
        var trimmed = Trim(raw);
        if (trimmed.Length == 0)
        {
            return [];
        }

        // PEM は「-----BEGIN CERTIFICATE-----」の塊ごとに読む。
        // **カンマで割ると PEM の中身まで割れてしまう**ので、先に PEM を見る
        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return ReadPem(trimmed);
        }

        var builder = ImmutableArray.CreateBuilder<X509Certificate2>();
        foreach (var part in trimmed.Split(
            [',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            builder.Add(LoadBase64(part));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<X509Certificate2> ReadPem(string pem)
    {
        var builder = ImmutableArray.CreateBuilder<X509Certificate2>();
        var index = 0;

        while (true)
        {
            var begin = pem.IndexOf("-----BEGIN", index, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            var endMarker = pem.IndexOf("-----END", begin, StringComparison.Ordinal);
            if (endMarker < 0)
            {
                throw new SettingException(ServerMessageKeys.SamlCertificatePemIncomplete, [IdpCertificateKey]);
            }

            var end = pem.IndexOf("-----", endMarker + "-----END".Length, StringComparison.Ordinal);
            var length = (end < 0 ? pem.Length : end + "-----".Length) - begin;

            try
            {
                builder.Add(X509Certificate2.CreateFromPem(pem.AsSpan(begin, length)));
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                throw new SettingException(ServerMessageKeys.SamlCertificatePemUnreadable, [IdpCertificateKey], exception);
            }

            index = begin + length;
        }

        if (builder.Count == 0)
        {
            throw new SettingException(ServerMessageKeys.SamlCertificateNotFound, [IdpCertificateKey]);
        }

        return builder.ToImmutable();
    }

    private static X509Certificate2 LoadBase64(string value)
    {
        try
        {
            // **.NET 9 以降は X509CertificateLoader を使う**（従来の構築子は非推奨）
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new SettingException(ServerMessageKeys.SamlCertificateUnreadable, [IdpCertificateKey], exception);
        }
    }
}
