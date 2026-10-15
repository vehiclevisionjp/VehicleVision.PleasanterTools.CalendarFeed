using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>マイグレーションを当てる。</summary>
/// <remarks>
/// <para>
/// **起動時に自動で当てる**（無効にして手で当てることもできる）。履歴は FluentMigrator の <c>VersionInfo</c> 表に残る。
/// </para>
/// <para>
/// **複数インスタンスを同時に起動しても、当てるのは 1 台だけ**（DB の排他ロック。<see cref="MigrationLock"/>）。
/// 後から来た台は、先の台が当て終わるまで待ち、当て終わった後は何もしない。
/// </para>
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>未適用のマイグレーションをすべて当てる。</summary>
    public static void Migrate(DatabaseProvider provider, string connectionString) =>
        Migrate(provider, connectionString, LockTimeout);

    /// <summary>ロックを待つ長さの既定。マイグレーション自体は短いが、大きい DB の変更を待つことがあるので長めに取る。</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>未適用のマイグレーションをすべて当てる。当てる間は排他ロックを持ち、<paramref name="lockTimeout"/> 待っても取れなければ <see cref="TimeoutException"/>。</summary>
    public static void Migrate(DatabaseProvider provider, string connectionString, TimeSpan lockTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        using var migrationLock = MigrationLock.Acquire(provider, connectionString, lockTimeout);

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
