using System.Reflection;
using System.Security.Cryptography;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using Xunit.Sdk;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>
/// 結合テストの対象 RDBMS。**SQLite は常に流す。** 他は接続文字列の環境変数があるときだけ流す。
/// </summary>
/// <remarks>
/// ⚠️ **環境変数を設定していないと、その RDBMS のテストは何も流れずに緑になる。その緑を「通った」と読まないこと。**
/// CI は <c>CALENDARFEED_REQUIRE_ALL_RDBMS=1</c> を設定し、未設定の RDBMS があれば
/// <see cref="FeedStoreTests.CIでは全RDBMSが設定されている"/> を落とす。
/// </remarks>
public static class TestDatabases
{
    public const string RequireAllKey = "CALENDARFEED_REQUIRE_ALL_RDBMS";

    public static string? ConnectionString(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => null,
        DatabaseProvider.SqlServer => Environment.GetEnvironmentVariable("CALENDARFEED_TEST_SQLSERVER"),
        DatabaseProvider.PostgreSql => Environment.GetEnvironmentVariable("CALENDARFEED_TEST_POSTGRESQL"),
        DatabaseProvider.MySql => Environment.GetEnvironmentVariable("CALENDARFEED_TEST_MYSQL"),
        _ => throw new NotSupportedException(),
    };

    public static bool IsEnabled(DatabaseProvider provider) =>
        provider is DatabaseProvider.Sqlite || !string.IsNullOrWhiteSpace(ConnectionString(provider));
}

/// <summary>設定された RDBMS だけをテストの引数に流す。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnabledDatabasesAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        Enum.GetValues<DatabaseProvider>().Where(TestDatabases.IsEnabled).Select(p => new object[] { p });
}

public sealed class FeedStoreTests : IDisposable
{
    private static readonly string TestKey = SecretProtector.GenerateKey();
    private const string ApiKey = "6ce6c0fd2f4aea3c093c3ebdd7d4ea3250a132b4867d68e7d1abe35c2499664a";

    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-{Guid.NewGuid():N}.db");

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

    /// <summary>RDBMS ごとのストアを用意し、マイグレーションを当てる。</summary>
    private FeedStore StoreFor(DatabaseProvider provider)
    {
        var factory = FactoryFor(provider);
        DatabaseMigrator.Migrate(provider, factory.ConnectionString);
        return new FeedStore(factory, new SecretProtector(TestKey));
    }

    [Fact]
    public void CIでは全RDBMSが設定されている()
    {
        if (Environment.GetEnvironmentVariable(TestDatabases.RequireAllKey) != "1")
        {
            return;
        }

        var missing = Enum.GetValues<DatabaseProvider>().Where(p => !TestDatabases.IsEnabled(p)).ToList();
        Assert.True(missing.Count == 0, $"接続文字列の環境変数が未設定の RDBMS: {string.Join(", ", missing)}");
    }

    [Theory]
    [EnabledDatabases]
    public async Task 作ったフィードをトークンで引ける(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, token) = await store.CreateAsync(
            "共有予定",
            10,
            new FeedMapping { AllDay = AllDayMode.Always, StartColumn = "DateA", EndColumn = "Date001" },
            new Dictionary<string, string> { ["ClassA"] = "x" });

        var found = await store.FindByTokenAsync(token);

        Assert.NotNull(found);
        Assert.Equal(feed.Id, found.Id);
        Assert.Equal("共有予定", found.Name);
        Assert.Equal(10, found.SiteId);
        Assert.Equal(AllDayMode.Always, found.Mapping.AllDay);
        Assert.Equal("Date001", found.Mapping.EndColumn);
        Assert.Equal("x", found.Filters["ClassA"]);
        Assert.False(found.IsRevoked);
        Assert.Equal(feed.CreatedAt.ToUnixTimeSeconds(), found.CreatedAt.ToUnixTimeSeconds());
    }

    [Theory]
    [EnabledDatabases]
    public async Task 日本語の名前と長い本文を保管できる(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var filters = Enumerable.Range(0, 20).ToDictionary(i => $"ClassA{i}", _ => new string('あ', 400));

        var (_, token) = await store.CreateAsync("予定🗓️共有", 1, new FeedMapping(), filters);
        var found = await store.FindByTokenAsync(token);

        Assert.Equal("予定🗓️共有", found!.Name);
        Assert.Equal(20, found.Filters.Count);
        Assert.Equal(new string('あ', 400), found.Filters["ClassA3"]);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 作り直すと旧トークンは使えない(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, oldToken) = await store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());

        var newToken = await store.RotateTokenAsync(feed.Id);

        Assert.NotNull(newToken);
        Assert.Null(await store.FindByTokenAsync(oldToken));
        Assert.NotNull(await store.FindByTokenAsync(newToken));
        Assert.Null(await store.RotateTokenAsync(Guid.NewGuid()));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 失効させると引けず一覧にも出ない(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, token) = await store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());
        Assert.Contains(await store.ListAsync(), f => f.Id == feed.Id);

        Assert.True(await store.RevokeAsync(feed.Id));

        Assert.Null(await store.FindByTokenAsync(token));
        Assert.DoesNotContain(await store.ListAsync(), f => f.Id == feed.Id);
        Assert.False(await store.RevokeAsync(feed.Id));
        Assert.Null(await store.RotateTokenAsync(feed.Id));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 一覧は新しい順(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (first, _) = await store.CreateAsync("1", 1, new FeedMapping(), new Dictionary<string, string>());
        await Task.Delay(20);
        var (second, _) = await store.CreateAsync("2", 1, new FeedMapping(), new Dictionary<string, string>());

        var ids = (await store.ListAsync()).Select(f => f.Id).ToList();

        Assert.True(ids.IndexOf(second.Id) < ids.IndexOf(first.Id));
    }

    [Theory]
    [EnabledDatabases]
    public async Task トークンは保管されずハッシュだけが残る(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, token) = await store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());

        await using var connection = FactoryFor(provider).Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SqlDialect.Format(provider, "SELECT [TokenHash] FROM [Feeds] WHERE [Id] = @Id");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Id";
        parameter.Value = feed.Id.ToString("D");
        command.Parameters.Add(parameter);
        var stored = (string?)await command.ExecuteScalarAsync();

        Assert.Equal(FeedTokens.Hash(token), stored);
        Assert.NotEqual(token, stored);
    }

    [Theory]
    [EnabledDatabases]
    public async Task APIキーは暗号化して保管し復号して取り出せる(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, _) = await store.CreateAsync(
            "a", 1, new FeedMapping(), new Dictionary<string, string>(),
            new FeedAccess(ApiKey, "Asia/Tokyo", "en"));

        Assert.Equal(ApiKey, await store.GetApiKeyAsync(feed.Id));
        var (cipher, hint, tz, language) = await RawAsync(provider, feed.Id);
        Assert.StartsWith("v1:", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, cipher, StringComparison.Ordinal);
        Assert.Equal(ApiKey[^4..], hint);
        Assert.Equal("Asia/Tokyo", tz);
        Assert.Equal("en", language);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 取得と一覧が返す定義にAPIキーは含まれず登録済みと末尾だけが分かる(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, token) = await store.CreateAsync(
            "a", 1, new FeedMapping(), new Dictionary<string, string>(), new FeedAccess(ApiKey));

        var found = await store.FindByTokenAsync(token);
        var listed = (await store.ListAsync()).Single(f => f.Id == feed.Id);

        Assert.True(found!.HasApiKey);
        Assert.Equal(ApiKey[^4..], found.ApiKeyHint);
        Assert.True(listed.HasApiKey);
        Assert.DoesNotContain(ApiKey, found.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [EnabledDatabases]
    public async Task APIキーが無いフィードは未登録として扱う(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, token) = await store.CreateAsync("a", 1, new FeedMapping(), new Dictionary<string, string>());

        Assert.Null(await store.GetApiKeyAsync(feed.Id));
        Assert.False((await store.FindByTokenAsync(token))!.HasApiKey);
    }

    [Theory]
    [EnabledDatabases]
    public async Task APIキーを差し替えられ失効したフィードは差し替えも取り出しもできない(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, _) = await store.CreateAsync(
            "a", 1, new FeedMapping(), new Dictionary<string, string>(), new FeedAccess(ApiKey));

        Assert.True(await store.SetApiKeyAsync(feed.Id, "another-key-0123456789", "UTC"));
        Assert.Equal("another-key-0123456789", await store.GetApiKeyAsync(feed.Id));
        Assert.Equal("UTC", (await store.ListAsync()).Single(f => f.Id == feed.Id).ApiKeyUserTimeZoneId);

        await store.RevokeAsync(feed.Id);
        Assert.False(await store.SetApiKeyAsync(feed.Id, "x", null));
        Assert.Null(await store.GetApiKeyAsync(feed.Id));
        Assert.False(await store.SetApiKeyAsync(Guid.NewGuid(), "x", null));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 暗号文を別のフィードの行へ差し替えても復号できない(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (victim, _) = await store.CreateAsync(
            "victim", 1, new FeedMapping(), new Dictionary<string, string>(), new FeedAccess(ApiKey));
        var (mine, _) = await store.CreateAsync("mine", 1, new FeedMapping(), new Dictionary<string, string>());
        var (cipher, _, _, _) = await RawAsync(provider, victim.Id);

        await using var connection = FactoryFor(provider).Create();
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, "UPDATE [Feeds] SET [ApiKeyCipher] = @Cipher WHERE [Id] = @Id"),
            new { Cipher = cipher, Id = mine.Id.ToString("D") });

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetApiKeyAsync(mine.Id));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 保管時と違う鍵では復号できない(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (feed, _) = await store.CreateAsync(
            "a", 1, new FeedMapping(), new Dictionary<string, string>(), new FeedAccess(ApiKey));
        var otherKey = new FeedStore(FactoryFor(provider), new SecretProtector(SecretProtector.GenerateKey()));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => otherKey.GetApiKeyAsync(feed.Id));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 言語は正規化して保管し対応外は既定へ落とす(DatabaseProvider provider)
    {
        var store = StoreFor(provider);
        var (a, _) = await store.CreateAsync(
            "a", 1, new FeedMapping(), new Dictionary<string, string>(), new FeedAccess(Language: "EN-us"));
        var (b, _) = await store.CreateAsync(
            "b", 1, new FeedMapping(), new Dictionary<string, string>(), new FeedAccess(Language: "fr"));

        var feeds = await store.ListAsync();
        Assert.Equal("en", feeds.Single(f => f.Id == a.Id).Language);
        Assert.Equal("ja", feeds.Single(f => f.Id == b.Id).Language);
    }

    private async Task<(string? Cipher, string? Hint, string? TimeZone, string Language)> RawAsync(
        DatabaseProvider provider, Guid id)
    {
        await using var connection = FactoryFor(provider).Create();
        var row = await connection.QuerySingleAsync<(string? Cipher, string? Hint, string? Tz, string Language)>(
            SqlDialect.Format(
                provider,
                "SELECT [ApiKeyCipher] AS Cipher, [ApiKeyHint] AS Hint, [ApiKeyUserTimeZoneId] AS Tz, [Language] AS Language "
                + "FROM [Feeds] WHERE [Id] = @Id"),
            new { Id = id.ToString("D") });
        return (row.Cipher, row.Hint, row.Tz, row.Language);
    }

    [Fact]
    public void マイグレーションは何度当てても同じ()
    {
        var factory = FactoryFor(DatabaseProvider.Sqlite);

        DatabaseMigrator.Migrate(DatabaseProvider.Sqlite, factory.ConnectionString);
        DatabaseMigrator.Migrate(DatabaseProvider.Sqlite, factory.ConnectionString);
    }
}

public sealed class SqlDialectTests
{
    [Theory]
    [InlineData(DatabaseProvider.SqlServer, "SELECT [Id] FROM [Feeds]")]
    [InlineData(DatabaseProvider.PostgreSql, "SELECT \"Id\" FROM \"Feeds\"")]
    [InlineData(DatabaseProvider.Sqlite, "SELECT \"Id\" FROM \"Feeds\"")]
    [InlineData(DatabaseProvider.MySql, "SELECT `Id` FROM `Feeds`")]
    public void 識別子を引用符へ書き換える(DatabaseProvider provider, string expected) =>
        Assert.Equal(expected, SqlDialect.Format(provider, "SELECT [Id] FROM [Feeds]"));

    [Fact]
    public void 囲んでいない識別子と添字は書き換えない() =>
        Assert.Equal(
            "SELECT Id, a[0] FROM \"Feeds\"",
            SqlDialect.Format(DatabaseProvider.Sqlite, "SELECT Id, a[0] FROM [Feeds]"));

    [Theory]
    [InlineData(null, DatabaseProvider.Sqlite)]
    [InlineData("", DatabaseProvider.Sqlite)]
    [InlineData("postgresql", DatabaseProvider.PostgreSql)]
    [InlineData("SqlServer", DatabaseProvider.SqlServer)]
    [InlineData(" mysql ", DatabaseProvider.MySql)]
    public void providerを読む(string? value, DatabaseProvider expected) =>
        Assert.Equal(expected, DbConnectionFactory.ParseProvider(value));

    [Theory]
    [InlineData("oracle")]
    [InlineData("99")]
    public void 読めないproviderは黙って落とさず止める(string value) =>
        Assert.Throws<InvalidOperationException>(() => DbConnectionFactory.ParseProvider(value));

    [Fact]
    public void SQL_Serverは暗号化を強制する()
    {
        var factory = new DbConnectionFactory(DatabaseProvider.SqlServer, "Server=x;Database=y;Encrypt=False");

        Assert.Contains("Encrypt=True", factory.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SQLite以外は接続文字列が無ければ止める() =>
        Assert.Throws<InvalidOperationException>(() =>
            DbConnectionFactory.ResolveConnectionString(DatabaseProvider.PostgreSql, null, Path.GetTempPath()));
}
