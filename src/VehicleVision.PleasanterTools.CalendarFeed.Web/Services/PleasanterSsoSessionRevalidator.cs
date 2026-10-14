using VehicleVision.PleasanterTools.CalendarFeed.Admin;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>再検証の結果。</summary>
public enum PleasanterSsoRevalidation
{
    /// <summary>Pleasanter のログインで入ったセッションではない。何もしない。</summary>
    NotApplicable,

    /// <summary>まだ間隔内、または問い合わせて同じ人だった。<b>通す。</b></summary>
    Valid,

    /// <summary>Pleasanter でログアウト済み・別人・所属が外れた・機能が無効。<b>本アプリからも落とす。</b></summary>
    Rejected,

    /// <summary>Pleasanter に問い合わせられなかった。<b>本アプリからも落とし、503 を返す。</b></summary>
    UpstreamError,
}

/// <summary>Pleasanter のログインで入ったセッションを、一定間隔で Pleasanter に確かめ直す（Issue #28）。</summary>
/// <remarks>
/// <para>
/// 呼ぶのは <see cref="AdminSessionFilter"/>（管理 API の入口）。通らなければ、そこでセッションを消して要求を断る。
/// 最後に確かめた時刻は<b>セッションの行</b>に持つので、複数台でも・再起動しても揃う。
/// </para>
/// <para>
/// ⚠️ <b>問い合わせられなかったときにセッションを延ばさない。</b> Pleasanter が落ちている間に
/// Pleasanter 側でログアウト・無効化されても気付けないため、本アプリからも落とす。
/// </para>
/// </remarks>
public sealed class PleasanterSsoSessionRevalidator(
    RuntimeSettings runtime,
    IPleasanterSessionVerifier verifier,
    IAdminSessionStore sessions,
    TimeProvider time,
    ILogger<PleasanterSsoSessionRevalidator> logger)
{
    /// <summary>要求のセッションを必要なら確かめ直す。</summary>
    public async Task<PleasanterSsoRevalidation> RevalidateAsync(
        HttpContext http, string sessionId, AdminSession session, CancellationToken cancellationToken = default)
    {
        if (session.Pleasanter is not { } keys)
        {
            return PleasanterSsoRevalidation.NotApplicable;
        }

        var options = runtime.Current.PleasanterSso;
        if (!options.Enabled)
        {
            // 確かめる手段が無いセッションは残さない。機能を止めたら、それで入った人も落とす
            logger.LogInformation("Pleasanter のログインが無効になったため、それで入ったセッションを終了します。");
            return PleasanterSsoRevalidation.Rejected;
        }

        var now = time.GetUtcNow();
        if (now - keys.VerifiedAt < options.RevalidateInterval)
        {
            return PleasanterSsoRevalidation.Valid;
        }

        var result = await verifier.VerifyAsync(http.Request.Headers.Cookie, options, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case PleasanterSessionStatus.Authenticated
                when result.Identity is { } identity && identity.TenantId == keys.TenantId && identity.UserId == keys.UserId:
                await sessions.TouchPleasanterVerifiedAsync(sessionId, now, cancellationToken).ConfigureAwait(false);
                return PleasanterSsoRevalidation.Valid;

            case PleasanterSessionStatus.Authenticated:
                // 別人に入れ替わっている（Pleasanter で別の人がログインし直した）
                logger.LogWarning("Pleasanter のログインが別の利用者に替わったため、セッションを終了します。");
                return PleasanterSsoRevalidation.Rejected;

            case PleasanterSessionStatus.NotAllowed:
            case PleasanterSessionStatus.Unauthenticated:
                logger.LogInformation("Pleasanter でのログインまたは所属が許可されないため、セッションを終了します（{Reason}）。", result.Reason);
                return PleasanterSsoRevalidation.Rejected;

            default:
                logger.LogWarning("Pleasanter に確かめ直せなかったため、セッションを終了します（{Reason}）。", result.Reason);
                return PleasanterSsoRevalidation.UpstreamError;
        }
    }
}
