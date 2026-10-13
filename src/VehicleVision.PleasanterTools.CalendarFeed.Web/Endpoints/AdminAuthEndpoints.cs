using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>管理者のログイン・2 要素・パスワード・最初の管理者。</summary>
/// <remarks>
/// <para>
/// 方式は Questionnaire に則る（<c>_documents/アーキテクチャ方針.md</c> 3 章）。
/// <b>2 段階で通す</b>: <c>login</c>（パスワード）→ <c>Pending</c>（5 分・何も操作させない）→
/// <c>login/totp</c> か <c>login/recovery</c> → <c>Session</c>（8 時間）。2 要素が要らない構成では <c>login</c> で <c>Session</c> になる。
/// </para>
/// <para>
/// <b>どの段階で外れたかを外へ伝えない。</b> 「居ない」「無効」「締め出し中」「パスワードが違う」を同じ応答にする
/// （区別して返すと、利用者名の総当たりに使える）。利用者が居なくても照合する（応答の速さで実在が分からないように）。
/// </para>
/// </remarks>
public static class AdminAuthEndpoints
{
    private static readonly object Generic = new
    {
        error = "ログイン ID またはパスワードが違います。何度も失敗した場合は、しばらく待ってからやり直してください。",
    };

    private static IResult Unauthorized() => Results.Json(Generic, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>管理者認証の入口を登録する。</summary>
    public static void MapAdminAuth(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api");

        // **状態を変える要求は、同じサイトから来たものだけ**（ログイン前の入口も同じ）
        group.AddEndpointFilter(async (context, next) =>
            AdminSessionGuard.IsSameOrigin(context.HttpContext)
                ? await next(context)
                : Results.Json(new { error = "別のサイトからの要求は受け付けません。" }, statusCode: StatusCodes.Status403Forbidden));

        group.MapGet("/status", async (IAdminUserStore users, AdminAuthOptions options, CancellationToken ct) =>
            Results.Ok(new { setupRequired = await users.CountAsync(ct) == 0, twoFactor = options.TwoFactor.ToString() }));

        // **ログインの試行だけ別枠のレート制限**（全体の枠に紛れさせると総当たりが通る）
        group.MapPost("/setup", Setup).RequireRateLimiting("adminLogin");
        group.MapPost("/login", Login).RequireRateLimiting("adminLogin");
        group.MapPost("/login/totp", LoginTotp).RequireRateLimiting("adminLogin");
        group.MapPost("/login/recovery", LoginRecovery).RequireRateLimiting("adminLogin");
        group.MapPost("/logout", Logout);

        // 2 要素の登録は、ログイン済み、または「必須で未登録」の Pending から。フィルタを付けずに中で判断する
        group.MapPost("/me/totp/begin", TotpBegin);
        group.MapPost("/me/totp/complete", TotpComplete).RequireRateLimiting("adminLogin");

        var me = group.MapGroup("/me").AddEndpointFilter<AdminSessionFilter>();
        me.MapGet("/", Me);
        me.MapPost("/password", ChangePassword).RequireRateLimiting("adminLogin");
        me.MapPost("/totp/disable", TotpDisable).RequireRateLimiting("adminLogin");
    }

    // ---- 最初の管理者 ----------------------------------------------------------------

    private static async Task<IResult> Setup(
        SetupRequest request,
        IAdminUserStore users,
        AdminAuthOptions options,
        ILogger<SetupRequest> logger,
        CancellationToken ct)
    {
        if (ValidateLoginId(request.LoginId) is { } loginIdError)
        {
            return Bad(loginIdError);
        }

        if (options.PasswordPolicy.Validate(request.LoginId!.Trim(), request.Password ?? "") is { } passwordError)
        {
            return Bad(passwordError);
        }

        // **管理者が 1 人も居ないときだけ通る**（原子的に 1 回だけ）。既定のパスワードを仕込むより、空の状態から作らせる
        var admin = await users.CreateFirstAsync(
            request.LoginId!, PasswordHasher.Hash(request.Password!), request.Language ?? "ja", ct);
        if (admin is null)
        {
            return Results.Json(new { error = "最初の管理者はすでに作られています。" }, statusCode: StatusCodes.Status409Conflict);
        }

        logger.LogInformation("最初の管理者を作成しました（{UserId}）", admin.Id);
        return Results.Created("/admin/api/me", new { admin.Id, admin.LoginId, admin.Role });
    }

    // ---- ログイン --------------------------------------------------------------------

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        ILogger<LoginRequest> logger,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var loginId = request.LoginId ?? "";
        var password = request.Password ?? "";
        var found = loginId.Length is > 0 and <= 100 && password.Length is > 0 and <= AdminPasswordPolicy.MaximumLength
            ? await users.FindForLoginAsync(loginId, ct)
            : null;

        // **居なくても同じ時間をかけて照合する**（応答の速さで実在が分からないように）
        var verified = PasswordHasher.Verify(password, found?.PasswordHash ?? PasswordHasher.Dummy);

        if (found is null)
        {
            return Unauthorized();
        }

        var (user, hash) = found.Value;
        if (user.IsDisabled || user.IsLocked(now))
        {
            return Unauthorized();
        }

        if (!verified)
        {
            var failure = await users.RecordFailureAsync(user.Id, options.LockoutThreshold, options.Lockout, now, ct);
            if (failure.LockedUntil is not null)
            {
                logger.LogWarning("管理者 {UserId} を {Until} まで締め出しました（ログインの失敗が続いたため）", user.Id, failure.LockedUntil);
            }

            return Unauthorized();
        }

        // 反復回数を上げた後も、次のログインで移行できる
        if (PasswordHasher.NeedsRehash(hash))
        {
            await users.SetPasswordHashAsync(user.Id, PasswordHasher.Hash(password), ct);
        }

        // 前のセッションがあれば捨てる（ログインし直したら、前の cookie は使えない）
        await DropExistingAsync(http, sessions, ct);

        var needsSecondFactor = user.TotpEnabled;
        var mustEnroll = !user.TotpEnabled && options.TwoFactor == TwoFactorMode.Required;
        if (needsSecondFactor || mustEnroll)
        {
            var pending = await sessions.CreateAsync(user.Id, AdminSessionKind.Pending, options.PendingLifetime, now, ct);
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
            AdminSessionGuard.SetCookie(http, AdminSessionGuard.PendingCookie, pending, options.PendingLifetime);
            return Results.Ok(new { step = needsSecondFactor ? "totp" : "enroll" });
        }

        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        await StartSessionAsync(http, sessions, users, user, options, now, ct);
        return Results.Ok(new { step = "done" });
    }

    private static async Task<IResult> LoginTotp(
        CodeRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (await ResolvePendingAsync(http, sessions, users, now, ct) is not { } pending)
        {
            return Unauthorized();
        }

        var (user, pendingId) = pending;
        if (user.IsDisabled || user.IsLocked(now) || await users.GetTotpSecretAsync(user.Id, ct) is not { } secret)
        {
            return Unauthorized();
        }

        // **数字の総当たりも、パスワードと同じ締め出しに数える**
        if (!TotpService.Verify(secret, request.Code ?? "", now.UtcDateTime, out var step)
            || !await users.TryUseTotpStepAsync(user.Id, step, ct))
        {
            // 同じ時間枠の数字は二度通さない（盗み見られた数字がそのまま使えてしまう）
            await RecordFailureAndMaybeDropAsync(http, sessions, users, options, user, pendingId, now, ct);
            return Unauthorized();
        }

        await sessions.DeleteAsync(pendingId, ct);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        await StartSessionAsync(http, sessions, users, user, options, now, ct);
        return Results.Ok(new { step = "done" });
    }

    private static async Task<IResult> LoginRecovery(
        CodeRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        ILogger<CodeRequest> logger,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (await ResolvePendingAsync(http, sessions, users, now, ct) is not { } pending)
        {
            return Unauthorized();
        }

        var (user, pendingId) = pending;
        if (user.IsDisabled || user.IsLocked(now))
        {
            return Unauthorized();
        }

        // **復旧コードは使い切り**（使用済みの印を条件に含めた UPDATE の件数で判断する）
        if (string.IsNullOrWhiteSpace(request.Code)
            || !await users.TryUseRecoveryCodeAsync(user.Id, RecoveryCodes.Hash(request.Code), now, ct))
        {
            await RecordFailureAndMaybeDropAsync(http, sessions, users, options, user, pendingId, now, ct);
            return Unauthorized();
        }

        logger.LogWarning("管理者 {UserId} が復旧コードでログインしました", user.Id);
        await sessions.DeleteAsync(pendingId, ct);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        await StartSessionAsync(http, sessions, users, user, options, now, ct);
        return Results.Ok(new { step = "done", recoveryCodesLeft = await users.CountRecoveryCodesAsync(user.Id, ct) });
    }

    private static async Task<IResult> Logout(
        HttpContext http, IAdminSessionStore sessions, CancellationToken ct)
    {
        await DropExistingAsync(http, sessions, ct);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        return Results.NoContent();
    }

    // ---- 自分 ------------------------------------------------------------------------

    private static async Task<IResult> Me(
        HttpContext http, IAdminUserStore users, AdminAuthOptions options, CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        return Results.Ok(new
        {
            admin.User.Id,
            admin.User.LoginId,
            admin.User.Role,
            admin.User.Language,
            admin.User.TotpEnabled,
            admin.User.LastLoginAt,
            twoFactor = options.TwoFactor.ToString(),
            recoveryCodesLeft = admin.User.TotpEnabled ? await users.CountRecoveryCodesAsync(admin.User.Id, ct) : 0,
        });
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        var now = time.GetUtcNow();

        // ログイン中の端末でも現在のパスワードを確かめる。**乗っ取られた端末から変えられない**ように、失敗は締め出しに数える
        var found = await users.FindForLoginAsync(admin.User.LoginId, ct);
        if (found is null
            || admin.User.IsLocked(now)
            || !PasswordHasher.Verify(request.CurrentPassword ?? "", found.Value.PasswordHash))
        {
            if (found is not null)
            {
                await users.RecordFailureAsync(admin.User.Id, options.LockoutThreshold, options.Lockout, now, ct);
            }

            return Results.Json(new { error = "現在のパスワードが違います。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        // **最初の管理者と同じ条件を通す**（片方だけ緩いと、そこが一番弱い所になる）
        if (options.PasswordPolicy.Validate(admin.User.LoginId, request.NewPassword ?? "") is { } error)
        {
            return Bad(error);
        }

        await users.SetPasswordHashAsync(admin.User.Id, PasswordHasher.Hash(request.NewPassword!), ct);

        // **いまの端末以外のセッション（盗まれた登録途中の状態も）を失効させる**
        await sessions.DeleteForUserAsync(admin.User.Id, admin.SessionId, ct);
        return Results.NoContent();
    }

    // ---- 2 要素の登録・解除 ----------------------------------------------------------

    private static async Task<IResult> TotpBegin(
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        // **無効では登録の口を閉じる**（画面から隠すだけでは、API を直接叩かれたときに通ってしまう）
        if (options.TwoFactor == TwoFactorMode.Disabled)
        {
            return Results.Json(new { error = "2 要素は無効に設定されています。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (await ResolveForEnrollmentAsync(http, sessions, users, options, time.GetUtcNow(), ct) is not { } who)
        {
            return Results.Json(new { error = "ログインが必要です。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var secret = TotpService.GenerateSecret();
        await users.SetTotpPendingAsync(who.User.Id, secret, ct);
        return Results.Ok(new { secret, uri = TotpService.BuildUri(options.Issuer, who.User.LoginId, secret) });
    }

    private static async Task<IResult> TotpComplete(
        CodeRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (options.TwoFactor == TwoFactorMode.Disabled)
        {
            return Results.Json(new { error = "2 要素は無効に設定されています。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (await ResolveForEnrollmentAsync(http, sessions, users, options, now, ct) is not { } who)
        {
            return Results.Json(new { error = "ログインが必要です。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (await users.GetTotpPendingAsync(who.User.Id, ct) is not { } secret)
        {
            return Bad("先に登録を始めてください（/admin/api/me/totp/begin）。");
        }

        if (!TotpService.Verify(secret, request.Code ?? "", now.UtcDateTime, out var step))
        {
            await users.RecordFailureAsync(who.User.Id, options.LockoutThreshold, options.Lockout, now, ct);
            return Bad("数字が違います。認証アプリに表示されている 6 桁を入力してください。");
        }

        // **登録し直すと前の復旧コードは消える。** 復旧コードは、ここで 1 度だけ見せる
        var codes = RecoveryCodes.Generate();
        await users.EnableTotpAsync(who.User.Id, secret, step, codes.Select(RecoveryCodes.Hash).ToList(), ct);

        // 「必須で未登録」の途中（Pending）から来た人は、ここでログインが完了する
        if (who.Kind == AdminSessionKind.Pending)
        {
            await sessions.DeleteAsync(who.SessionId, ct);
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
            await StartSessionAsync(http, sessions, users, who.User, options, now, ct);
        }

        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> TotpDisable(
        PasswordRequest request,
        HttpContext http,
        IAdminUserStore users,
        AdminAuthOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        var now = time.GetUtcNow();

        // **必須では自分で解除できない**
        if (options.TwoFactor == TwoFactorMode.Required)
        {
            return Results.Json(new { error = "2 要素は必須に設定されているため、解除できません。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        // パスワードの再確認つき（乗っ取られた端末から保護を外されない）
        var found = await users.FindForLoginAsync(admin.User.LoginId, ct);
        if (found is null
            || admin.User.IsLocked(now)
            || !PasswordHasher.Verify(request.Password ?? "", found.Value.PasswordHash))
        {
            if (found is not null)
            {
                await users.RecordFailureAsync(admin.User.Id, options.LockoutThreshold, options.Lockout, now, ct);
            }

            return Results.Json(new { error = "パスワードが違います。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        await users.DisableTotpAsync(admin.User.Id, ct);
        return Results.NoContent();
    }

    // ---- 共通 ------------------------------------------------------------------------

    /// <summary>ログイン済みのセッションを始める（cookie を発行し、成功を記録する）。</summary>
    private static async Task StartSessionAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        AdminUser user,
        AdminAuthOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var sessionId = await sessions.CreateAsync(user.Id, AdminSessionKind.Session, options.SessionLifetime, now, ct);
        AdminSessionGuard.SetCookie(http, AdminSessionGuard.SessionCookie, sessionId, options.SessionLifetime);
        await users.RecordSuccessAsync(user.Id, now, ct);
        await sessions.DeleteExpiredAsync(now, ct);
    }

    /// <summary>手元の cookie のセッション（ログイン済み・途中のどちらも）を、**サーバ側で**捨てる。cookie の扱いは呼び出し側が決める。</summary>
    private static async Task DropExistingAsync(HttpContext http, IAdminSessionStore sessions, CancellationToken ct)
    {
        foreach (var name in new[] { AdminSessionGuard.SessionCookie, AdminSessionGuard.PendingCookie })
        {
            if (http.Request.Cookies.TryGetValue(name, out var id) && !string.IsNullOrEmpty(id))
            {
                await sessions.DeleteAsync(id, ct);
            }
        }
    }

    /// <summary>2 要素の途中（Pending）のセッションと、その持ち主。</summary>
    private static async Task<(AdminUser User, string SessionId)?> ResolvePendingAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!http.Request.Cookies.TryGetValue(AdminSessionGuard.PendingCookie, out var id) || string.IsNullOrEmpty(id)
            || await sessions.FindAsync(id, now, ct) is not { Kind: AdminSessionKind.Pending } session
            || await users.FindByIdAsync(session.UserId, ct) is not { } user)
        {
            return null;
        }

        return (user, id);
    }

    /// <summary>2 要素を登録できる人: ログイン済み、または「必須で未登録」の途中。</summary>
    private static async Task<AdminContext?> ResolveForEnrollmentAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        AdminAuthOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(AdminSessionGuard.SessionCookie, out var sessionId)
            && !string.IsNullOrEmpty(sessionId)
            && await sessions.FindAsync(sessionId, now, ct) is { Kind: AdminSessionKind.Session } session
            && await users.FindByIdAsync(session.UserId, ct) is { IsDisabled: false } user)
        {
            return new AdminContext(user, sessionId, AdminSessionKind.Session);
        }

        // 途中の人が登録できるのは、**必須で、まだ登録していないとき**だけ（登録済みの人の鍵を差し替えさせない）
        if (options.TwoFactor == TwoFactorMode.Required
            && await ResolvePendingAsync(http, sessions, users, now, ct) is { } pending
            && !pending.User.IsDisabled
            && !pending.User.TotpEnabled)
        {
            return new AdminContext(pending.User, pending.SessionId, AdminSessionKind.Pending);
        }

        return null;
    }

    /// <summary>失敗を数え、締め出しになったら途中のセッションを捨てる。</summary>
    private static async Task RecordFailureAndMaybeDropAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        AdminAuthOptions options,
        AdminUser user,
        string pendingId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var failure = await users.RecordFailureAsync(user.Id, options.LockoutThreshold, options.Lockout, now, ct);
        if (failure.LockedUntil is not null)
        {
            await sessions.DeleteAsync(pendingId, ct);
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        }
    }

    private static string? ValidateLoginId(string? loginId)
    {
        var trimmed = loginId?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 100 || trimmed.Any(char.IsControl)
            ? "ログイン ID は 1〜100 文字（制御文字なし）で指定してください。"
            : null;
    }
}

/// <summary>最初の管理者の作成の要求。</summary>
public sealed record SetupRequest(string? LoginId, string? Password, string? Language = null);

/// <summary>ログインの要求。</summary>
public sealed record LoginRequest(string? LoginId, string? Password);

/// <summary>数字・コードの要求。</summary>
public sealed record CodeRequest(string? Code);

/// <summary>パスワードの再確認の要求。</summary>
public sealed record PasswordRequest(string? Password);

/// <summary>パスワード変更の要求。</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
