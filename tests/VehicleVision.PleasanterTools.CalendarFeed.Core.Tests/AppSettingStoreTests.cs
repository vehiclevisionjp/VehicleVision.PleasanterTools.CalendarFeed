using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>アプリケーション設定の保管（4 種の RDBMS。Issue #40）。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class AppSettingStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-appset-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    private async Task<AppSettingStore> SetUpAsync(DatabaseProvider provider)
    {
        var connectionString = provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!;
        var factory = new DbConnectionFactory(provider, connectionString);
        TestDatabases.Migrate(provider, connectionString);
        await using (var connection = factory.Create())
        {
            await connection.ExecuteAsync(SqlDialect.Format(provider, "DELETE FROM [AppSettings]"));
        }

        return new AppSettingStore(factory);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 保存して読み_置き換え_消せる(DatabaseProvider provider)
    {
        var store = await SetUpAsync(provider);
        var admin = Guid.NewGuid().ToString("D");

        await store.SaveAsync("CALENDARFEED_FEED_CACHESECONDS", "60", false, admin, Now);
        await store.SaveAsync("CALENDARFEED_MAIL_SMTP_PASSWORD", "ciphertext-日本語", true, null, Now.AddMinutes(1));

        var all = (await store.ListAsync()).ToDictionary(r => r.SettingKey);
        Assert.Equal(2, all.Count);
        Assert.Equal("60", all["CALENDARFEED_FEED_CACHESECONDS"].Value);
        Assert.False(all["CALENDARFEED_FEED_CACHESECONDS"].IsSecret);
        Assert.Equal(admin, all["CALENDARFEED_FEED_CACHESECONDS"].UpdatedByAdminUserId);
        Assert.Equal(Now, all["CALENDARFEED_FEED_CACHESECONDS"].UpdatedAt);
        Assert.True(all["CALENDARFEED_MAIL_SMTP_PASSWORD"].IsSecret);
        Assert.Equal("ciphertext-日本語", all["CALENDARFEED_MAIL_SMTP_PASSWORD"].Value);
        Assert.Null(all["CALENDARFEED_MAIL_SMTP_PASSWORD"].UpdatedByAdminUserId);

        // 置き換え
        await store.SaveAsync("CALENDARFEED_FEED_CACHESECONDS", "120", false, admin, Now.AddHours(1));
        var replaced = (await store.ListAsync()).Single(r => r.SettingKey == "CALENDARFEED_FEED_CACHESECONDS");
        Assert.Equal("120", replaced.Value);
        Assert.Equal(Now.AddHours(1), replaced.UpdatedAt);
        Assert.Equal(2, (await store.ListAsync()).Count);

        // 消す（無い設定を消しても落ちない）
        await store.DeleteAsync("CALENDARFEED_FEED_CACHESECONDS");
        await store.DeleteAsync("NO_SUCH_KEY");
        Assert.Equal(["CALENDARFEED_MAIL_SMTP_PASSWORD"], (await store.ListAsync()).Select(r => r.SettingKey));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 長い値と空の値を保存できる(DatabaseProvider provider)
    {
        var store = await SetUpAsync(provider);
        var longValue = new string('あ', 5000);

        await store.SaveAsync("A", longValue, false, null, Now);
        await store.SaveAsync("B", string.Empty, false, null, Now);

        var all = (await store.ListAsync()).ToDictionary(r => r.SettingKey);
        Assert.Equal(longValue, all["A"].Value);
        Assert.Equal(string.Empty, all["B"].Value);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同時に同じ設定を保存しても1行だけが残る(DatabaseProvider provider)
    {
        var store = await SetUpAsync(provider);

        // 「消して入れる」を同時に走らせても、主キーの衝突で落ちずに 1 行に収まる（落ちる RDBMS があれば、ここで分かる）
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try
            {
                await store.SaveAsync("RACE", i.ToString(), false, null, Now);
                return true;
            }
            catch (System.Data.Common.DbException)
            {
                return false;
            }
        }));

        Assert.Single(await store.ListAsync());
        Assert.Contains(results, r => r);
    }
}
