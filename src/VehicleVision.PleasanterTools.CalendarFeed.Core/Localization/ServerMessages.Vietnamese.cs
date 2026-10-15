using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

public static partial class ServerMessages
{
    private static FrozenDictionary<string, string> Vietnamese() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "ID đăng nhập hoặc mật khẩu không đúng. Nếu thất bại nhiều lần, vui lòng đợi một lúc rồi thử lại.",
        [ServerMessageKeys.CrossSiteRejected] = "Không chấp nhận yêu cầu từ trang web khác.",
        [ServerMessageKeys.LoginRequired] = "Bạn cần đăng nhập.",
        [ServerMessageKeys.UpstreamSessionEnded] = "Phiên đăng nhập đã kết thúc vì không thể kết nối tới Pleasanter để xác minh. Vui lòng thử lại sau.",
        [ServerMessageKeys.PermissionRequired] = "Thao tác này cần quyền ({0}).",
        [ServerMessageKeys.AdministratorAlreadyExists] = "Quản trị viên đầu tiên đã được tạo.",
        [ServerMessageKeys.CurrentPasswordRejected] = "Mật khẩu hiện tại không đúng.",
        [ServerMessageKeys.PasswordRejected] = "Mật khẩu không đúng.",
        [ServerMessageKeys.TwoFactorDisabled] = "Xác thực hai yếu tố đã bị tắt.",
        [ServerMessageKeys.TwoFactorRequired] = "Xác thực hai yếu tố là bắt buộc nên không thể gỡ bỏ.",
        [ServerMessageKeys.EnrollmentBeginRequired] = "Hãy bắt đầu đăng ký trước (/admin/api/me/totp/begin).",
        [ServerMessageKeys.TotpCodeMismatch] = "Mã không đúng. Hãy nhập mã 6 chữ số hiển thị trong ứng dụng xác thực của bạn.",
        [ServerMessageKeys.LoginIdInvalid] = "ID đăng nhập phải có từ 1 đến 100 ký tự (không có ký tự điều khiển).",
        [ServerMessageKeys.PasswordTooShort] = "Mật khẩu phải có ít nhất {0} ký tự.",
        [ServerMessageKeys.PasswordTooLong] = "Mật khẩu chỉ được có tối đa {0} ký tự.",
        [ServerMessageKeys.PasswordSameAsLoginId] = "Mật khẩu không được trùng với ID đăng nhập.",
        [ServerMessageKeys.PasswordPolicyMismatch] = "Mật khẩu không đáp ứng các yêu cầu.",
        [ServerMessageKeys.AdminUserNotFound] = "Không tìm thấy quản trị viên.",
        [ServerMessageKeys.RoleNotSupported] = "Hãy chỉ định vai trò là một trong các giá trị: {0}.",
        [ServerMessageKeys.DuplicateLoginId] = "ID đăng nhập đó đã tồn tại.",
        [ServerMessageKeys.InviteDisabledUser] = "Không thể mời quản trị viên đã bị vô hiệu hóa. Hãy bật lại tài khoản trước.",
        [ServerMessageKeys.SelfDisableNotAllowed] = "Bạn không thể tự vô hiệu hóa chính mình.",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "Bạn không thể thay đổi vai trò của chính mình.",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "Hãy gỡ bỏ xác thực hai yếu tố của chính bạn từ màn hình của bạn (/admin/api/me/totp/disable).",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "Hãy thu hồi các thiết bị của chính bạn từ màn hình của bạn (/admin/api/me/sessions).",
        [ServerMessageKeys.LastAdministrator] = "Thao tác này sẽ khiến không còn quản trị viên nào đang hoạt động (Administrator). Hãy chuẩn bị một quản trị viên khác trước.",
        [ServerMessageKeys.InvitationInvalid] = "Lời mời này không thể sử dụng (có thể đã hết hạn, đã được chấp nhận hoặc đã bị hủy). Hãy nhờ quản trị viên đã mời bạn gửi lại.",
        [ServerMessageKeys.SessionNotFound] = "Không tìm thấy thiết bị đó.",
        [ServerMessageKeys.AuditBeforeInvalid] = "Hãy chỉ định before là ngày giờ (ISO 8601).",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "Để lưu khóa API cần có thiết lập {0}.",
        [ServerMessageKeys.ApiKeyRequired] = "Hãy chỉ định ApiKey (khóa API mặc định dùng chung cho tất cả nguồn cấp cũng chưa được thiết lập).",
        [ServerMessageKeys.SettingsValuesRequired] = "Hãy chỉ định trong values các thiết lập cần thay đổi.",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "Để lưu thiết lập bí mật cần có thiết lập {0}.",
        [ServerMessageKeys.SamlNeedsSecretKey] = "Đăng nhập SAML cần có thiết lập khóa bí mật ({0}).",
        [ServerMessageKeys.FeedNameInvalid] = "Name phải có từ 1 đến 200 ký tự.",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId phải từ 1 trở lên.",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters cho phép tối đa 20 mục; tên cột chỉ dùng chữ cái, chữ số và _, còn giá trị tối đa 500 ký tự.",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey phải có từ 1 đến 512 ký tự (không có ký tự điều khiển).",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId là ID múi giờ mà máy chủ này không thể nhận diện.",
        [ServerMessageKeys.FeedLanguageInvalid] = "Hãy chỉ định Language là một trong các giá trị: {0}.",
    }.ToFrozenDictionary();
}
