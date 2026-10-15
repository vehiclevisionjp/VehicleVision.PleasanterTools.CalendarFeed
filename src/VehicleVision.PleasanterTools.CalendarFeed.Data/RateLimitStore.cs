using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>複数台で共有するレート制限の数え（固定窓）の保管先（Issue #49）。</summary>
public interface IRateLimitStore
{
    /// <summary>この窓での数えを 1 増やし、増やした後の数を返す（窓が変わっていたら 1 に戻す）。</summary>
    Task<int> HitAsync(string key, long window, CancellationToken cancellationToken = default);

    /// <summary>古い窓の行を消す。消した件数を返す。</summary>
    Task<int> PurgeAsync(long beforeWindow, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装（SQLite / SQL Server / PostgreSQL / MySQL）。</summary>
/// <remarks>
/// <b>読んでから足さず、1 文の UPDATE で数える</b>（複数台が同時に数えても、数えこぼさない）。
/// 行が無いときだけ INSERT し、同時に別の台が先に作っていたら（一意制約の違反）、UPDATE をやり直す。
/// </remarks>
public sealed class RateLimitStore(IDbConnectionFactory connections) : IRateLimitStore
{
    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    public async Task<int> HitAsync(string key, long window, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 同じ窓の行を 1 増やし、**同じトランザクションの中で**数を読む（行のロックを持ったまま読むので、自分が増やした値が返る。
            // 分けると、同時に増やした別の台の値を読んで、10 回目なのに 11 回目と数えてしまう）
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await connection.ExecuteAsync(new CommandDefinition(
                        Sql("UPDATE [RateLimits] SET [HitCount] = [HitCount] + 1 WHERE [LimitKey] = @Key AND [Window] = @Window"),
                        new { Key = key, Window = window }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) > 0)
                {
                    var count = await connection.QuerySingleAsync<int>(new CommandDefinition(
                        Sql("SELECT [HitCount] FROM [RateLimits] WHERE [LimitKey] = @Key"),
                        new { Key = key }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return count;
                }
            }

            // 古い窓の行を、新しい窓の 1 回目へ戻す
            if (await connection.ExecuteAsync(new CommandDefinition(
                    Sql("UPDATE [RateLimits] SET [Window] = @Window, [HitCount] = 1 WHERE [LimitKey] = @Key AND [Window] < @Window"),
                    new { Key = key, Window = window }, cancellationToken: cancellationToken)).ConfigureAwait(false) > 0)
            {
                return 1;
            }

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    Sql("INSERT INTO [RateLimits] ([LimitKey], [Window], [HitCount]) VALUES (@Key, @Window, 1)"),
                    new { Key = key, Window = window }, cancellationToken: cancellationToken)).ConfigureAwait(false);
                return 1;
            }
            catch (DbException)
            {
                // 同時に別の台が先に作った。UPDATE からやり直す
            }
        }

        // 3 回やり直しても決まらないのは異常（DB の不調など）。呼び出し側が代わりの数え方へ回す
        throw new InvalidOperationException("レート制限の数えを更新できませんでした。");
    }

    public async Task<int> PurgeAsync(long beforeWindow, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [RateLimits] WHERE [Window] < @Window"),
            new { Window = beforeWindow }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
