namespace VehicleVision.PleasanterTools.CalendarFeed.Admin;

/// <summary>役割。**認可は本アプリの役割だけで行う**（Pleasanter のグループ・組織では判定しない）。権限の割り当ては <see cref="AdminPermissions"/>。</summary>
/// <remarks>Questionnaire の役割（Administrator / SurveyAdministrator / UserAdministrator / Editor / Auditor）に則る。DB には名前で入る。</remarks>
public static class AdminRoles
{
    /// <summary>特権管理者。すべてを許す。</summary>
    public const string Administrator = "Administrator";

    /// <summary>フィード管理者。フィードの運用を任せる相手。<b>人には触れない。</b></summary>
    public const string FeedAdministrator = "FeedAdministrator";

    /// <summary>ユーザ管理者。人の出入りと、その記録。<b>フィードには触れない。</b></summary>
    public const string UserAdministrator = "UserAdministrator";

    /// <summary>フィード編集者。作るところまで。<b>失効はできない。</b></summary>
    public const string Editor = "Editor";

    /// <summary>監査担当。見るだけ。</summary>
    public const string Auditor = "Auditor";

    /// <summary>すべての役割。</summary>
    public static readonly IReadOnlyList<string> All =
        [Administrator, FeedAdministrator, UserAdministrator, Editor, Auditor];

    /// <summary>知っている役割か。</summary>
    public static bool IsValid(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}
