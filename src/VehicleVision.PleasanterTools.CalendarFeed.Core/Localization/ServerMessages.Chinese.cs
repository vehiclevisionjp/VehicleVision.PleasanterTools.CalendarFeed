using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

public static partial class ServerMessages
{
    private static FrozenDictionary<string, string> Chinese() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "登录 ID 或密码不正确。多次失败后，请稍候再试。",
        [ServerMessageKeys.CrossSiteRejected] = "不接受来自其他站点的请求。",
        [ServerMessageKeys.LoginRequired] = "需要登录。",
        [ServerMessageKeys.UpstreamSessionEnded] = "由于无法向 Pleasanter 确认，已结束登录。请稍后再试。",
        [ServerMessageKeys.PermissionRequired] = "此操作需要权限（{0}）。",
        [ServerMessageKeys.AdministratorAlreadyExists] = "第一位管理员已经创建。",
        [ServerMessageKeys.CurrentPasswordRejected] = "当前密码不正确。",
        [ServerMessageKeys.PasswordRejected] = "密码不正确。",
        [ServerMessageKeys.TwoFactorDisabled] = "两步验证已被设置为禁用。",
        [ServerMessageKeys.TwoFactorRequired] = "两步验证已被设置为必需，因此无法解除。",
        [ServerMessageKeys.EnrollmentBeginRequired] = "请先开始注册（/admin/api/me/totp/begin）。",
        [ServerMessageKeys.TotpCodeMismatch] = "验证码不正确。请输入验证器应用中显示的 6 位验证码。",
        [ServerMessageKeys.LoginIdInvalid] = "登录 ID 须为 1 到 100 个字符（不含控制字符）。",
        [ServerMessageKeys.PasswordTooShort] = "密码至少需要 {0} 个字符。",
        [ServerMessageKeys.PasswordTooLong] = "密码最多只能有 {0} 个字符。",
        [ServerMessageKeys.PasswordSameAsLoginId] = "密码不能与登录 ID 相同。",
        [ServerMessageKeys.PasswordPolicyMismatch] = "密码不符合要求。",
        [ServerMessageKeys.AdminUserNotFound] = "未找到该管理员。",
        [ServerMessageKeys.RoleNotSupported] = "请将角色指定为 {0} 之一。",
        [ServerMessageKeys.DuplicateLoginId] = "该登录 ID 已存在。",
        [ServerMessageKeys.InviteDisabledUser] = "无法邀请已被禁用的管理员。请先将其重新启用。",
        [ServerMessageKeys.SelfDisableNotAllowed] = "不能禁用自己。",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "不能更改自己的角色。",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "请在自己的界面（/admin/api/me/totp/disable）中解除自己的两步验证。",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "请在自己的界面（/admin/api/me/sessions）中让自己的设备失效。",
        [ServerMessageKeys.LastAdministrator] = "这样会导致没有任何有效的管理员（Administrator），因此无法执行。请先准备另一位管理员。",
        [ServerMessageKeys.InvitationInvalid] = "此邀请无法使用（可能已过期、已被接受或已被取消）。请联系邀请您的管理员重新发送。",
        [ServerMessageKeys.SessionNotFound] = "未找到该设备。",
        [ServerMessageKeys.AuditBeforeInvalid] = "请将 before 指定为日期和时间（ISO 8601）。",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "保存 API 密钥需要设置 {0}。",
        [ServerMessageKeys.ApiKeyRequired] = "请指定 ApiKey（所有订阅源共用的默认 API 密钥也未设置）。",
        [ServerMessageKeys.SettingsValuesRequired] = "请在 values 中指定要更改的设置。",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "保存机密设置需要设置 {0}。",
        [ServerMessageKeys.SamlNeedsSecretKey] = "SAML 登录需要设置密钥（{0}）。",
        [ServerMessageKeys.FeedNameInvalid] = "Name 须为 1 到 200 个字符。",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId 须大于或等于 1。",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters 最多 20 项；列名只能使用字母、数字和 _，值最多 500 个字符。",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey 须为 1 到 512 个字符（不含控制字符）。",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId 是本服务器无法解析的时区 ID。",
        [ServerMessageKeys.FeedLanguageInvalid] = "请将 Language 指定为 {0} 之一。",
    }.ToFrozenDictionary();
}
