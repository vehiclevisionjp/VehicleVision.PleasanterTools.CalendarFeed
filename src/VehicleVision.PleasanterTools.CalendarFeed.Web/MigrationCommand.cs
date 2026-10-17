using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web;

/// <summary>コマンドラインからマイグレーションを当てる。</summary>
/// <remarks>
/// <para>
/// 自動適用を無効にした導入先や、AKS の Helm フック（Pod の起動前に 1 回だけ当てる Job）のため、
/// 当てる口をアプリ本体と同じ実行ファイルに持たせる。別の道具にすると接続文字列の読み方が二重になり、
/// 片方だけ直す事故が起きる（Questionnaire の <c>MigrationCommand</c> に倣った）。
/// </para>
/// <para>
/// 当てる: <c>dotnet run --project src/…Web -- --migrate</c>（DB の起動を待つなら <c>--wait-for-db=90</c>）。<br/>
/// <b>公開済みのイメージからも同じことができる</b>（<c>dotnet …Web.dll --migrate</c>）。
/// </para>
/// <para>
/// <b>コンソールへ出す文字列は英語で書くこと。</b>Azure の Kudu の Debug console では日本語が化け、
/// 「当たった」のか「当たっていない」のかを運用者が読めない。コメントとドキュメントは日本語のままでよい。
/// </para>
/// </remarks>
public static class MigrationCommand
{
    /// <summary>コマンドラインの合図。</summary>
    public const string ApplyArgument = "--migrate";

    /// <summary>この起動がマイグレーションのためのものか。</summary>
    public static bool IsRequested(string[] args) => args.Contains(ApplyArgument, StringComparer.Ordinal);

    /// <summary>当てる。</summary>
    /// <returns>終了コード。0 なら「当たっている」、2 なら DB に繋がらなかった。</returns>
    public static async Task<int> RunAsync(
        DatabaseProvider provider,
        string connectionString,
        string[] args,
        TimeSpan? lockTimeout = null,
        CancellationToken cancellationToken = default)
    {
        // **DB より先にこちらが動き出すことがある**（コンテナで一緒に立ち上げたとき）。
        // 既定は待たない。設定の誤りを起動の遅さで隠さないため
        var wait = WaitFor(args);
        if (wait > TimeSpan.Zero)
        {
            Console.WriteLine($"Waiting for the database (up to {wait.TotalSeconds:0}s)...");
            var failure = await DatabaseMigrator
                .WaitForDatabaseAsync(provider, connectionString, wait, cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                await Console.Error.WriteLineAsync(
                    $"Database not reachable after {wait.TotalSeconds:0}s: {failure.Message}")
                    .ConfigureAwait(false);
                return 2;
            }
        }

        // 複数台が同時に当てても 1 台だけが当て、残りは待つ（DB の排他ロック）。当て済みなら何もしない
        DatabaseMigrator.Migrate(provider, connectionString, lockTimeout ?? DatabaseMigrator.LockTimeout);
        Console.WriteLine($"All migrations are applied ({provider}).");
        return 0;
    }

    /// <summary><c>--wait-for-db=90</c> の秒数。無ければ待たない。</summary>
    private static TimeSpan WaitFor(string[] args)
    {
        const string prefix = "--wait-for-db=";

        var argument = args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));

        return argument is not null && int.TryParse(argument[prefix.Length..], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
    }
}
