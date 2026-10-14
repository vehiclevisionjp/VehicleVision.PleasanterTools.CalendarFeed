using System.Globalization;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>設定から組み立てた、実行中に使う値の一式。</summary>
public sealed record RuntimeConfig(
    AppSettingsSnapshot Snapshot,
    PleasanterOptions Pleasanter,
    FeedOptions Feed,
    AdminAuthOptions AdminAuth,
    MailOptions Mail,
    MailSenderOptions MailSender,
    SamlOptions Saml,
    PleasanterSsoOptions PleasanterSso,
    string? PublicBaseUrl,
    int AuditLogRetentionDays);

/// <summary>
/// 実行中に使う設定（画面から編集できる設定を反映した、型つきの値）。
/// <b>呼び出し側は、使うたびに <see cref="Current"/> を読む</b>（保存や他のインスタンスの保存が、再起動なしで効くように）。
/// </summary>
/// <remarks>
/// 更新は <see cref="Apply"/>。保存したら即座に、他のインスタンスの保存は <see cref="AppSettingsMonitor"/> が 30 秒以内に反映する。
/// 組み立てに失敗した設定は反映せず、前の値を使い続ける。
/// </remarks>
public sealed class RuntimeSettings : IPleasanterOptionsSource
{
    private readonly IConfiguration _configuration;
    private RuntimeConfig _current;

    public RuntimeSettings(IConfiguration configuration)
    {
        _configuration = configuration;
        // 起動直後は、外部設定と既定値だけ（DB を読めるようになったら Apply する）。不正な外部設定は、ここで起動を止める
        _current = Build(AppSettingsProvider.InitialSnapshot(configuration), configuration);
    }

    /// <summary>いまの設定。</summary>
    public RuntimeConfig Current => Volatile.Read(ref _current);

    PleasanterOptions IPleasanterOptionsSource.Current => Current.Pleasanter;

    /// <summary>設定を反映する。組み立てに失敗したら例外（前の値のまま）。</summary>
    public void Apply(AppSettingsSnapshot snapshot) => Volatile.Write(ref _current, Build(snapshot, _configuration));

    /// <summary>設定の値から、型つきの値を組み立てる。</summary>
    public static RuntimeConfig Build(AppSettingsSnapshot snapshot, IConfiguration configuration)
    {
        string V(string key) => snapshot[key];
        int I(string key) => int.Parse(V(key), NumberStyles.Integer, CultureInfo.InvariantCulture);
        static string? Blank(string value) => value.Length == 0 ? null : value;

        var pleasanter = new PleasanterOptions
        {
            // 未設定でも起動する（画面から設定できる）。取得するときに、未設定だと分かるメッセージで失敗する
            BaseUrl = V(AppSettingDefinitions.PleasanterBaseUrl),
            ApiKey = Blank(V(AppSettingDefinitions.PleasanterApiKey)),
            // 未設定なら本アプリの既定タイムゾーン
            ApiKeyUserTimeZoneId = Blank(V(AppSettingDefinitions.PleasanterTimeZone)) ?? V(AppSettingDefinitions.TimeZoneDefault),
            LinkBaseUrl = Blank(V(AppSettingDefinitions.PleasanterLinkBaseUrl)),
            ApiVersion = decimal.Parse(V(AppSettingDefinitions.PleasanterApiVersion), NumberStyles.Number, CultureInfo.InvariantCulture),
            TimeoutSeconds = I(AppSettingDefinitions.PleasanterTimeoutSeconds),
        };

        var feed = new FeedOptions
        {
            CacheSeconds = I(AppSettingDefinitions.FeedCacheSeconds),
            MaxEvents = I(AppSettingDefinitions.FeedMaxEvents),
            PageSize = I(AppSettingDefinitions.FeedPageSize),
        };

        var adminAuth = new AdminAuthOptions(
            Enum.Parse<TwoFactorMode>(V(AppSettingDefinitions.AdminTwoFactor), ignoreCase: true),
            new AdminPasswordPolicy(
                I(AppSettingDefinitions.PasswordMinimumLength),
                bool.Parse(V(AppSettingDefinitions.PasswordAllowSameAsLoginId)),
                // 正規表現の条件は起動に係る（組み立てられない書き方は起動時に落とす）ので、ファイルから読む
                configuration.GetSection(AppSettings.PasswordPolicies).Get<List<AdminPasswordRule>>() ?? []));

        // メールの設定は、値の組から MailOptions を読む（組み合わせの不備はここで例外になる）
        var mailValues = AppSettingDefinitions.All
            .Where(d => d.Group == AppSettingGroups.Mail)
            .Where(d => V(d.Key).Length > 0)
            .ToDictionary(d => d.Key, d => (string?)V(d.Key), StringComparer.Ordinal);
        var mail = MailOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(mailValues).Build());

        var mailSender = new MailSenderOptions
        {
            MaxSendsPerMinute = I(AppSettingDefinitions.MailSendPerMinute),
            // 送信ワーカーを動かすかは、インスタンスごとの設定（画面からは変えない）
            WorkerEnabled = MailSenderOptions.FromConfiguration(configuration).WorkerEnabled,
        };

        // SAML（既定は無効）。有効なのに足りない・読めない外部設定は、ここで起動を止める
        var saml = SamlOptions.FromValues(key => snapshot.Values.TryGetValue(key, out var v) ? v : null);

        var pleasanterSso = PleasanterSsoOptions.FromValues(key => snapshot.Values.TryGetValue(key, out var v) ? v : null);

        return new RuntimeConfig(
            snapshot,
            pleasanter,
            feed,
            adminAuth,
            mail,
            mailSender,
            saml,
            pleasanterSso,
            Blank(V(AppSettingDefinitions.PublicBaseUrl)),
            I(AppSettingDefinitions.AuditLogRetentionDays));
    }
}

/// <summary>保存された設定を定期的に読み直す（他のインスタンスの保存を、30 秒以内に反映する）。</summary>
public sealed class AppSettingsMonitor(
    IAppSettingsProvider provider,
    RuntimeSettings runtime,
    ILogger<AppSettingsMonitor> logger) : BackgroundService
{
    /// <summary>読み直して反映する。失敗したら前の設定のまま。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            runtime.Apply(await provider.ReloadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "アプリケーション設定を読み直せませんでした。前の設定のまま、次の周期で再試行します");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AppSettingsProvider.CacheLifetime);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
