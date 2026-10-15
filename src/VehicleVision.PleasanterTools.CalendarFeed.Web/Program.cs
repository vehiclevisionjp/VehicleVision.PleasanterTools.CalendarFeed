using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Web;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

// **設定に書く鍵を作るための道。** 手で乱数を用意させると、短い値や使い回しが混ざる。
// コンソールへ出す文字列なので英語（.github/copilot-instructions.md）
if (args.Contains("--generate-secret-key"))
{
    Console.WriteLine(SecretProtector.GenerateKey());
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ---- App_Data/Parameters の設定ファイル ----------------------------------------
// **一番先に積む。** 以降の読み取りがすべてこれを見る。
// 優先順位は {名前}.local.json ＞ 環境変数 ＞ {名前}.json（App_Data/Parameters/README.md）
builder.Configuration.AddParameterFiles();

// ---- 設定は DI の生成時に読む（テストからの上書きを効かせるため） ---------------
// ---- アプリケーション設定（管理画面から編集できる設定。Questionnaire に則る） -------------------------
// 優先順位は 外部設定（環境変数・Parameters。固定）＞ DB ＞ 既定値。使う側は、使うたびに RuntimeSettings.Current を読む。
// **不正な外部設定は、ここで（最初に RuntimeSettings を作るとき）起動を止める**
builder.Services.AddSingleton<IAppSettingStore, AppSettingStore>();
builder.Services.AddSingleton<IAppSettingsProvider, AppSettingsProvider>();
builder.Services.AddSingleton<RuntimeSettings>();
builder.Services.AddSingleton<IPleasanterOptionsSource>(sp => sp.GetRequiredService<RuntimeSettings>());
builder.Services.AddSingleton<AppSettingsMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppSettingsMonitor>());
// エンドポイントの引数で受ける設定は、要求ごとに最新の値を渡す
builder.Services.AddTransient(sp => sp.GetRequiredService<RuntimeSettings>().Current.Pleasanter);
builder.Services.AddTransient(sp => sp.GetRequiredService<RuntimeSettings>().Current.Feed);
builder.Services.AddTransient(sp => sp.GetRequiredService<RuntimeSettings>().Current.AdminAuth);
builder.Services.AddTransient(sp => sp.GetRequiredService<RuntimeSettings>().Current.Mail);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<PleasanterClient>();
builder.Services.AddSingleton<FeedFailureNotifier>();
builder.Services.AddSingleton<FeedService>();
builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var provider = DbConnectionFactory.ParseProvider(AppSettings.Text(configuration, AppSettings.DbProvider));
    return new DbConnectionFactory(
        provider,
        DbConnectionFactory.ResolveConnectionString(
            provider,
            AppSettings.Text(configuration, AppSettings.DbConnectionString),
            sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
});
builder.Services.AddSingleton(sp => AppSettings.ReadSecretProtector(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IFeedStore, FeedStore>();
builder.Services.AddSingleton<IAdminUserStore, AdminUserStore>();
builder.Services.AddSingleton<IAdminSessionStore, AdminSessionStore>();
builder.Services.AddSingleton<IAuditLogStore, AuditLogStore>();
builder.Services.AddSingleton<IAdminInvitationStore, AdminInvitationStore>();

// ---- メール（招待）。送信待ちへ暗号化して積み、アプリの中のワーカーが送る ----------------------
// **無効（既定）なら何も送らない。** 招待は画面の URL を手で渡す。設定は管理画面から編集できる
builder.Services.AddSingleton<IMailOutbox, MailOutbox>();
builder.Services.AddSingleton<IRateLimitStore, RateLimitStore>();
builder.Services.AddSingleton<SharedRateLimiter>();
builder.Services.AddSingleton<RateLimitPurgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RateLimitPurgeService>());
builder.Services.AddSingleton<IMailPayloadProtector, MailPayloadProtector>();
builder.Services.AddSingleton<IMailTransport, ConfiguredMailTransport>();
builder.Services.AddSingleton<MailSender>();
builder.Services.AddSingleton<AdminInvitationMailer>();
builder.Services.AddHostedService<MailSenderHostedService>();
builder.Services.AddSingleton<AuditLogService>();
builder.Services.AddSingleton<AuditLogRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditLogRetentionService>());
builder.Services.AddSingleton<AdminSessionFilter>();
builder.Services.AddSingleton<SamlAuthenticator>();
builder.Services.AddSingleton<PleasanterSsoAuthenticator>();
builder.Services.AddSingleton<IPleasanterSessionVerifier, PleasanterSessionVerifier>();
builder.Services.AddSingleton<PleasanterSsoSessionRevalidator>();
builder.Services
    // 時間切れは要求ごとに設定の値で掛ける（PleasanterSessionVerifier）。ここでは無限にしておく
    .AddHttpClient(PleasanterSessionVerifier.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // ⚠️ cookie を覚えさせない。覚えると、ある管理者の cookie が別の管理者の問い合わせへ混ざる（cookie は要求ごとに Cookie ヘッダで付ける）
        UseCookies = false,

        // ⚠️ 転送を追わない。ログイン画面への転送などを成功と取り違えない
        AllowAutoRedirect = false,
    });

// **購読 URL の総当たりを遅くする。** トークンは 256 bit なので当たらないが、無駄な負荷も止める
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // **ログインの試行だけ別枠**（IP ごと 5 分に 10 回）。全体の枠に紛れさせると総当たりが通る
    // **ログインの試行・Pleasanter のログインの確認の枠は、複数台で数えを共有する**（SharedRateLimits。DB の数え。Issue #49）。
    // ここに残るのは、要求が多く DB への書き込みが重い購読 URL の枠（台ごとの数え）
    o.AddPolicy("feed", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// 設定の読み間違い（外部設定）は、最初の要求ではなく起動時に分かるようにする
var runtimeSettings = app.Services.GetRequiredService<RuntimeSettings>();

// ⚠️ **何を読んだかを残す。** 設定ファイルは optional なので、置き場を間違えても黙って既定で動いてしまう
app.Logger.LogInformation(
    "設定ファイルの置き場: {Directory} / 見つかったファイル: {Files}",
    app.Configuration[ParameterFiles.DirectoryKey],
    app.Configuration[ParameterFiles.FoundFilesKey]);
if (string.IsNullOrEmpty(app.Configuration[ParameterFiles.FoundFilesKey]))
{
    app.Logger.LogWarning("設定ファイルが 1 つも見つかりません。置き場を確かめてください。");
}

// ---- DB のマイグレーション ---------------------------------------------------------
// 起動時に自動で当てる（CALENDARFEED_DB_AUTOMIGRATE=false で無効。その場合は手で当てる）
var db = app.Services.GetRequiredService<IDbConnectionFactory>();
app.Logger.LogInformation("DB: {Provider}", db.Provider);
if (AppSettings.ReadAutoMigrate(app.Configuration))
{
    DatabaseMigrator.Migrate(db.Provider, db.ConnectionString);
}
else
{
    app.Logger.LogWarning("起動時のマイグレーションは無効です。DB に表が無いと動きません。");
}

// ---- 転送ヘッダ（リバースプロキシの背後でクライアントの IP・スキーム・Host を見る） ------------
// ⚠️ **信頼するネットワークを設定で明示したときだけ有効にする。** 既定では誰も信頼しない
// （X-Forwarded-For を鵜呑みにすると、IP ごとのレート制限を回避できる）。**レート制限より前に置く**
var forwardedNetworks = AppSettings.ReadForwardedProxyNetworks(app.Configuration);
if (forwardedNetworks.Count > 0)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    };

    // 既定で信頼されるループバックも外し、設定のネットワークだけにする
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var network in forwardedNetworks)
    {
        forwarded.KnownIPNetworks.Add(network);
    }

    app.UseForwardedHeaders(forwarded);
}
else
{
    app.Logger.LogInformation(
        "転送ヘッダは無視します（{Setting} が未設定）。プロキシの背後ではレート制限が全員で共有になります。",
        AppSettings.ForwardedProxyNetworks);
}

// ---- 保存済みのアプリケーション設定を反映する（DB を使えるようになったので） ---------------------------
try
{
    runtimeSettings.Apply(await app.Services.GetRequiredService<IAppSettingsProvider>().ReloadAsync());
}
catch (Exception ex)
{
    // 表が無い（自動マイグレーションを止めている）・秘密の鍵の不整合など。起動は止めず、外部設定と既定値で動く
    app.Logger.LogWarning(ex, "保存済みのアプリケーション設定を読めませんでした。外部設定と既定値で動きます。");
}

app.UseAdminSecurityHeaders();
app.UseRateLimiter();

// ---- 購読フィード ----------------------------------------------------------------
app.MapGet("/feeds/{token}.ics", async (
    string token,
    HttpContext http,
    IFeedStore feeds,
    FeedService service,
    RuntimeSettings settings,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    // **存在しない・失効済み・形が違うものは、どれも同じ 404 にする**（有効なトークンを推測させない）
    var feed = await feeds.FindByTokenAsync(token, cancellationToken);
    if (feed is null)
    {
        return Results.NotFound();
    }

    string body;
    try
    {
        body = await service.GetCalendarAsync(feed, cancellationToken);
    }
    catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
    {
        // 秘密の鍵が未設定、または保管時と違う鍵（失った・変えた）。**API キーを登録し直す必要がある**
        logger.LogError("フィード {FeedId} の API キーを復号できません: {Message}", feed.Id, ex.Message);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
    catch (PleasanterException ex)
    {
        // **トークンをログへ出さない。** フィード ID だけで追える
        logger.LogError("フィード {FeedId} の取得に失敗しました（{Kind}）: {Message}", feed.Id, ex.Kind, ex.Message);
        if (ex.Kind is PleasanterErrorKind.Transient)
        {
            http.Response.Headers.RetryAfter = "60";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }

    var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..32]}\"";
    http.Response.Headers.CacheControl = $"private, max-age={settings.Current.Feed.CacheSeconds}";
    http.Response.Headers.ETag = etag;
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";

    if (http.Request.Headers.IfNoneMatch == etag)
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Text(body, "text/calendar; charset=utf-8");
}).RequireRateLimiting("feed");

// ---- 管理者の認証・管理 API ----------------------------------------------------------
// 認証は Questionnaire の方式（ログイン ID ＋ パスワード ＋ TOTP・サーバ側セッション）。
// フィードの管理 API は、ログイン済みの管理者（Session）だけが使える
app.MapAdminAuth();
app.MapAdminFeeds();
app.MapAdminAudit();
app.MapAdminUsers();
app.MapAdminSettings();
app.MapAdminSaml();
app.MapAdminPleasanterSso();
app.MapAdminUi();

app.Run();

/// <summary>テストが <c>WebApplicationFactory</c> で起動するための宣言。</summary>
public partial class Program;
