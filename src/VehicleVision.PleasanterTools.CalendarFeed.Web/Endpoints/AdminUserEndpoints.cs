using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>管理者の管理（一覧・招待・無効化・役割・2 要素の解除）、招待の受け取り、自分の端末。</summary>
/// <remarks>
/// <para>
/// **招待は URL を管理者に 1 度だけ見せ、手渡しする**（メール送信の基盤は持たない）。トークンの現物は保管しない。
/// 受け取る人（認証なし）が自分でパスワードを決める。決めるまでは <c>PasswordHash</c> が空で、ログインできない。
/// </para>
/// <para>
/// **有効な Administrator が 0 人になる操作（最後の 1 人の無効化・降格）は、保管側が 1 文の UPDATE で断る。**
/// 自分自身の無効化・降格は、ここで断る（操作ミスで自分を締め出さない）。
/// </para>
/// </remarks>
public static class AdminUserEndpoints
{
    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound() => Results.Json(new { error = "管理者が見つかりません。" }, statusCode: StatusCodes.Status404NotFound);

    /// <summary>管理者の管理・招待の受け取り・自分の端末の入口を登録する。</summary>
    public static void MapAdminUsers(this WebApplication app)
    {
        // ---- 管理者の管理（Administrator だけ） ------------------------------------------
        var users = app.MapGroup("/admin/api/users");
        users.AddEndpointFilter<AdminSessionFilter>();
        users.AddEndpointFilter<AdministratorOnlyFilter>();

        users.MapGet("/", List);
        users.MapPost("/", Invite);
        users.MapPost("/{id}/invite", ReissueInvite);
        users.MapPost("/{id}/disable", (string id, HttpContext http, IAdminUserStore store, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct) =>
            SetDisabled(id, true, http, store, sessions, audit, ct));
        users.MapPost("/{id}/enable", (string id, HttpContext http, IAdminUserStore store, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct) =>
            SetDisabled(id, false, http, store, sessions, audit, ct));
        users.MapPut("/{id}/role", SetRole);
        users.MapPost("/{id}/totp/reset", ResetTotp);
        users.MapDelete("/{id}/sessions", RevokeSessions);

        // ---- 招待の受け取り（認証なし。パスワードを自分で決める） --------------------------
        var invite = app.MapGroup("/admin/api/invite");

        // 状態を変える要求は、同じサイトから来たものだけ（ログイン前の入口も同じ）
        invite.AddEndpointFilter(async (context, next) =>
            AdminSessionGuard.IsSameOrigin(context.HttpContext)
                ? await next(context)
                : Results.Json(new { error = "別のサイトからの要求は受け付けません。" }, statusCode: StatusCodes.Status403Forbidden));
        // **トークンの総当たりも、ログインと同じ枠で遅くする**
        invite.MapGet("/{token}", InviteInfo).RequireRateLimiting("adminLogin");
        invite.MapPost("/{token}/accept", AcceptInvite).RequireRateLimiting("adminLogin");

        // ---- 自分の端末（ログイン済みなら役割を問わない） ---------------------------------
        var sessions = app.MapGroup("/admin/api/me/sessions");
        sessions.AddEndpointFilter<AdminSessionFilter>();
        sessions.MapGet("/", MySessions);
        sessions.MapPost("/revoke-others", RevokeOtherSessions);
        sessions.MapDelete("/{handle}", RevokeMySession);
    }

    // ---- 一覧・招待 ------------------------------------------------------------------

    private static async Task<IResult> List(
        IAdminUserStore store, AdminAuthOptions options, TimeProvider time, bool? staleOnly, CancellationToken ct)
    {
        var now = time.GetUtcNow();
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
                u.InvitePending,
                u.InviteExpiresAt,
                inviteExpired = u.InvitePending && u.InviteExpiresAt is { } expires && expires <= now,
                // **棚卸し**: 長い間ログインしていない（招待中・無効は対象外）
                stale = u.IsStale(now, options.StaleAfter),
            });
        return Results.Ok(staleOnly == true ? rows.Where(r => r.stale) : rows);
    }

    private static async Task<IResult> Invite(
        InviteRequest request,
        HttpContext http,
        IAdminUserStore store,
        AdminAuthOptions options,
        IConfiguration configuration,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        if (AdminAuthEndpoints.ValidateLoginId(request.LoginId) is { } loginIdError)
        {
            return Bad(loginIdError);
        }

        var role = request.Role ?? AdminRoles.Viewer;
        if (!AdminRoles.IsValid(role))
        {
            return Bad($"役割は {AdminRoles.Administrator} か {AdminRoles.Viewer} で指定してください。");
        }

        var now = time.GetUtcNow();
        var token = InviteTokens.Generate();
        var expires = now + options.InviteLifetime;
        var created = await store.CreateInvitedAsync(
            request.LoginId!, role, request.Language ?? "ja", InviteTokens.Hash(token), expires, now, ct);
        if (created is null)
        {
            return Results.Json(new { error = "そのログイン ID はすでにあります。" }, statusCode: StatusCodes.Status409Conflict);
        }

        await audit.WriteAsync(http, AuditActions.UserInvited, true, AdminContext.Of(http).User,
            targetType: "AdminUser", targetId: created.Id, detail: $"role={role}");
        return Results.Created(
            $"/admin/api/users/{created.Id}",
            new { user = new { created.Id, created.LoginId, created.Role }, inviteUrl = InviteUrl(http, configuration, token), inviteExpiresAt = expires });
    }

    private static async Task<IResult> ReissueInvite(
        string id,
        HttpContext http,
        IAdminUserStore store,
        AdminAuthOptions options,
        IConfiguration configuration,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var token = InviteTokens.Generate();
        var expires = now + options.InviteLifetime;

        // 招待中の人だけ（前の招待はこの 1 文で使えなくなる）
        if (!await store.ReissueInviteAsync(id, InviteTokens.Hash(token), expires, ct))
        {
            return await store.FindByIdAsync(id, ct) is null
                ? NotFound()
                : Results.Json(new { error = "この管理者は招待中ではありません（すでにパスワードを決めています）。" }, statusCode: StatusCodes.Status409Conflict);
        }

        await audit.WriteAsync(http, AuditActions.UserInviteReissued, true, AdminContext.Of(http).User, targetType: "AdminUser", targetId: id);
        return Results.Ok(new { inviteUrl = InviteUrl(http, configuration, token), inviteExpiresAt = expires });
    }

    // ---- 無効化・役割・2 要素の解除・端末の失効 -----------------------------------------

    private static async Task<IResult> SetDisabled(
        string id, bool disabled, HttpContext http, IAdminUserStore store, IAdminSessionStore sessions, AuditLogService audit, CancellationToken ct)
    {
        var self = AdminContext.Of(http).User;
        if (disabled && id == self.Id)
        {
            return Bad("自分自身は無効化できません。");
        }

        var result = await store.SetDisabledAsync(id, disabled, ct);
        if (Reject(result) is { } rejected)
        {
            return rejected;
        }

        // 無効化したら、その人のログイン中の端末はその場で使えなくなる
        if (disabled)
        {
            await sessions.DeleteForUserAsync(id, null, ct);
        }

        await audit.WriteAsync(http, disabled ? AuditActions.UserDisabled : AuditActions.UserEnabled, true, self, targetType: "AdminUser", targetId: id);
        return Results.NoContent();
    }

    private static async Task<IResult> SetRole(
        string id, RoleRequest request, HttpContext http, IAdminUserStore store, AuditLogService audit, CancellationToken ct)
    {
        var self = AdminContext.Of(http).User;
        if (!AdminRoles.IsValid(request.Role))
        {
            return Bad($"役割は {AdminRoles.Administrator} か {AdminRoles.Viewer} で指定してください。");
        }

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

        // 端末を失った人の救済。**必須の設定でも、管理者は解除できる**（本人は次のログインで登録し直す）。
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

    private static async Task<IResult> InviteInfo(
        string token, IAdminUserStore store, AdminAuthOptions options, TimeProvider time, CancellationToken ct)
    {
        // **どの理由で使えなくても同じ 404**（形が違う・無い・期限切れ・無効・受け取り済み）
        if (!InviteTokens.LooksValid(token)
            || await store.FindByInviteAsync(InviteTokens.Hash(token), time.GetUtcNow(), ct) is not { } user)
        {
            return InviteInvalid();
        }

        return Results.Ok(new { user.LoginId, user.Language, expiresAt = user.InviteExpiresAt, twoFactor = options.TwoFactor.ToString() });
    }

    private static async Task<IResult> AcceptInvite(
        string token,
        AcceptInviteRequest request,
        HttpContext http,
        IAdminUserStore store,
        AdminAuthOptions options,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!InviteTokens.LooksValid(token)
            || await store.FindByInviteAsync(InviteTokens.Hash(token), now, ct) is not { } user)
        {
            return InviteInvalid();
        }

        // **最初の管理者・パスワード変更と同じ条件を通す**（ここだけ緩いと、そこが一番弱い所になる）
        if (options.PasswordPolicy.Validate(user.LoginId, request.Password ?? "") is { } error)
        {
            return Bad(error);
        }

        // 印・期限を条件に含めた 1 文。同時に 2 回来ても通るのは 1 回
        if (!await store.AcceptInviteAsync(InviteTokens.Hash(token), PasswordHasher.Hash(request.Password!), now, ct))
        {
            return InviteInvalid();
        }

        await audit.WriteAsync(http, AuditActions.UserInviteAccepted, true, user, targetType: "AdminUser", targetId: user.Id);
        return Results.NoContent();
    }

    private static IResult InviteInvalid() =>
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

    private static string InviteUrl(HttpContext http, IConfiguration configuration, string token)
    {
        // **リバースプロキシの内側では Host が内部の値になる。公開 URL は設定で固定する**
        var baseUrl = AppSettings.Text(configuration, AppSettings.PublicBaseUrl)?.TrimEnd('/')
            ?? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        return $"{baseUrl}/admin/invite/{token}";
    }
}

/// <summary>招待の要求。</summary>
public sealed record InviteRequest(string? LoginId, string? Role = null, string? Language = null);

/// <summary>役割の変更の要求。</summary>
public sealed record RoleRequest(string? Role);

/// <summary>招待の受け取りの要求。</summary>
public sealed record AcceptInviteRequest(string? Password);
