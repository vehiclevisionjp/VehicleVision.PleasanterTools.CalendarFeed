using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;
using VehicleVision.PleasanterTools.Core.Web;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>Core へ渡す本アプリ固有の値と、多言語例外・SAML 実装との境界。</summary>
public sealed class AuthenticationSettingsTests
{
    [Fact]
    public void Coreの設定キーは既存の設定定義と全件一致する()
    {
        foreach (var (keys, group, prefix) in new (object, string, string)[]
        {
            (CalendarFeedAuthenticationSettings.SsoKeys, AppSettingGroups.PleasanterSso, "CALENDARFEED_PLEASANTERSSO_"),
            (CalendarFeedAuthenticationSettings.SamlKeys, AppSettingGroups.Saml, "CALENDARFEED_SAML_"),
        })
        {
            var actual = keys.GetType().GetProperties().Select(p => Assert.IsType<string>(p.GetValue(keys))).Order().ToArray();
            var expected = AppSettingDefinitions.All.Where(d => d.Group == group).Select(d => d.Key).Order().ToArray();
            Assert.Equal(expected, actual);
            Assert.All(actual, key => Assert.StartsWith(prefix, key));
        }
    }

    [Fact]
    public void 未設定の既定値と本アプリcookieの除外を維持する()
    {
        var sso = CalendarFeedAuthenticationSettings.ReadSso(_ => null);
        var saml = CalendarFeedAuthenticationSettings.ReadSaml(_ => null);

        Assert.False(sso.Enabled);
        Assert.False(saml.Enabled);
        Assert.Equal("Editor", sso.RegisterRole);
        Assert.Equal("Editor", saml.RegisterRole);
        Assert.Equal(PleasanterSsoUnknownUserPolicy.Reject, sso.UnknownUser);
        Assert.Equal(SamlUnknownUserPolicy.Reject, saml.UnknownUser);
        Assert.Equal(SamlLoginIdSource.NameId, saml.LoginIdSource);
        Assert.Equal(TimeSpan.FromMinutes(5), sso.RevalidateInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), sso.Timeout);
        Assert.Equal([".AspNetCore.Cookies", "Pleasanter_SessionGuid"], sso.CookieNamePrefixes.ToArray());
        Assert.Equal("calendarfeed_", sso.OwnCookiePrefix);
        Assert.False(sso.ShouldForwardCookie("CALENDARFEED_admin"));
        Assert.True(sso.ShouldForwardCookie(".AspNetCore.CookiesC1"));
        Assert.False(sso.HasMembershipRestriction);
        Assert.Empty(sso.LoginUrl);
        Assert.Empty(sso.LogoutUrl);
        Assert.Empty(sso.ButtonLabel);
        Assert.False(saml.SingleLogoutEnabled);
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("FeedAdministrator")]
    [InlineData("UserAdministrator")]
    [InlineData("Editor")]
    [InlineData("Auditor")]
    public void 本アプリの役割名だけを正規名へ戻す(string role)
    {
        using var idp = new FakeIdp();
        var values = SamlValues(idp);
        values["CALENDARFEED_SAML_REGISTERROLE"] = role.ToLowerInvariant();
        Assert.Equal(role, CalendarFeedAuthenticationSettings.ReadSaml(values.GetValueOrDefault).RegisterRole);
        Assert.Equal(role, CalendarFeedAuthenticationSettings.ReadSso(
            key => key == "CALENDARFEED_PLEASANTERSSO_REGISTERROLE" ? role.ToLowerInvariant() : null).RegisterRole);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("SurveyAdministrator")]
    [InlineData("Editor, Administrator")]
    public void 他アプリの役割や数値や複合表記は受理しない(string role)
    {
        using var idp = new FakeIdp();
        var values = SamlValues(idp);
        values["CALENDARFEED_SAML_REGISTERROLE"] = role;
        var saml = Assert.Throws<SettingException>(() =>
            CalendarFeedAuthenticationSettings.ReadSaml(values.GetValueOrDefault));
        var sso = Assert.Throws<SettingException>(() => CalendarFeedAuthenticationSettings.ReadSso(
            key => key == "CALENDARFEED_PLEASANTERSSO_REGISTERROLE" ? role : null));

        AssertError(saml, ServerMessageKeys.SettingUnknownValue,
            ["CALENDARFEED_SAML_REGISTERROLE", role, string.Join(" / ", AdminRoles.All)]);
        AssertError(sso, ServerMessageKeys.SettingUnknownValue,
            ["CALENDARFEED_PLEASANTERSSO_REGISTERROLE", role, string.Join(" / ", AdminRoles.All)]);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("false")]
    [InlineData("")]
    public void SAML無効時は他の設定を先読みしない(string enabled)
    {
        var saml = CalendarFeedAuthenticationSettings.ReadSaml(key =>
            key == "CALENDARFEED_SAML_ENABLED" ? enabled : throw new InvalidOperationException(key));
        Assert.False(saml.Enabled);
    }

    public static IEnumerable<object[]> SsoErrors()
    {
        yield return ["ENABLED", "yes", ServerMessageKeys.SettingBooleanValueInvalid, new object[] { "yes" }];
        yield return ["ENABLED", "true", ServerMessageKeys.SettingRequiredWhenEnabled,
            new object[] { "CALENDARFEED_PLEASANTERSSO_INTERNALBASEURL" }];
        yield return ["REVALIDATEMINUTES", "0", ServerMessageKeys.SettingIntegerRangeInvalid, new object[] { 1, 60, "0" }];
        yield return ["TIMEOUTSECONDS", "31", ServerMessageKeys.SettingIntegerRangeInvalid, new object[] { 1, 30, "31" }];
        yield return ["UNKNOWNUSER", "1", ServerMessageKeys.SettingUnknownValue, new object[] { "1", "Reject / Register" }];
        yield return ["COOKIENAMES", "bad name", ServerMessageKeys.SsoCookieNameInvalidChars, new object[] { "bad name" }];
        yield return ["COOKIENAMES", "CALENDARFEED_admin", ServerMessageKeys.SsoCookieNameOverlapsOwn,
            new object[] { "calendarfeed_", "CALENDARFEED_admin" }];
        yield return ["COOKIENAMES", ",", ServerMessageKeys.SsoCookieNamesEmpty, Array.Empty<object>()];
        yield return ["INTERNALBASEURL", "/relative", ServerMessageKeys.SsoInternalUrlInvalid, new object[] { "/relative" }];
        yield return ["LOGINURL", "//evil.test", ServerMessageKeys.SsoBrowserUrlInvalid, new object[] { "//evil.test" }];
        yield return ["ALLOWEDDEPTIDS", new string('1', 1025), ServerMessageKeys.SsoIdListTooLong, Array.Empty<object>()];
        yield return ["ALLOWEDGROUPIDS", "0", ServerMessageKeys.SsoIdListInvalid, Array.Empty<object>()];
    }

    [Theory]
    [MemberData(nameof(SsoErrors))]
    public void SSOの構造化例外を既存の鍵と引数と多言語文言へ戻す(
        string suffix, string value, string messageKey, object[] remainingArgs)
    {
        var key = "CALENDARFEED_PLEASANTERSSO_" + suffix;
        var exception = Assert.Throws<SettingException>(() =>
            CalendarFeedAuthenticationSettings.ReadSso(name => name == key ? value : null));
        AssertError(exception, messageKey, [key, .. remainingArgs]);
        Assert.Null(exception.InnerException);
    }

    public static IEnumerable<object[]> SamlErrors()
    {
        yield return ["ENTITYID", "", ServerMessageKeys.SettingRequiredWhenEnabled,
            new object[] { "CALENDARFEED_SAML_ENABLED", "CALENDARFEED_SAML_ENTITYID" }, false];
        yield return ["SINGLESIGNONURL", "relative", ServerMessageKeys.SettingAbsoluteUrlInvalid,
            new object[] { "CALENDARFEED_SAML_SINGLESIGNONURL", "relative" }, false];
        yield return ["SINGLELOGOUTURL", "relative", ServerMessageKeys.SettingAbsoluteUrlInvalid,
            new object[] { "CALENDARFEED_SAML_SINGLELOGOUTURL", "relative" }, false];
        yield return ["IDPCERTIFICATE", "", ServerMessageKeys.SamlCertificateRequired,
            new object[] { "CALENDARFEED_SAML_IDPCERTIFICATE" }, false];
        yield return ["LOGINIDSOURCE", "Claim", ServerMessageKeys.SamlLoginIdClaimRequired,
            new object[] { "CALENDARFEED_SAML_LOGINIDSOURCE", "CALENDARFEED_SAML_LOGINIDCLAIM" }, false];
        yield return ["IDPCERTIFICATE", "-----BEGIN CERTIFICATE-----", ServerMessageKeys.SamlCertificatePemIncomplete,
            new object[] { "CALENDARFEED_SAML_IDPCERTIFICATE" }, false];
        yield return ["IDPCERTIFICATE", "-----BEGIN CERTIFICATE-----\nbad\n-----END CERTIFICATE-----",
            ServerMessageKeys.SamlCertificatePemUnreadable, new object[] { "CALENDARFEED_SAML_IDPCERTIFICATE" }, true];
        yield return ["IDPCERTIFICATE", "!", ServerMessageKeys.SamlCertificateUnreadable,
            new object[] { "CALENDARFEED_SAML_IDPCERTIFICATE" }, true];
        yield return ["UNKNOWNUSER", "unknown", ServerMessageKeys.SettingUnknownValue,
            new object[] { "CALENDARFEED_SAML_UNKNOWNUSER", "unknown", "Reject / Register" }, false];
    }

    [Theory]
    [MemberData(nameof(SamlErrors))]
    public void SAMLの構造化例外を既存の鍵と引数と元の例外へ戻す(
        string suffix, string value, string messageKey, object[] args, bool hasInnerException)
    {
        using var idp = new FakeIdp();
        var values = SamlValues(idp);
        values["CALENDARFEED_SAML_" + suffix] = value;
        var exception = Assert.Throws<SettingException>(() =>
            CalendarFeedAuthenticationSettings.ReadSaml(values.GetValueOrDefault));
        AssertError(exception, messageKey, args);
        if (hasInnerException)
        {
            Assert.True(exception.InnerException is ArgumentException or CryptographicException or FormatException);
        }
        else
        {
            Assert.Null(exception.InnerException);
        }
    }

    [Fact]
    public void SAMLの数値の受理とITfoxtecの信頼設定を維持する()
    {
        using var idp = new FakeIdp();
        var values = SamlValues(idp);
        values["CALENDARFEED_SAML_UNKNOWNUSER"] = "1";
        values["CALENDARFEED_SAML_LOGINIDSOURCE"] = "0";
        values["CALENDARFEED_SAML_SINGLELOGOUTURL"] = FakeIdp.SingleLogoutUrl;
        var options = CalendarFeedAuthenticationSettings.ReadSaml(values.GetValueOrDefault);
        var configuration = options.ToSaml2Configuration();

        Assert.Equal(SamlUnknownUserPolicy.Register, options.UnknownUser);
        Assert.Equal(SamlLoginIdSource.NameId, options.LoginIdSource);
        Assert.Equal(FakeIdp.SpEntityId, configuration.Issuer);
        Assert.Equal(FakeIdp.EntityId, configuration.AllowedIssuer);
        Assert.True(configuration.AudienceRestricted);
        Assert.Equal(System.ServiceModel.Security.X509CertificateValidationMode.None, configuration.CertificateValidationMode);
        Assert.Equal(System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck, configuration.RevocationMode);
        Assert.Equal([FakeIdp.SpEntityId], configuration.AllowedAudienceUris);
        Assert.Equal(idp.Certificate.RawData, Assert.Single(configuration.SignatureValidationCertificates).RawData);
        Assert.Equal(new Uri(FakeIdp.SingleSignOnUrl), configuration.SingleSignOnDestination);
        Assert.Equal(new Uri(FakeIdp.SingleLogoutUrl), configuration.SingleLogoutDestination);
        Assert.Equal(
            [SecurityAlgorithms.RsaSha256Signature, SecurityAlgorithms.RsaSha384Signature, SecurityAlgorithms.RsaSha512Signature],
            configuration.SignatureValidationAlgorithms);
        var disabled = CalendarFeedAuthenticationSettings.ReadSaml(_ => null);
        Assert.Equal("SAML は無効です。",
            Assert.Throws<InvalidOperationException>(() => disabled.ToSaml2Configuration()).Message);
    }

    private static Dictionary<string, string?> SamlValues(FakeIdp idp) => new()
    {
        ["CALENDARFEED_SAML_ENABLED"] = "true",
        ["CALENDARFEED_SAML_ENTITYID"] = FakeIdp.SpEntityId,
        ["CALENDARFEED_SAML_IDPENTITYID"] = FakeIdp.EntityId,
        ["CALENDARFEED_SAML_SINGLESIGNONURL"] = FakeIdp.SingleSignOnUrl,
        ["CALENDARFEED_SAML_IDPCERTIFICATE"] = idp.CertificatePem,
    };

    private static void AssertError(SettingException exception, string messageKey, object[] args)
    {
        Assert.Equal(messageKey, exception.MessageKey);
        Assert.Equal(args, exception.Args);
        Assert.Equal(ServerMessages.Get(messageKey, "ja", args), exception.Message);
    }
}
