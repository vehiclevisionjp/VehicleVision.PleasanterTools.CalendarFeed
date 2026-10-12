using System.Globalization;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;

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

    public const string DbConnectionString = "CALENDARFEED_DB_CONNECTIONSTRING";
    public const string AdminApiKey = "CALENDARFEED_ADMIN_APIKEY";

    /// <summary>Pleasanter の接続設定を読む。必須の値が無ければ、設定名を挙げて止める。</summary>
    public static PleasanterOptions ReadPleasanter(IConfiguration configuration) => new()
    {
        BaseUrl = Required(configuration, PleasanterBaseUrl),
        ApiKey = Required(configuration, PleasanterApiKey),

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

    /// <summary>本アプリの DB の接続文字列。</summary>
    public static string ReadConnectionString(IConfiguration configuration) =>
        Text(configuration, DbConnectionString) ?? "Data Source=App_Data/calendarfeed.db";

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
