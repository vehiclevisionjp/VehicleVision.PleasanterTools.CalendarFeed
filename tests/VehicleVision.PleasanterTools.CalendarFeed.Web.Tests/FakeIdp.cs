using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Tokens.Saml2;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>
/// テスト用の IdP（SAML 2.0）。自己署名の証明書で、SP（本アプリ）へ返す応答に署名する。
/// 本物の IdP を用意せずに、署名・宛先・受信者・InResponseTo の検証を機械で確かめるためのもの。
/// </summary>
internal sealed class FakeIdp : IDisposable
{
    public const string EntityId = "https://idp.test/entity";
    public const string SingleSignOnUrl = "https://idp.test/sso";
    public const string SingleLogoutUrl = "https://idp.test/slo";
    public const string SpEntityId = "https://feed.test/admin/api/saml/metadata";
    public static readonly Uri AcsUrl = new("http://feed.test/admin/api/saml/acs");

    public FakeIdp(string subject = "CN=fake-idp")
    {
        Key = RSA.Create(2048);
        var request = new CertificateRequest(subject, Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public RSA Key { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>SP へ登録する証明書（PEM）。</summary>
    public string CertificatePem => Certificate.ExportCertificatePem();

    public void Dispose()
    {
        Certificate.Dispose();
        Key.Dispose();
    }

    /// <summary>SP が出した AuthnRequest（リダイレクト結合）から、要求の ID を読む。</summary>
    public static string RequestIdOf(string redirectLocation)
    {
        var query = HttpUtility.ParseQueryString(new Uri(redirectLocation).Query);
        var deflated = Convert.FromBase64String(query["SAMLRequest"]!);
        using var inflater = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        return Regex.Match(reader.ReadToEnd(), "ID=\"([^\"]+)\"").Groups[1].Value;
    }

    /// <summary>署名した応答（POST 結合の SAMLResponse）を作る。</summary>
    public string Response(
        string inResponseTo,
        string nameId,
        string? sessionIndex = "session-1",
        string? audience = null,
        string? issuer = null,
        Uri? destination = null,
        X509Certificate2? signWith = null,
        bool success = true,
        IEnumerable<Claim>? extraClaims = null)
    {
        var configuration = new Saml2Configuration
        {
            Issuer = issuer ?? EntityId,
            SigningCertificate = signWith ?? Certificate,
            SignatureAlgorithm = Saml2SecurityAlgorithms.RsaSha256Signature,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        var claims = new List<Claim> { new(Saml2ClaimTypes.NameId, nameId), new(Saml2ClaimTypes.NameIdFormat, NameIdentifierFormats.Email.OriginalString) };
        if (sessionIndex is not null)
        {
            claims.Add(new Claim(Saml2ClaimTypes.SessionIndex, sessionIndex));
        }

        claims.AddRange(extraClaims ?? []);

        var response = new Saml2AuthnResponse(configuration)
        {
            Status = success ? Saml2StatusCodes.Success : Saml2StatusCodes.Responder,
            InResponseTo = new Saml2Id(inResponseTo),
            Destination = destination ?? AcsUrl,
            NameId = new Saml2NameIdentifier(nameId, new Uri(NameIdentifierFormats.Email.OriginalString)),
            ClaimsIdentity = new ClaimsIdentity(claims),
        };
        if (success)
        {
            response.CreateSecurityToken(audience ?? SpEntityId, subjectConfirmationLifetime: 5, issuedTokenLifetime: 60);
        }

        var binding = new Saml2PostBinding();
        binding.Bind(response);
        return Regex.Match(binding.PostContent, "name=\"SAMLResponse\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    /// <summary>署名した LogoutRequest（POST 結合の SAMLRequest）を作る。</summary>
    public string LogoutRequest(string nameId, string? sessionIndex)
    {
        var configuration = new Saml2Configuration
        {
            Issuer = EntityId,
            SigningCertificate = Certificate,
            SignatureAlgorithm = Saml2SecurityAlgorithms.RsaSha256Signature,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        var request = new Saml2LogoutRequest(configuration)
        {
            Destination = new Uri("http://feed.test/admin/api/saml/slo"),
            NameId = new Saml2NameIdentifier(nameId),
            SessionIndex = sessionIndex,
        };
        var binding = new Saml2PostBinding();
        binding.Bind(request);
        return Regex.Match(binding.PostContent, "name=\"SAMLRequest\" value=\"([^\"]+)\"").Groups[1].Value;
    }
}
