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
    public const string MappingSummaryColumnInvalid = "mappingSummaryColumnInvalid";
    public const string MappingColumnInvalid = "mappingColumnInvalid";
    public const string MappingDateColumnNoFormat = "mappingDateColumnNoFormat";
    public const string MappingDateColumnKind = "mappingDateColumnKind";
    public const string MappingTextColumnNeedsFormat = "mappingTextColumnNeedsFormat";
    public const string MappingDateFormatNotRoundTrip = "mappingDateFormatNotRoundTrip";
    public const string MappingFormatsOnlyDateNumber = "mappingFormatsOnlyDateNumber";
    public const string MappingSummaryTemplateColumnInvalid = "mappingSummaryTemplateColumnInvalid";
    public const string MappingSummaryTemplateNeedsFormat = "mappingSummaryTemplateNeedsFormat";
    public const string MappingSummaryTemplateFormatError = "mappingSummaryTemplateFormatError";
    public const string MappingSummaryColumnNeedsFormat = "mappingSummaryColumnNeedsFormat";
    public const string MappingColumnNeedsFormat = "mappingColumnNeedsFormat";
    public const string TemplateTooLong = "templateTooLong";
    public const string TemplateBraceUnclosed = "templateBraceUnclosed";
    public const string TemplateColumnInvalid = "templateColumnInvalid";
    public const string TemplateFormatEmpty = "templateFormatEmpty";
    public const string TemplateBraceStray = "templateBraceStray";
    public const string SettingBooleanInvalid = "settingBooleanInvalid";
    public const string SettingTooLong = "settingTooLong";
    public const string SettingControlChars = "settingControlChars";
    public const string SettingChoiceInvalid = "settingChoiceInvalid";
    public const string SettingIntegerInvalid = "settingIntegerInvalid";
    public const string SettingMinimum = "settingMinimum";
    public const string SettingMaximum = "settingMaximum";
    public const string SettingUrlHttpsRequired = "settingUrlHttpsRequired";
    public const string SettingUrlHttpRequired = "settingUrlHttpRequired";
    public const string SettingTimeZoneRequired = "settingTimeZoneRequired";
    public const string SettingTimeZoneUnresolvable = "settingTimeZoneUnresolvable";
    public const string SettingApiVersionInvalid = "settingApiVersionInvalid";
    public const string SettingEmailInvalid = "settingEmailInvalid";
    public const string SettingNotManaged = "settingNotManaged";
    public const string SettingFixedExternally = "settingFixedExternally";
    public const string MailFromRequired = "mailFromRequired";
    public const string MailPublicUrlRequired = "mailPublicUrlRequired";
    public const string MailSmtpHostRequired = "mailSmtpHostRequired";
    public const string MailSesRegionRequired = "mailSesRegionRequired";
    public const string MailAcsEndpointRequired = "mailAcsEndpointRequired";
    public const string SettingRequiredWhenEnabled = "settingRequiredWhenEnabled";
    public const string SettingAbsoluteUrlInvalid = "settingAbsoluteUrlInvalid";
    public const string SettingUnknownValue = "settingUnknownValue";
    public const string SettingBooleanValueInvalid = "settingBooleanValueInvalid";
    public const string SettingIntegerRangeInvalid = "settingIntegerRangeInvalid";
    public const string SamlCertificateRequired = "samlCertificateRequired";
    public const string SamlLoginIdClaimRequired = "samlLoginIdClaimRequired";
    public const string SamlCertificatePemIncomplete = "samlCertificatePemIncomplete";
    public const string SamlCertificatePemUnreadable = "samlCertificatePemUnreadable";
    public const string SamlCertificateNotFound = "samlCertificateNotFound";
    public const string SamlCertificateUnreadable = "samlCertificateUnreadable";
    public const string SsoCookieNameInvalidChars = "ssoCookieNameInvalidChars";
    public const string SsoCookieNameOverlapsOwn = "ssoCookieNameOverlapsOwn";
    public const string SsoCookieNamesEmpty = "ssoCookieNamesEmpty";
    public const string SsoInternalUrlInvalid = "ssoInternalUrlInvalid";
    public const string SsoBrowserUrlInvalid = "ssoBrowserUrlInvalid";
    public const string SsoIdListTooLong = "ssoIdListTooLong";
    public const string SsoIdListInvalid = "ssoIdListInvalid";
    public const string MappingEndFormatWithoutEnd = "mappingEndFormatWithoutEnd";
    public const string MappingDurationInvalid = "mappingDurationInvalid";
    public const string MappingFormatLengthInvalid = "mappingFormatLengthInvalid";
    public const string MappingFormatsTooMany = "mappingFormatsTooMany";
    public const string MappingSummaryTemplateNoColumns = "mappingSummaryTemplateNoColumns";
    public const string MappingNumberFormatUnusable = "mappingNumberFormatUnusable";
    public const string MappingDateFormatUnusable = "mappingDateFormatUnusable";

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
        MappingSummaryColumnInvalid, MappingColumnInvalid, MappingDateColumnNoFormat, MappingDateColumnKind,
        MappingTextColumnNeedsFormat, MappingDateFormatNotRoundTrip, MappingFormatsOnlyDateNumber, MappingSummaryTemplateColumnInvalid,
        MappingSummaryTemplateNeedsFormat, MappingSummaryTemplateFormatError, MappingSummaryColumnNeedsFormat, MappingColumnNeedsFormat,
        TemplateTooLong, TemplateBraceUnclosed, TemplateColumnInvalid, TemplateFormatEmpty,
        TemplateBraceStray,
        SettingBooleanInvalid, SettingTooLong, SettingControlChars, SettingChoiceInvalid,
        SettingIntegerInvalid, SettingMinimum, SettingMaximum, SettingUrlHttpsRequired,
        SettingUrlHttpRequired, SettingTimeZoneRequired, SettingTimeZoneUnresolvable, SettingApiVersionInvalid,
        SettingEmailInvalid, SettingNotManaged, SettingFixedExternally, MailFromRequired,
        MailPublicUrlRequired, MailSmtpHostRequired, MailSesRegionRequired, MailAcsEndpointRequired,
        SettingRequiredWhenEnabled, SettingAbsoluteUrlInvalid, SettingUnknownValue, SettingBooleanValueInvalid,
        SettingIntegerRangeInvalid, SamlCertificateRequired, SamlLoginIdClaimRequired, SamlCertificatePemIncomplete,
        SamlCertificatePemUnreadable, SamlCertificateNotFound, SamlCertificateUnreadable, SsoCookieNameInvalidChars,
        SsoCookieNameOverlapsOwn, SsoCookieNamesEmpty, SsoInternalUrlInvalid, SsoBrowserUrlInvalid,
        SsoIdListTooLong, SsoIdListInvalid, MappingEndFormatWithoutEnd, MappingDurationInvalid,
        MappingFormatLengthInvalid, MappingFormatsTooMany, MappingSummaryTemplateNoColumns, MappingNumberFormatUnusable,
        MappingDateFormatUnusable,
    ];
}
