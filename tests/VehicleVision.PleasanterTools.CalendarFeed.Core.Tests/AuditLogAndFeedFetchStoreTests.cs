using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>フィードの最後の取得結果の保管（4 種の RDBMS）。監査ログは Core.Data のストアになり、Core のテストが見る。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class AuditLogAndFeedFetchStoreTests : IDisposable
{
    private static readonly string TestKey = SecretProtector.GenerateKey();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-auditfetch-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    private async Task<FeedStore> SetUpAsync(DatabaseProvider provider)
    {
        var connectionString = provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!;
        var factory = new DbConnectionFactory(provider, connectionString);
        TestDatabases.Migrate(provider, connectionString);
        await using (var connection = factory.Create())
        {
            await connection.ExecuteAsync(SqlDialect.Format(provider, "DELETE FROM [Feeds]"));
        }

        return new FeedStore(factory, new SecretProtector(TestKey));
    }

    [Theory]
    [EnabledDatabases]
    public async Task フィードの最後の取得結果を上書きで残し_一覧が返す(DatabaseProvider provider)
    {
        var feeds = await SetUpAsync(provider);
        var (feed, _) = await feeds.CreateAsync("共有予定", 123, new FeedMapping(), new Dictionary<string, string>());
        Assert.Null((await feeds.ListAsync()).Single().LastFetch);

        await feeds.RecordFetchAsync(feed.Id, new FeedFetchResult(Now, FeedFetchStatus.Success, null, 42));
        var success = (await feeds.ListAsync()).Single().LastFetch!;
        Assert.Equal(FeedFetchStatus.Success, success.Status);
        Assert.Equal(42, success.EventCount);
        Assert.Equal(Now, success.At);

        await feeds.RecordFetchAsync(feed.Id, new FeedFetchResult(Now.AddMinutes(5), FeedFetchStatus.Unauthorized, "Pleasanter が HTTP 401 を返しました。\n" + new string('x', 900)));
        var failed = (await feeds.ListAsync()).Single().LastFetch!;
        Assert.Equal(FeedFetchStatus.Unauthorized, failed.Status);
        Assert.Null(failed.EventCount);
        Assert.DoesNotContain('\n', failed.Message!);
        Assert.Equal(500, failed.Message!.Length);
    }
}
