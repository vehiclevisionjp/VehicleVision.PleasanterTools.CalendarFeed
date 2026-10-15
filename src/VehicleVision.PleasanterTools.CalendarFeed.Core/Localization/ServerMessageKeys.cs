namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

/// <summary>サーバが管理画面へ返す文言の鍵（<see cref="ServerMessages"/>）。</summary>
/// <remarks>
/// <b>鍵を足したら、7 言語すべてに文言を足す</b>（足りない言語は <c>en</c> → <c>ja</c> の順で落ちるが、
/// テスト <c>ServerMessagesTests</c> が抜けを検出する）。
/// </remarks>
public static class ServerMessageKeys
{
    public const string InvalidCredentials = "invalidCredentials";
    public const string CrossSiteRejected = "crossSiteRejected";
    public const string LoginRequired = "loginRequired";
    public const string UpstreamSessionEnded = "upstreamSessionEnded";
    public const string PermissionRequired = "permissionRequired";
    public const string AdministratorAlreadyExists = "administratorAlreadyExists";
    public const string CurrentPasswordRejected = "currentPasswordRejected";
    public const string PasswordRejected = "passwordRejected";
    public const string TwoFactorDisabled = "twoFactorDisabled";
    public const string TwoFactorRequired = "twoFactorRequired";
    public const string EnrollmentBeginRequired = "enrollmentBeginRequired";
    public const string TotpCodeMismatch = "totpCodeMismatch";
    public const string LoginIdInvalid = "loginIdInvalid";
    public const string PasswordTooShort = "passwordTooShort";
    public const string PasswordTooLong = "passwordTooLong";
    public const string PasswordSameAsLoginId = "passwordSameAsLoginId";
    public const string PasswordPolicyMismatch = "passwordPolicyMismatch";
    public const string AdminUserNotFound = "adminUserNotFound";
    public const string RoleNotSupported = "roleNotSupported";
    public const string DuplicateLoginId = "duplicateLoginId";
    public const string InviteDisabledUser = "inviteDisabledUser";
    public const string SelfDisableNotAllowed = "selfDisableNotAllowed";
    public const string SelfRoleChangeNotAllowed = "selfRoleChangeNotAllowed";
    public const string SelfTotpResetNotAllowed = "selfTotpResetNotAllowed";
    public const string SelfSessionRevokeNotAllowed = "selfSessionRevokeNotAllowed";
    public const string LastAdministrator = "lastAdministrator";
    public const string InvitationInvalid = "invitationInvalid";
    public const string SessionNotFound = "sessionNotFound";
    public const string AuditBeforeInvalid = "auditBeforeInvalid";
    public const string ApiKeyNeedsSecretKey = "apiKeyNeedsSecretKey";
    public const string ApiKeyRequired = "apiKeyRequired";
    public const string SettingsValuesRequired = "settingsValuesRequired";
    public const string SecretSettingNeedsSecretKey = "secretSettingNeedsSecretKey";
    public const string SamlNeedsSecretKey = "samlNeedsSecretKey";
    public const string FeedNameInvalid = "feedNameInvalid";
    public const string FeedSiteIdInvalid = "feedSiteIdInvalid";
    public const string FeedFiltersInvalid = "feedFiltersInvalid";
    public const string FeedApiKeyInvalid = "feedApiKeyInvalid";
    public const string FeedTimeZoneInvalid = "feedTimeZoneInvalid";
    public const string FeedLanguageInvalid = "feedLanguageInvalid";

    /// <summary>すべての鍵（テストで、7 言語に抜けが無いかを確かめるのに使う）。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        InvalidCredentials, CrossSiteRejected, LoginRequired, UpstreamSessionEnded, PermissionRequired,
        AdministratorAlreadyExists, CurrentPasswordRejected, PasswordRejected, TwoFactorDisabled, TwoFactorRequired,
        EnrollmentBeginRequired, TotpCodeMismatch, LoginIdInvalid, PasswordTooShort, PasswordTooLong,
        PasswordSameAsLoginId, PasswordPolicyMismatch, AdminUserNotFound, RoleNotSupported, DuplicateLoginId,
        InviteDisabledUser, SelfDisableNotAllowed, SelfRoleChangeNotAllowed, SelfTotpResetNotAllowed,
        SelfSessionRevokeNotAllowed, LastAdministrator, InvitationInvalid, SessionNotFound, AuditBeforeInvalid,
        ApiKeyNeedsSecretKey, ApiKeyRequired, SettingsValuesRequired, SecretSettingNeedsSecretKey, SamlNeedsSecretKey,
        FeedNameInvalid, FeedSiteIdInvalid, FeedFiltersInvalid, FeedApiKeyInvalid, FeedTimeZoneInvalid, FeedLanguageInvalid,
    ];
}
