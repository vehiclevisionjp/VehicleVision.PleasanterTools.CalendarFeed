using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
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
            Results.Ok((await feeds.ListAsync(ct)).Select(FeedView.From)))
            .AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.FeedsRead));

        // 権限: 一覧は feeds.read、作成・トークン再発行・API キーは feeds.write、失効は feeds.delete
        admin.MapPost("/", async (
            CreateFeedRequest request,
            HttpContext http,
            IFeedStore feeds,
            PleasanterOptions pleasanter,
            ISecretProtector protector,
            RuntimeSettings settings,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (request.Validate(RequestLanguage.Of(http)) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (!string.IsNullOrEmpty(request.ApiKey) && !protector.IsConfigured)
            {
                return ApiError.Of(StatusCodes.Status400BadRequest, ServerMessageKeys.ApiKeyNeedsSecretKey, UnconfiguredSecretProtector.SettingName);
            }

            // API キーが無いフィードは、全フィード共通の既定があるときだけ動く
            if (string.IsNullOrEmpty(request.ApiKey) && string.IsNullOrEmpty(pleasanter.ApiKey))
            {
                return ApiError.Of(StatusCodes.Status400BadRequest, ServerMessageKeys.ApiKeyRequired);
            }

            var (feed, token) = await feeds.CreateAsync(
                request.Name.Trim(),
                request.SiteId,
                request.Mapping ?? new FeedMapping(),
                request.Filters ?? new Dictionary<string, string>(),
                new FeedAccess(request.ApiKey, request.ApiKeyUserTimeZoneId, FeedLanguages.Normalize(request.Language)),
                ct);
            await audit.WriteAsync(
                http, AuditActions.FeedCreated, true, AdminContext.Of(http).User, targetType: "Feed", targetId: feed.Id.ToString("D"),
                detail: $"siteId={request.SiteId}");
            return Results.Created(
                $"/admin/api/feeds/{feed.Id}",
                new { feed = FeedView.From(feed), url = FeedUrl(http, settings, token) });
        }).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.FeedsWrite));

        // **フィードの編集。** 購読 URL（トークン）と API キーは変えない。変えた項目の名前だけを監査ログへ残す（値は残さない）
        admin.MapPut("/{id:guid}", async (
            Guid id,
            UpdateFeedRequest request,
            HttpContext http,
            IFeedStore feeds,
            FeedService service,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (request.Validate(RequestLanguage.Of(http)) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (await feeds.FindByIdAsync(id, ct) is not { } before)
            {
                return Results.NotFound();
            }

            var update = new FeedUpdate(
                request.Name.Trim(),
                request.SiteId,
                request.Mapping ?? new FeedMapping(),
                request.Filters ?? new Dictionary<string, string>(),
                FeedLanguages.Normalize(request.Language),
                string.IsNullOrWhiteSpace(request.ApiKeyUserTimeZoneId) ? null : request.ApiKeyUserTimeZoneId);
            if (!await feeds.UpdateAsync(id, update, ct))
            {
                return Results.NotFound();
            }

            // 古い定義で作った .ics を返さない
            service.Invalidate(id);

            var changed = new List<string>();
            if (before.Name != update.Name) { changed.Add("name"); }
            if (before.SiteId != update.SiteId) { changed.Add("siteId"); }
            if (System.Text.Json.JsonSerializer.Serialize(before.Mapping) != System.Text.Json.JsonSerializer.Serialize(update.Mapping)) { changed.Add("mapping"); }
            if (!before.Filters.OrderBy(p => p.Key).SequenceEqual(update.Filters.OrderBy(p => p.Key))) { changed.Add("filters"); }
            if (before.Language != update.Language) { changed.Add("language"); }
            if (before.ApiKeyUserTimeZoneId != update.ApiKeyUserTimeZoneId) { changed.Add("apiKeyUserTimeZoneId"); }

            await audit.WriteAsync(
                http, AuditActions.FeedUpdated, true, AdminContext.Of(http).User, targetType: "Feed", targetId: id.ToString("D"),
                detail: changed.Count == 0 ? "no-change" : $"changed={string.Join(',', changed)}");
            return Results.Ok(FeedView.From((await feeds.FindByIdAsync(id, ct))!));
        }).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.FeedsWrite));

        admin.MapPost("/{id:guid}/rotate", async (
            Guid id,
            HttpContext http,
            IFeedStore feeds,
            RuntimeSettings settings,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (await feeds.RotateTokenAsync(id, ct) is not { } token)
            {
                return Results.NotFound();
            }

            await audit.WriteAsync(http, AuditActions.FeedTokenRotated, true, AdminContext.Of(http).User, targetType: "Feed", targetId: id.ToString("D"));
            return Results.Ok(new { url = FeedUrl(http, settings, token) });
        }).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.FeedsWrite));

        // **API キーの差し替え。** 登録後の値は返さない。差し替えたら、古いキーで取った生成済みの .ics を捨てる
        admin.MapPut("/{id:guid}/apikey", async (
            Guid id,
            SetApiKeyRequest request,
            HttpContext http,
            IFeedStore feeds,
            FeedService service,
            ISecretProtector protector,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (request.Validate(RequestLanguage.Of(http)) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (!protector.IsConfigured)
            {
                return ApiError.Of(StatusCodes.Status400BadRequest, ServerMessageKeys.ApiKeyNeedsSecretKey, UnconfiguredSecretProtector.SettingName);
            }

            if (!await feeds.SetApiKeyAsync(id, request.ApiKey, request.ApiKeyUserTimeZoneId, ct))
            {
                return Results.NotFound();
            }

            service.Invalidate(id);
            // **API キーそのものは残さない**（対象のフィードだけ）
            await audit.WriteAsync(http, AuditActions.FeedApiKeySet, true, AdminContext.Of(http).User, targetType: "Feed", targetId: id.ToString("D"));
            return Results.NoContent();
        }).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.FeedsWrite));

        admin.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, IFeedStore feeds, FeedService service, AuditLogService audit, CancellationToken ct) =>
        {
            if (!await feeds.RevokeAsync(id, ct))
            {
                return Results.NotFound();
            }

            service.Invalidate(id);
            await audit.WriteAsync(http, AuditActions.FeedRevoked, true, AdminContext.Of(http).User, targetType: "Feed", targetId: id.ToString("D"));
            return Results.NoContent();
        }).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.FeedsDelete));
    }

    private static string FeedUrl(HttpContext http, RuntimeSettings settings, string token)
    {
        // **リバースプロキシの内側では Host が内部の値になる。公開 URL は設定で固定する**
        var baseUrl = settings.Current.PublicBaseUrl?.TrimEnd('/')
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
    public string? Validate(string? language = null)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200)
        {
            return ServerMessages.Get(ServerMessageKeys.FeedNameInvalid, language);
        }

        if (SiteId <= 0)
        {
            return ServerMessages.Get(ServerMessageKeys.FeedSiteIdInvalid, language);
        }

        if (Mapping?.Validate() is { } mappingError)
        {
            return mappingError;
        }

        if (Filters is { } f
            && (f.Count > 20 || f.Keys.Any(k => !ColumnName.IsMatch(k)) || f.Values.Any(v => v.Length > 500)))
        {
            return ServerMessages.Get(ServerMessageKeys.FeedFiltersInvalid, language);
        }

        if (ApiKey is not null && (ApiKey.Length is 0 or > 512 || ApiKey.Any(char.IsControl)))
        {
            return ServerMessages.Get(ServerMessageKeys.FeedApiKeyInvalid, language);
        }

        if (SetApiKeyRequest.InvalidTimeZone(ApiKeyUserTimeZoneId))
        {
            return ServerMessages.Get(ServerMessageKeys.FeedTimeZoneInvalid, language);
        }

        return Language is not null && !FeedLanguages.IsSupported(Language)
            ? ServerMessages.Get(ServerMessageKeys.FeedLanguageInvalid, language, string.Join(" / ", FeedLanguages.Supported))
            : null;
    }
}

/// <summary>フィードの更新の要求。**全項目を置き換える**（PUT）。API キーは別の口（<c>PUT …/apikey</c>）。</summary>
public sealed record UpdateFeedRequest(
    string Name,
    long SiteId,
    FeedMapping? Mapping,
    Dictionary<string, string>? Filters,
    string? ApiKeyUserTimeZoneId = null,
    string? Language = null)
{
    /// <summary>入力を検証する（作成と同じ条件）。問題があれば理由を返す。</summary>
    public string? Validate(string? language = null) => new CreateFeedRequest(Name, SiteId, Mapping, Filters, null, ApiKeyUserTimeZoneId, Language).Validate(language);
}

/// <summary>API キーの差し替えの要求。</summary>
public sealed record SetApiKeyRequest(string ApiKey, string? ApiKeyUserTimeZoneId = null)
{
    /// <summary>入力を検証する。問題があれば理由を返す。</summary>
    public string? Validate(string? language = null)
    {
        if (string.IsNullOrEmpty(ApiKey) || ApiKey.Length > 512 || ApiKey.Any(char.IsControl))
        {
            return ServerMessages.Get(ServerMessageKeys.FeedApiKeyInvalid, language);
        }

        return InvalidTimeZone(ApiKeyUserTimeZoneId)
            ? ServerMessages.Get(ServerMessageKeys.FeedTimeZoneInvalid, language)
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
    string? ApiKeyHint,
    FeedFetchResult? LastFetch)
{
    public static FeedView From(FeedDefinition f) => new(
        f.Id, f.Name, f.SiteId, f.Mapping, f.Filters, f.CreatedAt,
        f.Language, f.ApiKeyUserTimeZoneId, f.HasApiKey, f.ApiKeyHint, f.LastFetch);
}
