using System.Collections.Frozen;
using System.Globalization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

/// <summary>
/// サーバが管理画面へ返す文言（7 言語）。Questionnaire の <c>ServerMessages</c> に則る。
/// </summary>
/// <remarks>
/// <para>
/// <b>言語の決め方</b>: 要求の言語 → <c>en</c> → <c>ja</c>（方針書 10 章）。対応外の言語コードは例外にせず、<c>en</c> へ落とす。
/// 地域（<c>en-US</c>）は見ない。
/// </para>
/// <para>
/// <b>ここに出るのは管理画面向けの文言だけ。</b>失敗の理由が漏れない方針は変えない
/// （ログインの失敗は、言語が変わるだけで、どの理由でも同じ文言）。
/// 設定の値・列の対応などの動的な検証メッセージ（<c>AppSettingDefinitions</c>・<c>FeedMapping</c>）は、まだ日本語だけ。
/// </para>
/// <para>文言の言語ごとの定義は、<c>ServerMessages.{言語}.cs</c>。</para>
/// </remarks>
public static partial class ServerMessages
{
    private const string FallbackLanguage = "en";
    private const string LastResortLanguage = "ja";

    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> Catalog = new(() =>
        new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["ja"] = Japanese(),
            ["en"] = English(),
            ["zh"] = Chinese(),
            ["de"] = German(),
            ["ko"] = Korean(),
            ["es"] = Spanish(),
            ["vi"] = Vietnamese(),
        }.ToFrozenDictionary());

    /// <summary>対応する言語。</summary>
    public static IReadOnlyCollection<string> Languages => Catalog.Value.Keys;

    /// <summary>その言語の文言を返す。<paramref name="args"/> は <c>{0}</c> から順に埋める。</summary>
    public static string Get(string key, string? language, params object[] args)
    {
        var template = Resolve(key, language);
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>その言語が、この鍵の文言を持っているか（落とし先を使わずに）。</summary>
    public static bool Has(string key, string language) =>
        Catalog.Value.TryGetValue(language, out var messages) && messages.ContainsKey(key);

    private static string Resolve(string key, string? language)
    {
        var code = Primary(language);
        foreach (var candidate in new[] { code, FallbackLanguage, LastResortLanguage })
        {
            if (candidate is not null
                && Catalog.Value.TryGetValue(candidate, out var messages)
                && messages.TryGetValue(key, out var text))
            {
                return text;
            }
        }

        // 鍵そのものを返す（抜けに気づける。例外にして画面ごと壊さない）
        return key;
    }

    /// <summary>言語のコードの主要部分（<c>en-US</c> → <c>en</c>）。対応外は <c>null</c>。</summary>
    private static string? Primary(string? language)
    {
        var code = language?.Trim().Split('-', '_')[0].ToLowerInvariant();
        return code is not null && Catalog.Value.ContainsKey(code) ? code : null;
    }

    private static FrozenDictionary<string, string> Japanese() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "ログイン ID またはパスワードが違います。何度も失敗した場合は、しばらく待ってからやり直してください。",
        [ServerMessageKeys.CrossSiteRejected] = "別のサイトからの要求は受け付けません。",
        [ServerMessageKeys.LoginRequired] = "ログインが必要です。",
        [ServerMessageKeys.UpstreamSessionEnded] = "Pleasanter に確かめられなかったため、ログインを終了しました。しばらくしてからやり直してください。",
        [ServerMessageKeys.PermissionRequired] = "この操作には権限（{0}）が要ります。",
        [ServerMessageKeys.AdministratorAlreadyExists] = "最初の管理者はすでに作られています。",
        [ServerMessageKeys.CurrentPasswordRejected] = "現在のパスワードが違います。",
        [ServerMessageKeys.PasswordRejected] = "パスワードが違います。",
        [ServerMessageKeys.TwoFactorDisabled] = "2 要素は無効に設定されています。",
        [ServerMessageKeys.TwoFactorRequired] = "2 要素は必須に設定されているため、解除できません。",
        [ServerMessageKeys.EnrollmentBeginRequired] = "先に登録を始めてください（/admin/api/me/totp/begin）。",
        [ServerMessageKeys.TotpCodeMismatch] = "数字が違います。認証アプリに表示されている 6 桁を入力してください。",
        [ServerMessageKeys.LoginIdInvalid] = "ログイン ID は 1〜100 文字（制御文字なし）で指定してください。",
        [ServerMessageKeys.PasswordTooShort] = "パスワードは {0} 文字以上にしてください。",
        [ServerMessageKeys.PasswordTooLong] = "パスワードは {0} 文字以下にしてください。",
        [ServerMessageKeys.PasswordSameAsLoginId] = "パスワードにログイン ID と同じものは使えません。",
        [ServerMessageKeys.PasswordPolicyMismatch] = "パスワードが条件を満たしていません。",
        [ServerMessageKeys.AdminUserNotFound] = "管理者が見つかりません。",
        [ServerMessageKeys.RoleNotSupported] = "役割は {0} のいずれかで指定してください。",
        [ServerMessageKeys.DuplicateLoginId] = "そのログイン ID はすでにあります。",
        [ServerMessageKeys.InviteDisabledUser] = "無効化されている管理者へは招待を出せません。先に有効へ戻してください。",
        [ServerMessageKeys.SelfDisableNotAllowed] = "自分自身は無効化できません。",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "自分自身の役割は変えられません。",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "自分の 2 要素は、自分の画面（/admin/api/me/totp/disable）から解除してください。",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "自分の端末は、自分の画面（/admin/api/me/sessions）から失効させてください。",
        [ServerMessageKeys.LastAdministrator] = "有効な管理者（Administrator）が 1 人も居なくなるため、できません。先に別の管理者を用意してください。",
        [ServerMessageKeys.InvitationInvalid] = "この招待は使えません（期限切れ・受け取り済み・取り消された可能性があります）。招待した管理者に出し直しを頼んでください。",
        [ServerMessageKeys.SessionNotFound] = "その端末は見つかりません。",
        [ServerMessageKeys.AuditBeforeInvalid] = "before は日時（ISO 8601）で指定してください。",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "API キーを保管するには {0} の設定が要ります。",
        [ServerMessageKeys.ApiKeyRequired] = "ApiKey を指定してください（全フィード共通の既定の API キーも未設定です）。",
        [ServerMessageKeys.SettingsValuesRequired] = "values に、変える設定を指定してください。",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "秘密の設定を保管するには {0} の設定が要ります。",
        [ServerMessageKeys.SamlNeedsSecretKey] = "SAML ログインには秘密の鍵（{0}）の設定が要ります。",
        [ServerMessageKeys.FeedNameInvalid] = "Name は 1〜200 文字で指定してください。",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId は 1 以上で指定してください。",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters は 20 件まで、列名は英数字と _ 、値は 500 文字までです。",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey は 1〜512 文字（制御文字なし）で指定してください。",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId に、このサーバが解決できないタイムゾーン ID が指定されています。",
        [ServerMessageKeys.FeedLanguageInvalid] = "Language は {0} のいずれかで指定してください。",
    }.ToFrozenDictionary();

    private static FrozenDictionary<string, string> English() => new Dictionary<string, string>
    {
        [ServerMessageKeys.InvalidCredentials] = "The login ID or password is incorrect. If this keeps happening, wait a while and try again.",
        [ServerMessageKeys.CrossSiteRejected] = "Requests from another site are not accepted.",
        [ServerMessageKeys.LoginRequired] = "You need to sign in.",
        [ServerMessageKeys.UpstreamSessionEnded] = "The sign-in was ended because Pleasanter could not be reached to verify it. Please try again later.",
        [ServerMessageKeys.PermissionRequired] = "This operation requires the permission ({0}).",
        [ServerMessageKeys.AdministratorAlreadyExists] = "The first administrator has already been created.",
        [ServerMessageKeys.CurrentPasswordRejected] = "The current password is incorrect.",
        [ServerMessageKeys.PasswordRejected] = "The password is incorrect.",
        [ServerMessageKeys.TwoFactorDisabled] = "Two-factor authentication is disabled.",
        [ServerMessageKeys.TwoFactorRequired] = "Two-factor authentication is required, so it cannot be removed.",
        [ServerMessageKeys.EnrollmentBeginRequired] = "Start the enrollment first (/admin/api/me/totp/begin).",
        [ServerMessageKeys.TotpCodeMismatch] = "The code is incorrect. Enter the 6 digits shown in your authenticator app.",
        [ServerMessageKeys.LoginIdInvalid] = "The login ID must be 1 to 100 characters (no control characters).",
        [ServerMessageKeys.PasswordTooShort] = "The password must be at least {0} characters.",
        [ServerMessageKeys.PasswordTooLong] = "The password must be at most {0} characters.",
        [ServerMessageKeys.PasswordSameAsLoginId] = "The password cannot be the same as the login ID.",
        [ServerMessageKeys.PasswordPolicyMismatch] = "The password does not meet the requirements.",
        [ServerMessageKeys.AdminUserNotFound] = "The administrator was not found.",
        [ServerMessageKeys.RoleNotSupported] = "Specify the role as one of {0}.",
        [ServerMessageKeys.DuplicateLoginId] = "That login ID already exists.",
        [ServerMessageKeys.InviteDisabledUser] = "You cannot invite a disabled administrator. Enable the account first.",
        [ServerMessageKeys.SelfDisableNotAllowed] = "You cannot disable yourself.",
        [ServerMessageKeys.SelfRoleChangeNotAllowed] = "You cannot change your own role.",
        [ServerMessageKeys.SelfTotpResetNotAllowed] = "Remove your own two-factor authentication from your own screen (/admin/api/me/totp/disable).",
        [ServerMessageKeys.SelfSessionRevokeNotAllowed] = "Revoke your own devices from your own screen (/admin/api/me/sessions).",
        [ServerMessageKeys.LastAdministrator] = "This would leave no active administrator (Administrator). Prepare another administrator first.",
        [ServerMessageKeys.InvitationInvalid] = "This invitation cannot be used (it may have expired, been accepted, or been cancelled). Ask the administrator who invited you to send it again.",
        [ServerMessageKeys.SessionNotFound] = "That device was not found.",
        [ServerMessageKeys.AuditBeforeInvalid] = "Specify before as a date and time (ISO 8601).",
        [ServerMessageKeys.ApiKeyNeedsSecretKey] = "Storing an API key requires the setting {0}.",
        [ServerMessageKeys.ApiKeyRequired] = "Specify ApiKey (the default API key shared by all feeds is not set either).",
        [ServerMessageKeys.SettingsValuesRequired] = "Specify the settings to change in values.",
        [ServerMessageKeys.SecretSettingNeedsSecretKey] = "Storing a secret setting requires the setting {0}.",
        [ServerMessageKeys.SamlNeedsSecretKey] = "SAML sign-in requires the secret key setting ({0}).",
        [ServerMessageKeys.FeedNameInvalid] = "Name must be 1 to 200 characters.",
        [ServerMessageKeys.FeedSiteIdInvalid] = "SiteId must be 1 or greater.",
        [ServerMessageKeys.FeedFiltersInvalid] = "Filters allows up to 20 entries; column names use letters, digits and _ only, and values are at most 500 characters.",
        [ServerMessageKeys.FeedApiKeyInvalid] = "ApiKey must be 1 to 512 characters (no control characters).",
        [ServerMessageKeys.FeedTimeZoneInvalid] = "ApiKeyUserTimeZoneId is a time zone ID this server cannot resolve.",
        [ServerMessageKeys.FeedLanguageInvalid] = "Specify Language as one of {0}.",
    }.ToFrozenDictionary();
}
