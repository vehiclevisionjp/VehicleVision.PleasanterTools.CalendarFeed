using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>マイグレーションを当てる。</summary>
/// <remarks>
/// <para>
/// **起動時に自動で当てる**（無効にして手で当てることもできる）。履歴は FluentMigrator の <c>VersionInfo</c> 表に残る。
/// </para>
/// <para>
/// ⚠️ **複数インスタンスの同時起動の排他は無い。** 単一プロセスが既定の前提（方針書 8 章 #5）。
/// 複数台にするときは、先に 1 台だけで当ててから増やす。
/// </para>
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>未適用のマイグレーションをすべて当てる。</summary>
    public static void Migrate(DatabaseProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner =>
            {
                _ = provider switch
                {
                    DatabaseProvider.Sqlite => runner.AddSQLite(),
                    DatabaseProvider.SqlServer => runner.AddSqlServer(),
                    DatabaseProvider.PostgreSql => runner.AddPostgres(),
                    DatabaseProvider.MySql => runner.AddMySql5(),
                    _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
                };

                runner.WithGlobalConnectionString(connectionString)
                    .ScanIn(typeof(DatabaseMigrator).Assembly).For.Migrations();
            })
            .BuildServiceProvider(validateScopes: false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}
