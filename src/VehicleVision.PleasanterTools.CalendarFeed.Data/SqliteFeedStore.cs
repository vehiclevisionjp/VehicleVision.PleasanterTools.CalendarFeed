using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>フィード定義を SQLite へ保管する。</summary>
/// <remarks>
/// <para>
/// **足場の実装。** Questionnaire は SQL Server / PostgreSQL / MySQL に対応しているが、
/// ここでは SQLite だけ。対応 RDBMS の範囲は <c>_documents/アーキテクチャ方針.md</c> の未確定事項。
/// </para>
/// <para>
/// トークンは SHA-256 だけを保管する（<see cref="FeedTokens"/>）。
/// </para>
/// </remarks>
public sealed class SqliteFeedStore(string connectionString) : IFeedStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>表が無ければ作る。起動時に 1 度呼ぶ。</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Feeds (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                SiteId INTEGER NOT NULL,
                MappingJson TEXT NOT NULL,
                FiltersJson TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                IsRevoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Feeds_TokenHash ON Feeds (TokenHash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Feeds (Id, Name, SiteId, MappingJson, FiltersJson, TokenHash, CreatedAt, IsRevoked)
            VALUES ($id, $name, $siteId, $mapping, $filters, $hash, $createdAt, 0)
            """;
        command.Parameters.AddWithValue("$id", feed.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$siteId", siteId);
        command.Parameters.AddWithValue("$mapping", JsonSerializer.Serialize(mapping, Json));
        command.Parameters.AddWithValue("$filters", JsonSerializer.Serialize(filters, Json));
        command.Parameters.AddWithValue("$hash", FeedTokens.Hash(token));
        command.Parameters.AddWithValue("$createdAt", feed.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (feed, token);
    }

    public async Task<FeedDefinition?> FindByTokenAsync(
        string token, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE TokenHash = $hash AND IsRevoked = 0";
        command.Parameters.AddWithValue("$hash", FeedTokens.Hash(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<FeedDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE IsRevoked = 0 ORDER BY CreatedAt DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var feeds = new List<FeedDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            feeds.Add(Read(reader));
        }

        return feeds;
    }

    public async Task<string?> RotateTokenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var token = FeedTokens.Generate();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Feeds SET TokenHash = $hash WHERE Id = $id AND IsRevoked = 0";
        command.Parameters.AddWithValue("$hash", FeedTokens.Hash(token));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed > 0 ? token : null;
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Feeds SET IsRevoked = 1 WHERE Id = $id AND IsRevoked = 0";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private const string SelectColumns =
        "SELECT Id, Name, SiteId, MappingJson, FiltersJson, CreatedAt, IsRevoked FROM Feeds";

    private static FeedDefinition Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetInt64(2),
        JsonSerializer.Deserialize<FeedMapping>(reader.GetString(3), Json) ?? new FeedMapping(),
        JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), Json)
            ?? new Dictionary<string, string>(),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.GetInt64(6) != 0);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
