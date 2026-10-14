using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
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
builder.Services.AddSingleton(sp => AppSettings.ReadPleasanter(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => AppSettings.ReadFeed(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<PleasanterClient>();
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
builder.Services.AddSingleton<AuditLogService>();
builder.Services.AddSingleton<AuditLogRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditLogRetentionService>());
builder.Services.AddSingleton<AdminSessionFilter>();
// **知らない値は起動時に落とす。** 黙って既定へ落ちると、必須にしたつもりで任意のまま動く
builder.Services.AddSingleton(sp => AppSettings.ReadAdminAuth(sp.GetRequiredService<IConfiguration>()));

// **購読 URL の総当たりを遅くする。** トークンは 256 bit なので当たらないが、無駄な負荷も止める
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // **ログインの試行だけ別枠**（IP ごと 5 分に 10 回）。全体の枠に紛れさせると総当たりが通る
    o.AddPolicy("adminLogin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
    o.AddPolicy("feed", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// 設定の読み間違いは、最初の要求ではなく起動時に分かるようにする
_ = app.Services.GetRequiredService<AdminAuthOptions>();

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

app.UseRateLimiter();

// ---- 購読フィード ----------------------------------------------------------------
app.MapGet("/feeds/{token}.ics", async (
    string token,
    HttpContext http,
    IFeedStore feeds,
    FeedService service,
    FeedOptions options,
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
    http.Response.Headers.CacheControl = $"private, max-age={options.CacheSeconds}";
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

app.Run();

/// <summary>テストが <c>WebApplicationFactory</c> で起動するための宣言。</summary>
public partial class Program;
