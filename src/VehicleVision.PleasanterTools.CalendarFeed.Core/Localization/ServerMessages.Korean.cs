using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

public static partial class ServerMessages
{
    private static FrozenDictionary<string, string> Korean() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "로그인 ID 또는 비밀번호가 올바르지 않습니다. 여러 번 실패하면 잠시 기다린 후 다시 시도해 주세요.",
        [ServerMessageKeys.CrossSiteRejected] = "다른 사이트에서 온 요청은 받아들이지 않습니다.",
        [ServerMessageKeys.LoginRequired] = "로그인이 필요합니다.",
        [ServerMessageKeys.UpstreamSessionEnded] = "Pleasanter에 확인할 수 없어 로그인을 종료했습니다. 잠시 후 다시 시도해 주세요.",
        [ServerMessageKeys.PermissionRequired] = "이 작업에는 권한({0})이 필요합니다.",
        [ServerMessageKeys.AdministratorAlreadyExists] = "첫 관리자는 이미 만들어져 있습니다.",
        [ServerMessageKeys.CurrentPasswordRejected] = "현재 비밀번호가 올바르지 않습니다.",
        [ServerMessageKeys.PasswordRejected] = "비밀번호가 올바르지 않습니다.",
        [ServerMessageKeys.TwoFactorDisabled] = "2단계 인증은 사용 안 함으로 설정되어 있습니다.",
        [ServerMessageKeys.TwoFactorRequired] = "2단계 인증은 필수로 설정되어 있어 해제할 수 없습니다.",
        [ServerMessageKeys.EnrollmentBeginRequired] = "먼저 등록을 시작해 주세요(/admin/api/me/totp/begin).",
        [ServerMessageKeys.TotpCodeMismatch] = "코드가 올바르지 않습니다. 인증 앱에 표시된 6자리 코드를 입력해 주세요.",
        [ServerMessageKeys.LoginIdInvalid] = "로그인 ID는 1~100자(제어 문자 제외)로 지정해 주세요.",
        [ServerMessageKeys.PasswordTooShort] = "비밀번호는 {0}자 이상이어야 합니다.",
        [ServerMessageKeys.PasswordTooLong] = "비밀번호는 {0}자 이하여야 합니다.",
        [ServerMessageKeys.PasswordSameAsLoginId] = "비밀번호는 로그인 ID와 같게 할 수 없습니다.",
        [ServerMessageKeys.PasswordPolicyMismatch] = "비밀번호가 조건을 충족하지 않습니다.",
        [ServerMessageKeys.AdminUserNotFound] = "관리자를 찾을 수 없습니다.",
        [ServerMessageKeys.RoleNotSupported] = "역할은 {0} 중 하나로 지정해 주세요.",
        [ServerMessageKeys.DuplicateLoginId] = "해당 로그인 ID는 이미 있습니다.",
        [ServerMessageKeys.InviteDisabledUser] = "비활성화된 관리자는 초대할 수 없습니다. 먼저 계정을 활성화해 주세요.",
        [ServerMessageKeys.SelfDisableNotAllowed] = "자기 자신은 비활성화할 수 없습니다.",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "자기 자신의 역할은 변경할 수 없습니다.",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "자신의 2단계 인증은 자신의 화면(/admin/api/me/totp/disable)에서 해제해 주세요.",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "자신의 기기는 자신의 화면(/admin/api/me/sessions)에서 로그아웃 처리해 주세요.",
        [ServerMessageKeys.LastAdministrator] = "활성 관리자(Administrator)가 한 명도 없게 되므로 할 수 없습니다. 먼저 다른 관리자를 준비해 주세요.",
        [ServerMessageKeys.InvitationInvalid] = "이 초대는 사용할 수 없습니다(만료되었거나, 이미 수락되었거나, 취소되었을 수 있습니다). 초대한 관리자에게 다시 보내 달라고 요청해 주세요.",
        [ServerMessageKeys.SessionNotFound] = "해당 기기를 찾을 수 없습니다.",
        [ServerMessageKeys.AuditBeforeInvalid] = "before는 날짜와 시각(ISO 8601)으로 지정해 주세요.",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "API 키를 저장하려면 {0} 설정이 필요합니다.",
        [ServerMessageKeys.ApiKeyRequired] = "ApiKey를 지정해 주세요(모든 피드 공통 기본 API 키도 설정되어 있지 않습니다).",
        [ServerMessageKeys.SettingsValuesRequired] = "values에 변경할 설정을 지정해 주세요.",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "비밀 설정을 저장하려면 {0} 설정이 필요합니다.",
        [ServerMessageKeys.SamlNeedsSecretKey] = "SAML 로그인에는 비밀 키({0}) 설정이 필요합니다.",
        [ServerMessageKeys.FeedNameInvalid] = "Name은 1~200자로 지정해 주세요.",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId는 1 이상으로 지정해 주세요.",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters는 최대 20개까지, 열 이름은 영문자·숫자·_만, 값은 500자까지 가능합니다.",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey는 1~512자(제어 문자 제외)로 지정해 주세요.",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId에 이 서버가 해석할 수 없는 시간대 ID가 지정되어 있습니다.",
        [ServerMessageKeys.FeedLanguageInvalid] = "Language는 {0} 중 하나로 지정해 주세요.",
    }.ToFrozenDictionary();
}
