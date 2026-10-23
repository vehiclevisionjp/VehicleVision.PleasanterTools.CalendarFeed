using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.Core.Web;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>Core の認証設定へ本アプリの値を渡し、設定の誤りを既存の多言語例外へ戻す。</summary>
public static class CalendarFeedAuthenticationSettings
{
    public const string Prefix = "CALENDARFEED_";
    public const string OwnCookiePrefix = "calendarfeed_";
    public static PleasanterSsoSettingKeys SsoKeys { get; } = new(Prefix);
    public static SamlSettingKeys SamlKeys { get; } = new(Prefix);

    public static PleasanterSsoOptions ReadSso(Func<string, string?> valueOf)
    {
        try
        {
            return PleasanterSsoOptions.FromValues(
                valueOf, SsoKeys, OwnCookiePrefix, AdminRoles.All, AdminRoles.Editor);
        }
        catch (AuthenticationSettingException exception)
        {
            throw Translate(exception);
        }
    }

    public static SamlOptions ReadSaml(Func<string, string?> valueOf)
    {
        try
        {
            return SamlOptions.FromValues(valueOf, SamlKeys, AdminRoles.All, AdminRoles.Editor);
        }
        catch (AuthenticationSettingException exception)
        {
            throw Translate(exception);
        }
    }

    private static SettingException Translate(AuthenticationSettingException exception)
    {
        var key = exception.Error switch
        {
            AuthenticationSettingError.RequiredWhenEnabled => ServerMessageKeys.SettingRequiredWhenEnabled,
            AuthenticationSettingError.BooleanValueInvalid => ServerMessageKeys.SettingBooleanValueInvalid,
            AuthenticationSettingError.IntegerRangeInvalid => ServerMessageKeys.SettingIntegerRangeInvalid,
            AuthenticationSettingError.UnknownValue => ServerMessageKeys.SettingUnknownValue,
            AuthenticationSettingError.CookieNameInvalidChars => ServerMessageKeys.SsoCookieNameInvalidChars,
            AuthenticationSettingError.CookieNameOverlapsOwn => ServerMessageKeys.SsoCookieNameOverlapsOwn,
            AuthenticationSettingError.CookieNamesEmpty => ServerMessageKeys.SsoCookieNamesEmpty,
            AuthenticationSettingError.InternalUrlInvalid => ServerMessageKeys.SsoInternalUrlInvalid,
            AuthenticationSettingError.BrowserUrlInvalid => ServerMessageKeys.SsoBrowserUrlInvalid,
            AuthenticationSettingError.IdListTooLong => ServerMessageKeys.SsoIdListTooLong,
            AuthenticationSettingError.IdListInvalid => ServerMessageKeys.SsoIdListInvalid,
            AuthenticationSettingError.AbsoluteUrlInvalid => ServerMessageKeys.SettingAbsoluteUrlInvalid,
            AuthenticationSettingError.CertificateRequired => ServerMessageKeys.SamlCertificateRequired,
            AuthenticationSettingError.LoginIdClaimRequired => ServerMessageKeys.SamlLoginIdClaimRequired,
            AuthenticationSettingError.CertificatePemIncomplete => ServerMessageKeys.SamlCertificatePemIncomplete,
            AuthenticationSettingError.CertificatePemUnreadable => ServerMessageKeys.SamlCertificatePemUnreadable,
            AuthenticationSettingError.CertificateNotFound => ServerMessageKeys.SamlCertificateNotFound,
            AuthenticationSettingError.CertificateUnreadable => ServerMessageKeys.SamlCertificateUnreadable,
            _ => throw new ArgumentOutOfRangeException(nameof(exception), exception.Error, "未対応の認証設定エラーです。"),
        };
        return new SettingException(key, [.. exception.Arguments], exception.InnerException);
    }
}
