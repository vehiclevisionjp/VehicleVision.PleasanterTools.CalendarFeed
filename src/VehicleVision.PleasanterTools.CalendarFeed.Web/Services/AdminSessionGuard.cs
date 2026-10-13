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
        || (http.RequestServices.GetService<IConfiguration>() is { } configuration
            && AppSettings.Text(configuration, AppSettings.PublicBaseUrl) is { } baseUrl
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

        var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
        var expected = AppSettings.Text(configuration, AppSettings.PublicBaseUrl) is { } baseUrl
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
    TimeProvider time) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!AdminSessionGuard.IsSameOrigin(http))
        {
            return Results.Json(new { error = "別のサイトからの要求は受け付けません。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        // **2 要素の途中（Pending）では何も操作させない。** 見るのは Session だけ
        if (!http.Request.Cookies.TryGetValue(AdminSessionGuard.SessionCookie, out var sessionId)
            || string.IsNullOrEmpty(sessionId)
            || await sessions.FindAsync(sessionId, time.GetUtcNow(), http.RequestAborted).ConfigureAwait(false) is not { Kind: AdminSessionKind.Session } session
            || await users.FindByIdAsync(session.UserId, http.RequestAborted).ConfigureAwait(false) is not { IsDisabled: false } user)
        {
            return Results.Json(new { error = "ログインが必要です。" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        http.Items[AdminContext.ItemKey] = new AdminContext(user, sessionId, AdminSessionKind.Session);
        return await next(context).ConfigureAwait(false);
    }
}
