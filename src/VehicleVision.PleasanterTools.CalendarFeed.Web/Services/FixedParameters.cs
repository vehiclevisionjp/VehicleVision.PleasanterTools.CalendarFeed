using System.Globalization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>共有のレート制限の枠 1 つ（回数と窓）。</summary>
/// <param name="Limit">窓の中で通す回数。</param>
/// <param name="Window">窓の長さ。</param>
public sealed record RateLimitPolicy(int Limit, TimeSpan Window);

/// <summary>
/// <b>管理画面から編集しない、固定のパラメータ</b>（運用で調整しうる「上限・時間」。Issue #92）。
/// <c>App_Data/Parameters/*.json</c> ・環境変数・<c>*.local.json</c> から読む。
/// </summary>
/// <remarks>
/// <para>
/// <b>Pleasanter の Parameters の持ち方に倣った。</b>
/// <c>RateLimit.json</c>（<c>Policies</c> の階層）・<c>Session.json</c>（<c>RetentionPeriod</c>）・<c>Security.json</c>（<c>LockoutCount</c> など）・
/// <c>Rds.json</c>、本アプリ固有の <c>CalDav.json</c>。<b>既定値は今のコードの値と同じ</b>（ファイルが無くても同じに動く）。
/// </para>
/// <para>
/// <b>読めない値・範囲の外の値は、起動時に落とす</b>（静かに既定へ戻さない。上限が黙って緩むのを防ぐ）。
/// <b>暗号の長さ（鍵・ソルト・PBKDF2 の反復回数など）はここへ出さない</b>（調整させない）。
/// </para>
/// <para>
/// 階層のあるファイル（<c>RateLimit.json</c>・<c>CalDav.json</c>）は、ファイル名を節の名前にして読む
/// （<c>RateLimit:Policies:CalDav:Limit</c>。環境変数なら <c>RateLimit__Policies__CalDav__Limit</c>）。
/// 平らなファイルは、これまでどおり正式な名前（<c>CALENDARFEED_*</c>）へ写して読む。
/// </para>
/// </remarks>
public sealed class FixedParameters
{
    /// <summary>枠の名前。</summary>
    public const string AdminLogin = "AdminLogin";

    public const string PleasanterSso = "PleasanterSso";
    public const string Feed = "Feed";
    public const string CalDav = "CalDav";

    // ---- 正式な設定名（平らなファイル。ファイルのキー → これ。ParameterFiles が写す） ----
    public const string SessionRetentionPeriod = "CALENDARFEED_SESSION_RETENTIONPERIOD";
    public const string LockoutCountKey = "CALENDARFEED_LOCKOUT_COUNT";
    public const string LockoutPeriodKey = "CALENDARFEED_LOCKOUT_PERIOD";
    public const string PendingAuthenticationPeriodKey = "CALENDARFEED_PENDING_AUTHENTICATION_PERIOD";
    public const string InvitationPeriodKey = "CALENDARFEED_INVITATION_PERIOD";
    public const string StaleAfterDaysKey = "CALENDARFEED_STALE_AFTER_DAYS";
    public const string MigrationLockTimeoutKey = "CALENDARFEED_DB_MIGRATION_LOCK_TIMEOUT_SECONDS";

    private static readonly Dictionary<string, RateLimitPolicy> DefaultPolicies = new(StringComparer.Ordinal)
    {
        // ログインの試行。全体の枠に紛れさせると総当たりが通るので別枠（IP ごと）
        [AdminLogin] = new(10, TimeSpan.FromMinutes(5)),

        // Pleasanter のログインの確認
        [PleasanterSso] = new(300, TimeSpan.FromMinutes(5)),

        // 購読 URL（台ごとの数え）
        [Feed] = new(60, TimeSpan.FromMinutes(1)),

        // CalDAV（カレンダーアプリの同期。購読より要求が多い）
        [CalDav] = new(600, TimeSpan.FromMinutes(5)),
    };

    private FixedParameters(Dictionary<string, RateLimitPolicy> policies)
    {
        Policies = policies;
    }

    /// <summary>枠の一覧（名前 → 回数と窓）。</summary>
    public IReadOnlyDictionary<string, RateLimitPolicy> Policies { get; }

    /// <summary>ログイン済みセッションの有効期間（<c>Session.json</c> の <c>RetentionPeriod</c>。分）。</summary>
    public TimeSpan SessionRetention { get; private init; } = TimeSpan.FromHours(8);

    /// <summary>連続して何回失敗したら締め出すか（<c>Security.json</c> の <c>LockoutCount</c>）。</summary>
    public int LockoutCount { get; private init; } = 5;

    /// <summary>締め出しの長さ（<c>LockoutPeriod</c>。分）。恒久的には締め出さない。</summary>
    public TimeSpan LockoutPeriod { get; private init; } = TimeSpan.FromMinutes(15);

    /// <summary>パスワードは通ったが 2 要素がまだ、の段階の有効期間（<c>PendingAuthenticationPeriod</c>。秒）。</summary>
    public TimeSpan PendingAuthenticationPeriod { get; private init; } = TimeSpan.FromMinutes(5);

    /// <summary>招待の有効期間（<c>InvitationPeriod</c>。時間）。</summary>
    public TimeSpan InvitationPeriod { get; private init; } = TimeSpan.FromHours(48);

    /// <summary>棚卸しで「長期間使われていない」とみなす日数（<c>StaleAfterDays</c>）。</summary>
    public TimeSpan StaleAfter { get; private init; } = TimeSpan.FromDays(90);

    /// <summary>マイグレーションの排他を待つ上限（<c>Rds.json</c> の <c>MigrationLockTimeoutSeconds</c>）。</summary>
    public TimeSpan MigrationLockTimeout { get; private init; } = TimeSpan.FromMinutes(5);

    /// <summary>CalDAV の XML の要求（PROPFIND・REPORT）の本文の上限（バイト。<c>CalDav.json</c> の <c>MaxXmlBodyKB</c>）。</summary>
    public int CalDavMaxXmlBodyBytes { get; private init; } = 64 * 1024;

    /// <summary>CalDAV の 1 件の資源（書き込まれる iCalendar）の上限（バイト。<c>MaxResourceKB</c>）。</summary>
    public int CalDavMaxResourceBytes { get; private init; } = 256 * 1024;

    /// <summary>同じ予定への書き込みの排他の貸し出しの有効期間（<c>WriteLockLeaseSeconds</c>）。落ちても、これが過ぎれば取り直せる。</summary>
    public TimeSpan CalDavWriteLockLease { get; private init; } = TimeSpan.FromSeconds(30);

    /// <summary>排他を待つ上限（<c>WriteLockWaitSeconds</c>）。待ちきれなければ 503。</summary>
    public TimeSpan CalDavWriteLockWait { get; private init; } = TimeSpan.FromSeconds(5);

    /// <summary>枠の名前から引く。無ければ既定。</summary>
    public RateLimitPolicy Policy(string name) =>
        Policies.TryGetValue(name, out var policy) ? policy : DefaultPolicies[name];

    /// <summary>
    /// 設定から読む。<b>読めない値・範囲の外の値は <see cref="InvalidOperationException"/>（キーの名前つき）</b>。
    /// 未設定は既定（今のコードの値）。
    /// </summary>
    public static FixedParameters FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var policies = new Dictionary<string, RateLimitPolicy>(DefaultPolicies, StringComparer.Ordinal);
        foreach (var name in DefaultPolicies.Keys)
        {
            var section = $"RateLimit:Policies:{name}";
            var fallback = DefaultPolicies[name];
            policies[name] = new RateLimitPolicy(
                Number(configuration, $"{section}:Limit", fallback.Limit, 1, 1_000_000),
                TimeSpan.FromSeconds(Number(configuration, $"{section}:WindowSeconds", (int)fallback.Window.TotalSeconds, 1, 86_400)));
        }

        var leaseSeconds = Number(configuration, "CalDav:WriteLockLeaseSeconds", 30, 1, 3_600);
        var waitSeconds = Number(configuration, "CalDav:WriteLockWaitSeconds", 5, 0, 600);
        if (waitSeconds > leaseSeconds)
        {
            // 貸し出しの期間より長く待つと、持ち主が落ちたときに期限切れを待つだけになる（意味のない設定）
            throw new InvalidOperationException(
                $"CalDav:WriteLockWaitSeconds（{waitSeconds}）は CalDav:WriteLockLeaseSeconds（{leaseSeconds}）以下にしてください。");
        }

        return new FixedParameters(policies)
        {
            SessionRetention = TimeSpan.FromMinutes(Number(configuration, SessionRetentionPeriod, 8 * 60, 1, 60 * 24 * 30)),
            LockoutCount = Number(configuration, LockoutCountKey, 5, 1, 100),
            LockoutPeriod = TimeSpan.FromMinutes(Number(configuration, LockoutPeriodKey, 15, 1, 60 * 24)),
            PendingAuthenticationPeriod = TimeSpan.FromSeconds(Number(configuration, PendingAuthenticationPeriodKey, 300, 30, 3_600)),
            InvitationPeriod = TimeSpan.FromHours(Number(configuration, InvitationPeriodKey, 48, 1, 24 * 30)),
            StaleAfter = TimeSpan.FromDays(Number(configuration, StaleAfterDaysKey, 90, 1, 3_650)),
            MigrationLockTimeout = TimeSpan.FromSeconds(Number(configuration, MigrationLockTimeoutKey, 300, 1, 86_400)),
            CalDavMaxXmlBodyBytes = Number(configuration, "CalDav:MaxXmlBodyKB", 64, 1, 10_240) * 1024,
            CalDavMaxResourceBytes = Number(configuration, "CalDav:MaxResourceKB", 256, 1, 10_240) * 1024,
            CalDavWriteLockLease = TimeSpan.FromSeconds(leaseSeconds),
            CalDavWriteLockWait = TimeSpan.FromSeconds(waitSeconds),
        };
    }

    /// <summary>既定のまま（設定を読まない）。テスト・設定の無い起動用。</summary>
    public static FixedParameters Defaults { get; } = FromConfiguration(new ConfigurationBuilder().Build());

    /// <summary>整数を読む。未設定・空は <paramref name="fallback"/>。読めない・範囲の外は、キーの名前つきで落とす。</summary>
    private static int Number(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new InvalidOperationException($"{key} は {min} 以上 {max} 以下の整数で指定してください（今の値: {raw}）");
    }
}
