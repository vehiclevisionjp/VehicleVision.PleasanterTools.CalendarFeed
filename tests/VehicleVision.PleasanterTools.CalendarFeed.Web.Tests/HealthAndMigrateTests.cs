using System.Net;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>AKS のプローブ（<c>/healthz</c> <c>/ready</c>）と、<c>--migrate</c> の口（Issue #78）。</summary>
public sealed class HealthAndMigrateTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task healthz_は認証なしで200を返す()
    {
        var response = await _app.Client().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ready_は_DBに繋がれば200を返す()
    {
        var response = await _app.Client().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void migrate_の合図を見分ける()
    {
        Assert.True(MigrationCommand.IsRequested(["--migrate", "--wait-for-db=30"]));
        Assert.False(MigrationCommand.IsRequested(["--generate-secret-key"]));
        Assert.False(MigrationCommand.IsRequested([]));
    }

    [Fact]
    public async Task migrate_は表を作り_もう一度やっても何も壊さない()
    {
        var file = Path.Combine(Path.GetTempPath(), $"calendarfeed-migrate-{Guid.NewGuid():N}.db");
        var connection = $"Data Source={file}";
        try
        {
            Assert.Equal(0, await MigrationCommand.RunAsync(DatabaseProvider.Sqlite, connection, ["--migrate"]));
            Assert.Equal(0, await MigrationCommand.RunAsync(DatabaseProvider.Sqlite, connection, ["--migrate"]));

            using var db = new DbConnectionFactory(DatabaseProvider.Sqlite, connection).Create();
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Feeds";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task migrate_は旧の表を落とし_Core_の共有表とFeedsだけを残す()
    {
        var file = Path.Combine(Path.GetTempPath(), $"calendarfeed-migrate-{Guid.NewGuid():N}.db");
        var connection = $"Data Source={file}";
        try
        {
            // 旧表を作る M0003〜M0012 が先に走り、M0016 が落とす。2 回目は何もしない
            Assert.Equal(0, await MigrationCommand.RunAsync(DatabaseProvider.Sqlite, connection, ["--migrate"]));
            Assert.Equal(0, await MigrationCommand.RunAsync(DatabaseProvider.Sqlite, connection, ["--migrate"]));

            using var db = new DbConnectionFactory(DatabaseProvider.Sqlite, connection).Create();
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            var tables = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            string[] legacy =
            [
                "AdminUsers", "AdminSetup", "AdminSessions", "AdminRecoveryCodes", "AuditLogs",
                "AdminInvitations", "MailOutbox", "AppSettings", "RateLimits",
            ];
            string[] shared =
            [
                "Core_AdminUsers", "Core_AdminSetup", "Core_AdminSessions", "Core_AdminRecoveryCodes", "Core_AuditLogs",
                "Core_AdminInvitations", "Core_MailOutbox", "Core_AppSettings", "Core_RateLimits",
            ];
            Assert.All(legacy, name => Assert.DoesNotContain(name, tables));
            Assert.All(shared, name => Assert.Contains(name, tables));
            Assert.Contains("Feeds", tables);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task migrate_は繋がらないDBを待ちきれなければ_終了コード2()
    {
        // 到達できない接続先（SQL Server の既定ポートが閉じた 127.0.0.1）。1 秒だけ待つ
        const string unreachable = "Server=127.0.0.1,1;Database=x;User Id=sa;Password=x;Connect Timeout=1;TrustServerCertificate=True";

        var code = await MigrationCommand.RunAsync(DatabaseProvider.SqlServer, unreachable, ["--migrate", "--wait-for-db=1"]);

        Assert.Equal(2, code);
    }
}
