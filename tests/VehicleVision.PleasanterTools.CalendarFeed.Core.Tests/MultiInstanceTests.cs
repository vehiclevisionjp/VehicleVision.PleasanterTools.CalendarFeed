using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>複数台で動かしたときの前提（Issue #49）: レート制限の数えの共有と、マイグレーションの排他（4 種の RDBMS）。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class MultiInstanceTests : IDisposable
{
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-multi-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    private DbConnectionFactory FactoryFor(DatabaseProvider provider) => new(
        provider,
        provider is DatabaseProvider.Sqlite
            ? $"Data Source={_sqlitePath};Pooling=False"
            : TestDatabases.ConnectionString(provider)!);

    private async Task<RateLimitStore> StoreAsync(DatabaseProvider provider)
    {
        var factory = FactoryFor(provider);
        TestDatabases.Migrate(provider, factory.ConnectionString);
        await using (var connection = factory.Create())
        {
            await connection.ExecuteAsync(SqlDialect.Format(provider, "DELETE FROM [RateLimits]"));
        }

        return new RateLimitStore(factory);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同じ窓では数えが増え_窓が変わったら1に戻る(DatabaseProvider provider)
    {
        var store = await StoreAsync(provider);

        Assert.Equal(1, await store.HitAsync("adminLogin:203.0.113.1", 1000));
        Assert.Equal(2, await store.HitAsync("adminLogin:203.0.113.1", 1000));
        Assert.Equal(3, await store.HitAsync("adminLogin:203.0.113.1", 1000));

        // 別の鍵は別に数える
        Assert.Equal(1, await store.HitAsync("adminLogin:203.0.113.2", 1000));

        // 次の窓では 1 に戻る
        Assert.Equal(1, await store.HitAsync("adminLogin:203.0.113.1", 1300));
        Assert.Equal(2, await store.HitAsync("adminLogin:203.0.113.1", 1300));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同時に数えても数えこぼさない_台をまたいだ同時の更新を真似る(DatabaseProvider provider)
    {
        var store = await StoreAsync(provider);

        // 別々のストア（別の台）から、同じ鍵へ同時に 30 回
        var stores = Enumerable.Range(0, 3).Select(_ => new RateLimitStore(FactoryFor(provider))).ToArray();
        var counts = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => stores[i % 3].HitAsync("race", 5000))));

        // 1 から 30 まで、重複も欠けもなく数えられている（初回の INSERT の競合も取りこぼさない）
        Assert.Equal(Enumerable.Range(1, 30), counts.Order());
        Assert.Equal(31, await store.HitAsync("race", 5000));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 古い窓の行だけを消す(DatabaseProvider provider)
    {
        var store = await StoreAsync(provider);
        await store.HitAsync("old", 100);
        await store.HitAsync("new", 9000);

        Assert.Equal(1, await store.PurgeAsync(5000));

        // 消えたものは、また 1 から数える
        Assert.Equal(1, await store.HitAsync("old", 100));
        Assert.Equal(2, await store.HitAsync("new", 9000));
    }

    [Theory]
    [EnabledDatabases]
    public void マイグレーションのロックは同時に1台だけが持ち_手放せば次の台が取れる(DatabaseProvider provider)
    {
        var connectionString = FactoryFor(provider).ConnectionString;
        TestDatabases.Migrate(provider, connectionString);

        using var first = MigrationLock.Acquire(provider, connectionString, TimeSpan.FromSeconds(5));

        if (provider is DatabaseProvider.Sqlite)
        {
            // SQLite は、1 つのファイルを複数のホストで共有しない前提でロックしない
            using var second = MigrationLock.Acquire(provider, connectionString, TimeSpan.FromSeconds(1));
            return;
        }

        // 持っている間、別の台は待ち切れずに諦める
        Assert.Throws<TimeoutException>(() => MigrationLock.Acquire(provider, connectionString, TimeSpan.FromSeconds(1)));

        first.Dispose();

        // 手放したら、次の台が取れる
        using var next = MigrationLock.Acquire(provider, connectionString, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [EnabledDatabases]
    public void ロックを持つ台が居る間でも_当て終わった後のマイグレーションは待ってから何もしない(DatabaseProvider provider)
    {
        var connectionString = FactoryFor(provider).ConnectionString;
        TestDatabases.Migrate(provider, connectionString);
        if (provider is DatabaseProvider.Sqlite)
        {
            return;
        }

        // 別の台がロックを持って当てている最中を真似る。後から来た台は待つ（待てなければ TimeoutException）
        using (MigrationLock.Acquire(provider, connectionString, TimeSpan.FromSeconds(5)))
        {
            Assert.Throws<TimeoutException>(() => DatabaseMigrator.Migrate(provider, connectionString, TimeSpan.FromSeconds(1)));
        }

        // 手放したら、当て終わっているので何も起きずに戻る
        DatabaseMigrator.Migrate(provider, connectionString, TimeSpan.FromSeconds(10));
    }
}
