using System.Runtime.CompilerServices;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>
/// このアプリの DB の設定。DB の基盤（接続・マイグレーションと排他）は Core.Data にあり、
/// <b>アプリごとに違う値はここで持って、引数で渡す</b>。
/// </summary>
public static class CalendarFeedDatabase
{
    /// <summary>SQLite の既定ファイル名。</summary>
    public const string DefaultSqliteFileName = "calendarfeed.db";

    /// <summary>RDBMS を選ぶ設定名。</summary>
    public const string ProviderSetting = "CALENDARFEED_DB_PROVIDER";

    /// <summary>接続文字列を与える設定名。</summary>
    public const string ConnectionStringSetting = "CALENDARFEED_DB_CONNECTIONSTRING";

    /// <summary>
    /// 起動時のマイグレーションの排他に使う名前。
    /// **アプリごとに別にする。** 同じ DB サーバを複数のアプリが使うとき、名前が同じだと別のアプリのマイグレーションを待たせる。
    /// </summary>
    public const string MigrationLockName = "calendarfeed-migration";

    /// <summary>排他ロックを待つ長さの既定。マイグレーション自体は短いが、大きい DB の変更を待つことがある。</summary>
    public static readonly TimeSpan MigrationLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>このアプリのマイグレーション（M0001〜）を適用する。</summary>
    public static DatabaseMigrator Migrator { get; } =
        new(typeof(CalendarFeedDatabase).Assembly, MigrationLockName);

    /// <summary>
    /// このアセンブリのモデル型（列挙型・record）を、SQLite の型の対応表へ知らせる。
    /// **アセンブリの読み込み時に 1 回だけ呼ぶ**（<c>[ModuleInitializer]</c>）。
    /// </summary>
    /// <remarks>
    /// Core.Data は、知らされたアセンブリの型だけを SQLite 用に登録する。実際の登録は SQLite の接続を作るときだけで、
    /// ほかの RDBMS の挙動は変わらない。
    /// </remarks>
    // CA2255 は「ライブラリでの [ModuleInitializer]」への注意。**ここは再利用するライブラリではなく、このアプリのデータ層**。
    // アセンブリの読み込み時に必ず通る経路が要る（ファクトリやストアを、このクラスを経由せずに作る試験や呼び出しもあるため）
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterModelAssembly() =>
        SqliteTypeHandlers.AddModelAssemblies(typeof(CalendarFeedDatabase).Assembly);

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
                $"{ProviderSetting} の値 '{value}' は対応していません。"
                + $"{string.Join(" / ", Enum.GetNames<DatabaseProvider>())} のいずれかで指定してください。");
    }

    /// <summary>接続文字列を解決する。SQLite だけは App_Data 配下のファイルを既定に持つ。</summary>
    public static string ResolveConnectionString(
        DatabaseProvider provider,
        string? configured,
        string contentRootPath) =>
        DatabaseConnectionString.Resolve(
            provider,
            configured,
            contentRootPath,
            DefaultSqliteFileName,
            ConnectionStringSetting);
}

/// <summary>
/// 解決済みの DB の接続先（RDBMS と接続文字列）。
/// マイグレーションと準備の確認が、接続文字列そのものを要るので、解決した結果を 1 か所で持つ。
/// </summary>
public sealed record DatabaseConnectionInfo(DatabaseProvider Provider, string ConnectionString);
