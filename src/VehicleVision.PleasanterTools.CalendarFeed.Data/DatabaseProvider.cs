using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>対応する RDBMS。</summary>
/// <remarks>
/// **原則は Pleasanter が対応する 3 つに揃える**（導入先の Pleasanter が使う RDBMS に合わせられないと、
/// DB サーバを別に立てることになる）。SQLite は簡易セットアップとデバッグ用の例外で、本番運用には使わない。
/// </remarks>
public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
    PostgreSql,
    MySql,
}

/// <summary>接続を作る。</summary>
public interface IDbConnectionFactory
{
    DatabaseProvider Provider { get; }

    /// <summary>接続文字列（マイグレーションの実行に渡す）。</summary>
    string ConnectionString { get; }

    DbConnection Create();
}

/// <summary>設定から接続を作る。</summary>
/// <remarks>
/// **接続文字列は設定ファイルへ直書きしない。** 環境変数か <c>Rds.local.json</c> から読む。
/// </remarks>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    /// <summary>SQLite の既定ファイル名。</summary>
    public const string DefaultSqliteFileName = "calendarfeed.db";

    private const int SqliteBusyTimeoutSeconds = 30;

    public DbConnectionFactory(DatabaseProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Provider = provider;
        ConnectionString = provider switch
        {
            // **暗号化を既定値任せにしない。** ドライバの既定は版で変わり得る。Encrypt=False と書かれていても暗号化する
            // （証明書の確認を緩めたいときは TrustServerCertificate を使う）
            DatabaseProvider.SqlServer => new SqlConnectionStringBuilder(connectionString) { Encrypt = true }
                .ConnectionString,
            DatabaseProvider.Sqlite => new SqliteConnectionStringBuilder(connectionString)
            {
                DefaultTimeout = SqliteBusyTimeoutSeconds,
            }.ConnectionString,
            _ => connectionString,
        };
    }

    public DatabaseProvider Provider { get; }

    public string ConnectionString { get; }

    public DbConnection Create() => Provider switch
    {
        DatabaseProvider.Sqlite => new SqliteConnection(ConnectionString),
        DatabaseProvider.SqlServer => new SqlConnection(ConnectionString),
        DatabaseProvider.PostgreSql => new NpgsqlConnection(ConnectionString),
        DatabaseProvider.MySql => new MySqlConnection(ConnectionString),
        _ => throw new NotSupportedException($"対応していない RDBMS: {Provider}"),
    };

    /// <summary>設定値から provider を読む。未設定は SQLite。読めない値は黙って落とさず止める。</summary>
    public static DatabaseProvider ParseProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DatabaseProvider.Sqlite;
        }

        return Enum.TryParse<DatabaseProvider>(value.Trim(), ignoreCase: true, out var provider)
            && Enum.IsDefined(provider)
            ? provider
            : throw new InvalidOperationException(
                $"CALENDARFEED_DB_PROVIDER の値 '{value}' は対応していません。"
                + $"{string.Join(" / ", Enum.GetNames<DatabaseProvider>())} のいずれかで指定してください。");
    }

    /// <summary>接続文字列を解決する。SQLite だけは App_Data 配下のファイルを既定に持つ。</summary>
    public static string ResolveConnectionString(
        DatabaseProvider provider,
        string? configured,
        string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (provider is not DatabaseProvider.Sqlite)
        {
            throw new InvalidOperationException(
                $"{provider} には接続文字列が要ります（CALENDARFEED_DB_CONNECTIONSTRING）。");
        }

        var appData = Path.Combine(contentRootPath, "App_Data");
        Directory.CreateDirectory(appData);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(appData, DefaultSqliteFileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString;
    }
}
