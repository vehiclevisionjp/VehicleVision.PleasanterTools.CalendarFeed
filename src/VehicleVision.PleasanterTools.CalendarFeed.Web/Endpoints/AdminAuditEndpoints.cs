using System.Globalization;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>監査ログの閲覧 API。**ログイン済みの <c>Administrator</c> だけ**が読める（閲覧のみの役割には見せない）。</summary>
public static class AdminAuditEndpoints
{
    /// <summary>監査ログの閲覧 API を登録する。</summary>
    public static void MapAdminAudit(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api/audit-logs");
        group.AddEndpointFilter<AdminSessionFilter>();
        group.AddEndpointFilter<AdministratorOnlyFilter>();

        // 新しい順。ページ送りは、前のページの最後の occurredAt を before へ渡す
        group.MapGet("/", async (
            IAuditLogStore store,
            int? limit,
            string? before,
            string? admin,
            string? action,
            bool? failed,
            CancellationToken ct) =>
        {
            DateTimeOffset? beforeTime = null;
            if (!string.IsNullOrEmpty(before))
            {
                if (!DateTimeOffset.TryParse(
                        before, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return Results.BadRequest(new { error = "before は日時（ISO 8601）で指定してください。" });
                }

                beforeTime = parsed;
            }

            var entries = await store.ListAsync(
                new AuditLogQuery
                {
                    Limit = limit ?? 100,
                    Before = beforeTime,
                    AdminUserId = admin,
                    Action = action,
                    FailedOnly = failed ?? false,
                },
                ct);

            return Results.Ok(entries.Select(e => new
            {
                occurredAt = DbTime.Format(e.OccurredAt),
                e.Action,
                succeeded = e.Succeeded,
                adminUserId = e.AdminUserId,
                e.LoginId,
                e.TargetType,
                e.TargetId,
                e.Detail,
                e.IpAddress,
            }));
        });
    }
}
