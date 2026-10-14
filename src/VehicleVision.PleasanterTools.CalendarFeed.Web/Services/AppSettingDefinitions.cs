using System.Collections.Frozen;
using System.Globalization;
using System.Net.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>設定値の型。</summary>
public enum AppSettingValueType
{
    String,
    Boolean,
    Integer,
}

/// <summary>設定のまとまり（管理画面の見出し）。</summary>
public static class AppSettingGroups
{
    public const string Service = "service";
    public const string Pleasanter = "pleasanter";
    public const string Feed = "feed";
    public const string Security = "security";
    public const string Mail = "mail";
    public const string Saml = "saml";
    public const string PleasanterSso = "pleasanterSso";
}

/// <summary>画面から検証に落ちた設定。メッセージはそのまま利用者へ返す。</summary>
public sealed class AppSettingValidationException(string message) : Exception(message);

/// <summary>画面から編集できる設定 1 つ分の定義。</summary>
/// <remarks>Questionnaire の <c>AppSettingDefinition</c> に則る。画面の見出し・説明は、画面側が <see cref="Key"/> で引く（多言語）。</remarks>
public sealed record AppSettingDefinition(
    string Key,
    string Group,
    AppSettingValueType Type,
    string DefaultValue,
    string Label,
    bool IsSecret = false,
    int? Minimum = null,
    int? Maximum = null,
    int? MaximumLength = null,
    IReadOnlyList<string>? AllowedValues = null,
    Func<string, string>? StringNormalizer = null,
    bool Multiline = false)
{
    /// <summary>入力を正規化する。範囲・形式が合わなければ <see cref="AppSettingValidationException"/>。</summary>
    public string Normalize(string? requested) => Normalize(requested, enforceRange: true);

    /// <summary>外部設定（環境変数・Parameters）の値を正規化する。<b>範囲は課さない</b>（検証環境の設定で起動できなくならないように）。</summary>
    public string NormalizeExternal(string? value) => Normalize(value, enforceRange: false);

    private string Normalize(string? requested, bool enforceRange)
    {
        var value = requested?.Trim() ?? string.Empty;
        return Type switch
        {
            AppSettingValueType.String => NormalizeString(value, enforceRange),
            AppSettingValueType.Boolean => bool.TryParse(value, out var b)
                ? (b ? "true" : "false")
                : throw new AppSettingValidationException($"{Label}は真偽値（true / false）で指定してください。"),
            AppSettingValueType.Integer => NormalizeInteger(value, enforceRange),
            _ => throw new InvalidOperationException($"未対応の設定型です: {Type}"),
        };
    }

    private string NormalizeString(string value, bool enforceRange)
    {
        if (enforceRange && MaximumLength is { } max && value.Length > max)
        {
            throw new AppSettingValidationException($"{Label}は {max} 文字以内で入力してください。");
        }

        // 複数行の設定（証明書の PEM など）は、改行とタブだけを許す
        if (value.Any(c => char.IsControl(c) && !(Multiline && c is '\r' or '\n' or '\t')))
        {
            throw new AppSettingValidationException($"{Label}に制御文字は使えません。");
        }

        if (AllowedValues is { } allowed)
        {
            return allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase))
                ?? throw new AppSettingValidationException($"{Label}は {string.Join(" / ", allowed)} から選んでください。");
        }

        return StringNormalizer?.Invoke(value) ?? value;
    }

    private string NormalizeInteger(string value, bool enforceRange)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new AppSettingValidationException($"{Label}は整数で指定してください。");
        }

        if (enforceRange && Minimum is { } min && parsed < min)
        {
            throw new AppSettingValidationException($"{Label}は {min} 以上で指定してください。");
        }

        if (enforceRange && Maximum is { } max && parsed > max)
        {
            throw new AppSettingValidationException($"{Label}は {max} 以下で指定してください。");
        }

        return parsed.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>画面から編集できる設定の一覧。</summary>
/// <remarks>
/// <b>起動に要る値はここへ入れない</b>: DB の接続・秘密の鍵・自動マイグレーション・転送ヘッダの信頼ネットワーク・
/// パスワード条件の正規表現・メール送信ワーカーの切り替えは、これまでどおりファイル／環境変数。
/// </remarks>
public static class AppSettingDefinitions
{
    public const string PublicBaseUrl = "CALENDARFEED_PUBLIC_BASEURL";
    public const string TimeZoneDefault = "CALENDARFEED_TIMEZONE_DEFAULT";

    public const string PleasanterBaseUrl = "CALENDARFEED_PLEASANTER_BASEURL";
    public const string PleasanterApiKey = "CALENDARFEED_PLEASANTER_APIKEY";
    public const string PleasanterApiVersion = "CALENDARFEED_PLEASANTER_APIVERSION";
    public const string PleasanterTimeoutSeconds = "CALENDARFEED_PLEASANTER_TIMEOUTSECONDS";
    public const string PleasanterTimeZone = "CALENDARFEED_PLEASANTER_TIMEZONE";
    public const string PleasanterLinkBaseUrl = "CALENDARFEED_PLEASANTER_LINKBASEURL";

    public const string FeedCacheSeconds = "CALENDARFEED_FEED_CACHESECONDS";
    public const string FeedMaxEvents = "CALENDARFEED_FEED_MAXEVENTS";
    public const string FeedPageSize = "CALENDARFEED_FEED_PAGESIZE";

    public const string AdminTwoFactor = "CALENDARFEED_ADMIN_TWOFACTOR";
    public const string PasswordMinimumLength = "CALENDARFEED_PASSWORD_MINIMUMLENGTH";
    public const string PasswordAllowSameAsLoginId = "CALENDARFEED_PASSWORD_ALLOWSAMEASLOGINID";
    public const string AuditLogRetentionDays = "CALENDARFEED_AUDITLOG_RETENTION_DAYS";

    public const string MailEnabled = "CALENDARFEED_MAIL_ENABLED";
    public const string MailTransport = "CALENDARFEED_MAIL_TRANSPORT";
    public const string MailSmtpHost = "CALENDARFEED_MAIL_SMTP_HOST";
    public const string MailSmtpPort = "CALENDARFEED_MAIL_SMTP_PORT";
    public const string MailSmtpSecurity = "CALENDARFEED_MAIL_SMTP_SECURITY";
    public const string MailSmtpUser = "CALENDARFEED_MAIL_SMTP_USER";
    public const string MailSmtpPassword = "CALENDARFEED_MAIL_SMTP_PASSWORD";
    public const string MailFromAddress = "CALENDARFEED_MAIL_FROM_ADDRESS";
    public const string MailFromName = "CALENDARFEED_MAIL_FROM_NAME";
    public const string MailReplyToAddress = "CALENDARFEED_MAIL_REPLYTO_ADDRESS";
    public const string MailSesRegion = "CALENDARFEED_MAIL_SES_REGION";
    public const string MailAcsEndpoint = "CALENDARFEED_MAIL_ACS_ENDPOINT";
    public const string MailTimeoutSeconds = "CALENDARFEED_MAIL_TIMEOUT_SECONDS";
    public const string MailSendPerMinute = "CALENDARFEED_MAIL_SEND_PER_MINUTE";

    public const string SamlEnabled = "CALENDARFEED_SAML_ENABLED";
    public const string SamlEntityId = "CALENDARFEED_SAML_ENTITYID";
    public const string SamlIdpEntityId = "CALENDARFEED_SAML_IDPENTITYID";
    public const string SamlSingleSignOnUrl = "CALENDARFEED_SAML_SINGLESIGNONURL";
    public const string SamlIdpCertificate = "CALENDARFEED_SAML_IDPCERTIFICATE";
    public const string SamlUnknownUser = "CALENDARFEED_SAML_UNKNOWNUSER";
    public const string SamlRegisterRole = "CALENDARFEED_SAML_REGISTERROLE";
    public const string SamlLoginIdSource = "CALENDARFEED_SAML_LOGINIDSOURCE";
    public const string SamlLoginIdClaim = "CALENDARFEED_SAML_LOGINIDCLAIM";
    public const string SamlButtonLabel = "CALENDARFEED_SAML_BUTTONLABEL";
    public const string SamlSingleLogoutUrl = "CALENDARFEED_SAML_SINGLELOGOUTURL";
    public const string PleasanterSsoEnabled = "CALENDARFEED_PLEASANTERSSO_ENABLED";
    public const string PleasanterSsoInternalBaseUrl = "CALENDARFEED_PLEASANTERSSO_INTERNALBASEURL";
    public const string PleasanterSsoLoginUrl = "CALENDARFEED_PLEASANTERSSO_LOGINURL";
    public const string PleasanterSsoLogoutUrl = "CALENDARFEED_PLEASANTERSSO_LOGOUTURL";
    public const string PleasanterSsoCookieNames = "CALENDARFEED_PLEASANTERSSO_COOKIENAMES";
    public const string PleasanterSsoAllowedDeptIds = "CALENDARFEED_PLEASANTERSSO_ALLOWEDDEPTIDS";
    public const string PleasanterSsoAllowedGroupIds = "CALENDARFEED_PLEASANTERSSO_ALLOWEDGROUPIDS";
    public const string PleasanterSsoUnknownUser = "CALENDARFEED_PLEASANTERSSO_UNKNOWNUSER";
    public const string PleasanterSsoRegisterRole = "CALENDARFEED_PLEASANTERSSO_REGISTERROLE";
    public const string PleasanterSsoRevalidateMinutes = "CALENDARFEED_PLEASANTERSSO_REVALIDATEMINUTES";
    public const string PleasanterSsoTimeoutSeconds = "CALENDARFEED_PLEASANTERSSO_TIMEOUTSECONDS";
    public const string PleasanterSsoButtonLabel = "CALENDARFEED_PLEASANTERSSO_BUTTONLABEL";

    private static readonly string[] TwoFactorModes = ["required", "optional", "disabled"];

    /// <summary>すべての定義（画面に出す順）。</summary>
    public static readonly IReadOnlyList<AppSettingDefinition> All =
    [
        new(PublicBaseUrl, AppSettingGroups.Service, AppSettingValueType.String, string.Empty, "公開 URL", MaximumLength: 2000, StringNormalizer: v => Url(v, requireHttps: false)),
        new(TimeZoneDefault, AppSettingGroups.Service, AppSettingValueType.String, "Asia/Tokyo", "既定のタイムゾーン", MaximumLength: 64, StringNormalizer: v => TimeZone(v, required: true)),

        new(PleasanterBaseUrl, AppSettingGroups.Pleasanter, AppSettingValueType.String, string.Empty, "Pleasanter の URL", MaximumLength: 2000, StringNormalizer: v => Url(v, requireHttps: false)),
        new(PleasanterApiKey, AppSettingGroups.Pleasanter, AppSettingValueType.String, string.Empty, "Pleasanter の共通 API キー", IsSecret: true, MaximumLength: 512),
        new(PleasanterApiVersion, AppSettingGroups.Pleasanter, AppSettingValueType.String, "1.1", "Pleasanter の API バージョン", MaximumLength: 8, StringNormalizer: ApiVersion),
        new(PleasanterTimeoutSeconds, AppSettingGroups.Pleasanter, AppSettingValueType.Integer, "30", "Pleasanter 呼び出しのタイムアウト（秒）", Minimum: 1, Maximum: 600),
        new(PleasanterTimeZone, AppSettingGroups.Pleasanter, AppSettingValueType.String, string.Empty, "API キー利用者のタイムゾーン", MaximumLength: 64, StringNormalizer: v => TimeZone(v, required: false)),
        new(PleasanterLinkBaseUrl, AppSettingGroups.Pleasanter, AppSettingValueType.String, string.Empty, "予定のリンクの基点 URL", MaximumLength: 2000, StringNormalizer: v => Url(v, requireHttps: false)),

        new(FeedCacheSeconds, AppSettingGroups.Feed, AppSettingValueType.Integer, "300", "キャッシュ秒数", Minimum: 1, Maximum: 86400),
        new(FeedMaxEvents, AppSettingGroups.Feed, AppSettingValueType.Integer, "5000", "1 フィードの予定の上限", Minimum: 1, Maximum: 100000),
        new(FeedPageSize, AppSettingGroups.Feed, AppSettingValueType.Integer, "1000", "1 ページの取得件数", Minimum: 1, Maximum: 10000),

        new(AdminTwoFactor, AppSettingGroups.Security, AppSettingValueType.String, "optional", "管理者の 2 要素", AllowedValues: TwoFactorModes),
        new(PasswordMinimumLength, AppSettingGroups.Security, AppSettingValueType.Integer, "12", "パスワードの最低の長さ", Minimum: 8, Maximum: 128),
        new(PasswordAllowSameAsLoginId, AppSettingGroups.Security, AppSettingValueType.Boolean, "false", "ログイン ID と同じパスワードを許す"),
        new(AuditLogRetentionDays, AppSettingGroups.Security, AppSettingValueType.Integer, "365", "監査ログを残す日数", Minimum: 0, Maximum: 36500),

        new(MailEnabled, AppSettingGroups.Mail, AppSettingValueType.Boolean, "false", "メール送信を有効にする"),
        new(MailTransport, AppSettingGroups.Mail, AppSettingValueType.String, "Smtp", "送信方式", AllowedValues: ["Smtp", "AmazonSes", "AzureCommunicationServices"]),
        new(MailSmtpHost, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "SMTP サーバー", MaximumLength: 255),
        new(MailSmtpPort, AppSettingGroups.Mail, AppSettingValueType.Integer, "587", "SMTP ポート", Minimum: 1, Maximum: 65535),
        new(MailSmtpSecurity, AppSettingGroups.Mail, AppSettingValueType.String, "StartTls", "SMTP の暗号化", AllowedValues: ["StartTls", "ImplicitTls", "None"]),
        new(MailSmtpUser, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "SMTP 利用者", MaximumLength: 320),
        new(MailSmtpPassword, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "SMTP パスワード", IsSecret: true, MaximumLength: 2000),
        new(MailFromAddress, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "差出人メールアドレス", MaximumLength: 320, StringNormalizer: MailAddressOrEmpty),
        new(MailFromName, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "差出人名", MaximumLength: 200),
        new(MailReplyToAddress, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "返信先メールアドレス", MaximumLength: 320, StringNormalizer: MailAddressOrEmpty),
        new(MailSesRegion, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "Amazon SES のリージョン", MaximumLength: 64),
        new(MailAcsEndpoint, AppSettingGroups.Mail, AppSettingValueType.String, string.Empty, "Azure Communication Services のエンドポイント", MaximumLength: 2000, StringNormalizer: v => Url(v, requireHttps: true)),
        new(MailTimeoutSeconds, AppSettingGroups.Mail, AppSettingValueType.Integer, "30", "1 通あたりのタイムアウト（秒）", Minimum: 1, Maximum: 300),
        new(MailSendPerMinute, AppSettingGroups.Mail, AppSettingValueType.Integer, "60", "1 分あたりの送信数の上限", Minimum: 1, Maximum: 6000),

        // SAML 2.0 でのログイン（既定は無効。Questionnaire の SAML と同じ項目）
        new(SamlEnabled, AppSettingGroups.Saml, AppSettingValueType.Boolean, "false", "SAML ログインを有効にする"),
        new(SamlEntityId, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "SP のエンティティ ID", MaximumLength: 2000),
        new(SamlIdpEntityId, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "IdP のエンティティ ID", MaximumLength: 2000),
        new(SamlSingleSignOnUrl, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "IdP のシングルサインオン URL", MaximumLength: 2000, StringNormalizer: v => Url(v, requireHttps: false)),
        new(SamlIdpCertificate, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "IdP の署名証明書", MaximumLength: 20000, Multiline: true),
        new(SamlUnknownUser, AppSettingGroups.Saml, AppSettingValueType.String, "Reject", "本アプリに居ない利用者の扱い", AllowedValues: ["Reject", "Register"]),
        new(SamlRegisterRole, AppSettingGroups.Saml, AppSettingValueType.String, AdminRoles.Editor, "自動で作る利用者の役割", AllowedValues: AdminRoles.All),
        new(SamlLoginIdSource, AppSettingGroups.Saml, AppSettingValueType.String, "NameId", "ログイン ID の取り方", AllowedValues: ["NameId", "Claim"]),
        new(SamlLoginIdClaim, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "ログイン ID にする属性の名前", MaximumLength: 500),
        new(SamlButtonLabel, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "ログイン画面の釦の文字", MaximumLength: 100),
        new(SamlSingleLogoutUrl, AppSettingGroups.Saml, AppSettingValueType.String, string.Empty, "IdP の単一ログアウト URL", MaximumLength: 2000, StringNormalizer: v => Url(v, requireHttps: false)),
        new(PleasanterSsoEnabled, AppSettingGroups.PleasanterSso, AppSettingValueType.Boolean, "false", "Pleasanter のログインを有効にする"),
        new(PleasanterSsoInternalBaseUrl, AppSettingGroups.PleasanterSso, AppSettingValueType.String, string.Empty, "本アプリから Pleasanter へ問い合わせる URL（サーバ間）", MaximumLength: 2000),
        new(PleasanterSsoLoginUrl, AppSettingGroups.PleasanterSso, AppSettingValueType.String, string.Empty, "Pleasanter のログイン画面の URL", MaximumLength: 2000),
        new(PleasanterSsoLogoutUrl, AppSettingGroups.PleasanterSso, AppSettingValueType.String, string.Empty, "Pleasanter のログアウト画面の URL", MaximumLength: 2000),
        new(PleasanterSsoCookieNames, AppSettingGroups.PleasanterSso, AppSettingValueType.String, ".AspNetCore.Cookies,Pleasanter_SessionGuid", "Pleasanter へ転送する cookie の名前（前方一致）", MaximumLength: 500),
        new(PleasanterSsoAllowedDeptIds, AppSettingGroups.PleasanterSso, AppSettingValueType.String, string.Empty, "許可する組織の ID", MaximumLength: 1024),
        new(PleasanterSsoAllowedGroupIds, AppSettingGroups.PleasanterSso, AppSettingValueType.String, string.Empty, "許可するグループの ID", MaximumLength: 1024),
        new(PleasanterSsoUnknownUser, AppSettingGroups.PleasanterSso, AppSettingValueType.String, "Reject", "本アプリに居ない利用者の扱い", AllowedValues: ["Reject", "Register"]),
        new(PleasanterSsoRegisterRole, AppSettingGroups.PleasanterSso, AppSettingValueType.String, AdminRoles.Editor, "自動で作る利用者の役割", AllowedValues: AdminRoles.All),
        new(PleasanterSsoRevalidateMinutes, AppSettingGroups.PleasanterSso, AppSettingValueType.Integer, "5", "Pleasanter へ確かめ直す間隔（分）", Minimum: 1, Maximum: 60),
        new(PleasanterSsoTimeoutSeconds, AppSettingGroups.PleasanterSso, AppSettingValueType.Integer, "5", "Pleasanter への問い合わせの待ち時間（秒）", Minimum: 1, Maximum: 30),
        new(PleasanterSsoButtonLabel, AppSettingGroups.PleasanterSso, AppSettingValueType.String, string.Empty, "ログイン画面の釦の文字", MaximumLength: 100),
    ];

    /// <summary>キーで引く。</summary>
    public static readonly FrozenDictionary<string, AppSettingDefinition> ByKey =
        All.ToFrozenDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>
    /// 値の組み合わせを検証する。<b>メールを有効にするなら、差出人・公開 URL・送信経路に応じた設定が要る</b>
    /// （足りないまま有効にすると、招待が黙って送られない）。
    /// </summary>
    public static void ValidateCombination(IReadOnlyDictionary<string, string> values)
    {
        ValidateSaml(values);
        ValidatePleasanterSso(values);
        if (!bool.Parse(values[MailEnabled]))
        {
            return;
        }

        if (values[MailFromAddress].Length == 0)
        {
            throw new AppSettingValidationException("メール送信を有効にするには差出人メールアドレスが必要です。");
        }

        if (values[PublicBaseUrl].Length == 0)
        {
            throw new AppSettingValidationException("メール送信を有効にするには公開 URL が必要です（招待のリンクを作るため）。");
        }

        switch (Enum.Parse<MailTransportKind>(values[MailTransport]))
        {
            case MailTransportKind.Smtp when values[MailSmtpHost].Length == 0:
                throw new AppSettingValidationException("SMTP を使うには SMTP サーバーが必要です。");
            case MailTransportKind.AmazonSes when values[MailSesRegion].Length == 0:
                throw new AppSettingValidationException("Amazon SES を使うにはリージョンが必要です。");
            case MailTransportKind.AzureCommunicationServices when values[MailAcsEndpoint].Length == 0:
                throw new AppSettingValidationException("Azure Communication Services を使うにはエンドポイントが必要です。");
        }
    }

    /// <summary>SAML を有効にするなら、必要な設定がそろっていること（足りないまま有効にすると、ログインできなくなる）。</summary>
    private static void ValidateSaml(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            _ = Services.SamlOptions.FromValues(key => values.TryGetValue(key, out var v) ? v : null);
        }
        catch (InvalidOperationException ex)
        {
            throw new AppSettingValidationException(ex.Message);
        }
    }

    /// <summary>Pleasanter のログインを有効にするなら、必要な設定がそろっていること。</summary>
    private static void ValidatePleasanterSso(IReadOnlyDictionary<string, string> values)
    {
        try
        {
            _ = Services.PleasanterSsoOptions.FromValues(key => values.TryGetValue(key, out var v) ? v : null);
        }
        catch (InvalidOperationException ex)
        {
            throw new AppSettingValidationException(ex.Message);
        }
    }

    private static string Url(string value, bool requireHttps)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (requireHttps ? uri.Scheme == Uri.UriSchemeHttps : uri.Scheme is "http" or "https")
            ? value.TrimEnd('/')
            : throw new AppSettingValidationException(
                requireHttps ? "HTTPS の絶対 URL を入力してください。" : "http:// か https:// から始まる絶対 URL を入力してください。");
    }

    private static string TimeZone(string value, bool required)
    {
        if (value.Length == 0)
        {
            return required
                ? throw new AppSettingValidationException("タイムゾーンを指定してください（例: Asia/Tokyo）。")
                : string.Empty;
        }

        return System.TimeZoneInfo.TryFindSystemTimeZoneById(value, out _)
            ? value
            : throw new AppSettingValidationException($"このサーバが解決できないタイムゾーン ID です: {value}");
    }

    private static string ApiVersion(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v.ToString(CultureInfo.InvariantCulture)
            : throw new AppSettingValidationException("Pleasanter の API バージョンは正の数（例: 1.1）で指定してください。");

    private static string MailAddressOrEmpty(string value) =>
        value.Length == 0
            ? string.Empty
            : MailAddress.TryCreate(value, out var address) && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
                ? address.Address
                : throw new AppSettingValidationException("メールアドレスの形式が正しくありません。");
}
