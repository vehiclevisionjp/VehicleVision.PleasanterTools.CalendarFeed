using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>複数台で動かしたときの前提（Issue #49）: マイグレーションの排他（レート制限の数えは Core.Data のストアになり、Core のテストが見る）（4 種の RDBMS）。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class MultiInstanceTests : IDisposable
{
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-multi-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    private string ConnectionStringFor(DatabaseProvider provider) =>
        provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!;

    private DbConnectionFactory FactoryFor(DatabaseProvider provider) => new(provider, ConnectionStringFor(provider));

    [Theory]
    [EnabledDatabases]
    public async Task マイグレーションのロックは同時に1台だけが持ち_手放せば次の台が取れる(DatabaseProvider provider)
    {
        var connectionString = ConnectionStringFor(provider);
        TestDatabases.Migrate(provider, connectionString);

        var first = await AcquireLockAsync(provider, connectionString, TimeSpan.FromSeconds(5));
        try
        {
            // 持っている間、別の台は待ち切れずに諦める
            await Assert.ThrowsAsync<TimeoutException>(
                () => AcquireLockAsync(provider, connectionString, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await first.DisposeAsync();
        }

        // 手放したら、次の台が取れる
        await using var next = await AcquireLockAsync(provider, connectionString, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [EnabledDatabases]
    public async Task ロックを持つ台が居る間でも_当て終わった後のマイグレーションは待ってから何もしない(DatabaseProvider provider)
    {
        var connectionString = ConnectionStringFor(provider);
        TestDatabases.Migrate(provider, connectionString);

        // 別の台がロックを持って当てている最中を真似る。後から来た台は待つ（待てなければ TimeoutException）
        await using (await AcquireLockAsync(provider, connectionString, TimeSpan.FromSeconds(5)))
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => CalendarFeedDatabase.Instance.Migrator.MigrateUpWithLockAsync(provider, connectionString, TimeSpan.FromSeconds(1)));
        }

        // 手放したら、当て終わっているので何も起きずに戻る
        var result = await CalendarFeedDatabase.Instance.Migrator.MigrateUpWithLockAsync(provider, connectionString, TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.AppliedCount);
    }

    private static Task<DatabaseMigrationLock> AcquireLockAsync(
        DatabaseProvider provider,
        string connectionString,
        TimeSpan timeout) =>
        DatabaseMigrationLock.AcquireAsync(
            provider,
            connectionString,
            CalendarFeedDatabase.Instance.MigrationLockName,
            timeout,
            CancellationToken.None);
}
