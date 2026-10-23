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
        [ServerMessageKeys.EnrollmentRestartRequired] = "登録をやり直してください。パスワードをもう一度確認する必要があります。",
        [ServerMessageKeys.OperationTemporarilyLocked] = "失敗が続いたため、一時的に操作できません。しばらく待ってからやり直してください。",
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
        [ServerMessageKeys.AuditOffsetInvalid] = "offset は 0 以上の整数で指定してください。",
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
        [ServerMessageKeys.FeedRangeInvalid] = "PastDays・FutureDays は 0〜{0}（日）で指定してください。空なら無制限です。",
        [ServerMessageKeys.MappingSummaryColumnInvalid] = "SummaryColumn に指定できない列です: {0}",
        [ServerMessageKeys.MappingColumnInvalid] = "{0} に指定できない列です: {1}",
        [ServerMessageKeys.MappingDateColumnNoFormat] = "{0} は日付の列なので書式は要りません（文字の列に日付が入っているときだけ指定します）: {1}",
        [ServerMessageKeys.MappingDateColumnKind] = "{0} は日付の列（StartTime / CompletionTime / DateA〜DateZ / Date001〜Date100 など）か、日付が文字で入っている列（Class* / Description* / Title / Body）を指定してください: {1}",
        [ServerMessageKeys.MappingTextColumnNeedsFormat] = "{0} に文字の列を指定するときは、日付の書式（{1}）が要ります: {2}",
        [ServerMessageKeys.MappingDateFormatNotRoundTrip] = "書式「{0}」は、日付を読む書式として使えません（整形した値を同じ書式で読み戻せません）。",
        [ServerMessageKeys.MappingFormatsOnlyDateNumber] = "Formats に指定できるのは日付・数値の列だけです: {0}",
        [ServerMessageKeys.MappingSummaryTemplateColumnInvalid] = "SummaryTemplate に指定できない列です: {0}",
        [ServerMessageKeys.MappingSummaryTemplateNeedsFormat] = "SummaryTemplate: 日付・数値の列には書式が要ります（{{{0}:書式}} か Formats）: {0}",
        [ServerMessageKeys.MappingSummaryTemplateFormatError] = "SummaryTemplate の {{{0}:{1}}}: {2}",
        [ServerMessageKeys.MappingSummaryColumnNeedsFormat] = "SummaryColumn に日付・数値の列を指定するときは、Formats に書式が要ります: {0}",
        [ServerMessageKeys.MappingColumnNeedsFormat] = "{0} に日付・数値の列を指定するときは、Formats に書式が要ります: {1}",
        [ServerMessageKeys.TemplateTooLong] = "テンプレートは {0} 文字までです。",
        [ServerMessageKeys.TemplateBraceUnclosed] = "「{」が閉じていません。波括弧そのものは「{{」と書きます。",
        [ServerMessageKeys.TemplateColumnInvalid] = "列名が不正です: 「{0}」",
        [ServerMessageKeys.TemplateFormatEmpty] = "書式が空です: 「{0}:」",
        [ServerMessageKeys.TemplateBraceStray] = "「}」だけがあります。波括弧そのものは「}}」と書きます。",
        [ServerMessageKeys.SettingBooleanInvalid] = "{0} は真偽値（true / false）で指定してください。",
        [ServerMessageKeys.SettingTooLong] = "{0} は {1} 文字以内で入力してください。",
        [ServerMessageKeys.SettingControlChars] = "{0} に制御文字は使えません。",
        [ServerMessageKeys.SettingChoiceInvalid] = "{0} は {1} から選んでください。",
        [ServerMessageKeys.SettingIntegerInvalid] = "{0} は整数で指定してください。",
        [ServerMessageKeys.SettingMinimum] = "{0} は {1} 以上で指定してください。",
        [ServerMessageKeys.SettingMaximum] = "{0} は {1} 以下で指定してください。",
        [ServerMessageKeys.SettingUrlHttpsRequired] = "HTTPS の絶対 URL を入力してください。",
        [ServerMessageKeys.SettingUrlHttpRequired] = "http:// か https:// から始まる絶対 URL を入力してください。",
        [ServerMessageKeys.SettingTimeZoneRequired] = "タイムゾーンを指定してください（例: Asia/Tokyo）。",
        [ServerMessageKeys.SettingTimeZoneUnresolvable] = "このサーバが解決できないタイムゾーン ID です: {0}",
        [ServerMessageKeys.SettingApiVersionInvalid] = "Pleasanter の API バージョンは正の数（例: 1.1）で指定してください。",
        [ServerMessageKeys.SettingEmailInvalid] = "メールアドレスの形式が正しくありません。",
        [ServerMessageKeys.SettingNotManaged] = "管理対象ではない設定です: {0}",
        [ServerMessageKeys.SettingFixedExternally] = "{0} は外部設定で固定されているため、画面から変更できません。",
        [ServerMessageKeys.MailFromRequired] = "メール送信を有効にするには差出人メールアドレスが必要です。",
        [ServerMessageKeys.MailPublicUrlRequired] = "メール送信を有効にするには公開 URL が必要です（招待のリンクを作るため）。",
        [ServerMessageKeys.MailSmtpHostRequired] = "SMTP を使うには SMTP サーバーが必要です。",
        [ServerMessageKeys.MailSesRegionRequired] = "Amazon SES を使うにはリージョンが必要です。",
        [ServerMessageKeys.MailAcsEndpointRequired] = "Azure Communication Services を使うにはエンドポイントが必要です。",
        [ServerMessageKeys.SettingRequiredWhenEnabled] = "{0} を有効にしたときは {1} が要ります。",
        [ServerMessageKeys.SettingAbsoluteUrlInvalid] = "{0} は http(s) の絶対 URL で書いてください: {1}",
        [ServerMessageKeys.SettingUnknownValue] = "{0} に知らない値が入っています: {1}（使えるのは {2}）",
        [ServerMessageKeys.SettingBooleanValueInvalid] = "{0} は true か false で指定してください: {1}",
        [ServerMessageKeys.SettingIntegerRangeInvalid] = "{0} は {1} 以上 {2} 以下の整数で指定してください: {3}",
        [ServerMessageKeys.SamlCertificateRequired] = "{0} が要ります。IdP の署名証明書（PEM か base64）を入れてください。",
        [ServerMessageKeys.SamlLoginIdClaimRequired] = "{0}=Claim のときは {1} が要ります。",
        [ServerMessageKeys.SamlCertificatePemIncomplete] = "{0} の PEM が途中で終わっています。",
        [ServerMessageKeys.SamlCertificatePemUnreadable] = "{0} の PEM を読めませんでした。",
        [ServerMessageKeys.SamlCertificateNotFound] = "{0} に証明書が見つかりませんでした。",
        [ServerMessageKeys.SamlCertificateUnreadable] = "{0} を証明書として読めませんでした。PEM か、DER を base64 にしたものを入れてください。",
        [ServerMessageKeys.SsoCookieNameInvalidChars] = "{0} に cookie の名前として使えない文字があります: {1}",
        [ServerMessageKeys.SsoCookieNameOverlapsOwn] = "{0} に本アプリの cookie（{1} で始まる名前）と重なる指定があります: {2}",
        [ServerMessageKeys.SsoCookieNamesEmpty] = "{0} に転送する cookie の名前がありません。",
        [ServerMessageKeys.SsoInternalUrlInvalid] = "{0} は http(s) の絶対 URL で書いてください（問い合わせ文字列・資格情報は不可）: {1}",
        [ServerMessageKeys.SsoBrowserUrlInvalid] = "{0} は / で始まる同じホストのパスか、http(s) の絶対 URL で書いてください: {1}",
        [ServerMessageKeys.SsoIdListTooLong] = "{0} は正の ID を 64 件以内、1024 文字以内で指定してください。",
        [ServerMessageKeys.SsoIdListInvalid] = "{0} は正の ID をカンマか改行で区切って指定してください。",
        [ServerMessageKeys.MappingEndFormatWithoutEnd] = "EndColumn が無いときは EndFormat を指定できません。",
        [ServerMessageKeys.MappingDurationInvalid] = "DefaultDurationMinutes は 1〜1440 で指定してください。",
        [ServerMessageKeys.MappingFormatLengthInvalid] = "書式は 1〜100 文字（制御文字なし）で指定してください。",
        [ServerMessageKeys.MappingFormatsTooMany] = "Formats は 50 件までです。",
        [ServerMessageKeys.MappingSummaryTemplateNoColumns] = "SummaryTemplate に列が 1 つもありません（{列名} で指定します）。",
        [ServerMessageKeys.MappingNumberFormatUnusable] = "書式「{0}」は、数値の書式として使えません。",
        [ServerMessageKeys.MappingDateFormatUnusable] = "書式「{0}」は、日付の書式として使えません。",
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
        [ServerMessageKeys.EnrollmentRestartRequired] = "Start enrollment again and confirm your password once more.",
        [ServerMessageKeys.OperationTemporarilyLocked] = "This operation is temporarily locked after repeated failures. Wait a while and try again.",
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
        [ServerMessageKeys.AuditOffsetInvalid] = "Specify offset as an integer of 0 or more.",
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
        [ServerMessageKeys.FeedRangeInvalid] = "PastDays and FutureDays must be between 0 and {0} (days). Leave empty for unlimited.",
        [ServerMessageKeys.MappingSummaryColumnInvalid] = "SummaryColumn cannot use this column: {0}",
        [ServerMessageKeys.MappingColumnInvalid] = "{0} cannot use this column: {1}",
        [ServerMessageKeys.MappingDateColumnNoFormat] = "{0} is a date column, so no format is needed (specify one only when a text column holds the date): {1}",
        [ServerMessageKeys.MappingDateColumnKind] = "{0} must be a date column (StartTime / CompletionTime / DateA to DateZ / Date001 to Date100, etc.) or a column that holds the date as text (Class* / Description* / Title / Body): {1}",
        [ServerMessageKeys.MappingTextColumnNeedsFormat] = "When {0} is a text column, a date format ({1}) is required: {2}",
        [ServerMessageKeys.MappingDateFormatNotRoundTrip] = "The format \"{0}\" cannot be used to read dates (a formatted value cannot be read back with the same format).",
        [ServerMessageKeys.MappingFormatsOnlyDateNumber] = "Formats accepts only date and number columns: {0}",
        [ServerMessageKeys.MappingSummaryTemplateColumnInvalid] = "SummaryTemplate cannot use this column: {0}",
        [ServerMessageKeys.MappingSummaryTemplateNeedsFormat] = "SummaryTemplate: a date or number column needs a format ({{{0}:format}} or Formats): {0}",
        [ServerMessageKeys.MappingSummaryTemplateFormatError] = "SummaryTemplate {{{0}:{1}}}: {2}",
        [ServerMessageKeys.MappingSummaryColumnNeedsFormat] = "When SummaryColumn is a date or number column, a format is required in Formats: {0}",
        [ServerMessageKeys.MappingColumnNeedsFormat] = "When {0} is a date or number column, a format is required in Formats: {1}",
        [ServerMessageKeys.TemplateTooLong] = "The template can be at most {0} characters.",
        [ServerMessageKeys.TemplateBraceUnclosed] = "A \"{\" is not closed. To write a brace itself, use \"{{\".",
        [ServerMessageKeys.TemplateColumnInvalid] = "Invalid column name: \"{0}\"",
        [ServerMessageKeys.TemplateFormatEmpty] = "The format is empty: \"{0}:\"",
        [ServerMessageKeys.TemplateBraceStray] = "There is a stray \"}\". To write a brace itself, use \"}}\".",
        [ServerMessageKeys.SettingBooleanInvalid] = "{0} must be a boolean (true / false).",
        [ServerMessageKeys.SettingTooLong] = "{0} must be at most {1} characters.",
        [ServerMessageKeys.SettingControlChars] = "{0} cannot contain control characters.",
        [ServerMessageKeys.SettingChoiceInvalid] = "{0} must be one of {1}.",
        [ServerMessageKeys.SettingIntegerInvalid] = "{0} must be an integer.",
        [ServerMessageKeys.SettingMinimum] = "{0} must be {1} or greater.",
        [ServerMessageKeys.SettingMaximum] = "{0} must be {1} or less.",
        [ServerMessageKeys.SettingUrlHttpsRequired] = "Enter an absolute HTTPS URL.",
        [ServerMessageKeys.SettingUrlHttpRequired] = "Enter an absolute URL starting with http:// or https://.",
        [ServerMessageKeys.SettingTimeZoneRequired] = "Specify a time zone (for example Asia/Tokyo).",
        [ServerMessageKeys.SettingTimeZoneUnresolvable] = "This server cannot resolve the time zone ID: {0}",
        [ServerMessageKeys.SettingApiVersionInvalid] = "The Pleasanter API version must be a positive number (for example 1.1).",
        [ServerMessageKeys.SettingEmailInvalid] = "The email address format is invalid.",
        [ServerMessageKeys.SettingNotManaged] = "This setting is not managed here: {0}",
        [ServerMessageKeys.SettingFixedExternally] = "{0} is fixed by external configuration and cannot be changed from the screen.",
        [ServerMessageKeys.MailFromRequired] = "Enabling email sending requires a sender email address.",
        [ServerMessageKeys.MailPublicUrlRequired] = "Enabling email sending requires the public URL (to build invitation links).",
        [ServerMessageKeys.MailSmtpHostRequired] = "Using SMTP requires an SMTP server.",
        [ServerMessageKeys.MailSesRegionRequired] = "Using Amazon SES requires a region.",
        [ServerMessageKeys.MailAcsEndpointRequired] = "Using Azure Communication Services requires an endpoint.",
        [ServerMessageKeys.SettingRequiredWhenEnabled] = "When {0} is enabled, {1} is required.",
        [ServerMessageKeys.SettingAbsoluteUrlInvalid] = "{0} must be an absolute http(s) URL: {1}",
        [ServerMessageKeys.SettingUnknownValue] = "{0} has an unknown value: {1} (allowed: {2})",
        [ServerMessageKeys.SettingBooleanValueInvalid] = "{0} must be true or false: {1}",
        [ServerMessageKeys.SettingIntegerRangeInvalid] = "{0} must be an integer from {1} to {2}: {3}",
        [ServerMessageKeys.SamlCertificateRequired] = "{0} is required. Enter the IdP signing certificate (PEM or base64).",
        [ServerMessageKeys.SamlLoginIdClaimRequired] = "When {0}=Claim, {1} is required.",
        [ServerMessageKeys.SamlCertificatePemIncomplete] = "The PEM in {0} ends unexpectedly.",
        [ServerMessageKeys.SamlCertificatePemUnreadable] = "The PEM in {0} could not be read.",
        [ServerMessageKeys.SamlCertificateNotFound] = "No certificate was found in {0}.",
        [ServerMessageKeys.SamlCertificateUnreadable] = "{0} could not be read as a certificate. Enter a PEM, or a DER encoded as base64.",
        [ServerMessageKeys.SsoCookieNameInvalidChars] = "{0} contains characters that cannot be used in a cookie name: {1}",
        [ServerMessageKeys.SsoCookieNameOverlapsOwn] = "{0} overlaps with this app's own cookies (names starting with {1}): {2}",
        [ServerMessageKeys.SsoCookieNamesEmpty] = "{0} has no cookie names to forward.",
        [ServerMessageKeys.SsoInternalUrlInvalid] = "{0} must be an absolute http(s) URL (no query string or credentials): {1}",
        [ServerMessageKeys.SsoBrowserUrlInvalid] = "{0} must be a same-host path starting with / or an absolute http(s) URL: {1}",
        [ServerMessageKeys.SsoIdListTooLong] = "{0} must contain at most 64 positive IDs and at most 1024 characters.",
        [ServerMessageKeys.SsoIdListInvalid] = "{0} must be positive IDs separated by commas or line breaks.",
        [ServerMessageKeys.MappingEndFormatWithoutEnd] = "EndFormat cannot be specified without EndColumn.",
        [ServerMessageKeys.MappingDurationInvalid] = "DefaultDurationMinutes must be between 1 and 1440.",
        [ServerMessageKeys.MappingFormatLengthInvalid] = "A format must be 1 to 100 characters (no control characters).",
        [ServerMessageKeys.MappingFormatsTooMany] = "Formats allows at most 50 entries.",
        [ServerMessageKeys.MappingSummaryTemplateNoColumns] = "SummaryTemplate contains no columns (specify them as {ColumnName}).",
        [ServerMessageKeys.MappingNumberFormatUnusable] = "The format \"{0}\" cannot be used as a number format.",
        [ServerMessageKeys.MappingDateFormatUnusable] = "The format \"{0}\" cannot be used as a date format.",
    }.ToFrozenDictionary();
}
