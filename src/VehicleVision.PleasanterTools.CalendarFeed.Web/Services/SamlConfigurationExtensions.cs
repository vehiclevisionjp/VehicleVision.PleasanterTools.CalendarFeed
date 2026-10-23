using System.IdentityModel.Selectors;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.Core.Web;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>Core の設定を、本アプリが使う ITfoxtec の照合設定へ変換する。</summary>
public static class SamlConfigurationExtensions
{
    /// <summary>設定の証明書だけを信頼し、発行者・宛先・署名の種類を固定する。</summary>
    public static Saml2Configuration ToSaml2Configuration(this SamlOptions options)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("SAML は無効です。");
        }

        var configuration = new Saml2Configuration
        {
            Issuer = options.EntityId,
            SingleSignOnDestination = options.SingleSignOnUrl,
            SingleLogoutDestination = options.SingleLogoutUrl,
            AllowedIssuer = options.IdpEntityId,
            AudienceRestricted = true,
            // 信頼の起点は設定の証明書そのもの。自己署名の IdP も使うため連鎖検証はしない。
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        configuration.AllowedAudienceUris.Add(options.EntityId);
        configuration.SignatureValidationCertificates.AddRange(options.IdpCertificates);
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha256Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha384Signature);
        configuration.SignatureValidationAlgorithms.Add(SecurityAlgorithms.RsaSha512Signature);

        return configuration;
    }
}
