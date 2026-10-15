using System.Data;
using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>
/// マイグレーションの同時実行を、DB の排他ロックで 1 台に絞る（Issue #49）。
/// </summary>
/// <remarks>
/// <para>
/// 複数台を同時に起動すると、全台が未適用のマイグレーションを同時に当てようとして、表の二重作成などで落ちる。
/// 当てる間だけ、DB のセッション単位の排他ロックを持つ。**後から来た台は、先の台が当て終わるまで待ち、当て終わった後は何もしない**
/// （適用済みの履歴を見るので、同じマイグレーションを二度は当てない）。
/// </para>
/// <list type="bullet">
/// <item>SQL Server: <c>sp_getapplock</c>（セッション単位）</item>
/// <item>PostgreSQL: <c>pg_advisory_lock</c></item>
/// <item>MySQL: <c>GET_LOCK</c></item>
/// <item>SQLite: 1 つのファイルを複数のホストで共有しない前提なので、ロックしない（ファイルのロックが書き込みを直列にする）</item>
/// </list>
/// <para>ロックは、持っている接続を閉じれば解放される（落ちた台のロックが残り続けない）。</para>
/// </remarks>
public sealed class MigrationLock : IDisposable
{
    /// <summary>ロックの名前（アプリケーションごとに固有。ほかのアプリと同じ DB サーバを共有しても衝突しない）。</summary>
    public const string Name = "calendarfeed-migration";

    /// <summary>PostgreSQL のアドバイザリロックの鍵（名前の代わりの固定の整数）。</summary>
    private const long PostgresKey = 7_240_411_049;

    private readonly DatabaseProvider _provider;
    private DbConnection? _connection;

    private MigrationLock(DatabaseProvider provider, DbConnection? connection)
    {
        _provider = provider;
        _connection = connection;
    }

    /// <summary>ロックを取る。取れるまで待ち、<paramref name="timeout"/> を過ぎたら <see cref="TimeoutException"/>。</summary>
    public static MigrationLock Acquire(DatabaseProvider provider, string connectionString, TimeSpan timeout)
    {
        if (provider is DatabaseProvider.Sqlite)
        {
            return new MigrationLock(provider, null);
        }

        var connection = new DbConnectionFactory(provider, connectionString).Create();
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            var seconds = (int)Math.Ceiling(timeout.TotalSeconds);
            var acquired = provider switch
            {
                DatabaseProvider.SqlServer => AcquireSqlServer(connection, seconds),
                DatabaseProvider.PostgreSql => AcquirePostgres(connection, seconds),
                DatabaseProvider.MySql => connection.ExecuteScalar<int?>("SELECT GET_LOCK(@Name, @Seconds)", new { Name, Seconds = seconds }) == 1,
                _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
            };

            if (!acquired)
            {
                throw new TimeoutException($"マイグレーションのロックを {timeout.TotalSeconds:0} 秒待っても取れませんでした。別の台が当てている最中です。");
            }

            return new MigrationLock(provider, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool AcquireSqlServer(DbConnection connection, int seconds)
    {
        // 戻り値 0 以上が成功（0 = 取れた、1 = 待って取れた）。負は失敗（タイムアウトなど）
        var parameters = new DynamicParameters();
        parameters.Add("Resource", Name);
        parameters.Add("Timeout", seconds * 1000);
        parameters.Add("Result", dbType: DbType.Int32, direction: ParameterDirection.Output);
        connection.Execute(
            "EXEC @Result = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @Timeout",
            parameters);
        return parameters.Get<int>("Result") >= 0;
    }

    private static bool AcquirePostgres(DbConnection connection, int seconds)
    {
        // pg_advisory_lock は待ち続ける。待ちの上限は lock_timeout で掛ける
        connection.Execute($"SET lock_timeout = {seconds * 1000}");
        try
        {
            connection.Execute("SELECT pg_advisory_lock(@Key)", new { Key = PostgresKey });
            return true;
        }
        catch (DbException ex) when (ex.SqlState == "55P03")
        {
            // lock_not_available
            return false;
        }
    }

    /// <summary>
    /// ロックを手放す。<b>接続プールへ戻す前に、明示的に解放する</b>（プールされた接続はセッションが生き続けるので、
    /// 閉じるだけではロックが残る恐れがある）。
    /// </summary>
    public void Dispose()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            switch (_provider)
            {
                case DatabaseProvider.SqlServer:
                    _connection.Execute("EXEC sp_releaseapplock @Resource = @Name, @LockOwner = 'Session'", new { Name });
                    break;
                case DatabaseProvider.PostgreSql:
                    _connection.Execute("SELECT pg_advisory_unlock(@Key)", new { Key = PostgresKey });
                    _connection.Execute("RESET lock_timeout");
                    break;
                case DatabaseProvider.MySql:
                    _connection.Execute("SELECT RELEASE_LOCK(@Name)", new { Name });
                    break;
            }
        }
        catch (DbException)
        {
            // 解放に失敗しても、接続を閉じれば（プールを使わなければ）DB が解放する。次の台は待ち切る
        }
        finally
        {
            _connection.Dispose();
            _connection = null;
        }
    }
}
