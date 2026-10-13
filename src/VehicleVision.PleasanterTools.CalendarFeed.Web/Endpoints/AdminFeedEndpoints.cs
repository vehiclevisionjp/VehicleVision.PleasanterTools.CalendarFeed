using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>フィードの管理 API。**ログイン済みの管理者（<c>Session</c>）だけ**が使える。</summary>
public static class AdminFeedEndpoints
{
    /// <summary>フィードの管理 API を登録する。</summary>
    public static void MapAdminFeeds(this WebApplication app)
    {
        var admin = app.MapGroup("/admin/api/feeds");

        // ログイン済みか・同じサイトからの要求かを、入口で確かめる（2 要素の途中では何も操作させない）
        admin.AddEndpointFilter<AdminSessionFilter>();

        admin.MapGet("/", async (IFeedStore feeds, CancellationToken ct) =>
            Results.Ok((await feeds.ListAsync(ct)).Select(FeedView.From)));

        admin.MapPost("/", async (
            CreateFeedRequest request,
            HttpContext http,
            IFeedStore feeds,
            PleasanterOptions pleasanter,
            ISecretProtector protector,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (request.Validate() is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (!string.IsNullOrEmpty(request.ApiKey) && !protector.IsConfigured)
            {
                return Results.BadRequest(new { error = $"API キーを保管するには {UnconfiguredSecretProtector.SettingName} の設定が要ります。" });
            }

            // API キーが無いフィードは、全フィード共通の既定があるときだけ動く
            if (string.IsNullOrEmpty(request.ApiKey) && string.IsNullOrEmpty(pleasanter.ApiKey))
            {
                return Results.BadRequest(new { error = "ApiKey を指定してください（全フィード共通の既定の API キーも未設定です）。" });
            }

            var (feed, token) = await feeds.CreateAsync(
                request.Name.Trim(),
                request.SiteId,
                request.Mapping ?? new FeedMapping(),
                request.Filters ?? new Dictionary<string, string>(),
                new FeedAccess(request.ApiKey, request.ApiKeyUserTimeZoneId, FeedLanguages.Normalize(request.Language)),
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

        // **API キーの差し替え。** 登録後の値は返さない。差し替えたら、古いキーで取った生成済みの .ics を捨てる
        admin.MapPut("/{id:guid}/apikey", async (
            Guid id,
            SetApiKeyRequest request,
            IFeedStore feeds,
            FeedService service,
            ISecretProtector protector,
            CancellationToken ct) =>
        {
            if (request.Validate() is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (!protector.IsConfigured)
            {
                return Results.BadRequest(new { error = $"API キーを保管するには {UnconfiguredSecretProtector.SettingName} の設定が要ります。" });
            }

            if (!await feeds.SetApiKeyAsync(id, request.ApiKey, request.ApiKeyUserTimeZoneId, ct))
            {
                return Results.NotFound();
            }

            service.Invalidate(id);
            return Results.NoContent();
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IFeedStore feeds, FeedService service, CancellationToken ct) =>
        {
            if (!await feeds.RevokeAsync(id, ct))
            {
                return Results.NotFound();
            }

            service.Invalidate(id);
            return Results.NoContent();
        });
    }

    private static string FeedUrl(HttpContext http, IConfiguration configuration, string token)
    {
        // **リバースプロキシの内側では Host が内部の値になる。公開 URL は設定で固定する**
        var baseUrl = AppSettings.Text(configuration, AppSettings.PublicBaseUrl)?.TrimEnd('/')
            ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        return $"{baseUrl}/feeds/{token}.ics";
    }
}

/// <summary>フィード作成の要求。</summary>
public sealed record CreateFeedRequest(
    string Name,
    long SiteId,
    FeedMapping? Mapping,
    Dictionary<string, string>? Filters,
    string? ApiKey = null,
    string? ApiKeyUserTimeZoneId = null,
    string? Language = null)
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

        if (ApiKey is not null && (ApiKey.Length is 0 or > 512 || ApiKey.Any(char.IsControl)))
        {
            return "ApiKey は 1〜512 文字（制御文字なし）で指定してください。";
        }

        if (SetApiKeyRequest.InvalidTimeZone(ApiKeyUserTimeZoneId))
        {
            return "ApiKeyUserTimeZoneId に、このサーバが解決できないタイムゾーン ID が指定されています。";
        }

        return Language is not null && !FeedLanguages.IsSupported(Language)
            ? $"Language は {string.Join(" / ", FeedLanguages.Supported)} のいずれかで指定してください。"
            : null;
    }
}

/// <summary>API キーの差し替えの要求。</summary>
public sealed record SetApiKeyRequest(string ApiKey, string? ApiKeyUserTimeZoneId = null)
{
    /// <summary>入力を検証する。問題があれば理由を返す。</summary>
    public string? Validate()
    {
        if (string.IsNullOrEmpty(ApiKey) || ApiKey.Length > 512 || ApiKey.Any(char.IsControl))
        {
            return "ApiKey は 1〜512 文字（制御文字なし）で指定してください。";
        }

        return InvalidTimeZone(ApiKeyUserTimeZoneId)
            ? "ApiKeyUserTimeZoneId に、このサーバが解決できないタイムゾーン ID が指定されています。"
            : null;
    }

    /// <summary>指定されているのに、解決できないタイムゾーン ID か。</summary>
    public static bool InvalidTimeZone(string? id) =>
        id is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
}

/// <summary>管理 API が返すフィードの姿。**トークンも API キーも含めない**（API キーは登録済みかと末尾だけ）。</summary>
public sealed record FeedView(
    Guid Id,
    string Name,
    long SiteId,
    FeedMapping Mapping,
    IReadOnlyDictionary<string, string> Filters,
    DateTimeOffset CreatedAt,
    string Language,
    string? ApiKeyUserTimeZoneId,
    bool HasApiKey,
    string? ApiKeyHint)
{
    public static FeedView From(FeedDefinition f) => new(
        f.Id, f.Name, f.SiteId, f.Mapping, f.Filters, f.CreatedAt,
        f.Language, f.ApiKeyUserTimeZoneId, f.HasApiKey, f.ApiKeyHint);
}
