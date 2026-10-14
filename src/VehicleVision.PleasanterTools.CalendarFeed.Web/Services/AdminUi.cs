namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>管理画面（Svelte。<c>wwwroot/admin</c> に組み立てる）の配信とセキュリティヘッダ。</summary>
/// <remarks>
/// <para>
/// 入口は <c>/admin</c>（セッション cookie の <c>Path</c> と同じ）。<c>/admin/assets/…</c> は静的ファイル、
/// <c>/admin/invite/…</c> などのパスは SPA が解釈するので <c>index.html</c> を返す。
/// <c>/admin/api/…</c> は API で、見つからなければ HTML ではなく 404 を返す。
/// </para>
/// <para>
/// **CSP は自分のオリジンだけ。** スクリプト・スタイル・接続先とも <c>'self'</c>、画像は QR コード（<c>data:</c>）のため許す。
/// インラインのスクリプト・スタイル（<c>style</c> 属性を含む）は許さない。管理画面はこれに収まるように書く。
/// </para>
/// </remarks>
public static class AdminUi
{
    /// <summary>管理画面の入口。</summary>
    public const string Root = "/admin";

    private const string IndexFile = "admin/index.html";

    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
        + "font-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary><c>/admin</c> 配下の応答へセキュリティヘッダを付ける。</summary>
    public static IApplicationBuilder UseAdminSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (IsUnderAdmin(http.Request.Path))
            {
                http.Response.OnStarting(() =>
                {
                    var headers = http.Response.Headers;
                    headers["Content-Security-Policy"] = ContentSecurityPolicy;
                    headers["X-Content-Type-Options"] = "nosniff";
                    headers["X-Frame-Options"] = "DENY";
                    headers["Referrer-Policy"] = "no-referrer";

                    // **API の応答（TOTP の共有鍵・復旧コード・招待 URL を含む）を、ブラウザにも中継にも残させない**
                    if (http.Request.Path.StartsWithSegments($"{Root}/api", StringComparison.OrdinalIgnoreCase))
                    {
                        headers.CacheControl = "no-store";
                    }

                    return Task.CompletedTask;
                });
            }

            await next();
        });

    /// <summary>管理画面の入口（<c>index.html</c>）と、SPA のパスの受け口を登録する。</summary>
    public static void MapAdminUi(this WebApplication app) =>
        app.MapGet($"{Root}/{{**path}}", (string? path, IWebHostEnvironment environment) =>
        {
            // API の見つからないパスは、HTML を返さず 404
            if (path is not null && (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase) || path == "api"))
            {
                return Results.NotFound();
            }

            // 組み立て済みのファイル（assets/…）。**UseStaticFiles は使えない**（経路が先に決まると、静的ファイルの手前で止まる）。
            // 範囲外へ出る指定（..）は断り、ファイルを指す URL が無ければ HTML を返さず 404
            if (path is not null && Path.HasExtension(path))
            {
                var file = path.Contains("..", StringComparison.Ordinal) || path.Contains('\\')
                    ? null
                    : environment.WebRootFileProvider.GetFileInfo($"admin/{path}");
                if (file is not { Exists: true, IsDirectory: false } || !ContentTypes.TryGetContentType(file.Name, out var contentType))
                {
                    return Results.NotFound();
                }

                return Results.File(file.CreateReadStream(), contentType);
            }

            var index = environment.WebRootFileProvider.GetFileInfo(IndexFile);
            return index.Exists
                ? Results.File(index.CreateReadStream(), "text/html; charset=utf-8")
                : Results.Text(
                    "The admin UI is not built. Run `npm ci && npm run build` in the Frontend project.",
                    "text/plain; charset=utf-8",
                    statusCode: StatusCodes.Status404NotFound);
        });

    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypes = new();

    private static bool IsUnderAdmin(PathString path) =>
        path.Equals(Root, StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments(Root, StringComparison.OrdinalIgnoreCase);
}
