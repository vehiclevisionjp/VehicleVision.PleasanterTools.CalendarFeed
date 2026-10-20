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

    /// <summary>フィードの CalDAV を有効・無効にした。</summary>
    public const string FeedCalDavChanged = "feed.caldav_changed";
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
