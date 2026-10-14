using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>Pleasanter から来た利用者の突き合わせの結果。</summary>
public enum PleasanterSsoResolveOutcome
{
    /// <summary>管理者が決まった（既存、または自動で作った）。続きは 2 要素の方針に従う。</summary>
    Resolved,

    /// <summary>本アプリに居ない（自動で作らない設定）。</summary>
    Unknown,

    /// <summary>止められている利用者。</summary>
    Disabled,

    /// <summary>許可する所属を設定していないため、自動登録しない。</summary>
    NotAllowed,
}

/// <param name="Registered">この場で作ったか（JIT）。監査ログへ残すために持つ。</param>
public sealed record PleasanterSsoResolveResult(
    PleasanterSsoResolveOutcome Outcome, AdminUser? User = null, bool Registered = false);

/// <summary>Pleasanter が本人だと答えた利用者を、本アプリの管理者へ結び付ける（Issue #28）。</summary>
/// <remarks>
/// <para>**突き合わせはログイン ID で行う**（SAML と同じ。Pleasanter の <c>LoginId</c> を本アプリのログイン ID として扱う）。</para>
/// <para>
/// ⚠️ **登録済みの 2 要素は、Pleasanter から来ても通す**（続きは <c>AdminAuthEndpoints.SignInAsync</c> が方針に従って決める）。
/// Pleasanter 側の 2 要素は、終えるまで Pleasanter が認証 cookie を出さないので、ここへ来る時点で済んでいる。
/// </para>
/// <para>
/// ⚠️ **自動登録（JIT）は、許可する所属（組織・グループ）を設定したときだけ。**
/// 未設定のまま自動登録すると、Pleasanter の全利用者が管理画面へ入れてしまう。
/// </para>
/// </remarks>
public sealed class PleasanterSsoAuthenticator(IAdminUserStore users, TimeProvider time, ILogger<PleasanterSsoAuthenticator> logger)
{
    /// <summary>ログイン ID から管理者を決める。居なければ設定に従って作るか断る。</summary>
    public async Task<PleasanterSsoResolveResult> ResolveAsync(
        string loginId, PleasanterSsoOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        ArgumentNullException.ThrowIfNull(options);

        var trimmed = loginId.Trim();
        var registered = false;
        AdminUser? user = (await users.FindForLoginAsync(trimmed, cancellationToken).ConfigureAwait(false))?.User;

        if (user is null)
        {
            if (options.UnknownUser != PleasanterSsoUnknownUserPolicy.Register)
            {
                logger.LogInformation("Pleasanter から来た利用者は本アプリに居ないため通しませんでした。");
                return new PleasanterSsoResolveResult(PleasanterSsoResolveOutcome.Unknown);
            }

            if (!options.HasMembershipRestriction)
            {
                logger.LogWarning("Pleasanter SSO の許可する所属が未設定のため、自動登録を拒否しました。");
                return new PleasanterSsoResolveResult(PleasanterSsoResolveOutcome.NotAllowed);
            }

            // 推測できない値。誰にも渡さないので、パスワードでは入れない
            var unknowable = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            user = await users.CreateAsync(trimmed, PasswordHasher.Hash(unknowable), options.RegisterRole, "ja", time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (user is null)
            {
                // 同時に同じ人が来て、先に作られた。作られた方を使う
                user = (await users.FindForLoginAsync(trimmed, cancellationToken).ConfigureAwait(false))?.User;
                if (user is null)
                {
                    return new PleasanterSsoResolveResult(PleasanterSsoResolveOutcome.Unknown);
                }
            }
            else
            {
                registered = true;
                logger.LogInformation("Pleasanter から来た利用者を {Role} として作りました。", options.RegisterRole);
            }
        }

        return user.IsDisabled
            ? new PleasanterSsoResolveResult(PleasanterSsoResolveOutcome.Disabled, user)
            : new PleasanterSsoResolveResult(PleasanterSsoResolveOutcome.Resolved, user, registered);
    }
}
