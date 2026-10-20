using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;
using System.Globalization;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>監査ログの閲覧 API。**ログイン済みで <c>audit.read</c> を持つ役割だけ**が読める。</summary>
public static class AdminAuditEndpoints
{
    /// <summary>監査ログの閲覧 API を登録する。</summary>
    public static void MapAdminAudit(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api/audit-logs");
        group.AddEndpointFilter<AdminSessionFilter>();
        group.AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.AuditRead));

        // 新しい順。ページ送りは、前のページまでに読んだ件数を offset へ渡す（時刻で区切ると、同じ秒の記録を取りこぼす）
        group.MapGet("/", async (
            IAuditLogStore store,
            int? limit,
            int? offset,
            string? admin,
            string? action,
            bool? failed,
            CancellationToken ct) =>
        {
            if (offset is < 0)
            {
                return ApiError.Of(StatusCodes.Status400BadRequest, ServerMessageKeys.AuditOffsetInvalid);
            }

            var entries = await store.ListAsync(
                new AuditLogQuery
                {
                    Limit = limit ?? 100,
                    Offset = offset ?? 0,
                    AdminUserId = admin,
                    Action = action,
                    FailedOnly = failed ?? false,
                },
                ct);

            return Results.Ok(entries.Select(e => new
            {
                occurredAt = e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
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
