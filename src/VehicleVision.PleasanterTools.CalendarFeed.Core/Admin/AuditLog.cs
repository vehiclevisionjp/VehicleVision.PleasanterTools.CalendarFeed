namespace VehicleVision.PleasanterTools.CalendarFeed.Admin;

/// <summary>残す操作の名前。</summary>
/// <remarks>画面・集計の絞り込みに使うので、名前を変えると過去の記録と食い違う。</remarks>
public static class AuditActions
{
    public const string AdminSetup = "admin.setup";
    public const string LoginSucceeded = "login.succeeded";

    /// <summary>パスワード段階の失敗（ID が無い・パスワードが違う・無効・締め出し中）。理由は <c>Detail</c> に残す（外へは伝えない）。</summary>
    public const string LoginFailed = "login.failed";

    public const string LoginTotpFailed = "login.totp_failed";
    public const string LoginRecoveryFailed = "login.recovery_failed";
    public const string LoginRecoveryUsed = "login.recovery_used";
    public const string LoginLocked = "login.locked";
    public const string Logout = "logout";
    public const string SamlLoginFailed = "login.saml_failed";
    public const string SamlRegistered = "user.saml_registered";
    public const string PleasanterSsoFailed = "login.pleasanter_sso_failed";
    public const string PleasanterSsoRegistered = "user.pleasanter_sso_registered";
    public const string PasswordChanged = "password.changed";
    public const string PasswordChangeFailed = "password.change_failed";
    public const string TotpEnrolled = "totp.enrolled";
    public const string TotpEnrollFailed = "totp.enroll_failed";
    public const string TotpDisabled = "totp.disabled";
    public const string TotpDisableFailed = "totp.disable_failed";
    public const string FeedCreated = "feed.created";
    public const string FeedUpdated = "feed.updated";
    public const string SettingsUpdated = "settings.updated";
    public const string SettingsMailTest = "settings.mail_test";
    public const string FeedTokenRotated = "feed.token_rotated";
    public const string FeedApiKeySet = "feed.apikey_set";
    public const string FeedRevoked = "feed.revoked";
    public const string UserInvited = "user.invited";
    public const string UserInviteReissued = "user.invite_reissued";
    public const string UserInviteAccepted = "user.invite_accepted";
    public const string UserDisabled = "user.disabled";
    public const string UserEnabled = "user.enabled";
    public const string UserRoleChanged = "user.role_changed";
    public const string UserTotpReset = "user.totp_reset";
    public const string UserSessionsRevoked = "user.sessions_revoked";
    public const string SessionRevoked = "session.revoked";
    public const string UserSessionRevoked = "user.session_revoked";
    public const string SessionsRevokedOthers = "session.revoked_others";
}

/// <summary>残す操作 1 件。</summary>
/// <remarks>
/// **パスワード・API キー・トークン・復旧コード・TOTP の数字を入れない。** 入れないことを設計で担保する:
/// 要求本文は渡さず、呼び出し側が「何を・誰が・どの対象か」だけを渡す。
/// </remarks>
/// <param name="OccurredAt">起きた時刻。</param>
/// <param name="Action"><see cref="AuditActions"/> の名前。</param>
/// <param name="Succeeded">成功したか。<b>失敗も残す</b>（試みられたことが分からなくなる）。</param>
/// <param name="AdminUserId">誰が。認証を通っていない試みでは <c>null</c>。</param>
/// <param name="LoginId">ログイン ID。居ない ID での試みも残るよう、利用者の行とは別に持つ。</param>
/// <param name="TargetType">対象の種類（<c>Feed</c>）。</param>
/// <param name="TargetId">対象の識別子。</param>
/// <param name="Detail">補足（失敗の理由など）。短い文字列。<b>資格情報を入れない。</b></param>
/// <param name="IpAddress">送信元。</param>
public sealed record AuditEntry(
    DateTimeOffset OccurredAt,
    string Action,
    bool Succeeded,
    string? AdminUserId = null,
    string? LoginId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Detail = null,
    string? IpAddress = null);

/// <summary>記録を読むときの絞り込み。既定は「直近を新しい順に」。</summary>
public sealed record AuditLogQuery
{
    public const int MaxLimit = 500;

    /// <summary>読む件数の上限（1〜<see cref="MaxLimit"/>）。</summary>
    public int Limit { get; init; } = 100;

    /// <summary>この時刻より前だけ（ページ送り。前のページの最後の <c>OccurredAt</c> を渡す）。</summary>
    public DateTimeOffset? Before { get; init; }

    /// <summary>この管理者の操作だけ。</summary>
    public string? AdminUserId { get; init; }

    /// <summary>この操作だけ（<see cref="AuditActions"/>）。</summary>
    public string? Action { get; init; }

    /// <summary>失敗だけ。</summary>
    public bool FailedOnly { get; init; }
}

/// <summary>管理操作の記録の保管先。</summary>
public interface IAuditLogStore
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>新しい順に読む。</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(AuditLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>指定した時刻より古い記録を消す。消した件数を返す。</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default);
}

/// <summary>記録へ入れる文字列を安全にする（LogSafe 相当）。</summary>
public static class AuditText
{
    /// <summary>制御文字（改行を含む）を落とし、長さを切り詰める。ログの偽装（行の差し込み）を防ぐ。</summary>
    public static string? Safe(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var cleaned = string.Concat(value.Where(c => !char.IsControl(c)));
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
}
