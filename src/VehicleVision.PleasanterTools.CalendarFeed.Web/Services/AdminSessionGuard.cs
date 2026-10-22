using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>管理者のセッション cookie と、状態を変える要求の確認。</summary>
/// <remarks>
/// <para>
/// cookie に載せるのは**推測できない 256 bit のセッション ID だけ**。中身（利用者・期限）はサーバ側（DB）にあり、
/// 失効（ログアウト・パスワード変更・無効化）がその場で効く。
/// </para>
/// <para>
/// <c>HttpOnly</c>（スクリプトから読めない）・<c>SameSite=Strict</c>（別サイトからの要求に付かない）・
/// <c>Secure</c>（HTTPS のときだけ送る）。<c>Path</c> は管理の入口の下だけ。
/// </para>
/// </remarks>
public static class AdminSessionGuard
{
    /// <summary>ログイン済みの cookie。</summary>
    public const string SessionCookie = "calendarfeed_admin";

    /// <summary>2 要素の途中の cookie。</summary>
    public const string PendingCookie = "calendarfeed_admin_pending";

    /// <summary>ログイン後の登録で、パスワードを再確認した途中の cookie。</summary>
    public const string ReenrollCookie = "calendarfeed_admin_reenroll";

    /// <summary>本アプリが追加する登録途中の段階。Pending・Session としては通さない。</summary>
    public const string ReenrollKind = "Reenroll";

    private const string CookiePath = "/admin";

    /// <summary>cookie を設定する。</summary>
    public static void SetCookie(HttpContext http, string name, string sessionId, TimeSpan lifetime) =>
        http.Response.Cookies.Append(name, sessionId, Options(http, lifetime));

    /// <summary>cookie を消す。</summary>
    public static void ClearCookie(HttpContext http, string name) =>
        http.Response.Cookies.Delete(name, Options(http, null));

    private static CookieOptions Options(HttpContext http, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = IsSecure(http),
        Path = CookiePath,
        MaxAge = lifetime,
        IsEssential = true,
    };

    /// <summary>
    /// <c>Secure</c> を付けるか。**HTTPS の要求か、公開 URL が HTTPS のとき。**
    /// TLS をプロキシで終端している構成では、要求自体は HTTP で届くので、公開 URL（<c>CALENDARFEED_PUBLIC_BASEURL</c>）で判断する。
    /// </summary>
    public static bool IsSecure(HttpContext http) =>
        http.Request.IsHttps
        || (http.RequestServices.GetService<RuntimeSettings>() is { } settings
            && settings.Current.PublicBaseUrl is { } baseUrl
            && baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// **状態を変える要求が、同じサイトから来たものか**を確かめる（CSRF）。
    /// <c>SameSite=Strict</c> に加えて、<c>Sec-Fetch-Site</c> と <c>Origin</c> を見る。
    /// どちらも無い要求（curl など）はブラウザ経由ではないので通す（CSRF はブラウザの cookie を悪用するもの）。
    /// </summary>
    public static bool IsSameOrigin(HttpContext http)
    {
        var request = http.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site is not ("same-origin" or "none"))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }

        var settings = http.RequestServices.GetRequiredService<RuntimeSettings>();
        var expected = settings.Current.PublicBaseUrl is { } baseUrl
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var configured)
            ? configured.GetLeftPart(UriPartial.Authority)
            : $"{request.Scheme}://{request.Host}";
        return string.Equals(origin.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>管理 API の入口で、ログイン済みか・同じサイトからの要求かを確かめる。</summary>
public sealed class AdminSessionFilter(
    IAdminSessionStore sessions,
    IAdminUserStore users,
    PleasanterSsoSessionRevalidator revalidator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!AdminSessionGuard.IsSameOrigin(http))
        {
            return ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.CrossSiteRejected);
        }

        // **2 要素の途中（Pending）では何も操作させない。** 見るのは Session だけ
        if (!http.Request.Cookies.TryGetValue(AdminSessionGuard.SessionCookie, out var sessionId)
            || string.IsNullOrEmpty(sessionId)
            || await sessions.FindAsync(sessionId, http.RequestAborted).ConfigureAwait(false) is not { Kind: AdminSessionKinds.Session } session
            || await users.FindByIdAsync(session.UserId, http.RequestAborted).ConfigureAwait(false) is not { IsDisabled: false } user)
        {
            return ApiError.Of(StatusCodes.Status401Unauthorized, ServerMessageKeys.LoginRequired);
        }

        // Pleasanter のログインで入ったセッションは、一定間隔で Pleasanter に確かめ直す（ログアウト・別人・所属の変更を、ここで受け止める）
        switch (await revalidator.RevalidateAsync(http, sessionId, session, http.RequestAborted).ConfigureAwait(false))
        {
            case PleasanterSsoRevalidation.Rejected:
                await sessions.DeleteAsync(sessionId, http.RequestAborted).ConfigureAwait(false);
                AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
                return ApiError.Of(StatusCodes.Status401Unauthorized, ServerMessageKeys.LoginRequired);
            case PleasanterSsoRevalidation.UpstreamError:
                await sessions.DeleteAsync(sessionId, http.RequestAborted).ConfigureAwait(false);
                AdminSessionGuard.ClearCookie(http, AdminSessionGuard.SessionCookie);
                return ApiError.Of(StatusCodes.Status503ServiceUnavailable, ServerMessageKeys.UpstreamSessionEnded);
        }

        http.Items[AdminContext.ItemKey] = new AdminContext(user, sessionId, AdminSessionKinds.Session);
        return await next(context).ConfigureAwait(false);
    }
}
