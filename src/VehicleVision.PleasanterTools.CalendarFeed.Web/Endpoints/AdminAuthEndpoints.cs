using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
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
    /// <summary>端末の一覧に出す接続元（表示用。認可には使わない）。</summary>
    internal static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    /// <summary>端末の一覧に出す User-Agent（表示用。長さは保管側で切る）。</summary>
    internal static string? UserAgentOf(HttpContext http) => http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    private static IResult Unauthorized() => ApiError.Of(StatusCodes.Status401Unauthorized, ServerMessageKeys.InvalidCredentials);

    /// <summary>すでに言語ごとに組み立てた文言（パスワードの条件など）で返す。</summary>
    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadKey(string key, params object[] args) => ApiError.Of(StatusCodes.Status400BadRequest, key, args);

    /// <summary>管理者認証の入口を登録する。</summary>
    public static void MapAdminAuth(this WebApplication app)
    {
        var group = app.MapGroup("/admin/api");

        // **状態を変える要求は、同じサイトから来たものだけ**（ログイン前の入口も同じ）
        group.AddEndpointFilter(async (context, next) =>
            AdminSessionGuard.IsSameOrigin(context.HttpContext)
                ? await next(context)
                : ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.CrossSiteRejected));

        // ログイン画面が出し分けに使う（認証前に見える値だけ。SAML の釦の有無と文字）
        group.MapGet("/status", async (IAdminUserStore users, AdminAuthOptions options, RuntimeSettings settings, CancellationToken ct) =>
        {
            var saml = settings.Current.Saml;
            var sso = settings.Current.PleasanterSso;
            return Results.Ok(new
            {
                setupRequired = await users.CountAsync(ct) == 0,
                twoFactor = options.TwoFactor.ToString(),
                saml = new { enabled = saml.Enabled, buttonLabel = saml.Enabled && saml.ButtonLabel.Length > 0 ? saml.ButtonLabel : null },
                // Pleasanter のログイン。ログイン画面が、別窓で開くログイン画面（loginUrl）と釦の文字を使う
                pleasanterSso = new
                {
                    enabled = sso.Enabled,
                    loginUrl = sso.Enabled ? sso.LoginUrl : null,
                    logoutUrl = sso.Enabled && sso.LogoutUrl.Length > 0 ? sso.LogoutUrl : null,
                    buttonLabel = sso.Enabled && sso.ButtonLabel.Length > 0 ? sso.ButtonLabel : null,
                },
            });
        });

        // **ログインの試行だけ別枠のレート制限**（全体の枠に紛れさせると総当たりが通る）
        group.MapPost("/setup", Setup).RequireAdminLoginRateLimit();
        group.MapPost("/login", Login).RequireAdminLoginRateLimit();
        group.MapPost("/login/totp", LoginTotp).RequireAdminLoginRateLimit();
        group.MapPost("/login/recovery", LoginRecovery).RequireAdminLoginRateLimit();
        group.MapPost("/logout", Logout);

        // 2 要素の登録は、ログイン済み、または「必須で未登録」の Pending から。フィルタを付けずに中で判断する
        group.MapPost("/me/totp/begin", TotpBegin);
        group.MapPost("/me/totp/complete", TotpComplete).RequireAdminLoginRateLimit();

        var me = group.MapGroup("/me").AddEndpointFilter<AdminSessionFilter>();
        me.MapGet("/", Me);
        me.MapPost("/password", ChangePassword).RequireAdminLoginRateLimit();
        me.MapPost("/totp/disable", TotpDisable).RequireAdminLoginRateLimit();
    }

    // ---- 最初の管理者 ----------------------------------------------------------------

    private static async Task<IResult> Setup(
        SetupRequest request,
        IAdminUserStore users,
        AdminAuthOptions options,
        HttpContext http,
        AuditLogService audit,
        ILogger<SetupRequest> logger,
        CancellationToken ct)
    {
        if (ValidateLoginId(request.LoginId, RequestLanguage.Of(http)) is { } loginIdError)
        {
            return Bad(loginIdError);
        }

        if (options.PasswordPolicy.Validate(request.LoginId!.Trim(), request.Password ?? "", RequestLanguage.Of(http) ?? request.Language) is { } passwordError)
        {
            return Bad(passwordError);
        }

        // **管理者が 1 人も居ないときだけ通る**（原子的に 1 回だけ）。既定のパスワードを仕込むより、空の状態から作らせる
        var admin = await users.CreateFirstAsync(
            request.LoginId!, PasswordHasher.Hash(request.Password!), FeedLanguages.Normalize(request.Language ?? "ja"), ct);
        if (admin is null)
        {
            return ApiError.Of(StatusCodes.Status409Conflict, ServerMessageKeys.AdministratorAlreadyExists);
        }

        logger.LogInformation("最初の管理者を作成しました（{UserId}）", admin.Id);
        await audit.WriteAsync(http, AuditActions.AdminSetup, true, admin);
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
        AuditLogService audit,
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

        // **外へは同じ応答にするが、記録には理由を残す**（運用者が総当たりと打ち間違いを見分けられるように）
        if (found is null)
        {
            await audit.WriteAsync(http, AuditActions.LoginFailed, false, loginId: loginId, detail: "unknown-user");
            return Unauthorized();
        }

        var (user, hash) = found.Value;
        if (user.IsDisabled || user.IsLocked(now))
        {
            await audit.WriteAsync(http, AuditActions.LoginFailed, false, user, detail: user.IsDisabled ? "disabled" : "locked");
            return Unauthorized();
        }

        if (!verified)
        {
            var failure = await users.RecordFailureAsync(user.Id, options.LockoutThreshold, options.Lockout, ct);
            await audit.WriteAsync(http, AuditActions.LoginFailed, false, user, detail: "bad-password");
            if (failure.LockedUntil is not null)
            {
                logger.LogWarning("管理者 {UserId} を {Until} まで締め出しました（ログインの失敗が続いたため）", user.Id, failure.LockedUntil);
                await audit.WriteAsync(http, AuditActions.LoginLocked, false, user);
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

        return Results.Ok(new { step = await SignInAsync(http, sessions, users, user, options, audit, ct) });
    }

    /// <summary>
    /// パスワードが通った人を、2 要素の方針に従って次の段階へ進める。<b>ログインと招待の受け取りで同じ道を通す</b>
    /// （片方だけ違うと、招待された人だけ方針が食い違う）。登録済みなら <c>totp</c>、必須で未登録なら <c>enroll</c>（どちらも途中の cookie）、
    /// それ以外はそのままログイン済み（<c>done</c>）。
    /// </summary>
    internal static async Task<string> SignInAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        AdminUser user,
        AdminAuthOptions options,
        AuditLogService audit,
        CancellationToken ct,
        SessionSso? sso = null)
    {
        var needsSecondFactor = user.TotpEnabled;
        var mustEnroll = !user.TotpEnabled && options.TwoFactor == TwoFactorMode.Required;
        if (needsSecondFactor || mustEnroll)
        {
            var pending = await sessions.CreateAsync(
                user.Id, AdminSessionKinds.Pending, options.PendingLifetime, ct, Ip(http), UserAgentOf(http), sso);
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
            AdminSessionGuard.SetCookie(http, AdminSessionGuard.PendingCookie, pending, options.PendingLifetime);
            return needsSecondFactor ? "totp" : "enroll";
        }

        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        await StartSessionAsync(http, sessions, users, user, options, audit, ct, sso);
        return "done";
    }

    private static async Task<IResult> LoginTotp(
        CodeRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        AuditLogService audit,
        ILogger<CodeRequest> logger,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (await ResolvePendingAsync(http, sessions, users, ct) is not { } pending)
        {
            return Unauthorized();
        }

        var (user, pendingId) = pending;
        if (user.IsDisabled || user.IsLocked(now))
        {
            return Unauthorized();
        }

        string? secret;
        try
        {
            secret = await users.GetTotpSecretAsync(user.Id, ct);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            // **秘密の鍵を失った・変えた（または未設定）。** 500 にせず通常の失敗として扱う。
            // 復旧コードは鍵に依存しないので、それで入って 2 要素を登録し直せる（運用手順書）
            logger.LogError(
                "管理者 {UserId} の TOTP の共有鍵を復号できません（{Reason}）。復旧コードで入り、2 要素を登録し直してください",
                user.Id, ex.Message);
            return Unauthorized();
        }

        if (secret is null)
        {
            return Unauthorized();
        }

        // **数字の総当たりも、パスワードと同じ締め出しに数える**
        if (!TotpService.Verify(secret, request.Code ?? "", now.UtcDateTime, out var step)
            || !await users.TryUseTotpStepAsync(user.Id, step, ct))
        {
            // 同じ時間枠の数字は二度通さない（盗み見られた数字がそのまま使えてしまう）
            await RecordFailureAndMaybeDropAsync(http, sessions, users, options, audit, AuditActions.LoginTotpFailed, user, pendingId, ct);
            return Unauthorized();
        }

        // SAML で入った人は、途中の段階が持つ単一ログアウトの鍵を、ログイン済みのセッションへ引き継ぐ
        var sso = await sessions.GetSsoAsync(pendingId, ct);
        await sessions.DeleteAsync(pendingId, ct);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        await StartSessionAsync(http, sessions, users, user, options, audit, ct, sso);
        return Results.Ok(new { step = "done" });
    }

    private static async Task<IResult> LoginRecovery(
        CodeRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        AuditLogService audit,
        ILogger<CodeRequest> logger,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (await ResolvePendingAsync(http, sessions, users, ct) is not { } pending)
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
            || !await users.TryUseRecoveryCodeAsync(user.Id, RecoveryCodes.Hash(request.Code), ct))
        {
            await RecordFailureAndMaybeDropAsync(http, sessions, users, options, audit, AuditActions.LoginRecoveryFailed, user, pendingId, ct);
            return Unauthorized();
        }

        logger.LogWarning("管理者 {UserId} が復旧コードでログインしました", user.Id);
        await audit.WriteAsync(http, AuditActions.LoginRecoveryUsed, true, user);
        // SAML で入った人は、途中の段階が持つ単一ログアウトの鍵を、ログイン済みのセッションへ引き継ぐ
        var sso = await sessions.GetSsoAsync(pendingId, ct);
        await sessions.DeleteAsync(pendingId, ct);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        await StartSessionAsync(http, sessions, users, user, options, audit, ct, sso);
        return Results.Ok(new { step = "done", recoveryCodesLeft = await users.CountRecoveryCodesAsync(user.Id, ct) });
    }

    private static async Task<IResult> Logout(
        HttpContext http, IAdminSessionStore sessions, IAdminUserStore users, AuditLogService audit, TimeProvider time, CancellationToken ct)
    {
        // ログイン済みの端末からのときだけ、誰が出たかを残す（cookie が無い・切れていれば何も残さない）
        if (http.Request.Cookies.TryGetValue(AdminSessionGuard.SessionCookie, out var sessionId)
            && !string.IsNullOrEmpty(sessionId)
            && await sessions.FindAsync(sessionId, ct) is { Kind: AdminSessionKinds.Session } session
            && await users.FindByIdAsync(session.UserId, ct) is { } who)
        {
            await audit.WriteAsync(http, AuditActions.Logout, true, who);
        }

        await DropExistingAsync(http, sessions, ct);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
        AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        return Results.NoContent();
    }

    // ---- 自分 ------------------------------------------------------------------------

    private static async Task<IResult> Me(
        HttpContext http, IAdminUserStore users, IAdminSessionStore sessions, AdminAuthOptions options, CancellationToken ct)
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
            // 画面はこれで出し分ける（「Administrator かどうか」で分けると、役割を増やすたびに画面を直すことになる）
            permissions = AdminPermissions.Of(admin.User.Role),
            twoFactor = options.TwoFactor.ToString(),
            recoveryCodesLeft = admin.User.TotpEnabled ? await users.CountRecoveryCodesAsync(admin.User.Id, ct) : 0,
            // SAML で入ったセッションか。ログアウトは IdP へも頼む経路（/admin/api/saml/logout）を通す
            viaSaml = await sessions.GetSamlKeysAsync(admin.SessionId, ct) is not null,
            // Pleasanter で入ったセッションか。ログアウトの後に、Pleasanter のログアウト画面を開く（設定があれば）
            viaPleasanterSso = (await sessions.GetSsoAsync(admin.SessionId, ct))?.Pleasanter is not null,
        });
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        HttpContext http,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthOptions options,
        TimeProvider time,
        AuditLogService audit,
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
                await users.RecordFailureAsync(admin.User.Id, options.LockoutThreshold, options.Lockout, ct);
            }

            await audit.WriteAsync(http, AuditActions.PasswordChangeFailed, false, admin.User, detail: "bad-current-password");
            return ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.CurrentPasswordRejected);
        }

        // **最初の管理者と同じ条件を通す**（片方だけ緩いと、そこが一番弱い所になる）
        if (options.PasswordPolicy.Validate(admin.User.LoginId, request.NewPassword ?? "", RequestLanguage.Of(http) ?? admin.User.Language) is { } error)
        {
            return Bad(error);
        }

        await users.SetPasswordHashAsync(admin.User.Id, PasswordHasher.Hash(request.NewPassword!), ct);

        // **いまの端末以外のセッション（盗まれた登録途中の状態も）を失効させる**
        await sessions.DeleteForUserAsync(admin.User.Id, admin.SessionId, ct);
        await audit.WriteAsync(http, AuditActions.PasswordChanged, true, admin.User);
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
            return ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.TwoFactorDisabled);
        }

        if (await ResolveForEnrollmentAsync(http, sessions, users, options, ct) is not { } who)
        {
            return ApiError.Of(StatusCodes.Status401Unauthorized, ServerMessageKeys.LoginRequired);
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
        AuditLogService audit,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (options.TwoFactor == TwoFactorMode.Disabled)
        {
            return ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.TwoFactorDisabled);
        }

        if (await ResolveForEnrollmentAsync(http, sessions, users, options, ct) is not { } who)
        {
            return ApiError.Of(StatusCodes.Status401Unauthorized, ServerMessageKeys.LoginRequired);
        }

        if (await users.GetTotpPendingAsync(who.User.Id, ct) is not { } secret)
        {
            return BadKey(ServerMessageKeys.EnrollmentBeginRequired);
        }

        if (!TotpService.Verify(secret, request.Code ?? "", now.UtcDateTime, out var step))
        {
            await users.RecordFailureAsync(who.User.Id, options.LockoutThreshold, options.Lockout, ct);
            await audit.WriteAsync(http, AuditActions.TotpEnrollFailed, false, who.User);
            return BadKey(ServerMessageKeys.TotpCodeMismatch);
        }

        // **登録し直すと前の復旧コードは消える。** 復旧コードは、ここで 1 度だけ見せる
        var codes = RecoveryCodes.Generate();
        await users.EnableTotpAsync(who.User.Id, secret, step, codes.Select(RecoveryCodes.Hash).ToList(), ct);
        await audit.WriteAsync(http, AuditActions.TotpEnrolled, true, who.User);

        // 「必須で未登録」の途中（Pending）から来た人は、ここでログインが完了する
        if (who.Kind == AdminSessionKinds.Pending)
        {
            var sso = await sessions.GetSsoAsync(who.SessionId, ct);
            await sessions.DeleteAsync(who.SessionId, ct);
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
            await StartSessionAsync(http, sessions, users, who.User, options, audit, ct, sso);
        }

        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> TotpDisable(
        PasswordRequest request,
        HttpContext http,
        IAdminUserStore users,
        AdminAuthOptions options,
        TimeProvider time,
        AuditLogService audit,
        CancellationToken ct)
    {
        var admin = AdminContext.Of(http);
        var now = time.GetUtcNow();

        // **必須では自分で解除できない**
        if (options.TwoFactor == TwoFactorMode.Required)
        {
            return ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.TwoFactorRequired);
        }

        // パスワードの再確認つき（乗っ取られた端末から保護を外されない）
        var found = await users.FindForLoginAsync(admin.User.LoginId, ct);
        if (found is null
            || admin.User.IsLocked(now)
            || !PasswordHasher.Verify(request.Password ?? "", found.Value.PasswordHash))
        {
            if (found is not null)
            {
                await users.RecordFailureAsync(admin.User.Id, options.LockoutThreshold, options.Lockout, ct);
            }

            await audit.WriteAsync(http, AuditActions.TotpDisableFailed, false, admin.User, detail: "bad-password");
            return ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.PasswordRejected);
        }

        await users.DisableTotpAsync(admin.User.Id, ct);
        await audit.WriteAsync(http, AuditActions.TotpDisabled, true, admin.User);
        return Results.NoContent();
    }

    // ---- 共通 ------------------------------------------------------------------------

    /// <summary>ログイン済みのセッションを始める（cookie を発行し、成功を記録する）。</summary>
    internal static async Task StartSessionAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        AdminUser user,
        AdminAuthOptions options,
        AuditLogService audit,
        CancellationToken ct,
        SessionSso? sso = null)
    {
        var sessionId = await sessions.CreateAsync(
            user.Id, AdminSessionKinds.Session, options.SessionLifetime, ct, Ip(http), UserAgentOf(http), sso);
        AdminSessionGuard.SetCookie(http, AdminSessionGuard.SessionCookie, sessionId, options.SessionLifetime);
        await users.RecordSuccessAsync(user.Id, ct);
        await sessions.DeleteExpiredAsync(ct);
        await audit.WriteAsync(http, AuditActions.LoginSucceeded, true, user);
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
        CancellationToken ct)
    {
        if (!http.Request.Cookies.TryGetValue(AdminSessionGuard.PendingCookie, out var id) || string.IsNullOrEmpty(id)
            || await sessions.FindAsync(id, ct) is not { Kind: AdminSessionKinds.Pending } session
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
        CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(AdminSessionGuard.SessionCookie, out var sessionId)
            && !string.IsNullOrEmpty(sessionId)
            && await sessions.FindAsync(sessionId, ct) is { Kind: AdminSessionKinds.Session } session
            && await users.FindByIdAsync(session.UserId, ct) is { IsDisabled: false } user)
        {
            return new AdminContext(user, sessionId, AdminSessionKinds.Session);
        }

        // 途中の人が登録できるのは、**必須で、まだ登録していないとき**だけ（登録済みの人の鍵を差し替えさせない）
        if (options.TwoFactor == TwoFactorMode.Required
            && await ResolvePendingAsync(http, sessions, users, ct) is { } pending
            && !pending.User.IsDisabled
            && !pending.User.TotpEnabled)
        {
            return new AdminContext(pending.User, pending.SessionId, AdminSessionKinds.Pending);
        }

        return null;
    }

    /// <summary>失敗を数え、締め出しになったら途中のセッションを捨てる。</summary>
    private static async Task RecordFailureAndMaybeDropAsync(
        HttpContext http,
        IAdminSessionStore sessions,
        IAdminUserStore users,
        AdminAuthOptions options,
        AuditLogService audit,
        string action,
        AdminUser user,
        string pendingId,
        CancellationToken ct)
    {
        var failure = await users.RecordFailureAsync(user.Id, options.LockoutThreshold, options.Lockout, ct);
        await audit.WriteAsync(http, action, false, user);
        if (failure.LockedUntil is not null)
        {
            await audit.WriteAsync(http, AuditActions.LoginLocked, false, user);
            await sessions.DeleteAsync(pendingId, ct);
            AdminSessionGuard.ClearCookie(http, AdminSessionGuard.PendingCookie);
        }
    }

    internal static string? ValidateLoginId(string? loginId, string? language = null)
    {
        var trimmed = loginId?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 100 || trimmed.Any(char.IsControl)
            ? ServerMessages.Get(ServerMessageKeys.LoginIdInvalid, language)
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
