using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>管理者の管理（一覧・招待・無効化・役割・2 要素の解除）、招待の受け取り、自分の端末、メールの送信状況。</summary>
/// <remarks>
/// <para>
/// 方式は Questionnaire の「管理者の管理」に則る。
/// **既定のパスワードを配らない**: 追加した時点では誰も知らない値でハッシュを埋め、<b>期限付き（既定 48 時間）で 1 回しか使えない招待</b>を渡す。
/// 招待は**本人へメールで直接送る**（送れない構成では、画面の URL を手で渡す）。パスワードは招かれた本人が決める。
/// トークンは SHA-256 だけ保管する。
/// </para>
/// <para>
/// **最後の有効な Administrator は、無効化も降格もできない**（保管側の 1 文の UPDATE が断る）。
/// **自分自身は止めさせない・自分の役割は変えさせない**（手が滑ったときに自分では戻せない）。
/// **止めた相手は、その場で追い出し、宛ての未使用の招待は取り消す。**
/// </para>
/// </remarks>
public static class AdminUserEndpoints
{
    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound() => Results.Json(new { error = "管理者が見つかりません。" }, statusCode: StatusCodes.Status404NotFound);

    /// <summary>管理者の管理・招待の受け取り・自分の端末・メールの送信状況の入口を登録する。</summary>
    public static void MapAdminUsers(this WebApplication app)
    {
        // ---- 管理者の管理（権限で分ける） --------------------------------------------------
        var users = app.MapGroup("/admin/api/users");
        users.AddEndpointFilter<AdminSessionFilter>();

        users.MapGet("/", List).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersRead));
        users.MapPost("/", Invite).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersWrite));
        users.MapPost("/{id}/invitation", ReissueInvitation).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersWrite));
        users.MapPost("/{id}/disable", (string id, HttpContext http, IAdminUserStore store, IAdminInvitationStore invitations, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct) =>
            SetDisabled(id, true, http, store, invitations, sessions, audit, ct)).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersWrite));
        users.MapPost("/{id}/enable", (string id, HttpContext http, IAdminUserStore store, IAdminInvitationStore invitations, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct) =>
            SetDisabled(id, false, http, store, invitations, sessions, audit, ct)).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersWrite));
        users.MapPut("/{id}/role", SetRole).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersWrite));
        users.MapPost("/{id}/totp/reset", ResetTotp).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersResetTwoFactor));
        users.MapDelete("/{id}/sessions", RevokeSessions).AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.UsersWrite));

        // ---- 招待の受け取り（認証なし。パスワードを自分で決める） --------------------------
        var invitations = app.MapGroup("/admin/api/invitations");
        invitations.AddEndpointFilter(async (context, next) =>
            AdminSessionGuard.IsSameOrigin(context.HttpContext)
                ? await next(context)
                : Results.Json(new { error = "別のサイトからの要求は受け付けません。" }, statusCode: StatusCodes.Status403Forbidden));
        // **トークンの総当たりも、ログインと同じ枠で遅くする**
        invitations.MapPost("/accept", AcceptInvitation).RequireRateLimiting("adminLogin");

        // ---- 自分の端末（ログイン済みなら役割を問わない） ---------------------------------
        var mySessions = app.MapGroup("/admin/api/me/sessions");
        mySessions.AddEndpointFilter<AdminSessionFilter>();
        mySessions.MapGet("/", MySessions);
        mySessions.MapPost("/revoke-others", RevokeOtherSessions);
        mySessions.MapDelete("/{handle}", RevokeMySession);

        // ---- メールの送信状況（宛先・本文は出さない。件数と、送れる状態かどうかだけ） ------------
        var mail = app.MapGroup("/admin/api/mail");
        mail.AddEndpointFilter<AdminSessionFilter>();
        mail.AddEndpointFilter(RequirePermissionFilter.Of(AdminPermissions.OutboxRead));
        mail.MapGet("/status", async (IMailOutbox outbox, VehicleVision.PleasanterTools.CalendarFeed.Mail.MailOptions options, CancellationToken ct) =>
        {
            var status = await outbox.GetStatusAsync(ct);
            return Results.Ok(new
            {
                ready = options.IsReady,
                transport = options.Enabled ? options.Transport.ToString() : null,
                status.PendingCount,
                status.OldestPendingAt,
                status.DeadLetterCount,
            });
        });
    }

    // ---- 一覧・招待 ------------------------------------------------------------------

    private static async Task<IResult> List(
        IAdminUserStore store, IAdminInvitationStore invitations, AdminAuthOptions options, TimeProvider time, bool? staleOnly, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var pending = (await invitations.ListPendingAdminUserIdsAsync(now, ct)).ToHashSet();
        var rows = (await store.ListAsync(ct))
            .Select(u => new
            {
                u.Id,
                u.LoginId,
                u.Role,
                u.IsDisabled,
                isLocked = u.IsLocked(now),
                u.LastLoginAt,
                u.CreatedAt,
                u.TotpEnabled,
                // **招待中**（未使用で期限内）。一覧に出し、出し直しの目安にする
                invitationPending = pending.Contains(u.Id),
                // **棚卸し**: 長い間ログインしていない（招待中・無効は対象外）
                stale = u.IsStale(now, options.StaleAfter, pending.Contains(u.Id)),
            });
        return Results.Ok(staleOnly == true ? rows.Where(r => r.stale) : rows);
    }

    private static async Task<IResult> Invite(
        InviteRequest request,
        HttpContext http,
        IAdminUserStore store,
        IAdminInvitationStore invitations,
        AdminInvitationMailer mailer,
        AdminAuthOptions options,
        RuntimeSettings settings,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        if (AdminAuthEndpoints.ValidateLoginId(request.LoginId) is { } loginIdError)
        {
            return Bad(loginIdError);
        }

        if (!AdminRoles.IsValid(request.Role))
        {
            return Bad($"役割は {string.Join(" / ", AdminRoles.All)} のいずれかで指定してください。");
        }

        var actor = AdminContext.Of(http).User;
        var now = time.GetUtcNow();

        // 誰も知らない値でハッシュを埋める（既定のパスワードを配らない。資格情報ではない）
        var created = await store.CreateAsync(
            request.LoginId!, PasswordHasher.Hash(Guid.NewGuid().ToString()), request.Role!, request.Language ?? actor.Language, now, ct);
        if (created is null)
        {
            return Results.Json(new { error = "そのログイン ID はすでにあります。" }, statusCode: StatusCodes.Status409Conflict);
        }

        await audit.WriteAsync(http, AuditActions.UserInvited, true, actor, targetType: "AdminUser", targetId: created.Id, detail: $"role={request.Role}");

        var invitation = await IssueAsync(actor, created, invitations, mailer, options, settings, http, now, ct);
        return Results.Created($"/admin/api/users/{created.Id}", new { user = new { created.Id, created.LoginId, created.Role }, invitation });
    }

    private static async Task<IResult> ReissueInvitation(
        string id,
        HttpContext http,
        IAdminUserStore store,
        IAdminInvitationStore invitations,
        AdminInvitationMailer mailer,
        AdminAuthOptions options,
        RuntimeSettings settings,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        if (await store.FindByIdAsync(id, ct) is not { } target)
        {
            return NotFound();
        }

        // 止めた相手を呼び戻さない。先に有効へ戻すこと
        if (target.IsDisabled)
        {
            return Bad("無効化されている管理者へは招待を出せません。先に有効へ戻してください。");
        }

        var actor = AdminContext.Of(http).User;
        // **出し直しはパスワードの決め直しでもある**（受け取ると、以前のパスワードで張られたセッションはすべて失効する）
        var invitation = await IssueAsync(actor, target, invitations, mailer, options, settings, http, time.GetUtcNow(), ct);
        await audit.WriteAsync(http, AuditActions.UserInviteReissued, true, actor, targetType: "AdminUser", targetId: id);
        return Results.Ok(new { invitation });
    }

    /// <summary>招待を発行する（前の未使用の招待は死ぬ）。<b>画面の URL も返す</b>: メールが送れない構成でも、招待は出せること。</summary>
    private static async Task<object> IssueAsync(
        AdminUser actor,
        AdminUser target,
        IAdminInvitationStore invitations,
        AdminInvitationMailer mailer,
        AdminAuthOptions options,
        RuntimeSettings settings,
        HttpContext http,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var token = InviteTokens.Generate();
        var expires = now + options.InvitationLifetime;
        await invitations.ReplaceAsync(
            new AdminInvitation(Guid.NewGuid().ToString("D"), target.Id, InviteTokens.Hash(token), expires, CreatedBy: actor.Id), ct);

        // 本人へ直接送る。招いた側の言語で書く
        var mailSent = await mailer.TryEnqueueAsync(target.LoginId, token, expires, actor.Language, ct);
        return new
        {
            adminUserId = target.Id,
            // **返せるのはこの時だけ。** 保存しているのはハッシュのみ
            invitationToken = token,
            url = InviteUrl(http, settings, token),
            expiresAt = expires,
            // メールを積めたか。**送れていなければ、画面の URL を手で渡してもらう必要がある**
            mailSent,
        };
    }

    // ---- 無効化・役割・2 要素の解除・端末の失効 -----------------------------------------

    private static async Task<IResult> SetDisabled(
        string id, bool disabled, HttpContext http, IAdminUserStore store, IAdminInvitationStore invitations, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var self = AdminContext.Of(http).User;
        // 自分は止めさせない。手が滑ったときに自分では戻せない
        if (disabled && id == self.Id)
        {
            return Bad("自分自身は無効化できません。");
        }

        if (Reject(await store.SetDisabledAsync(id, disabled, ct)) is { } rejected)
        {
            return rejected;
        }

        if (disabled)
        {
            // 止めた相手は、その場で追い出す。宛ての未使用の招待は取り消す（止めたのにパスワードを決められては困る）
            await sessions.DeleteForUserAsync(id, null, ct);
            await invitations.RevokeUnusedAsync(id, ct);
        }

        await audit.WriteAsync(http, disabled ? AuditActions.UserDisabled : AuditActions.UserEnabled, true, self, targetType: "AdminUser", targetId: id);
        return Results.NoContent();
    }

    private static async Task<IResult> SetRole(
        string id, RoleRequest request, HttpContext http, IAdminUserStore store, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var self = AdminContext.Of(http).User;
        if (!AdminRoles.IsValid(request.Role))
        {
            return Bad($"役割は {string.Join(" / ", AdminRoles.All)} のいずれかで指定してください。");
        }

        // 自分の役割は変えさせない。降ろした瞬間に戻す権限を失う
        if (id == self.Id)
        {
            return Bad("自分自身の役割は変えられません。");
        }

        if (Reject(await store.SetRoleAsync(id, request.Role!, ct)) is { } rejected)
        {
            return rejected;
        }

        await audit.WriteAsync(http, AuditActions.UserRoleChanged, true, self, targetType: "AdminUser", targetId: id, detail: $"role={request.Role}");
        return Results.NoContent();
    }

    private static async Task<IResult> ResetTotp(
        string id, HttpContext http, IAdminUserStore store, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var self = AdminContext.Of(http).User;
        if (id == self.Id)
        {
            return Bad("自分の 2 要素は、自分の画面（/admin/api/me/totp/disable）から解除してください。");
        }

        if (await store.FindByIdAsync(id, ct) is null)
        {
            return NotFound();
        }

        // 端末を失った人の救済。**必須の設定でも、解除できる**（本人は次のログインで登録し直す）。
        // 失くした端末が盗まれていることも考え、ログイン中の端末も失効させる
        await store.DisableTotpAsync(id, ct);
        await sessions.DeleteForUserAsync(id, null, ct);
        await audit.WriteAsync(http, AuditActions.UserTotpReset, true, self, targetType: "AdminUser", targetId: id);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSessions(
        string id, HttpContext http, IAdminUserStore store, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var self = AdminContext.Of(http).User;
        if (id == self.Id)
        {
            return Bad("自分の端末は、自分の画面（/admin/api/me/sessions）から失効させてください。");
        }

        if (await store.FindByIdAsync(id, ct) is null)
        {
            return NotFound();
        }

        await sessions.DeleteForUserAsync(id, null, ct);
        await audit.WriteAsync(http, AuditActions.UserSessionsRevoked, true, self, targetType: "AdminUser", targetId: id);
        return Results.NoContent();
    }

    private static IResult? Reject(AdminChangeResult result) => result switch
    {
        AdminChangeResult.NotFound => NotFound(),
        AdminChangeResult.LastAdministrator => Results.Json(
            new { error = "有効な管理者（Administrator）が 1 人も居なくなるため、できません。先に別の管理者を用意してください。" },
            statusCode: StatusCodes.Status409Conflict),
        _ => null,
    };

    // ---- 招待の受け取り --------------------------------------------------------------

    private static async Task<IResult> AcceptInvitation(
        AcceptInvitationRequest request,
        HttpContext http,
        IAdminUserStore store,
        IAdminInvitationStore invitations,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // **どの理由で使えなくても同じ 404**（形が違う・無い・期限切れ・使用済み・取り消された・止められた）
        if (!InviteTokens.LooksValid(request.Token)
            || await invitations.FindByTokenHashAsync(InviteTokens.Hash(request.Token!), ct) is not { } invitation
            || invitation.UsedAt is not null
            || invitation.ExpiresAt <= now
            || await store.FindByIdAsync(invitation.AdminUserId, ct) is not { IsDisabled: false } user)
        {
            return InvitationInvalid();
        }

        // **条件を満たさないパスワードは、招待を使い切る前に断る**（使い切ってから断ると、招待が死んで受け取れなくなる）。
        // 最初の管理者・パスワード変更と同じ判定を通す（ここだけ緩いと、そこが一番弱い所になる）
        if (options.PasswordPolicy.Validate(user.LoginId, request.Password ?? "", user.Language) is { } problem)
        {
            return Bad(problem);
        }

        // **先に使い切る。** パスワードを入れてから印を付けると、同時に来た 2 つが両方通る
        if (!await invitations.TryConsumeAsync(invitation.InvitationId, now, ct))
        {
            return InvitationInvalid();
        }

        await store.SetPasswordHashAsync(user.Id, PasswordHasher.Hash(request.Password!), ct);

        // **招待の出し直しはパスワードの決め直しでもある。** 以前のパスワードで張られた通常・途中のセッションをすべて失効させる
        await sessions.DeleteForUserAsync(user.Id, null, ct);
        await audit.WriteAsync(http, AuditActions.UserInviteAccepted, true, user, targetType: "AdminUser", targetId: user.Id);

        // 2 要素の方針に従う（登録済みならまず 2 要素、必須ならその場で登録、それ以外はそのまま入れる）
        return Results.Ok(new { next = await AdminAuthEndpoints.SignInAsync(http, sessions, store, user, options, audit, now, ct) });
    }

    private static IResult InvitationInvalid() =>
        Results.Json(new { error = "この招待は使えません（期限切れ・受け取り済み・取り消された可能性があります）。招待した管理者に出し直しを頼んでください。" },
            statusCode: StatusCodes.Status404NotFound);

    // ---- 自分の端末 ------------------------------------------------------------------

    private static async Task<IResult> MySessions(
        HttpContext http, IAdminSessionStore sessions, TimeProvider time, CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        var current = sessions.HandleOf(admin.SessionId);
        return Results.Ok((await sessions.ListForUserAsync(admin.User.Id, time.GetUtcNow(), ct)).Select(s => new
        {
            s.Handle,
            isCurrent = s.Handle == current,
            s.CreatedAt,
            s.ExpiresAt,
            s.IpAddress,
            s.UserAgent,
        }));
    }

    private static async Task<IResult> RevokeMySession(
        string handle, HttpContext http, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        if (!await sessions.DeleteByHandleAsync(admin.User.Id, handle, ct))
        {
            return Results.Json(new { error = "その端末は見つかりません。" }, statusCode: StatusCodes.Status404NotFound);
        }

        await audit.WriteAsync(http, AuditActions.SessionRevoked, true, admin.User);
        if (handle == sessions.HandleOf(admin.SessionId))
        {
            // いまの端末を失効させたなら、cookie も片付ける
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> RevokeOtherSessions(
        HttpContext http, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        await sessions.DeleteForUserAsync(admin.User.Id, admin.SessionId, ct);
        await audit.WriteAsync(http, AuditActions.SessionsRevokedOthers, true, admin.User);
        return Results.NoContent();
    }

    private static string InviteUrl(HttpContext http, RuntimeSettings settings, string token)
    {
        // 画面へ返す URL（ログイン済みの管理者が見る）。リバースプロキシの内側では Host が内部の値になるので、公開 URL を優先する。
        // ⚠️ メールのリンクは、Host から組み立てない（AdminInvitationMailer）
        var baseUrl = settings.Current.PublicBaseUrl?.TrimEnd('/')
            ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        return $"{baseUrl}{AdminInvitationMailer.AcceptPath}?token={Uri.EscapeDataString(token)}";
    }
}

/// <summary>招待の要求。役割は必須（知らない役割は断る）。</summary>
public sealed record InviteRequest(string? LoginId, string? Role = null, string? Language = null);

/// <summary>役割の変更の要求。</summary>
public sealed record RoleRequest(string? Role);

/// <summary>招待の受け取りの要求。</summary>
public sealed record AcceptInvitationRequest(string? Token, string? Password);
