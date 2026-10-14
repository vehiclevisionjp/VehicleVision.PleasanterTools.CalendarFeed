using System.Globalization;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>フィード定義を RDBMS へ保管する（SQLite / SQL Server / PostgreSQL / MySQL）。</summary>
/// <remarks>
/// <para>
/// トークンは SHA-256 だけを保管する（<see cref="FeedTokens"/>）。
/// **API キーは <see cref="ISecretProtector"/> で暗号化して保管し、フィード ID に束縛する。** 平文は DB に置かない。
/// 一覧・取得が返す <see cref="FeedDefinition"/> に API キーは含まれない（末尾の <c>ApiKeyHint</c> だけ）。
/// </para>
/// <para>
/// SQL は <see cref="SqlDialect.Format"/> を通して書く（識別子は角括弧で囲む）。**方言差を持ち込まない。**
/// </para>
/// </remarks>
public sealed class FeedStore(IDbConnectionFactory connections, ISecretProtector protector) : IFeedStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string SelectColumns =
        "SELECT [Id], [Name], [SiteId], [MappingJson], [FiltersJson], [CreatedAt], [IsRevoked], "
        + "[Language], [ApiKeyUserTimeZoneId], [ApiKeyHint], "
        + "[LastFetchAt], [LastFetchStatus], [LastFetchMessage], [LastFetchEventCount] FROM [Feeds]";

    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    /// <summary>暗号文を持ち主（フィード ID）に束縛する文脈。</summary>
    private static string ContextOf(Guid id) => $"calendarfeed:apikey:{id:D}";

    /// <summary>見分けるための末尾。短い API キーは末尾を出さない（空文字＝登録済み・末尾なし）。</summary>
    internal static string HintOf(string apiKey) => apiKey.Length >= 16 ? apiKey[^4..] : string.Empty;

    public async Task<(FeedDefinition Feed, string Token)> CreateAsync(
        string name,
        long siteId,
        FeedMapping mapping,
        IReadOnlyDictionary<string, string> filters,
        FeedAccess? access = null,
        CancellationToken cancellationToken = default)
    {
        access ??= new FeedAccess();
        var language = FeedLanguages.Normalize(access.Language);
        var id = Guid.NewGuid();
        var hasKey = !string.IsNullOrEmpty(access.ApiKey);
        var feed = new FeedDefinition(
            id, name, siteId, mapping, filters, DateTimeOffset.UtcNow, IsRevoked: false,
            language, access.ApiKeyUserTimeZoneId, hasKey ? HintOf(access.ApiKey!) : null);
        var token = FeedTokens.Generate();

        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [Feeds] ([Id], [Name], [SiteId], [MappingJson], [FiltersJson], [TokenHash], [CreatedAt], "
                + "[IsRevoked], [Language], [ApiKeyCipher], [ApiKeyHint], [ApiKeyUserTimeZoneId]) "
                + "VALUES (@Id, @Name, @SiteId, @MappingJson, @FiltersJson, @TokenHash, @CreatedAt, "
                + "0, @Language, @ApiKeyCipher, @ApiKeyHint, @ApiKeyUserTimeZoneId)"),
            new
            {
                Id = id.ToString("D"),
                Name = name,
                SiteId = siteId,
                MappingJson = JsonSerializer.Serialize(mapping, Json),
                FiltersJson = JsonSerializer.Serialize(filters, Json),
                TokenHash = FeedTokens.Hash(token),
                CreatedAt = feed.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                Language = language,
                ApiKeyCipher = hasKey ? protector.Protect(access.ApiKey!, ContextOf(id)) : null,
                ApiKeyHint = feed.ApiKeyHint,
                access.ApiKeyUserTimeZoneId,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return (feed, token);
    }

    public async Task<FeedDefinition?> FindByTokenAsync(
        string token, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<FeedRow>(new CommandDefinition(
            Sql($"{SelectColumns} WHERE [TokenHash] = @TokenHash AND [IsRevoked] = 0"),
            new { TokenHash = FeedTokens.Hash(token) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToDefinition();
    }

    public async Task<IReadOnlyList<FeedDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<FeedRow>(new CommandDefinition(
            Sql($"{SelectColumns} WHERE [IsRevoked] = 0 ORDER BY [CreatedAt] DESC"),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToDefinition()).ToList();
    }

    public async Task<FeedDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<FeedRow>(new CommandDefinition(
            Sql($"{SelectColumns} WHERE [Id] = @Id AND [IsRevoked] = 0"), new { Id = id.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToDefinition();
    }

    public async Task<bool> UpdateAsync(Guid id, FeedUpdate update, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        // トークン・API キーは触らない。失効済みは更新しない
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Feeds] SET [Name] = @Name, [SiteId] = @SiteId, [MappingJson] = @MappingJson, [FiltersJson] = @FiltersJson, "
                + "[Language] = @Language, [ApiKeyUserTimeZoneId] = @ApiKeyUserTimeZoneId WHERE [Id] = @Id AND [IsRevoked] = 0"),
            new
            {
                Id = id.ToString("D"),
                update.Name,
                update.SiteId,
                MappingJson = JsonSerializer.Serialize(update.Mapping, Json),
                FiltersJson = JsonSerializer.Serialize(update.Filters, Json),
                Language = FeedLanguages.Normalize(update.Language),
                update.ApiKeyUserTimeZoneId,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<string?> RotateTokenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var token = FeedTokens.Generate();
        await using var connection = connections.Create();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Feeds] SET [TokenHash] = @TokenHash WHERE [Id] = @Id AND [IsRevoked] = 0"),
            new { TokenHash = FeedTokens.Hash(token), Id = id.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0 ? token : null;
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Feeds] SET [IsRevoked] = 1 WHERE [Id] = @Id AND [IsRevoked] = 0"),
            new { Id = id.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<string?> GetApiKeyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var cipher = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            Sql("SELECT [ApiKeyCipher] FROM [Feeds] WHERE [Id] = @Id AND [IsRevoked] = 0"),
            new { Id = id.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return string.IsNullOrEmpty(cipher) ? null : protector.Unprotect(cipher, ContextOf(id));
    }

    public async Task RecordFetchAsync(
        Guid id, FeedFetchResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Feeds] SET [LastFetchAt] = @At, [LastFetchStatus] = @Status, [LastFetchMessage] = @Message, "
                + "[LastFetchEventCount] = @EventCount WHERE [Id] = @Id"),
            new
            {
                Id = id.ToString("D"),
                At = DbTime.Format(result.At),
                Status = AuditText.Safe(result.Status, 32),
                // 制御文字を落として桁で切る（桁溢れで記録が落ちないように。API キーは渡さない前提）
                Message = AuditText.Safe(result.Message, 500),
                result.EventCount,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> SetApiKeyAsync(
        Guid id,
        string apiKey,
        string? apiKeyUserTimeZoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [Feeds] SET [ApiKeyCipher] = @ApiKeyCipher, [ApiKeyHint] = @ApiKeyHint, "
                + "[ApiKeyUserTimeZoneId] = @ApiKeyUserTimeZoneId WHERE [Id] = @Id AND [IsRevoked] = 0"),
            new
            {
                Id = id.ToString("D"),
                ApiKeyCipher = protector.Protect(apiKey, ContextOf(id)),
                ApiKeyHint = HintOf(apiKey),
                ApiKeyUserTimeZoneId = apiKeyUserTimeZoneId,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    /// <summary>表の 1 行（API キーの暗号文は読まない）。**型は 4 種の RDBMS で通じるものだけ。**</summary>
    private sealed class FeedRow
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public long SiteId { get; set; }

        public string MappingJson { get; set; } = "";

        public string FiltersJson { get; set; } = "";

        public string CreatedAt { get; set; } = "";

        public int IsRevoked { get; set; }

        public string Language { get; set; } = FeedLanguages.Default;

        public string? ApiKeyUserTimeZoneId { get; set; }

        public string? ApiKeyHint { get; set; }

        public string? LastFetchAt { get; set; }

        public string? LastFetchStatus { get; set; }

        public string? LastFetchMessage { get; set; }

        public int? LastFetchEventCount { get; set; }

        public FeedDefinition ToDefinition() => new(
            Guid.Parse(Id),
            Name,
            SiteId,
            JsonSerializer.Deserialize<FeedMapping>(MappingJson, Json) ?? new FeedMapping(),
            JsonSerializer.Deserialize<Dictionary<string, string>>(FiltersJson, Json)
                ?? new Dictionary<string, string>(),
            DateTimeOffset.Parse(CreatedAt, CultureInfo.InvariantCulture),
            IsRevoked != 0,
            Language,
            ApiKeyUserTimeZoneId,
            ApiKeyHint,
            DbTime.Parse(LastFetchAt) is { } at && !string.IsNullOrEmpty(LastFetchStatus)
                ? new FeedFetchResult(at, LastFetchStatus, LastFetchMessage, LastFetchEventCount)
                : null);
    }
}
