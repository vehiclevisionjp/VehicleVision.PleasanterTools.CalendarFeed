using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

public sealed class SqliteFeedStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"calendarfeed-{Guid.NewGuid():N}.db");
    private readonly SqliteFeedStore _store;

    public SqliteFeedStoreTests()
    {
        _store = new SqliteFeedStore($"Data Source={_path};Pooling=False");
        _store.EnsureSchemaAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task 作ったフィードをトークンで引ける()
    {
        var (feed, token) = await _store.CreateAsync(
            "共有予定", 10, new FeedMapping { AllDay = true }, new Dictionary<string, string> { ["ClassA"] = "x" });

        var found = await _store.FindByTokenAsync(token);

        Assert.NotNull(found);
        Assert.Equal(feed.Id, found.Id);
        Assert.True(found.Mapping.AllDay);
        Assert.Equal("x", found.Filters["ClassA"]);
    }

    [Fact]
    public async Task トークンは保管されずハッシュだけが残る()
    {
        var (_, token) = await _store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());

        var text = Convert.ToHexStringLower(await File.ReadAllBytesAsync(_path));
        Assert.DoesNotContain(Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(token)), text);
        Assert.Contains(Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(FeedTokens.Hash(token))), text);
    }

    [Fact]
    public async Task 作り直すと旧トークンは使えない()
    {
        var (feed, oldToken) = await _store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());

        var newToken = await _store.RotateTokenAsync(feed.Id);

        Assert.NotNull(newToken);
        Assert.Null(await _store.FindByTokenAsync(oldToken));
        Assert.NotNull(await _store.FindByTokenAsync(newToken));
    }

    [Fact]
    public async Task 失効させると引けず一覧にも出ない()
    {
        var (feed, token) = await _store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());

        Assert.True(await _store.RevokeAsync(feed.Id));

        Assert.Null(await _store.FindByTokenAsync(token));
        Assert.Empty(await _store.ListAsync());
        Assert.False(await _store.RevokeAsync(feed.Id));
    }
}
