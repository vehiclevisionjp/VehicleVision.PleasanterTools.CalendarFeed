using System.Globalization;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>フィード定義を RDBMS へ保管する（SQLite / SQL Server / PostgreSQL / MySQL）。</summary>
/// <remarks>
/// <para>
/// トークンは SHA-256 だけを保管する（<see cref="FeedTokens"/>）。
/// </para>
/// <para>
/// SQL は <see cref="SqlDialect.Format"/> を通して書く（識別子は角括弧で囲む）。**方言差を持ち込まない。**
/// </para>
/// </remarks>
public sealed class FeedStore(IDbConnectionFactory connections) : IFeedStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string SelectColumns =
        "SELECT [Id], [Name], [SiteId], [MappingJson], [FiltersJson], [CreatedAt], [IsRevoked] FROM [Feeds]";

    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    public async Task<(FeedDefinition Feed, string Token)> CreateAsync(
        string name,
        long siteId,
        FeedMapping mapping,
        IReadOnlyDictionary<string, string> filters,
        CancellationToken cancellationToken = default)
    {
        var feed = new FeedDefinition(
            Guid.NewGuid(), name, siteId, mapping, filters, DateTimeOffset.UtcNow, IsRevoked: false);
        var token = FeedTokens.Generate();

        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [Feeds] ([Id], [Name], [SiteId], [MappingJson], [FiltersJson], [TokenHash], [CreatedAt], [IsRevoked]) "
                + "VALUES (@Id, @Name, @SiteId, @MappingJson, @FiltersJson, @TokenHash, @CreatedAt, 0)"),
            new
            {
                Id = feed.Id.ToString("D"),
                Name = name,
                SiteId = siteId,
                MappingJson = JsonSerializer.Serialize(mapping, Json),
                FiltersJson = JsonSerializer.Serialize(filters, Json),
                TokenHash = FeedTokens.Hash(token),
                CreatedAt = feed.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
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

    /// <summary>表の 1 行。**型は 4 種の RDBMS で通じるものだけ**（M0001_Feeds）。</summary>
    private sealed class FeedRow
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public long SiteId { get; set; }

        public string MappingJson { get; set; } = "";

        public string FiltersJson { get; set; } = "";

        public string CreatedAt { get; set; } = "";

        public int IsRevoked { get; set; }

        public FeedDefinition ToDefinition() => new(
            Guid.Parse(Id),
            Name,
            SiteId,
            JsonSerializer.Deserialize<FeedMapping>(MappingJson, Json) ?? new FeedMapping(),
            JsonSerializer.Deserialize<Dictionary<string, string>>(FiltersJson, Json)
                ?? new Dictionary<string, string>(),
            DateTimeOffset.Parse(CreatedAt, CultureInfo.InvariantCulture),
            IsRevoked != 0);
    }
}
