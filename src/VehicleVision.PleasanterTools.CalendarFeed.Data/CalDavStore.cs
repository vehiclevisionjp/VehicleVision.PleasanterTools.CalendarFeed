using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>カレンダーアプリが付けた UID・資源の名前と、Pleasanter のレコード ID の対応。</summary>
public sealed record CalDavResource(Guid FeedId, string Name, string Uid, long RecordId);

/// <summary>CalDAV の書き込みの記録の保管先（Issue #85）。</summary>
public interface ICalDavStore
{
    /// <summary>フィードの対応をすべて返す。</summary>
    Task<IReadOnlyList<CalDavResource>> ListAsync(Guid feedId, CancellationToken cancellationToken = default);

    /// <summary>資源の名前から引く。</summary>
    Task<CalDavResource?> FindByNameAsync(Guid feedId, string name, CancellationToken cancellationToken = default);

    /// <summary>UID から引く。</summary>
    Task<CalDavResource?> FindByUidAsync(Guid feedId, string uid, CancellationToken cancellationToken = default);

    /// <summary>対応を足す。名前か UID が既にあれば（一意制約）<c>false</c>。</summary>
    Task<bool> TryAddAsync(CalDavResource resource, CancellationToken cancellationToken = default);

    /// <summary>レコードの対応を消す（予定を消したとき）。</summary>
    Task DeleteByRecordAsync(Guid feedId, long recordId, CancellationToken cancellationToken = default);

    /// <summary>フィードの対応をすべて消す（CalDAV を無効にした・フィードを失効させたとき）。</summary>
    Task DeleteFeedAsync(Guid feedId, CancellationToken cancellationToken = default);

    /// <summary>排他の貸し出しを受ける。<paramref name="lease"/> の間だけ有効。取れなければ <c>false</c>。</summary>
    Task<bool> TryAcquireLockAsync(string key, string owner, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>貸し出しを返す（自分のものだけ）。</summary>
    Task ReleaseLockAsync(string key, string owner, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装（4 種の RDBMS）。</summary>
public sealed class CalDavStore(IDbConnectionFactory connections) : ICalDavStore
{
    private const string Select = "SELECT [FeedId], [Name], [Uid], [RecordId] FROM [CalDavResources]";

    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    public async Task<IReadOnlyList<CalDavResource>> ListAsync(Guid feedId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Sql($"{Select} WHERE [FeedId] = @FeedId"), new { FeedId = feedId.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToResource()).ToList();
    }

    public async Task<CalDavResource?> FindByNameAsync(Guid feedId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return (await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Sql($"{Select} WHERE [FeedId] = @FeedId AND [Name] = @Name"),
            new { FeedId = feedId.ToString("D"), Name = name }, cancellationToken: cancellationToken)).ConfigureAwait(false))?.ToResource();
    }

    public async Task<CalDavResource?> FindByUidAsync(Guid feedId, string uid, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return (await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Sql($"{Select} WHERE [FeedId] = @FeedId AND [Uid] = @Uid"),
            new { FeedId = feedId.ToString("D"), Uid = uid }, cancellationToken: cancellationToken)).ConfigureAwait(false))?.ToResource();
    }

    public async Task<bool> TryAddAsync(CalDavResource resource, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [CalDavResources] ([FeedId], [Name], [Uid], [RecordId]) VALUES (@FeedId, @Name, @Uid, @RecordId)"),
                new { FeedId = resource.FeedId.ToString("D"), resource.Name, resource.Uid, resource.RecordId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            // 名前か UID の一意制約の違反（同時に同じものを足した）。呼び出し側が 409 にする
            return false;
        }
    }

    public async Task DeleteByRecordAsync(Guid feedId, long recordId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [CalDavResources] WHERE [FeedId] = @FeedId AND [RecordId] = @RecordId"),
            new { FeedId = feedId.ToString("D"), RecordId = recordId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteFeedAsync(Guid feedId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [CalDavResources] WHERE [FeedId] = @FeedId"),
            new { FeedId = feedId.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> TryAcquireLockAsync(string key, string owner, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();

        // 期限の切れた貸し出し（持ち主が落ちた）を片付けてから、取りに行く
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [CalDavLocks] WHERE [LockKey] = @Key AND [ExpiresAt] < @Now"),
            new { Key = key, Now = now.ToUnixTimeSeconds() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [CalDavLocks] ([LockKey], [Owner], [ExpiresAt]) VALUES (@Key, @Owner, @Expires)"),
                new { Key = key, Owner = owner, Expires = (now + lease).ToUnixTimeSeconds() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    public async Task ReleaseLockAsync(string key, string owner, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [CalDavLocks] WHERE [LockKey] = @Key AND [Owner] = @Owner"),
            new { Key = key, Owner = owner }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
    }

    private sealed class Row
    {
        public string FeedId { get; set; } = "";

        public string Name { get; set; } = "";

        public string Uid { get; set; } = "";

        public long RecordId { get; set; }

        public CalDavResource ToResource() => new(Guid.Parse(FeedId), Name, Uid, RecordId);
    }
}
