using System.Security.Claims;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using Microsoft.IdentityModel.Tokens.Saml2;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>SAML 2.0 で管理画面へ入る（SP。Questionnaire の <c>AdminSamlEndpoints</c> に則る）。<b>既定は無効。</b></summary>
/// <remarks>
/// <para>
/// 設定は管理画面から編集する（<c>CALENDARFEED_SAML_*</c>。外部設定に値があれば固定）。
/// 認可は本アプリの役割で行い、IdP が証明するのは「その人が誰か」だけ。
/// </para>
/// <para>
/// <b>応答は必ず署名を検証する</b>（IdP の署名証明書は設定）。宛先・発行者・受信者（Audience）・有効期間を見る。
/// <b>こちらの出した要求への応答だけを受け付ける</b>（<c>InResponseTo</c> を、要求を出したときに置いた相関 cookie と突き合わせる）。
/// IdP からの応答は他サイトからの POST で戻るので、相関 cookie は <c>SameSite=None; Secure</c>（HTTPS が前提）で、パスは SAML の入口の下だけ、中身は暗号化する。
/// </para>
/// <para>
/// 失敗は、画面へ戻して <c>?samlError=</c> で伝える（<c>invalid</c> / <c>unknown-user</c> / <c>disabled</c>。詳しい理由はログと監査ログ）。
/// </para>
/// </remarks>
public static class AdminSamlEndpoints
{
    private const string RelayCookieName = "calendarfeed_saml";
    private const string ProtectorContext = "calendarfeed:saml-relay";
    private const string Entry = "/admin/";
    private static readonly TimeSpan RelayLifetime = TimeSpan.FromMinutes(15);

    /// <summary>SAML の入口を登録する。</summary>
    public static void MapAdminSaml(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api/saml");

        // SP のメタデータ（IdP へ渡す。有効なときだけ）
        group.MapGet("/metadata", (HttpContext http, RuntimeSettings settings) =>
        {
            var options = settings.Current.Saml;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var descriptor = new EntityDescriptor(options.ToSaml2Configuration())
            {
                ValidUntil = 365,
                SPSsoDescriptor = new SPSsoDescriptor
                {
                    AuthnRequestsSigned = false,
                    WantAssertionsSigned = true,
                    NameIDFormats = [NameIdentifierFormats.Email],
                    AssertionConsumerServices =
                    [
                        new AssertionConsumerService { Binding = ProtocolBindings.HttpPost, Location = AssertionConsumerServiceUrl(http, settings) },
                    ],
                },
            };
            return Results.Content(new Saml2Metadata(descriptor).CreateMetadata().ToXml(), "application/samlmetadata+xml");
        });

        // ログインの開始: IdP へ AuthnRequest を渡す（リダイレクト結合）
        group.MapGet("/login", (HttpContext http, RuntimeSettings settings, ISecretProtector protection, string? returnUrl) =>
        {
            var options = settings.Current.Saml;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            if (!protection.IsConfigured)
            {
                // 相関 cookie を暗号化する鍵（CALENDARFEED_SECRET_KEY）が要る。複数台・再起動でも読めるよう、ASP.NET の既定の鍵束は使わない
                return Results.Problem("SAML ログインには秘密の鍵（CALENDARFEED_SECRET_KEY）の設定が要ります。", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var request = new Saml2AuthnRequest(options.ToSaml2Configuration())
            {
                Destination = options.SingleSignOnUrl,
                AssertionConsumerServiceUrl = AssertionConsumerServiceUrl(http, settings),
                ProtocolBinding = ProtocolBindings.HttpPost,
            };
            var binding = new Saml2RedirectBinding();
            binding.Bind(request);

            // 要求の識別子を、応答の InResponseTo と突き合わせるために、暗号化した cookie へ置く
            SaveRelay(http, protection, request.IdAsString, LocalReturnUrl(returnUrl));
            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireRateLimiting("adminLogin");

        // IdP からの応答（POST）
        group.MapPost("/acs", async (
            HttpContext http,
            RuntimeSettings settings,
            SamlAuthenticator authenticator,
            IAdminSessionStore sessions,
            IAdminUserStore users,
            AuditLogService audit,
            ISecretProtector protection,
            TimeProvider time,
            ILogger<SamlAuthenticator> logger,
            CancellationToken ct) =>
        {
            var config = settings.Current;
            var options = config.Saml;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var relay = ReadRelay(http, protection);
            ClearRelay(http);
            if (relay is null)
            {
                logger.LogWarning("SAML の応答に、対応する要求が見つかりませんでした。");
                await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, detail: "no-relay");
                return Failed(SamlFailure.Invalid);
            }

            var response = new Saml2AuthnResponse(options.ToSaml2Configuration());
            try
            {
                var httpRequest = http.Request.ToGenericHttpRequest(validate: true);
                httpRequest.Binding.ReadSamlResponse(httpRequest, response);
                if (response.Status != Saml2StatusCodes.Success)
                {
                    logger.LogWarning("IdP が認証を断りました: {Status}", response.Status);
                    await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, detail: $"idp-status={response.Status}");
                    return Failed(SamlFailure.Invalid);
                }

                // 署名・宛先・発行者・受信者・有効期間の検証は、ここで行われる
                httpRequest.Binding.Unbind(httpRequest, response);
            }
            catch (Exception ex) when (IsSamlFailure(ex))
            {
                logger.LogWarning(ex, "SAML の応答を受け取れませんでした。");
                await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, detail: "invalid-response");
                return Failed(SamlFailure.Invalid);
            }

            // 宛先（Destination）が、こちらの ACS の URL か。**ライブラリは宛先を見ないので、ここで確かめる**
            // （別のサービス宛ての応答を、こちらへ持ち込んで使わせない）
            if (response.Destination is null
                || Uri.Compare(response.Destination, AssertionConsumerServiceUrl(http, settings), UriComponents.AbsoluteUri, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            {
                logger.LogWarning("SAML の応答の宛先が、こちらの ACS ではありませんでした。");
                await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, detail: "destination-mismatch");
                return Failed(SamlFailure.Invalid);
            }

            // こちらの出した要求への応答か（他の人の応答・古い応答の使い回しを断つ）
            if (!string.Equals(response.InResponseToAsString, relay.RequestId, StringComparison.Ordinal))
            {
                logger.LogWarning("SAML の応答が、こちらの出した要求のものではありませんでした。");
                await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, detail: "in-response-to-mismatch");
                return Failed(SamlFailure.Invalid);
            }

            var loginId = ReadLoginId(response.ClaimsIdentity, options);
            if (string.IsNullOrWhiteSpace(loginId))
            {
                logger.LogWarning("SAML の応答にログイン ID として使える値がありませんでした。");
                await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, detail: "no-login-id");
                return Failed(SamlFailure.Invalid);
            }

            var resolved = await authenticator.ResolveAsync(loginId, options, ct);
            switch (resolved.Outcome)
            {
                case SamlResolveOutcome.Unknown:
                    await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, loginId: loginId, detail: "unknown-user");
                    return Failed(SamlFailure.UnknownUser);
                case SamlResolveOutcome.Disabled:
                    await audit.WriteAsync(http, AuditActions.SamlLoginFailed, false, resolved.User, detail: "disabled");
                    return Failed(SamlFailure.Disabled);
            }

            var user = resolved.User!;
            if (resolved.Registered)
            {
                await audit.WriteAsync(http, AuditActions.SamlRegistered, true, user, targetType: "AdminUser", targetId: user.Id, detail: $"role={user.Role}");
            }

            // 前のセッションがあれば捨てる（ログインし直したら、前の cookie は使えない）
            foreach (var name in new[] { AdminSessionGuard.SessionCookie, AdminSessionGuard.PendingCookie })
            {
                if (http.Request.Cookies.TryGetValue(name, out var existing) && !string.IsNullOrEmpty(existing))
                {
                    await sessions.DeleteAsync(existing, ct);
                }
            }

            // 登録済みの 2 要素は省かせない。必須で未登録なら登録させる。それ以外はそのまま入れる
            var step = await AdminAuthEndpoints.SignInAsync(
                http, sessions, users, user, config.AdminAuth, audit, time.GetUtcNow(), ct, new SessionSso(ReadSessionKeys(response.ClaimsIdentity)));
            _ = step;
            return Results.Redirect(relay.ReturnUrl);
        }).RequireRateLimiting("adminLogin");

        MapSingleLogout(group);
    }

    private static void MapSingleLogout(RouteGroupBuilder group)
    {
        // 自分からのログアウト: こちらのセッションを捨て、IdP が単一ログアウトを持っていれば IdP へも頼む
        group.MapGet("/logout", async (
            HttpContext http,
            RuntimeSettings settings,
            IAdminSessionStore sessions,
            ISecretProtector protection,
            ILogger<SamlAuthenticator> logger,
            CancellationToken ct) =>
        {
            var options = settings.Current.Saml;

            SamlSessionKeys? keys = null;
            foreach (var name in new[] { AdminSessionGuard.SessionCookie, AdminSessionGuard.PendingCookie })
            {
                if (http.Request.Cookies.TryGetValue(name, out var id) && !string.IsNullOrEmpty(id))
                {
                    keys ??= await sessions.GetSamlKeysAsync(id, ct);
                    await sessions.DeleteAsync(id, ct);
                }

                AdminSessionGuard.ClearCookie(http, name);
            }

            if (!options.SingleLogoutEnabled || keys is null)
            {
                return Results.Redirect(Entry);
            }

            var request = new Saml2LogoutRequest(options.ToSaml2Configuration())
            {
                Destination = options.SingleLogoutUrl,
                NameId = new Saml2NameIdentifier(keys.NameId),
                SessionIndex = keys.SessionIndex,
            };
            var binding = new Saml2RedirectBinding();
            binding.Bind(request);
            SaveRelay(http, protection, request.IdAsString, Entry);
            logger.LogInformation("IdP へ単一ログアウトを頼みました。");
            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireRateLimiting("adminLogin");

        // IdP からの単一ログアウト（要求と、こちらが頼んだ応答の両方がここへ来る）
        var slo = async (
            HttpContext http,
            RuntimeSettings settings,
            IAdminSessionStore sessions,
            ISecretProtector protection,
            ILogger<SamlAuthenticator> logger,
            CancellationToken ct) =>
        {
            var options = settings.Current.Saml;
            if (!options.SingleLogoutEnabled)
            {
                return Results.NotFound();
            }

            var configuration = options.ToSaml2Configuration();
            var httpRequest = http.Request.ToGenericHttpRequest(validate: true);

            if (http.Request.Query.ContainsKey("SAMLResponse") || (http.Request.HasFormContentType && http.Request.Form.ContainsKey("SAMLResponse")))
            {
                // こちらが頼んだ単一ログアウトへの応答。確かめるだけで、画面へ戻す
                var relay = ReadRelay(http, protection);
                ClearRelay(http);
                try
                {
                    var response = new Saml2LogoutResponse(configuration);
                    httpRequest.Binding.Unbind(httpRequest, response);
                    if (relay is null || !string.Equals(response.InResponseToAsString, relay.RequestId, StringComparison.Ordinal))
                    {
                        logger.LogWarning("単一ログアウトの応答が、こちらの出した要求のものではありませんでした。");
                    }
                }
                catch (Exception ex) when (IsSamlFailure(ex))
                {
                    logger.LogWarning(ex, "単一ログアウトの応答を受け取れませんでした。");
                }

                return Results.Redirect(Entry);
            }

            try
            {
                // IdP が出した要求。**署名を検証してから**、その NameID・セッション索引のセッションを捨てる。
                // IdP からの他サイト POST では SameSite=Strict の cookie は届かないので、cookie ではなく鍵で探す
                var request = new Saml2LogoutRequest(configuration);
                httpRequest.Binding.Unbind(httpRequest, request);
                var nameId = request.NameId?.Value;
                if (!string.IsNullOrEmpty(nameId))
                {
                    var revoked = await sessions.DeleteBySamlKeysAsync(nameId, request.SessionIndex, ct);
                    logger.LogInformation("IdP からの単一ログアウトを受け取りました（{Count} 件のセッションを失効）。", revoked);
                }

                var logoutResponse = new Saml2LogoutResponse(configuration)
                {
                    InResponseTo = request.Id,
                    Status = Saml2StatusCodes.Success,
                    Destination = options.SingleLogoutUrl,
                };
                var binding = new Saml2RedirectBinding();
                binding.Bind(logoutResponse);
                return Results.Redirect(binding.RedirectLocation.OriginalString);
            }
            catch (Exception ex) when (IsSamlFailure(ex))
            {
                logger.LogWarning(ex, "単一ログアウトの要求を受け取れませんでした。");
                return Results.Redirect(Entry);
            }
        };

        group.MapGet("/slo", slo).RequireRateLimiting("adminLogin");
        group.MapPost("/slo", slo).RequireRateLimiting("adminLogin");
    }

    /// <summary>
    /// 応答・要求の解析と検証で起きた失敗。**検証ライブラリが投げる例外の種類は多い（署名・有効期間・受信者・XML の不正など）ので、
    /// 中断以外はすべて「受け付けない」側へ倒す**（例外を見落として 500 にしたり、通したりしない）。
    /// </summary>
    private static bool IsSamlFailure(Exception exception) => exception is not OperationCanceledException;

    private static SamlSessionKeys? ReadSessionKeys(ClaimsIdentity? identity)
    {
        if (identity is null)
        {
            return null;
        }

        var nameId = identity.FindFirst(Saml2ClaimTypes.NameId)?.Value ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(nameId) ? null : new SamlSessionKeys(nameId, identity.FindFirst(Saml2ClaimTypes.SessionIndex)?.Value);
    }

    private static string? ReadLoginId(ClaimsIdentity? identity, SamlOptions options)
    {
        if (identity is null)
        {
            return null;
        }

        return options.LoginIdSource == SamlLoginIdSource.Claim
            ? identity.FindFirst(options.LoginIdClaim)?.Value
            : identity.FindFirst(Saml2ClaimTypes.NameId)?.Value ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>ACS の URL。<b>公開 URL の設定があればそれから作る</b>（要求の Host を信じない）。無ければ要求から。</summary>
    private static Uri AssertionConsumerServiceUrl(HttpContext http, RuntimeSettings settings)
    {
        var baseUrl = settings.Current.PublicBaseUrl?.TrimEnd('/')
            ?? $"{http.Request.Scheme}://{http.Request.Host.ToUriComponent()}{http.Request.PathBase.ToUriComponent()}";
        return new Uri($"{baseUrl}/admin/api/saml/acs");
    }

    /// <summary>戻り先。<b>管理画面の中の絶対パスだけ</b>（オープンリダイレクトにしない）。</summary>
    private static string LocalReturnUrl(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl)
        || !returnUrl.StartsWith("/admin", StringComparison.Ordinal)
        || returnUrl.StartsWith("//", StringComparison.Ordinal)
        || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
        || returnUrl.Contains("://", StringComparison.Ordinal)
        || returnUrl.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
            ? Entry
            : returnUrl;

    private static IResult Failed(string failure) => Results.Redirect($"{Entry}?samlError={failure}");

    private static class SamlFailure
    {
        public const string Invalid = "invalid";
        public const string UnknownUser = "unknown-user";
        public const string Disabled = "disabled";
    }

    private sealed record Relay(string RequestId, string ReturnUrl);

    private static void SaveRelay(HttpContext http, ISecretProtector protection, string requestId, string returnUrl) =>
        http.Response.Cookies.Append(
            RelayCookieName,
            protection.Protect($"{requestId}\n{returnUrl}", ProtectorContext),
            RelayCookieOptions(http, RelayLifetime));

    private static Relay? ReadRelay(HttpContext http, ISecretProtector protection)
    {
        if (!http.Request.Cookies.TryGetValue(RelayCookieName, out var payload) || string.IsNullOrEmpty(payload))
        {
            return null;
        }

        string plain;
        try
        {
            plain = protection.Unprotect(payload, ProtectorContext);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            return null;
        }

        var parts = plain.Split('\n', 2);
        return parts.Length == 2 && parts[0].Length > 0 ? new Relay(parts[0], LocalReturnUrl(parts[1])) : null;
    }

    private static void ClearRelay(HttpContext http) => http.Response.Cookies.Delete(RelayCookieName, RelayCookieOptions(http, null));

    /// <summary>IdP からの他サイト POST で戻るので <c>SameSite=None</c>（<c>Secure</c> が前提）。パスは SAML の入口の下だけ。</summary>
    private static CookieOptions RelayCookieOptions(HttpContext http, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.None,
        Secure = true,
        IsEssential = true,
        Path = http.Request.PathBase.Add("/admin/api/saml").ToUriComponent(),
        MaxAge = lifetime,
    };
}
