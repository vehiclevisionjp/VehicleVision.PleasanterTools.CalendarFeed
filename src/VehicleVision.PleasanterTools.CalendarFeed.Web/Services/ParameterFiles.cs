using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>
/// <c>App_Data/Parameters/*.json</c> を設定として積む。
/// </summary>
/// <remarks>
/// <para>
/// **Pleasanter 本体・Questionnaire と同じ置き場・同じ書き方**にしてある
/// （<c>App_Data/Parameters/README.md</c>）。**優先順位は
/// <c>{名前}.local.json</c> ＞ 環境変数 ＞ <c>{名前}.json</c>。**
/// </para>
/// <para>
/// **1 か所にまとめてある。** 設定ごとに「JSON → 環境変数」を繰り返すと、
/// 後から積んだ環境変数が**前の <c>.local.json</c> を追い越す**（Questionnaire で踏んだ）。
/// </para>
/// <para>
/// ⚠️ **ファイルのキーと環境変数の名前が違うものがある。** ファイルは Pleasanter 本体に合わせた短い名前
/// （<c>BaseUrl</c> など）、環境変数は衝突を避けて <c>CALENDARFEED_*</c>。そのままでは片方しか効かないので、
/// <see cref="Aliases"/> で正式な名前（環境変数と同じ）へ写している。
/// </para>
/// <para>
/// **値が <c>null</c> や空のキーは写さない。** <c>ApiKey</c> は既定で <c>null</c>（＝ここへ書かせない）なので、
/// 写すと環境変数で与えた API キーを空で塗り潰してしまう。
/// </para>
/// </remarks>
public static class ParameterFiles
{
    /// <summary>既定の置き場（content root からの相対）。</summary>
    public const string DefaultDirectory = "App_Data/Parameters";

    /// <summary>実際に読んだ置き場。**起動時の記録に出す。**</summary>
    public const string DirectoryKey = "CALENDARFEED_PARAMETERS_DIRECTORY";

    /// <summary>実際に見つかったファイル。**空なら 1 つも読めていない。**</summary>
    /// <remarks>
    /// ⚠️ **「読めているつもりで読めていない」を起動時に見せるため。**
    /// 設定ファイルは <c>optional</c> なので、置き場を間違えても黙って既定で動いてしまう。
    /// </remarks>
    public const string FoundFilesKey = "CALENDARFEED_PARAMETERS_FOUND";

    /// <summary>読む対象。</summary>
    private static readonly ImmutableArray<string> Names =
        ["Service", "Pleasanter", "Feed", "Rds", "Security", "Mail", "Session", "Authentication", "RateLimit", "CalDav"];

    /// <summary>
    /// <b>階層のあるファイル</b>。ファイル名を節の名前にして読む（<c>RateLimit.json</c> の <c>Policies:CalDav:Limit</c> は
    /// <c>RateLimit:Policies:CalDav:Limit</c>。環境変数なら <c>RateLimit__Policies__CalDav__Limit</c>）。
    /// <b>全部のファイルが 1 つの設定の根へ混ざる</b>ので、他のファイルと同じ名前が出ても取り違えないための名前空間。
    /// </summary>
    private static readonly ImmutableArray<string> SectionedNames = ["RateLimit", "CalDav"];

    /// <summary>平らに（根へそのまま）読むファイル。</summary>
    private static readonly ImmutableArray<string> FlatNames = [.. Names.Where(n => !SectionedNames.Contains(n))];

    /// <summary>ファイルのキー → 正式な設定名（環境変数と同じ名前）。</summary>
    /// <remarks>**ここに無いキーはそのままの名前で積まれる**（読まれない）。</remarks>
    private static readonly ImmutableDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Service.json
            ["TimeZoneDefault"] = AppSettings.TimeZoneDefault,
            ["PublicBaseUrl"] = AppSettings.PublicBaseUrl,

            // Pleasanter.json
            ["BaseUrl"] = AppSettings.PleasanterBaseUrl,
            ["ApiKey"] = AppSettings.PleasanterApiKey,
            ["ApiVersion"] = AppSettings.PleasanterApiVersion,
            ["TimeoutSeconds"] = AppSettings.PleasanterTimeoutSeconds,
            ["ApiKeyUserTimeZoneId"] = AppSettings.PleasanterTimeZone,
            ["LinkBaseUrl"] = AppSettings.PleasanterLinkBaseUrl,

            // Feed.json
            ["CacheSeconds"] = AppSettings.FeedCacheSeconds,
            ["MaxEvents"] = AppSettings.FeedMaxEvents,
            ["PageSize"] = AppSettings.FeedPageSize,
            ["FailureNotify"] = AppSettingDefinitions.FeedFailureNotify,
            ["FailureNotifyThreshold"] = AppSettingDefinitions.FeedFailureNotifyThreshold,
            ["FailureNotifyRepeatHours"] = AppSettingDefinitions.FeedFailureNotifyRepeatHours,

            // Rds.json
            ["Provider"] = AppSettings.DbProvider,
            ["ConnectionString"] = AppSettings.DbConnectionString,
            ["AutoMigrate"] = AppSettings.DbAutoMigrate,

            // Security.json
            ["AdminTwoFactor"] = AppSettings.AdminTwoFactor,
            ["PasswordMinimumLength"] = AppSettings.PasswordMinimumLength,
            ["PasswordAllowSameAsLoginId"] = AppSettings.PasswordAllowSameAsLoginId,
            ["SecretKey"] = AppSettings.SecretKey,
            ["LockoutCount"] = FixedParameters.LockoutCountKey,
            ["LockoutPeriod"] = FixedParameters.LockoutPeriodKey,
            ["PendingAuthenticationPeriod"] = FixedParameters.PendingAuthenticationPeriodKey,
            ["InvitationPeriod"] = FixedParameters.InvitationPeriodKey,
            ["StaleAfterDays"] = FixedParameters.StaleAfterDaysKey,

            // Session.json（Pleasanter の Session.json と同じ名前）
            ["RetentionPeriod"] = FixedParameters.SessionRetentionPeriod,

            // Rds.json の続き
            ["MigrationLockTimeoutSeconds"] = FixedParameters.MigrationLockTimeoutKey,

            // Mail.json（Questionnaire の QUESTIONNAIRE_MAIL_* と同じ項目）
            ["Enabled"] = "CALENDARFEED_MAIL_ENABLED",
            ["Transport"] = "CALENDARFEED_MAIL_TRANSPORT",
            ["SmtpHost"] = "CALENDARFEED_MAIL_SMTP_HOST",
            ["SmtpPort"] = "CALENDARFEED_MAIL_SMTP_PORT",
            ["SmtpSecurity"] = "CALENDARFEED_MAIL_SMTP_SECURITY",
            ["SmtpUser"] = "CALENDARFEED_MAIL_SMTP_USER",
            ["SmtpPassword"] = "CALENDARFEED_MAIL_SMTP_PASSWORD",
            ["FromAddress"] = "CALENDARFEED_MAIL_FROM_ADDRESS",
            ["FromName"] = "CALENDARFEED_MAIL_FROM_NAME",
            ["ReplyToAddress"] = "CALENDARFEED_MAIL_REPLYTO_ADDRESS",
            ["SesRegion"] = "CALENDARFEED_MAIL_SES_REGION",
            ["AcsEndpoint"] = "CALENDARFEED_MAIL_ACS_ENDPOINT",
            ["SendTimeoutSeconds"] = "CALENDARFEED_MAIL_TIMEOUT_SECONDS",
            ["SendPerMinute"] = MailSenderOptions.MaxSendsPerMinuteKey,
            ["Worker"] = MailSenderOptions.WorkerKey,
            ["AuditLogRetentionDays"] = AppSettings.AuditLogRetentionDays,
            ["ForwardedProxyNetworks"] = AppSettings.ForwardedProxyNetworks,
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <b>階層のあるキー</b>（<c>Authentication.json</c>）→ 正式な設定名（環境変数と同じ）。Pleasanter の <c>Authentication.json</c> の
    /// <c>SamlParameters</c>（<c>SPOptions</c>・<c>IdentityProviders</c>）に倣った形。<b>配列の値はカンマでつなぐ</b>
    /// （環境変数と同じ書き方で読ませるため）。
    /// </summary>
    private static readonly ImmutableArray<KeyValuePair<string, string>> NestedAliases =
    [
        // SAML 2.0 のログイン（SamlParameters。Pleasanter の SPOptions / IdentityProviders に合わせた階層）
        new("SamlParameters:Enabled", "CALENDARFEED_SAML_ENABLED"),
        new("SamlParameters:SPOptions:EntityId", "CALENDARFEED_SAML_ENTITYID"),
        new("SamlParameters:IdentityProviders:0:EntityId", "CALENDARFEED_SAML_IDPENTITYID"),
        new("SamlParameters:IdentityProviders:0:SignOnUrl", "CALENDARFEED_SAML_SINGLESIGNONURL"),
        new("SamlParameters:IdentityProviders:0:LogoutUrl", "CALENDARFEED_SAML_SINGLELOGOUTURL"),
        new("SamlParameters:IdentityProviders:0:SigningCertificate", "CALENDARFEED_SAML_IDPCERTIFICATE"),
        new("SamlParameters:UnknownUser", "CALENDARFEED_SAML_UNKNOWNUSER"),
        new("SamlParameters:RegisterRole", "CALENDARFEED_SAML_REGISTERROLE"),
        new("SamlParameters:LoginIdSource", "CALENDARFEED_SAML_LOGINIDSOURCE"),
        new("SamlParameters:LoginIdClaim", "CALENDARFEED_SAML_LOGINIDCLAIM"),
        new("SamlParameters:ButtonLabel", "CALENDARFEED_SAML_BUTTONLABEL"),

        // Pleasanter のログインで管理画面へ入る
        new("PleasanterSsoParameters:Enabled", "CALENDARFEED_PLEASANTERSSO_ENABLED"),
        new("PleasanterSsoParameters:InternalBaseUrl", "CALENDARFEED_PLEASANTERSSO_INTERNALBASEURL"),
        new("PleasanterSsoParameters:LoginUrl", "CALENDARFEED_PLEASANTERSSO_LOGINURL"),
        new("PleasanterSsoParameters:LogoutUrl", "CALENDARFEED_PLEASANTERSSO_LOGOUTURL"),
        new("PleasanterSsoParameters:CookieNames", "CALENDARFEED_PLEASANTERSSO_COOKIENAMES"),
        new("PleasanterSsoParameters:AllowedDeptIds", "CALENDARFEED_PLEASANTERSSO_ALLOWEDDEPTIDS"),
        new("PleasanterSsoParameters:AllowedGroupIds", "CALENDARFEED_PLEASANTERSSO_ALLOWEDGROUPIDS"),
        new("PleasanterSsoParameters:UnknownUser", "CALENDARFEED_PLEASANTERSSO_UNKNOWNUSER"),
        new("PleasanterSsoParameters:RegisterRole", "CALENDARFEED_PLEASANTERSSO_REGISTERROLE"),
        new("PleasanterSsoParameters:RevalidateMinutes", "CALENDARFEED_PLEASANTERSSO_REVALIDATEMINUTES"),
        new("PleasanterSsoParameters:TimeoutSeconds", "CALENDARFEED_PLEASANTERSSO_TIMEOUTSECONDS"),
        new("PleasanterSsoParameters:ButtonLabel", "CALENDARFEED_PLEASANTERSSO_BUTTONLABEL"),
    ];

    /// <summary>
    /// <b>ファイルで指定できる設定名の一覧</b>（平らなキーの写し先と、階層のキーの写し先）。
    /// <b>環境変数で指定できる設定は、ここに無ければならない</b>（テストが突き合わせる）。
    /// </summary>
    public static IEnumerable<string> FileBackedSettings =>
        Aliases.Values.Concat(NestedAliases.Select(a => a.Value)).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// 設定を積む。**アプリの一番先で 1 度だけ呼ぶ**（以降の読み取りが全てこれを見る）。
    /// </summary>
    public static IConfigurationBuilder AddParameterFiles(
        this IConfigurationBuilder builder,
        string directory = DefaultDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // ⚠️ **絶対パスへ直してから積む。** AddJsonFile は content root を基準にするが、
        // 別名を写すために自分で読む側は現在のディレクトリを基準にする。基準が食い違うと片方だけ読めて気付けない
        var resolved = Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(BasePathOf(builder), directory);

        // ---- 1. {名前}.json（一番弱い）
        foreach (var name in FlatNames)
        {
            builder.AddJsonFile(Path.Combine(resolved, $"{name}.json"), optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(AliasesOf(resolved, local: false));
        builder.AddInMemoryCollection(SectionsOf(resolved, local: false));

        // ---- 2. 環境変数
        builder.AddEnvironmentVariables();

        // ---- 3. {名前}.local.json（一番強い。git 管理外）
        foreach (var name in FlatNames)
        {
            builder.AddJsonFile(Path.Combine(resolved, $"{name}.local.json"), optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(AliasesOf(resolved, local: true));
        builder.AddInMemoryCollection(SectionsOf(resolved, local: true));

        // ---- 4. 何を読んだかを残す
        return builder.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(DirectoryKey, resolved),
            new KeyValuePair<string, string?>(FoundFilesKey, string.Join(" / ", FoundIn(resolved))),
        ]);
    }

    /// <summary>実際に置いてあるファイルの名前。</summary>
    public static IEnumerable<string> FoundIn(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        foreach (var name in Names)
        {
            foreach (var suffix in new[] { ".json", ".local.json" })
            {
                if (File.Exists(Path.Combine(directory, name + suffix)))
                {
                    yield return name + suffix;
                }
            }
        }
    }

    /// <summary>相対パスの基準。**content root に合わせる。**</summary>
    private static string BasePathOf(IConfigurationBuilder builder) =>
        builder.Properties.TryGetValue("FileProvider", out var provider)
            && provider is Microsoft.Extensions.FileProviders.PhysicalFileProvider physical
            ? physical.Root
            : Directory.GetCurrentDirectory();

    /// <summary>ファイルのキーを正式な名前へ写したものを作る。</summary>
    /// <remarks>**写す前に一度だけ読む。** 積んだ後の設定から読むと、環境変数の値まで巻き込んで写してしまう。</remarks>
    [SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "IConfigurationRoot は読み終えた時点で用済み")]
    private static IEnumerable<KeyValuePair<string, string?>> AliasesOf(string directory, bool local)
    {
        var suffix = local ? ".local.json" : ".json";
        var files = new ConfigurationBuilder();
        foreach (var name in FlatNames)
        {
            files.AddJsonFile(Path.Combine(directory, $"{name}{suffix}"), optional: true, reloadOnChange: false);
        }

        var configuration = files.Build();
        foreach (var (key, setting) in Aliases)
        {
            var value = configuration[key];

            // **null と空は写さない。** 環境変数で与えた値を塗り潰さないため
            if (!string.IsNullOrEmpty(value))
            {
                yield return new KeyValuePair<string, string?>(setting, value);
            }
        }

        // 階層のあるキー。配列（["a","b"]）は、環境変数と同じカンマ区切りへつなぐ
        foreach (var (path, setting) in NestedAliases)
        {
            var section = configuration.GetSection(path);
            var children = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
            var value = section.Value is { Length: > 0 } direct ? direct : children.Count > 0 ? string.Join(',', children) : null;
            if (!string.IsNullOrEmpty(value))
            {
                yield return new KeyValuePair<string, string?>(setting, value);
            }
        }
    }

    /// <summary>
    /// 階層のあるファイル（<see cref="SectionedNames"/>）を、ファイル名を節の名前にして写したもの。
    /// <b>値を持つキーだけ</b>写す（<c>null</c> は写さない。環境変数を塗り潰さないため）。
    /// </summary>
    [SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "IConfigurationRoot は読み終えた時点で用済み")]
    private static IEnumerable<KeyValuePair<string, string?>> SectionsOf(string directory, bool local)
    {
        var suffix = local ? ".local.json" : ".json";
        foreach (var name in SectionedNames)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(directory, name + suffix), optional: true, reloadOnChange: false)
                .Build();
            foreach (var (key, value) in configuration.AsEnumerable())
            {
                if (!string.IsNullOrEmpty(value))
                {
                    yield return new KeyValuePair<string, string?>($"{name}:{key}", value);
                }
            }
        }
    }
}
