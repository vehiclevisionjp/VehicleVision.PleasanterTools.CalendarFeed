using System.Globalization;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

public sealed partial class PleasanterSessionVerifier
{
    /// <summary>子グループも含めた問い合わせ数の上限。循環や巨大な構成で負荷を増幅しない。</summary>
    private const int MaxMembershipGroups = 64;

    /// <summary>本人確認後に所属を判定する。全体で本人確認と同じ時間制限を使う。</summary>
    private async Task<PleasanterSessionResult> VerifyMembershipAsync(
        PleasanterIdentity identity,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken)
    {
        PleasanterSessionResult Allowed() => new(PleasanterSessionStatus.Authenticated, identity);
        PleasanterSessionResult Denied() => new(PleasanterSessionStatus.NotAllowed, Reason: "membership-not-allowed");

        if (identity.DeptId is null)
        {
            return PleasanterSessionResult.Error("membership-no-dept-id");
        }

        if (options.AllowedDeptIds.Contains(identity.DeptId.Value))
        {
            return Allowed();
        }

        if (options.AllowedGroupIds.IsDefaultOrEmpty)
        {
            return Denied();
        }

        var connection = await GetApiKeyConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return PleasanterSessionResult.Error("membership-no-api-key");
        }

        // API キーの送信先は本アプリの接続設定だけ。cookie で確認した本人と同じかも確かめる。
        var query = JsonSerializer.Serialize(new { ApiVersion, ApiKey = connection.Value.ApiKey });
        var (failure, document) = await PostAsync(
            new Uri(connection.Value.BaseUrl, string.Create(CultureInfo.InvariantCulture, $"api/users/{identity.UserId}/get")),
            query, cookieHeader: null, reasonPrefix: "membership-user-", cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return failure!;
        }

        using (document)
        {
            var confirmed = ParseUsers(document.RootElement, identity.UserId, "membership-user-");
            if (confirmed.Status != PleasanterSessionStatus.Authenticated)
            {
                return confirmed;
            }

            if (confirmed.Identity!.TenantId != identity.TenantId
                || confirmed.Identity.LoginId != identity.LoginId
                || confirmed.Identity.DeptId != identity.DeptId)
            {
                return PleasanterSessionResult.Error("membership-user-mismatch");
            }
        }

        var pending = new Queue<int>(options.AllowedGroupIds);
        var visited = new HashSet<int>();
        var scheduled = options.AllowedGroupIds.ToHashSet();
        while (pending.TryDequeue(out var groupId))
        {
            if (!visited.Add(groupId))
            {
                continue;
            }

            if (visited.Count > MaxMembershipGroups)
            {
                return PleasanterSessionResult.Error("membership-group-limit");
            }

            (failure, document) = await PostAsync(
                new Uri(connection.Value.BaseUrl, string.Create(CultureInfo.InvariantCulture, $"api/groups/{groupId}/get")),
                query, cookieHeader: null, reasonPrefix: "membership-group-", cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return failure!;
            }

            using (document)
            {
                var row = ReadMembershipGroup(document.RootElement, identity.TenantId, groupId);
                if (row is null)
                {
                    return PleasanterSessionResult.Error("membership-group-invalid");
                }

                if (row.Value.GetProperty("Disabled").GetBoolean())
                {
                    continue;
                }

                var matches = false;
                foreach (var member in row.Value.GetProperty("GroupMembers").EnumerateArray())
                {
                    if (!TryReadMembership(member, child: false, out var kind, out var id))
                    {
                        return PleasanterSessionResult.Error("membership-member-invalid");
                    }

                    matches |= kind == "User" ? id == identity.UserId : id == identity.DeptId;
                }

                // 一部が壊れた応答を、先頭の一致だけで成功と扱わない。
                foreach (var child in row.Value.GetProperty("GroupChildren").EnumerateArray())
                {
                    if (!TryReadMembership(child, child: true, out _, out var id))
                    {
                        return PleasanterSessionResult.Error("membership-child-invalid");
                    }

                    if (scheduled.Add(id))
                    {
                        if (scheduled.Count > MaxMembershipGroups)
                        {
                            return PleasanterSessionResult.Error("membership-group-limit");
                        }

                        pending.Enqueue(id);
                    }
                }

                if (matches)
                {
                    return Allowed();
                }
            }
        }

        return Denied();
    }

    /// <summary>指定した同一テナントのグループが、欠落のない 1 行で返ったときだけ読む。</summary>
    private static JsonElement? ReadMembershipGroup(JsonElement root, int tenantId, int groupId)
    {
        if (!TryGetObject(root, "Response", out var response)
            || !TryGetPositiveInt(response, "TotalCount", out var count) || count != 1
            || !response.TryGetProperty("Data", out var rows) || rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() != 1 || rows[0].ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var row = rows[0];
        return TryGetPositiveInt(row, "TenantId", out var tenant) && tenant == tenantId
            && TryGetPositiveInt(row, "GroupId", out var group) && group == groupId
            && row.TryGetProperty("Disabled", out var disabled) && disabled.ValueKind is JsonValueKind.True or JsonValueKind.False
            && row.TryGetProperty("GroupMembers", out var members) && members.ValueKind == JsonValueKind.Array
            && row.TryGetProperty("GroupChildren", out var children) && children.ValueKind == JsonValueKind.Array
                ? row : null;
    }

    /// <summary>Pleasanter 1.5.8.1 の実機で確認した所属 ID の形式を読む（2026-09-25）。</summary>
    private static bool TryReadMembership(JsonElement element, bool child, out string kind, out int id)
    {
        kind = string.Empty;
        id = 0;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parts = element.GetString()!.Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
        {
            return false;
        }

        kind = parts[0];
        return child
            ? kind == "Group" && parts[2].Length == 0
            : (kind is "User" or "Dept") && bool.TryParse(parts[2], out _);
    }
}
