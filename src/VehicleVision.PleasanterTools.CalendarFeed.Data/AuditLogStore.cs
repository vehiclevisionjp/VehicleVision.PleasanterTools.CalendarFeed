using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>管理操作の記録を RDBMS へ保管する。</summary>
/// <remarks>
/// **列の桁より長い値は、溢れさせずに切り詰める**（桁溢れで例外にすると、操作は通ったのに記録だけ落ちる）。
/// 値はすべてパラメータで渡す。
/// </remarks>
public sealed class AuditLogStore(IDbConnectionFactory connections) : IAuditLogStore
{
    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [AuditLogs] ([Id], [OccurredAt], [AdminUserId], [LoginId], [Action], [Succeeded], "
                + "[TargetType], [TargetId], [Detail], [IpAddress]) "
                + "VALUES (@Id, @OccurredAt, @AdminUserId, @LoginId, @Action, @Succeeded, @TargetType, @TargetId, @Detail, @IpAddress)"),
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                OccurredAt = DbTime.ForDb(entry.OccurredAt.UtcDateTime),
                AdminUserId = AuditText.Safe(entry.AdminUserId, 36),
                LoginId = AuditText.Safe(entry.LoginId, 100),
                Action = AuditText.Safe(entry.Action, 64) ?? string.Empty,
                Succeeded = entry.Succeeded ? 1 : 0,
                TargetType = AuditText.Safe(entry.TargetType, 32),
                TargetId = AuditText.Safe(entry.TargetId, 64),
                Detail = AuditText.Safe(entry.Detail, 500),
                IpAddress = AuditText.Safe(entry.IpAddress, 45),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(
        AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, AuditLogQuery.MaxLimit);
        var where = new List<string>();
        var parameters = new DynamicParameters();
        if (!string.IsNullOrEmpty(query.AdminUserId))
        {
            where.Add("[AdminUserId] = @AdminUserId");
            parameters.Add("AdminUserId", query.AdminUserId);
        }

        if (!string.IsNullOrEmpty(query.Action))
        {
            where.Add("[Action] = @Action");
            parameters.Add("Action", query.Action);
        }

        if (query.FailedOnly)
        {
            where.Add("[Succeeded] = 0");
        }

        var sql = "SELECT [OccurredAt], [AdminUserId], [LoginId], [Action], [Succeeded], [TargetType], [TargetId], [Detail], [IpAddress] "
            + "FROM [AuditLogs]"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            // **同じ秒の記録の並びを定める**（秒未満を切り捨てて持つので、時刻だけでは並びが定まらない）
            + " ORDER BY [OccurredAt] DESC, [Id] DESC "
            + SqlDialect.Page(connections.Provider);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        parameters.Add("Limit", limit);
        parameters.Add("Offset", query.Offset);

        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Sql(sql), parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows.Select(r => new AuditEntry(
            DbTime.AsUtc(r.OccurredAt), r.Action, r.Succeeded != 0,
            r.AdminUserId, r.LoginId, r.TargetType, r.TargetId, r.Detail, r.IpAddress))];
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AuditLogs] WHERE [OccurredAt] < @Threshold"),
            new { Threshold = DbTime.ForDb(threshold.UtcDateTime) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class Row
    {
        public DateTime OccurredAt { get; set; }
        public string? AdminUserId { get; set; }
        public string? LoginId { get; set; }
        public string Action { get; set; } = string.Empty;
        public int Succeeded { get; set; }
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public string? Detail { get; set; }
        public string? IpAddress { get; set; }
    }
}
