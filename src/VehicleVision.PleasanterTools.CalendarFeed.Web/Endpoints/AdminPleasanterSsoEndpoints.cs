using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>Pleasanter のログインで管理画面へ入る入口（Issue #28。Questionnaire の <c>AdminPleasanterSsoEndpoints</c> に則る）。<b>既定は無効。</b></summary>
/// <remarks>
/// <para>
/// <b>流れ。</b> ログイン画面が <c>POST /admin/api/pleasanter-sso/check</c> を呼ぶ。サーバはブラウザから届いた Pleasanter の cookie を
/// Pleasanter の API へ転送し（標準の <c>/api/users/get</c> を <c>Own</c> で絞る）、本人が返れば本アプリの管理者へ結び付けて本アプリの cookie を発行する。
/// Pleasanter にログインしていなければ、画面は Pleasanter のログイン画面を別窓で開き、済むまでこの入口を繰り返し呼ぶ。
/// </para>
/// <para>
/// <b>認可は本アプリの役割で行う。</b> Pleasanter が証明するのは「誰か」だけ（許可する組織・グループは、自動登録の条件にだけ使う）。
/// <b>API キーはブラウザへ渡さない</b>（規約 8）。無効な間、入口は 404 を返す。
/// </para>
/// <para>失敗の理由は決まった印だけを返す。詳しい理由はサーバのログと監査ログに残す。</para>
/// </remarks>
public static class AdminPleasanterSsoEndpoints
{
    /// <summary>確認の入口に掛けるレート制限の名前。ログインの試行の枠（5 分に 10 回）とは分ける。</summary>
    /// <remarks>
    /// 画面は Pleasanter でのログインを待つ間この入口を繰り返し呼ぶため、同じ枠だと数十秒で使い切り、パスワードのログインまで止まる。
    /// ここで総当たりできるものは無い（資格情報は Pleasanter の cookie そのもの）が、Pleasanter への問い合わせを増幅させないために枠は掛ける。
    /// </remarks>
    public const string CheckRateLimitPolicy = "adminPleasanterSso";

    /// <summary>5 分あたりの上限。画面の問い合わせ間隔（2 秒）に余裕を持たせる。</summary>
    public const int CheckRequestsPer5Minutes = 300;

    /// <summary>入口を登録する。</summary>
    public static void MapAdminPleasanterSso(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api/pleasanter-sso");

        // 本文は JSON で受ける（中身は空でよい）。他所のサイトの form からは application/json を送れないので、別サイトから勝手にログインさせられない
        group.MapPost("/check", async (
            PleasanterSsoCheckRequest request,
            HttpContext http,
            RuntimeSettings settings,
            IAdminUserStore users,
            IAdminSessionStore sessions,
            IPleasanterSessionVerifier verifier,
            PleasanterSsoAuthenticator authenticator,
            AuditLogService audit,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!AdminSessionGuard.IsSameOrigin(http))
            {
                return Results.Json(new { error = "別のサイトからの要求は受け付けません。" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var config = settings.Current;
            var options = config.PleasanterSso;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            // 最初の管理者を Pleasanter から作らせない（誰でも全権を取れてしまう）
            if (await users.CountAsync(ct) == 0)
            {
                return Results.Conflict(new { code = "setup-required" });
            }

            var verification = await verifier.VerifyAsync(http.Request.Headers.Cookie, options, ct);
            switch (verification.Status)
            {
                case PleasanterSessionStatus.Unauthenticated:
                    // まだログインしていないだけ。画面は待つ間これを繰り返すので記録しない
                    return Results.Ok(new { status = "unauthenticated" });

                case PleasanterSessionStatus.NotAllowed:
                    await audit.WriteAsync(http, AuditActions.PleasanterSsoFailed, false, detail: verification.Reason);
                    return Results.Json(new { code = "not-allowed" }, statusCode: StatusCodes.Status403Forbidden);

                case PleasanterSessionStatus.UpstreamError:
                    await audit.WriteAsync(http, AuditActions.PleasanterSsoFailed, false, detail: $"upstream-error:{verification.Reason}");
                    return Results.Json(new { code = "upstream-error" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var identity = verification.Identity!;
            var resolved = await authenticator.ResolveAsync(identity.LoginId, options, ct);
            switch (resolved.Outcome)
            {
                case PleasanterSsoResolveOutcome.Unknown:
                    await audit.WriteAsync(http, AuditActions.PleasanterSsoFailed, false, loginId: identity.LoginId, detail: "unknown-user");
                    return Results.Json(new { code = "unknown-user" }, statusCode: StatusCodes.Status403Forbidden);
                case PleasanterSsoResolveOutcome.NotAllowed:
                    await audit.WriteAsync(http, AuditActions.PleasanterSsoFailed, false, loginId: identity.LoginId, detail: "membership-not-configured");
                    return Results.Json(new { code = "not-allowed" }, statusCode: StatusCodes.Status403Forbidden);
                case PleasanterSsoResolveOutcome.Disabled:
                    await audit.WriteAsync(http, AuditActions.PleasanterSsoFailed, false, resolved.User, detail: "disabled");
                    return Results.Json(new { code = "disabled" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var user = resolved.User!;
            if (resolved.Registered)
            {
                await audit.WriteAsync(http, AuditActions.PleasanterSsoRegistered, true, user, targetType: "AdminUser", targetId: user.Id, detail: $"role={user.Role}");
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
            var now = time.GetUtcNow();
            var step = await AdminAuthEndpoints.SignInAsync(
                http, sessions, users, user, config.AdminAuth, audit, now, ct,
                new SessionSso(Pleasanter: new PleasanterSsoKeys(identity.TenantId, identity.UserId, now)));
            return Results.Ok(new { status = "signedIn", next = step });
        }).RequirePleasanterSsoRateLimit();

        // ---- 接続の試験 ----------------------------------------------------------
        // 保存済みの設定で（有効にしていなくても）、いまのブラウザの Pleasanter の cookie を使って問い合わせる。
        // 同じブラウザで Pleasanter にログインしていれば、その人が返る。有効にする前に、内部 URL・cookie の転送・API 禁止時の API キーが揃っているかを確かめられる
        var test = group.MapPost("/test", async (
            HttpContext http,
            RuntimeSettings settings,
            IPleasanterSessionVerifier verifier,
            IAdminUserStore users,
            CancellationToken ct) =>
        {
            PleasanterSsoOptions options;
            try
            {
                options = PleasanterSsoOptions.FromValues(key => key == PleasanterSsoOptions.EnabledKey
                    ? "true"
                    : settings.Current.Snapshot.Values.TryGetValue(key, out var v) ? v : null);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var verification = await verifier.VerifyAsync(http.Request.Headers.Cookie, options, ct);
            if (verification.Identity is not { } identity)
            {
                return Results.Ok(new { status = verification.Status.ToString(), reason = verification.Reason });
            }

            var matched = await users.FindForLoginAsync(identity.LoginId, ct);
            return Results.Ok(new
            {
                status = verification.Status.ToString(),
                reason = verification.Reason,
                tenantId = identity.TenantId,
                userId = identity.UserId,
                loginId = identity.LoginId,
                name = identity.Name,
                registered = matched is not null,
                registrationAllowed = options.UnknownUser == PleasanterSsoUnknownUserPolicy.Register && options.HasMembershipRestriction,
            });
        });
        test.AddEndpointFilter<AdminSessionFilter>();
        test.AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.SettingsManage));
        test.RequirePleasanterSsoRateLimit();
    }

    /// <summary>確認の入口の本文。中身は使わない（JSON で送らせるためだけにある）。</summary>
    public sealed record PleasanterSsoCheckRequest;
}
