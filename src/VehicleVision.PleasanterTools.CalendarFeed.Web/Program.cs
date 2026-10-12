using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Web;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

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
builder.Services.AddSingleton<IFeedStore, FeedStore>();

// **購読 URL の総当たりを遅くする。** トークンは 256 bit なので当たらないが、無駄な負荷も止める
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("feed", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

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

// ---- 管理 API ----------------------------------------------------------------------
// **足場。** 管理画面（UI）と管理者ログインは未実装。当面は管理キー（Admin:ApiKey）を
// Authorization: Bearer で渡す。キーが未設定なら、この口は開かない
var adminKey = AppSettings.Text(app.Configuration, AppSettings.AdminApiKey);
if (!string.IsNullOrEmpty(adminKey))
{
    var admin = app.MapGroup("/admin/api/feeds");
    admin.AddEndpointFilter(async (context, next) =>
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var presented = header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : "";

        // 長さが漏れないよう、ハッシュ同士を定数時間で比べる
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(adminKey));
        return CryptographicOperations.FixedTimeEquals(a, b)
            ? await next(context)
            : Results.Unauthorized();
    });

    admin.MapGet("/", async (IFeedStore feeds, CancellationToken ct) =>
        Results.Ok((await feeds.ListAsync(ct)).Select(FeedView.From)));

    admin.MapPost("/", async (
        CreateFeedRequest request,
        HttpContext http,
        IFeedStore feeds,
        PleasanterOptions pleasanter,
        IConfiguration configuration,
        CancellationToken ct) =>
    {
        if (request.Validate() is { } error)
        {
            return Results.BadRequest(new { error });
        }

        var (feed, token) = await feeds.CreateAsync(
            request.Name.Trim(),
            request.SiteId,
            request.Mapping ?? new FeedMapping(),
            request.Filters ?? new Dictionary<string, string>(),
            ct);
        return Results.Created(
            $"/admin/api/feeds/{feed.Id}",
            new { feed = FeedView.From(feed), url = FeedUrl(http, configuration, token) });
    });

    admin.MapPost("/{id:guid}/rotate", async (
        Guid id,
        HttpContext http,
        IFeedStore feeds,
        IConfiguration configuration,
        CancellationToken ct) =>
        await feeds.RotateTokenAsync(id, ct) is { } token
            ? Results.Ok(new { url = FeedUrl(http, configuration, token) })
            : Results.NotFound());

    admin.MapDelete("/{id:guid}", async (Guid id, IFeedStore feeds, CancellationToken ct) =>
        await feeds.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());
}

app.Run();

static string FeedUrl(HttpContext http, IConfiguration configuration, string token)
{
    // **リバースプロキシの内側では Host が内部の値になる。公開 URL は設定で固定する**
    var baseUrl = AppSettings.Text(configuration, AppSettings.PublicBaseUrl)?.TrimEnd('/')
        ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
    return $"{baseUrl}/feeds/{token}.ics";
}

/// <summary>テストが <c>WebApplicationFactory</c> で起動するための宣言。</summary>
public partial class Program;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web
{
    /// <summary>フィード作成の要求。</summary>
    public sealed record CreateFeedRequest(
        string Name,
        long SiteId,
        FeedMapping? Mapping,
        Dictionary<string, string>? Filters)
    {
        private static readonly Regex ColumnName = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

        /// <summary>入力を検証する。問題があれば理由を返す。</summary>
        public string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200)
            {
                return "Name は 1〜200 文字で指定してください。";
            }

            if (SiteId <= 0)
            {
                return "SiteId は 1 以上で指定してください。";
            }

            if (Mapping?.Validate() is { } mappingError)
            {
                return mappingError;
            }

            if (Filters is { } f
                && (f.Count > 20 || f.Keys.Any(k => !ColumnName.IsMatch(k)) || f.Values.Any(v => v.Length > 500)))
            {
                return "Filters は 20 件まで、列名は英数字と _ 、値は 500 文字までです。";
            }

            return null;
        }
    }

    /// <summary>管理 API が返すフィードの姿。**トークンを含めない。**</summary>
    public sealed record FeedView(
        Guid Id,
        string Name,
        long SiteId,
        FeedMapping Mapping,
        IReadOnlyDictionary<string, string> Filters,
        DateTimeOffset CreatedAt)
    {
        public static FeedView From(FeedDefinition f) =>
            new(f.Id, f.Name, f.SiteId, f.Mapping, f.Filters, f.CreatedAt);
    }
}
