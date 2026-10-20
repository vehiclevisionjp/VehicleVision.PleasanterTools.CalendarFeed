using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>フィードの更新の保管（4 種の RDBMS。Issue #40）。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class FeedUpdateStoreTests : IDisposable
{
    private static readonly string TestKey = SecretProtector.GenerateKey();
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-feedupd-{Guid.NewGuid():N}.db");

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
    public async Task 定義を置き換え_トークンとAPIキーは変えない(DatabaseProvider provider)
    {
        var store = await SetUpAsync(provider);
        var (feed, token) = await store.CreateAsync(
            "元の名前", 10, new FeedMapping(), new Dictionary<string, string>(),
            new FeedAccess("0123456789abcdef0123456789abcdef", "UTC", "ja"));

        var update = new FeedUpdate(
            "新しい名前",
            20,
            new FeedMapping { SummaryColumn = "Title", LocationColumn = "ClassA" },
            new Dictionary<string, string> { ["Status"] = "[\"100\"]" },
            "en",
            null);
        Assert.True(await store.UpdateAsync(feed.Id, update));

        var found = (await store.FindByTokenAsync(token))!;
        Assert.Equal("新しい名前", found.Name);
        Assert.Equal(20, found.SiteId);
        Assert.Equal("ClassA", found.Mapping.LocationColumn);
        Assert.Equal("[\"100\"]", found.Filters["Status"]);
        Assert.Equal("en", found.Language);
        Assert.Null(found.ApiKeyUserTimeZoneId);
        Assert.Equal("0123456789abcdef0123456789abcdef", await store.GetApiKeyAsync(feed.Id));
        Assert.Equal("新しい名前", (await store.FindByIdAsync(feed.Id))!.Name);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 無いフィードと失効済みは更新できない(DatabaseProvider provider)
    {
        var store = await SetUpAsync(provider);
        var (feed, _) = await store.CreateAsync("名前", 10, new FeedMapping(), new Dictionary<string, string>());
        var update = new FeedUpdate("x", 1, new FeedMapping(), new Dictionary<string, string>(), "ja", null);

        Assert.False(await store.UpdateAsync(Guid.NewGuid(), update));
        Assert.True(await store.RevokeAsync(feed.Id));
        Assert.False(await store.UpdateAsync(feed.Id, update));
        Assert.Null(await store.FindByIdAsync(feed.Id));
    }
}
