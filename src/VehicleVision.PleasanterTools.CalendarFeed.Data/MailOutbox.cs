using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>送信待ちの状態。<b>DB へ整数で入る。値を入れ替えない。</b></summary>
public enum MailStatus
{
    /// <summary>送信待ち（再送待ちを含む）。</summary>
    Pending = 0,

    /// <summary>送信ワーカーが確保して送っている最中。</summary>
    Sending = 1,

    /// <summary>再送しても送れない。<b>人が見る必要がある。</b></summary>
    DeadLetter = 2,
}

/// <summary>確保した送信待ち 1 通。</summary>
/// <param name="MailId">識別子。</param>
/// <param name="Kind">種類（<see cref="MailKind"/> の値）。</param>
/// <param name="PayloadProtected">宛先・件名・本文を 1 つにまとめて暗号化したもの。</param>
/// <param name="RetryCount">ここまでの再送の回数。</param>
public sealed record PendingMail(string MailId, int Kind, string PayloadProtected, int RetryCount);

/// <summary>送信待ちの件数。管理画面・監視に出す。</summary>
public sealed record MailOutboxStatus(int PendingCount, DateTimeOffset? OldestPendingAt, int DeadLetterCount);

/// <summary>メールの種類。</summary>
public enum MailKind
{
    /// <summary>管理者の招待。<b>本文の URL が、それだけで管理者になれる値。</b></summary>
    AdminInvitation = 2,

    /// <summary>メール設定の試し送信。</summary>
    SettingsTest = 3,
}

/// <summary>メールの送信待ち（アウトボックス）。</summary>
/// <remarks>
/// <para>
/// **宛先・件名・本文は 1 つにまとめて暗号化して置く。** 列を分けると、暗号化の掛け忘れが列ごとに起き得る。
/// **送れたら行ごと消す**（残るのは、届いていない間だけ）。
/// </para>
/// <para>
/// 確保は 1 文の UPDATE で原子的に行う（複数の送信ワーカーが同じ 1 通を取らない）。
/// 確保したまま落ちても、期限（<c>LockedUntil</c>）が切れれば解放される。メールは失われない。
/// </para>
/// </remarks>
public interface IMailOutbox
{
    /// <summary>積む。<b>同じ識別子がすでにあれば何もせず <c>false</c></b>（二重に送らない側へ倒す）。</summary>
    Task<bool> EnqueueAsync(
        string mailId,
        int kind,
        string payloadProtected,
        CancellationToken cancellationToken = default);

    /// <summary>送れる 1 通を確保する。無ければ <c>null</c>。</summary>
    Task<PendingMail?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default);

    /// <summary>送れたので消す。<b>「念のため」残さない</b>（宛先と本文を持ち続けない）。</summary>
    Task CompleteAsync(string mailId, CancellationToken cancellationToken = default);

    /// <summary>再送待ちへ戻す。</summary>
    Task RescheduleAsync(
        string mailId,
        DateTimeOffset nextAttemptAt,
        string? error,
        CancellationToken cancellationToken = default);

    /// <summary>再送しても送れないので分離する。</summary>
    Task DeadLetterAsync(string mailId, string error, CancellationToken cancellationToken = default);

    /// <summary>確保したまま期限が切れた行を戻す。解放した件数を返す。</summary>
    Task<int> ReleaseExpiredLocksAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>件数を返す。</summary>
    Task<MailOutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装（SQLite / SQL Server / PostgreSQL / MySQL）。</summary>
public sealed class MailOutbox(IDbConnectionFactory connections, TimeProvider time) : IMailOutbox
{
    private DatabaseProvider Provider => connections.Provider;

    private string Sql(string sql) => SqlDialect.Format(Provider, sql);

    public async Task<bool> EnqueueAsync(
        string mailId,
        int kind,
        string payloadProtected,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadProtected);

        var now = DbTime.Format(time.GetUtcNow());
        await using var connection = connections.Create();
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [MailOutbox] ([MailId], [Kind], [PayloadProtected], [Status], [RetryCount], "
                    + "[NextAttemptAt], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@MailId, @Kind, @PayloadProtected, @PendingStatus, 0, @Now, @Now, @Now)"),
                new { MailId = mailId, Kind = kind, PayloadProtected = payloadProtected, PendingStatus = (int)MailStatus.Pending, Now = now },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return affected > 0;
        }
        catch (DbException)
        {
            // 主キーの衝突＝すでに積んである。**影響行数では見分けられない**（MySQL は一致行数を返す）ので、衝突そのもので判定する
            return false;
        }
    }

    public async Task<PendingMail?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();

        // **確保ごとに一意な値を入れる**（過去の確保と一致させない）
        var owner = lockedBy.Length > 24 ? lockedBy[..24] : lockedBy;
        var parameters = new
        {
            SendingStatus = (int)MailStatus.Sending,
            PendingStatus = (int)MailStatus.Pending,
            LockedBy = $"{owner}:{Guid.NewGuid():N}",
            LockedUntil = DbTime.Format(now + lockDuration),
            Now = DbTime.Format(now),
        };

        await using var connection = connections.Create();
        if (SqlDialect.SupportsReturning(Provider))
        {
            return (await connection.QueryFirstOrDefaultAsync<ClaimedRow>(new CommandDefinition(
                SqlDialect.ClaimPendingMail(Provider), parameters, cancellationToken: cancellationToken)).ConfigureAwait(false))?.ToPending();
        }

        // MySQL は RETURNING が無いので、確保してから読み直す
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            SqlDialect.ClaimPendingMail(Provider), parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 0
            ? null
            : (await connection.QueryFirstOrDefaultAsync<ClaimedRow>(new CommandDefinition(
                SqlDialect.ReadClaimedMailForMySql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false))?.ToPending();
    }

    public async Task CompleteAsync(string mailId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [MailOutbox] WHERE [MailId] = @MailId"), new { MailId = mailId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RescheduleAsync(
        string mailId,
        DateTimeOffset nextAttemptAt,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [MailOutbox] SET [Status] = @PendingStatus, [RetryCount] = [RetryCount] + 1, "
                + "[NextAttemptAt] = @NextAttemptAt, [LastError] = @Error, [LockedBy] = NULL, [LockedUntil] = NULL, "
                + "[UpdatedAt] = @Now WHERE [MailId] = @MailId"),
            new
            {
                MailId = mailId,
                PendingStatus = (int)MailStatus.Pending,
                NextAttemptAt = DbTime.Format(nextAttemptAt),
                Error = Truncate(error),
                Now = DbTime.Format(time.GetUtcNow()),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(string mailId, string error, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [MailOutbox] SET [Status] = @DeadLetterStatus, [LastError] = @Error, [LockedBy] = NULL, "
                + "[LockedUntil] = NULL, [UpdatedAt] = @Now WHERE [MailId] = @MailId"),
            new
            {
                MailId = mailId,
                DeadLetterStatus = (int)MailStatus.DeadLetter,
                Error = Truncate(error),
                Now = DbTime.Format(time.GetUtcNow()),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> ReleaseExpiredLocksAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [MailOutbox] SET [Status] = @PendingStatus, [LockedBy] = NULL, [LockedUntil] = NULL, [UpdatedAt] = @Now "
                + "WHERE [Status] = @SendingStatus AND [LockedUntil] < @Now"),
            new { PendingStatus = (int)MailStatus.Pending, SendingStatus = (int)MailStatus.Sending, Now = DbTime.Format(now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<MailOutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QueryFirstAsync<StatusRow>(new CommandDefinition(
            Sql("SELECT COUNT(CASE WHEN [Status] <> @DeadLetterStatus THEN 1 END) AS [PendingCount], "
                + "MIN(CASE WHEN [Status] <> @DeadLetterStatus THEN [CreatedAt] END) AS [OldestPendingAt], "
                + "COUNT(CASE WHEN [Status] = @DeadLetterStatus THEN 1 END) AS [DeadLetterCount] FROM [MailOutbox]"),
            new { DeadLetterStatus = (int)MailStatus.DeadLetter },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new MailOutboxStatus((int)row.PendingCount, DbTime.Parse(row.OldestPendingAt), (int)row.DeadLetterCount);
    }

    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

    /// <summary>確保した行。整数の列は RDBMS によって 64 ビットで返るので、受けてから変換する。</summary>
    private sealed class ClaimedRow
    {
        public string MailId { get; set; } = "";

        public long Kind { get; set; }

        public string PayloadProtected { get; set; } = "";

        public long RetryCount { get; set; }

        public PendingMail ToPending() => new(MailId, (int)Kind, PayloadProtected, (int)RetryCount);
    }

    private sealed class StatusRow
    {
        public long PendingCount { get; set; }

        public string? OldestPendingAt { get; set; }

        public long DeadLetterCount { get; set; }
    }
}
