using System.Net;
using System.Globalization;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>設定の名前と、読み取り。**設定名はここ 1 か所に置く。**</summary>
/// <remarks>
/// 名前は環境変数と同じ（<c>CALENDARFEED_*</c>）。<c>App_Data/Parameters/*.json</c> のキーは
/// <see cref="ParameterFiles"/> が写す。
/// </remarks>
public static class AppSettings
{
    public const string TimeZoneDefault = "CALENDARFEED_TIMEZONE_DEFAULT";
    public const string PublicBaseUrl = "CALENDARFEED_PUBLIC_BASEURL";

    public const string PleasanterBaseUrl = "CALENDARFEED_PLEASANTER_BASEURL";
    public const string PleasanterApiKey = "CALENDARFEED_PLEASANTER_APIKEY";
    public const string PleasanterApiVersion = "CALENDARFEED_PLEASANTER_APIVERSION";
    public const string PleasanterTimeoutSeconds = "CALENDARFEED_PLEASANTER_TIMEOUTSECONDS";
    public const string PleasanterTimeZone = "CALENDARFEED_PLEASANTER_TIMEZONE";
    public const string PleasanterLinkBaseUrl = "CALENDARFEED_PLEASANTER_LINKBASEURL";

    public const string FeedCacheSeconds = "CALENDARFEED_FEED_CACHESECONDS";
    public const string FeedMaxEvents = "CALENDARFEED_FEED_MAXEVENTS";
    public const string FeedPageSize = "CALENDARFEED_FEED_PAGESIZE";

    public const string DbProvider = "CALENDARFEED_DB_PROVIDER";
    public const string DbConnectionString = "CALENDARFEED_DB_CONNECTIONSTRING";
    public const string DbAutoMigrate = "CALENDARFEED_DB_AUTOMIGRATE";
    public const string AdminTwoFactor = "CALENDARFEED_ADMIN_TWOFACTOR";
    public const string PasswordMinimumLength = "CALENDARFEED_PASSWORD_MINIMUMLENGTH";
    public const string PasswordAllowSameAsLoginId = "CALENDARFEED_PASSWORD_ALLOWSAMEASLOGINID";
    public const string PasswordPolicies = "PasswordPolicies";
    public const string SecretKey = "CALENDARFEED_SECRET_KEY";
    public const string AuditLogRetentionDays = "CALENDARFEED_AUDITLOG_RETENTION_DAYS";
    public const string ForwardedProxyNetworks = "CALENDARFEED_FORWARDED_PROXY_NETWORKS";

    /// <summary>Pleasanter の接続設定を読む。必須の値が無ければ、設定名を挙げて止める。</summary>
    public static PleasanterOptions ReadPleasanter(IConfiguration configuration) => new()
    {
        BaseUrl = Required(configuration, PleasanterBaseUrl),
        // 全フィード共通の既定（任意）。フィードごとの API キーが未登録のときだけ使う
        ApiKey = Text(configuration, PleasanterApiKey),

        // **未設定なら本アプリの既定タイムゾーン**（Questionnaire と同じ）
        ApiKeyUserTimeZoneId = Text(configuration, PleasanterTimeZone)
            ?? Required(configuration, TimeZoneDefault),
        LinkBaseUrl = Text(configuration, PleasanterLinkBaseUrl),

        // **読めない値は既定へ落とす**（書き間違いで起動できなくなるより、既定で動く方がよい）
        ApiVersion = decimal.TryParse(
            Text(configuration, PleasanterApiVersion), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : 1.1m,
        TimeoutSeconds = PositiveInt(configuration, PleasanterTimeoutSeconds, 30),
    };

    /// <summary>フィードの表示に関する設定を読む。</summary>
    public static FeedOptions ReadFeed(IConfiguration configuration) => new()
    {
        CacheSeconds = PositiveInt(configuration, FeedCacheSeconds, 300),
        MaxEvents = PositiveInt(configuration, FeedMaxEvents, 5000),
        PageSize = PositiveInt(configuration, FeedPageSize, 1000),
    };

    /// <summary>秘密の鍵から <see cref="ISecretProtector"/> を作る。未設定なら、使おうとしたときに止まる実装を返す。</summary>
    public static ISecretProtector ReadSecretProtector(IConfiguration configuration) =>
        Text(configuration, SecretKey) is { } key ? new SecretProtector(key) : new UnconfiguredSecretProtector();

    /// <summary>
    /// 管理者認証の設定を読む。**知らない値は起動時に落とす**（黙って既定へ落ちると、必須にしたつもりで任意のまま動く）。
    /// </summary>
    public static AdminAuthOptions ReadAdminAuth(IConfiguration configuration)
    {
        var raw = Text(configuration, AdminTwoFactor);
        var mode = raw is null
            ? TwoFactorMode.Optional
            : Enum.TryParse<TwoFactorMode>(raw.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"{AdminTwoFactor} の値 '{raw}' は対応していません。required / optional / disabled のいずれかで指定してください。");

        return new AdminAuthOptions(
            mode,
            new AdminPasswordPolicy(
                PositiveInt(configuration, PasswordMinimumLength, 12),
                string.Equals(Text(configuration, PasswordAllowSameAsLoginId), "true", StringComparison.OrdinalIgnoreCase),
                // Pleasanter 本体の Security.json と同じ書き方。**組み立てられない正規表現は、ここで起動時に落ちる**
                configuration.GetSection(PasswordPolicies).Get<List<AdminPasswordRule>>() ?? []));
    }

    /// <summary>監査ログを残す日数（既定 365）。<c>0</c> なら消さない。読めない値は既定へ落とす。</summary>
    public static int ReadAuditLogRetentionDays(IConfiguration configuration) =>
        int.TryParse(Text(configuration, AuditLogRetentionDays), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v
            : 365;
    /// <summary>
    /// 転送ヘッダ（<c>X-Forwarded-*</c>）を信頼するプロキシのネットワーク（カンマ区切りの CIDR）を読む。
    /// **未設定なら空（誰も信頼しない）。** CIDR でない値は起動時に落とす。
    /// </summary>
    public static IReadOnlyList<IPNetwork> ReadForwardedProxyNetworks(IConfiguration configuration) =>
        (Text(configuration, ForwardedProxyNetworks) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => IPNetwork.TryParse(value, out var network)
                ? network
                : throw new InvalidOperationException(
                    $"{ForwardedProxyNetworks} に CIDR でない値があります: {value}（例: 10.0.0.0/8, 192.0.2.10/32）"))
            .ToArray();

    /// <summary>
    /// メールの送信設定（<c>CALENDARFEED_MAIL_*</c>）を読む。<b>無効なら他の値を読まない</b>（使わない設定の不備で起動を止めない）。
    /// 有効なのに足りない・読めない値は起動時に落とす。
    /// </summary>
    public static MailOptions ReadMail(IConfiguration configuration) => MailOptions.FromConfiguration(configuration);

    /// <summary>起動時にマイグレーションを当てるか。既定は当てる。<c>false</c> で無効（手で当てる）。</summary>
    public static bool ReadAutoMigrate(IConfiguration configuration) =>
        !string.Equals(Text(configuration, DbAutoMigrate), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>空でない値を返す。無ければ <c>null</c>。</summary>
    public static string? Text(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : null;

    private static string Required(IConfiguration configuration, string key) =>
        Text(configuration, key)
        ?? throw new InvalidOperationException(
            $"設定 {key} がありません。App_Data/Parameters の *.local.json か環境変数で与えてください。");

    private static int PositiveInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(Text(configuration, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : fallback;
}
