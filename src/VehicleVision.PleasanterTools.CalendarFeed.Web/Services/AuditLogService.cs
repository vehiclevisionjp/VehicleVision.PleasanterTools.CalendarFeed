using VehicleVision.PleasanterTools.CalendarFeed.Admin;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>管理操作を記録する窓口。</summary>
/// <remarks>
/// <para>
/// **記録に失敗しても、操作そのものは止めない**（記録の障害でログインできなくなると、運用が詰む）。
/// 失敗はアプリのログへ出す（そこには理由だけ。資格情報は入らない）。
/// </para>
/// <para>
/// **渡せるのは「何を・誰が・どの対象か」だけ。** 要求本文は受け取らないので、パスワードなどが入りようがない。
/// </para>
/// </remarks>
public sealed class AuditLogService(IAuditLogStore store, TimeProvider time, ILogger<AuditLogService> logger)
{
    /// <summary>記録する。</summary>
    public async Task WriteAsync(
        HttpContext http,
        string action,
        bool succeeded,
        AdminUser? actor = null,
        string? loginId = null,
        string? targetType = null,
        string? targetId = null,
        string? detail = null)
    {
        try
        {
            await store.WriteAsync(
                new AuditEntry(
                    time.GetUtcNow(),
                    action,
                    succeeded,
                    actor?.Id,
                    actor?.LoginId ?? loginId,
                    targetType,
                    targetId,
                    detail,
                    http.Connection.RemoteIpAddress?.ToString()),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "監査ログを書けませんでした（{Action}）", action);
        }
    }
}

/// <summary>古い監査ログを定期的に消す。</summary>
/// <remarks>**消さないと増え続ける。** 残す日数は設定 <c>CALENDARFEED_AUDITLOG_RETENTION_DAYS</c>（管理画面から編集できる。既定 365。0 なら消さない）。</remarks>
public sealed class AuditLogRetentionService(
    IAuditLogStore store,
    RuntimeSettings settings,
    TimeProvider time,
    ILogger<AuditLogRetentionService> logger) : BackgroundService
{
    /// <summary>掃除の間隔。消す対象は 1 日で大きく変わらないので、頻繁には走らせない。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>古い記録を消す。消した件数を返す（無効なら 0）。</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var days = settings.Current.AuditLogRetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var deleted = await store.DeleteOlderThanAsync(time.GetUtcNow().AddDays(-days), cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("{Days} 日より古い監査ログを {Count} 件消しました", days, deleted);
        }

        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 掃除の失敗でアプリを落とさない。次の周期でやり直す
                logger.LogError(ex, "監査ログの掃除に失敗しました");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
