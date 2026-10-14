using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>SAML で来た利用者を、本アプリの管理者へ結び付けた結果。</summary>
public enum SamlResolveOutcome
{
    /// <summary>管理者が決まった（既存、または自動で作った）。続きは 2 要素の方針に従う。</summary>
    Resolved,

    /// <summary>本アプリに居ない（自動で作らない設定）。</summary>
    Unknown,

    /// <summary>無効化されている。</summary>
    Disabled,
}

/// <param name="Outcome">結果。</param>
/// <param name="User">管理者。<see cref="SamlResolveOutcome.Unknown"/> では <c>null</c>。</param>
/// <param name="Registered">この回に自動で作ったか。</param>
public sealed record SamlResolveResult(SamlResolveOutcome Outcome, AdminUser? User = null, bool Registered = false);

/// <summary>
/// SAML で来た利用者を、本アプリの管理者へ結び付ける（Questionnaire の <c>SamlAuthenticator</c> に則る）。
/// </summary>
/// <remarks>
/// <para>
/// **認可は本アプリの役割で行う**（IdP のグループ・属性では行わない）。IdP が証明するのは「その人が誰か」だけ。
/// </para>
/// <para>
/// **本アプリに居ない相手は、既定では通さない**（<see cref="SamlUnknownUserPolicy.Reject"/>）。
/// <see cref="SamlUnknownUserPolicy.Register"/> なら、設定の役割で自動で作る（パスワードは誰も知らない値で、パスワードでは入れない）。
/// </para>
/// <para>
/// **登録済みの 2 要素は、IdP 経由でも省かせない**（2 要素の続きは <c>AdminAuthEndpoints.SignInAsync</c> が方針に従って決める）。
/// </para>
/// </remarks>
public sealed class SamlAuthenticator(IAdminUserStore users, TimeProvider time, ILogger<SamlAuthenticator> logger)
{
    /// <summary>ログイン ID から管理者を決める。</summary>
    public async Task<SamlResolveResult> ResolveAsync(string loginId, SamlOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        ArgumentNullException.ThrowIfNull(options);

        var trimmed = loginId.Trim();
        var registered = false;
        AdminUser? user = (await users.FindForLoginAsync(trimmed, cancellationToken).ConfigureAwait(false))?.User;

        if (user is null)
        {
            if (options.UnknownUser != SamlUnknownUserPolicy.Register)
            {
                // 居ない相手を黙って通さない。記録には残す（呼び出し側が監査ログへ）
                logger.LogInformation("SAML で来た利用者は本アプリに居ないため通しませんでした。");
                return new SamlResolveResult(SamlResolveOutcome.Unknown);
            }

            // 推測できない値。誰にも渡さないので、パスワードでは入れない
            var unknowable = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            user = await users.CreateAsync(trimmed, PasswordHasher.Hash(unknowable), options.RegisterRole, "ja", time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                // 同時に同じ人が来て、先に作られた。作られた方を使う
                user = (await users.FindForLoginAsync(trimmed, cancellationToken).ConfigureAwait(false))?.User;
                if (user is null)
                {
                    return new SamlResolveResult(SamlResolveOutcome.Unknown);
                }
            }
            else
            {
                registered = true;
                logger.LogInformation("SAML で来た利用者を {Role} として作りました。", options.RegisterRole);
            }
        }

        return user.IsDisabled
            ? new SamlResolveResult(SamlResolveOutcome.Disabled, user)
            : new SamlResolveResult(SamlResolveOutcome.Resolved, user, registered);
    }
}
