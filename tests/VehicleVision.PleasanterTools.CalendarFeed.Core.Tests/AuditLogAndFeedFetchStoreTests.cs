using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>監査ログ・フィードの最後の取得結果の保管（4 種の RDBMS）。件数の上限（方言差）もここで実機に当てる。</summary>
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

    private async Task<(AuditLogStore Audit, FeedStore Feeds)> SetUpAsync(DatabaseProvider provider)
    {
        var connectionString = provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!;
        var factory = new DbConnectionFactory(provider, connectionString);
        TestDatabases.Migrate(provider, connectionString);
        await using (var connection = factory.Create())
        {
            foreach (var table in new[] { "AuditLogs", "Feeds" })
            {
                await connection.ExecuteAsync(SqlDialect.Format(provider, $"DELETE FROM [{table}]"));
            }
        }

        return (new AuditLogStore(factory), new FeedStore(factory, new SecretProtector(TestKey)));
    }

    // ---- 監査ログ --------------------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task 監査ログは新しい順に読め_絞り込み_件数の上限_ページ送り_掃除ができる(DatabaseProvider provider)
    {
        var (audit, _) = await SetUpAsync(provider);
        for (var i = 0; i < 12; i++)
        {
            await audit.WriteAsync(new AuditEntry(
                Now.AddMinutes(i), i % 3 == 0 ? AuditActions.LoginFailed : AuditActions.LoginSucceeded, i % 3 != 0,
                AdminUserId: i % 2 == 0 ? "user-a" : "user-b", LoginId: $"id{i}", Detail: new string('d', 900), IpAddress: "198.51.100.7"));
        }

        var all = await audit.ListAsync(new AuditLogQuery { Limit = 500 });
        Assert.Equal(12, all.Count);
        Assert.Equal("id11", all[0].LoginId);
        Assert.Equal(500, all[0].Detail!.Length);
        Assert.Equal(Now.AddMinutes(11), all[0].OccurredAt);

        Assert.Equal(5, (await audit.ListAsync(new AuditLogQuery { Limit = 5 })).Count);
        Assert.All(await audit.ListAsync(new AuditLogQuery { FailedOnly = true }), e => Assert.False(e.Succeeded));
        Assert.Equal(4, (await audit.ListAsync(new AuditLogQuery { FailedOnly = true })).Count);
        Assert.All(await audit.ListAsync(new AuditLogQuery { AdminUserId = "user-a" }), e => Assert.Equal("user-a", e.AdminUserId));
        Assert.Equal(8, (await audit.ListAsync(new AuditLogQuery { Action = AuditActions.LoginSucceeded })).Count);

        // ページ送り: 前のページまでに読んだ件数だけ読み飛ばす（時刻で区切ると、同じ秒の記録を取りこぼす）
        var page1 = await audit.ListAsync(new AuditLogQuery { Limit = 5 });
        var page2 = await audit.ListAsync(new AuditLogQuery { Limit = 5, Offset = page1.Count });
        Assert.Equal(["id6", "id5", "id4", "id3", "id2"], page2.Select(e => e.LoginId));

        // 掃除: 指定より古いものだけ
        Assert.Equal(4, await audit.DeleteOlderThanAsync(Now.AddMinutes(4)));
        Assert.Equal(8, (await audit.ListAsync(new AuditLogQuery { Limit = 500 })).Count);
    }

    // ---- フィードの最後の取得結果 ------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task フィードの最後の取得結果を上書きで残し_一覧が返す(DatabaseProvider provider)
    {
        var (_, feeds) = await SetUpAsync(provider);
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
