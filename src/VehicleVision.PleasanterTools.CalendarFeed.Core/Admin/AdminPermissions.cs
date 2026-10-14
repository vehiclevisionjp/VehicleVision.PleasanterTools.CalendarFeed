using System.Collections.Frozen;
using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.CalendarFeed.Admin;

/// <summary>権限と、役割への割り当て。</summary>
/// <remarks>
/// <para>
/// **役割へ直接ぶら下げず、権限の集合を役割に割り当てる**（Questionnaire の <c>AdminPermissions</c> に則る）。
/// 機能が増えたときに権限を 1 つ足すだけで済む。
/// </para>
/// <para>
/// **権限は cookie へ焼かない。** 役割から、その都度対応表を引く（焼くと、役割を変えても再ログインまで効かない）。
/// **知らない役割には何も持たせない**（通してしまうより安全な壊れ方を選ぶ）。
/// </para>
/// </remarks>
public static class AdminPermissions
{
    /// <summary>フィードの一覧を見る。</summary>
    public const string FeedsRead = "feeds.read";

    /// <summary>フィードの作成・トークンの再発行・API キーの登録。</summary>
    public const string FeedsWrite = "feeds.write";

    /// <summary>フィードの失効（購読 URL が使えなくなる）。</summary>
    public const string FeedsDelete = "feeds.delete";

    /// <summary>メールの送信状況を見る。</summary>
    public const string OutboxRead = "outbox.read";

    /// <summary>監査ログを見る。</summary>
    public const string AuditRead = "audit.read";

    /// <summary>管理者の一覧を見る。</summary>
    public const string UsersRead = "users.read";

    /// <summary>管理者の追加・招待・役割の変更・停止・端末の失効。</summary>
    public const string UsersWrite = "users.write";

    /// <summary>他人の 2 要素を解除する。</summary>
    public const string UsersResetTwoFactor = "users.resetTwoFactor";

    /// <summary>すべての権限。</summary>
    public static readonly ImmutableArray<string> All =
    [
        FeedsRead,
        FeedsWrite,
        FeedsDelete,
        OutboxRead,
        AuditRead,
        UsersRead,
        UsersWrite,
        UsersResetTwoFactor,
    ];

    private static readonly FrozenDictionary<string, FrozenSet<string>> ByRole =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            // 特権管理者。すべて
            [AdminRoles.Administrator] = All.ToFrozenSet(StringComparer.Ordinal),

            // フィード管理者。フィードの運用を任せる相手。人には触れない
            [AdminRoles.FeedAdministrator] = new[] { FeedsRead, FeedsWrite, FeedsDelete, OutboxRead }
                .ToFrozenSet(StringComparer.Ordinal),

            // ユーザ管理者。人の出入りと、その記録。フィードには触れない
            [AdminRoles.UserAdministrator] = new[] { UsersRead, UsersWrite, UsersResetTwoFactor, AuditRead }
                .ToFrozenSet(StringComparer.Ordinal),

            // フィード編集者。作るところまで。失効はできない
            [AdminRoles.Editor] = new[] { FeedsRead, FeedsWrite }.ToFrozenSet(StringComparer.Ordinal),

            // 監査担当。見るだけ
            [AdminRoles.Auditor] = new[] { AuditRead, OutboxRead }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>役割が持つ権限。知らない役割は空。</summary>
    public static IReadOnlyCollection<string> Of(string? role) =>
        role is not null && ByRole.TryGetValue(role, out var permissions) ? permissions : FrozenSet<string>.Empty;

    /// <summary>役割がその権限を持つか。</summary>
    public static bool Has(string? role, string permission) => Of(role).Contains(permission);
}
