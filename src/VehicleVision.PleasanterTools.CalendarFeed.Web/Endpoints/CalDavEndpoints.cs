using System.Globalization;
using System.Text;
using System.Xml.Linq;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Endpoints;

/// <summary>
/// カレンダーアプリが直接つなぐ CalDAV（Issue #85。設計方針 <c>_documents/CalDAV書き込み-設計方針.md</c>）。<b>いまは読み取りのみ</b>。
/// </summary>
/// <remarks>
/// <para>
/// 一次情報: RFC 4791（CalDAV）、RFC 4918（WebDAV）。実装したのは、探索（<c>current-user-principal</c> → <c>calendar-home-set</c> → カレンダー）と、
/// <c>OPTIONS</c> / <c>PROPFIND</c> / <c>REPORT</c>（<c>calendar-query</c>・<c>calendar-multiget</c>）/ <c>GET</c> だけ。
/// 書き込み（<c>PUT</c>・<c>DELETE</c>）・<c>MKCALENDAR</c>・スケジューリング（<c>calendar-auto-schedule</c>）は<b>未対応</b>で、405 を返す。
/// </para>
/// <para>
/// <b>認証は Basic。</b>ユーザー名は任意（検証しない）、<b>パスワードは利用者本人の Pleasanter の API キー</b>。
/// 本アプリは API キーを<b>保管せず</b>、Pleasanter への取得にそのまま使う。見える予定は、その利用者の権限で決まる
/// （フィードに登録した API キーは使わない）。キーが通らなければ 401、権限が無ければ 403。
/// TLS 前提（Basic は平文と同じ）。
/// </para>
/// <para>
/// <b>フィードごとに有効にしたものだけ</b>がカレンダーとして出る（<see cref="FeedDefinition.CalDavEnabled"/>。既定は無効）。
/// </para>
/// </remarks>
public static class CalDavEndpoints
{
    /// <summary>共有のレート制限の枠の名前。</summary>
    public const string RateLimitPolicy = "caldav";

    private static readonly string[] AllMethods =
    [
        "OPTIONS", "GET", "HEAD", "PROPFIND", "REPORT",
        "PUT", "DELETE", "POST", "PROPPATCH", "MKCOL", "MKCALENDAR", "COPY", "MOVE", "LOCK", "UNLOCK",
    ];

    private const string AllowedMethods = "OPTIONS, GET, HEAD, PROPFIND, REPORT";
    private const string EventContentType = "text/calendar; charset=utf-8; component=vevent";

    public static void MapCalDav(this WebApplication app)
    {
        // RFC 6764: 利用者がドメインだけを入れたときの入口。/caldav/ へ転送する
        app.MapMethods("/.well-known/caldav", ["GET", "HEAD", "PROPFIND", "OPTIONS"], () => Results.Redirect(CalDavPaths.Root, permanent: true));

        var filter = new SharedRateLimitFilter(RateLimitPolicy, 600, TimeSpan.FromMinutes(5));
        app.MapMethods("/caldav", AllMethods, Handle).AddEndpointFilter(filter).ExcludeFromDescription();
        app.MapMethods("/caldav/{**path}", AllMethods, Handle).AddEndpointFilter(filter).ExcludeFromDescription();
    }

    private static async Task<IResult> Handle(
        HttpContext http,
        IFeedStore feeds,
        FeedService service,
        CancellationToken ct)
    {
        var method = http.Request.Method.ToUpperInvariant();
        SetHeaders(http);
        if (method == "OPTIONS")
        {
            return Results.Ok();
        }

        var target = CalDavPaths.Parse(http.Request.Path.Value ?? "/");
        if (ReadBasic(http) is not { } credentials)
        {
            return Challenge();
        }

        if (target.Kind == CalDavKind.Unknown)
        {
            return Results.NotFound();
        }

        if (method is not ("GET" or "HEAD" or "PROPFIND" or "REPORT"))
        {
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        try
        {
            return method switch
            {
                "PROPFIND" => await PropfindAsync(http, target, credentials, feeds, service, ct),
                "REPORT" => await ReportAsync(http, target, credentials, feeds, service, ct),
                _ => await GetAsync(target, credentials, feeds, service, ct),
            };
        }
        catch (DavXmlException ex)
        {
            return Results.Text(ex.Message, "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
        catch (PleasanterException ex)
        {
            return FromPleasanter(http, ex, service, target.FeedId is { } id ? await feeds.FindByIdAsync(id, ct) : null);
        }
    }

    // ---- 認証 ----------------------------------------------------------------------------------

    /// <summary>Basic 認証を読む。<b>パスワードが Pleasanter の API キー。</b>ログへ出さない。</summary>
    private static (string User, string ApiKey)? ReadBasic(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return null;
            }

            var user = decoded[..colon];
            var key = decoded[(colon + 1)..];
            return key.Length is > 0 and <= 512 && !key.Any(char.IsControl) && user.Length <= 256 && !user.Any(char.IsControl)
                ? (user.Length == 0 ? "user" : user, key)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IResult Challenge() => new CalDavResult(StatusCodes.Status401Unauthorized, headers:
    [
        ("WWW-Authenticate", "Basic realm=\"CalendarFeed\", charset=\"UTF-8\""),
    ]);

    private static void SetHeaders(HttpContext http)
    {
        http.Response.Headers["DAV"] = "1, calendar-access";
        http.Response.Headers.Allow = AllowedMethods;
        http.Response.Headers["Cache-Control"] = "no-store";
    }

    private static IResult FromPleasanter(HttpContext http, PleasanterException ex, FeedService service, FeedDefinition? feed)
    {
        switch (ex.Kind)
        {
            case PleasanterErrorKind.Unauthorized:
                return Challenge();
            case PleasanterErrorKind.Permanent:
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            default:
                var seconds = feed is null ? 60 : service.RetryAfterSeconds(feed, ex);
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    // ---- 資源 ----------------------------------------------------------------------------------

    private sealed record EventResource(string Uid, string Href, string Body, string ETag, CalendarEvent Event);

    private static async Task<FeedDefinition?> EnabledFeedAsync(Guid id, IFeedStore feeds, CancellationToken ct)
    {
        var feed = await feeds.FindByIdAsync(id, ct);
        return feed is { CalDavEnabled: true } ? feed : null;
    }

    private static List<EventResource> Materialize(FeedDefinition feed, IReadOnlyList<CalendarEvent> events) =>
        events
            .GroupBy(e => e.Uid, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(e =>
            {
                var body = ICalWriter.WriteResource(e);
                return new EventResource(e.Uid, CalDavPaths.Event(feed.Id, e.Uid), body, CalDavPaths.ETagOf(body), e);
            })
            .ToList();

    // ---- GET -----------------------------------------------------------------------------------

    private static async Task<IResult> GetAsync(
        CalDavTarget target, (string User, string ApiKey) credentials, IFeedStore feeds, FeedService service, CancellationToken ct)
    {
        if (target.Kind != CalDavKind.Event || target.FeedId is not { } feedId)
        {
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        var feed = await EnabledFeedAsync(feedId, feeds, ct);
        if (feed is null || CalDavPaths.UidOf(target.FileName!) is not { } uid)
        {
            return Results.NotFound();
        }

        var resources = Materialize(feed, await service.GetCalDavEventsAsync(feed, credentials.ApiKey, ct));
        var resource = resources.FirstOrDefault(r => r.Uid == uid);
        return resource is null
            ? Results.NotFound()
            : new CalDavResult(StatusCodes.Status200OK, resource.Body, EventContentType, [("ETag", resource.ETag)]);
    }

    // ---- PROPFIND ------------------------------------------------------------------------------

    private static async Task<IResult> PropfindAsync(
        HttpContext http, CalDavTarget target, (string User, string ApiKey) credentials,
        IFeedStore feeds, FeedService service, CancellationToken ct)
    {
        var depth = http.Request.Headers["Depth"].ToString().Trim();
        if (depth.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            return new CalDavResult(
                StatusCodes.Status403Forbidden,
                new XDocument(new XElement(DavXml.D + "error", new XElement(DavXml.D + "propfind-finite-depth"))).ToString(SaveOptions.DisableFormatting),
                "application/xml; charset=utf-8");
        }

        var children = depth == "1";
        var request = PropfindRequest.Parse(await ReadBodyAsync(http, ct));
        var multi = new MultiStatus();

        switch (target.Kind)
        {
            case CalDavKind.Root:
                Add(multi, CalDavPaths.Root, RootProps(credentials.User), request);
                break;

            case CalDavKind.Principal:
                Add(multi, CalDavPaths.Principal(target.User!), PrincipalProps(target.User!, credentials.User), request);
                break;

            case CalDavKind.Home:
                Add(multi, CalDavPaths.Home, HomeProps(credentials.User), request);
                if (children)
                {
                    foreach (var feed in await AccessibleFeedsAsync(credentials.ApiKey, feeds, service, ct))
                    {
                        Add(multi, CalDavPaths.Calendar(feed.Id), CalendarProps(feed, credentials.User, ctag: null), request);
                    }
                }

                break;

            case CalDavKind.Calendar:
            {
                var feed = await EnabledFeedAsync(target.FeedId!.Value, feeds, ct);
                if (feed is null)
                {
                    return Results.NotFound();
                }

                var resources = Materialize(feed, await service.GetCalDavEventsAsync(feed, credentials.ApiKey, ct));
                var ctag = CalDavPaths.CTagOf(resources.Select(r => r.ETag));
                Add(multi, CalDavPaths.Calendar(feed.Id), CalendarProps(feed, credentials.User, ctag), request);
                if (children)
                {
                    foreach (var resource in resources)
                    {
                        Add(multi, resource.Href, EventProps(resource, withData: false), request);
                    }
                }

                break;
            }

            case CalDavKind.Event:
            {
                var feed = await EnabledFeedAsync(target.FeedId!.Value, feeds, ct);
                if (feed is null || CalDavPaths.UidOf(target.FileName!) is not { } uid)
                {
                    return Results.NotFound();
                }

                var resource = Materialize(feed, await service.GetCalDavEventsAsync(feed, credentials.ApiKey, ct)).FirstOrDefault(r => r.Uid == uid);
                if (resource is null)
                {
                    return Results.NotFound();
                }

                Add(multi, resource.Href, EventProps(resource, withData: false), request);
                break;
            }

            default:
                return Results.NotFound();
        }

        return MultiStatusResult(multi);
    }

    /// <summary>
    /// ホームの子として出すカレンダー。<b>CalDAV を有効にしたフィードのうち、この API キーで読めるものだけ。</b>
    /// 有効なフィードがあるのに、すべてで API キーが通らなければ、鍵の誤りとして 401 にする。
    /// </summary>
    private static async Task<List<FeedDefinition>> AccessibleFeedsAsync(
        string apiKey, IFeedStore feeds, FeedService service, CancellationToken ct)
    {
        var enabled = (await feeds.ListAsync(ct)).Where(f => f.CalDavEnabled).ToList();
        var checks = await Task.WhenAll(enabled.Select(async feed =>
        {
            try
            {
                await service.EnsureCalDavAccessAsync(feed, apiKey, ct);
                return (feed, Kind: (PleasanterErrorKind?)null);
            }
            catch (PleasanterException ex)
            {
                return (feed, Kind: ex.Kind);
            }
        }));

        if (checks.Length > 0 && checks.All(c => c.Kind == PleasanterErrorKind.Unauthorized))
        {
            throw new PleasanterException(PleasanterErrorKind.Unauthorized, "The API key was rejected.");
        }

        // 一時的な失敗・上限は隠さず出す（一覧から消えると、カレンダーが消えたように見えるため）。権限が無い・鍵が通らないものは出さない
        return checks
            .Where(c => c.Kind is null or PleasanterErrorKind.Transient or PleasanterErrorKind.RateLimited)
            .Select(c => c.feed)
            .ToList();
    }

    // ---- REPORT --------------------------------------------------------------------------------

    private static async Task<IResult> ReportAsync(
        HttpContext http, CalDavTarget target, (string User, string ApiKey) credentials,
        IFeedStore feeds, FeedService service, CancellationToken ct)
    {
        if (target.Kind != CalDavKind.Calendar)
        {
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        ReportRequest request;
        try
        {
            request = ReportRequest.Parse(await ReadBodyAsync(http, ct));
        }
        catch (DavXmlException ex) when (ex.Message.StartsWith("Unsupported REPORT", StringComparison.Ordinal))
        {
            return new CalDavResult(
                StatusCodes.Status403Forbidden,
                new XDocument(new XElement(DavXml.D + "error", new XElement(DavXml.D + "supported-report"))).ToString(SaveOptions.DisableFormatting),
                "application/xml; charset=utf-8");
        }

        var feed = await EnabledFeedAsync(target.FeedId!.Value, feeds, ct);
        if (feed is null)
        {
            return Results.NotFound();
        }

        var resources = Materialize(feed, await service.GetCalDavEventsAsync(feed, credentials.ApiKey, ct));
        var propfind = new PropfindRequest(false, false, request.Props);
        var multi = new MultiStatus();

        if (request.Kind == ReportKind.CalendarMultiget)
        {
            foreach (var href in request.Hrefs)
            {
                var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
                var found = CalDavPaths.Parse(path) is { Kind: CalDavKind.Event } t && t.FeedId == feed.Id
                    && CalDavPaths.UidOf(t.FileName!) is { } uid
                    ? resources.FirstOrDefault(r => r.Uid == uid)
                    : null;
                if (found is null)
                {
                    multi.AddStatus(href, "HTTP/1.1 404 Not Found");
                }
                else
                {
                    Add(multi, found.Href, EventProps(found, withData: true), propfind);
                }
            }
        }
        else
        {
            foreach (var resource in resources.Where(r => InRange(r.Event, request.Start, request.End)))
            {
                Add(multi, resource.Href, EventProps(resource, withData: true), propfind);
            }
        }

        return MultiStatusResult(multi);
    }

    /// <summary>予定が期間と重なるか（開始 &lt; 期間の終わり、かつ 終了 &gt; 期間の始まり。終了が無ければ開始の 1 点）。</summary>
    internal static bool InRange(CalendarEvent e, DateTimeOffset? start, DateTimeOffset? end)
    {
        var eventStart = e.Start.UtcDateTime;
        var eventEnd = (e.End ?? e.Start).UtcDateTime;
        if (end is { } rangeEnd && eventStart >= rangeEnd.UtcDateTime)
        {
            return false;
        }

        if (start is { } rangeStart)
        {
            return eventEnd > rangeStart.UtcDateTime || (eventEnd == eventStart && eventStart >= rangeStart.UtcDateTime);
        }

        return true;
    }

    // ---- プロパティ ----------------------------------------------------------------------------

    private static XElement HrefOf(string href) => new(DavXml.D + "href", href);

    private static XElement CollectionType(params XName[] extra) =>
        new(DavXml.D + "resourcetype", new XElement(DavXml.D + "collection"), extra.Select(n => new XElement(n)));

    private static Dictionary<XName, XElement> RootProps(string user) => new()
    {
        [DavXml.D + "resourcetype"] = CollectionType(),
        [DavXml.D + "displayname"] = new XElement(DavXml.D + "displayname", "CalendarFeed"),
        [DavXml.D + "current-user-principal"] = new XElement(DavXml.D + "current-user-principal", HrefOf(CalDavPaths.Principal(user))),
    };

    private static Dictionary<XName, XElement> PrincipalProps(string principal, string user) => new()
    {
        [DavXml.D + "resourcetype"] = CollectionType(DavXml.D + "principal"),
        [DavXml.D + "displayname"] = new XElement(DavXml.D + "displayname", principal),
        [DavXml.D + "current-user-principal"] = new XElement(DavXml.D + "current-user-principal", HrefOf(CalDavPaths.Principal(user))),
        [DavXml.D + "principal-URL"] = new XElement(DavXml.D + "principal-URL", HrefOf(CalDavPaths.Principal(principal))),
        [DavXml.C + "calendar-home-set"] = new XElement(DavXml.C + "calendar-home-set", HrefOf(CalDavPaths.Home)),
    };

    private static Dictionary<XName, XElement> HomeProps(string user) => new()
    {
        [DavXml.D + "resourcetype"] = CollectionType(),
        [DavXml.D + "displayname"] = new XElement(DavXml.D + "displayname", "calendars"),
        [DavXml.D + "current-user-principal"] = new XElement(DavXml.D + "current-user-principal", HrefOf(CalDavPaths.Principal(user))),
    };

    private static Dictionary<XName, XElement> CalendarProps(FeedDefinition feed, string user, string? ctag)
    {
        var props = new Dictionary<XName, XElement>
        {
            [DavXml.D + "resourcetype"] = CollectionType(DavXml.C + "calendar"),
            [DavXml.D + "displayname"] = new XElement(DavXml.D + "displayname", feed.Name),
            [DavXml.D + "current-user-principal"] = new XElement(DavXml.D + "current-user-principal", HrefOf(CalDavPaths.Principal(user))),
            [DavXml.C + "supported-calendar-component-set"] = new XElement(
                DavXml.C + "supported-calendar-component-set", new XElement(DavXml.C + "comp", new XAttribute("name", "VEVENT"))),
            // **いまは読み取りのみ**なので、書き込みの権限（bind・write-content・unbind）は出さない
            [DavXml.D + "current-user-privilege-set"] = new XElement(
                DavXml.D + "current-user-privilege-set",
                new XElement(DavXml.D + "privilege", new XElement(DavXml.D + "read")),
                new XElement(DavXml.D + "privilege", new XElement(DavXml.D + "read-current-user-privilege-set"))),
            [DavXml.D + "supported-report-set"] = new XElement(
                DavXml.D + "supported-report-set",
                new XElement(DavXml.D + "supported-report", new XElement(DavXml.D + "report", new XElement(DavXml.C + "calendar-query"))),
                new XElement(DavXml.D + "supported-report", new XElement(DavXml.D + "report", new XElement(DavXml.C + "calendar-multiget")))),
        };
        if (ctag is not null)
        {
            props[DavXml.CS + "getctag"] = new XElement(DavXml.CS + "getctag", ctag);
        }

        return props;
    }

    private static Dictionary<XName, XElement> EventProps(EventResource resource, bool withData)
    {
        var props = new Dictionary<XName, XElement>
        {
            [DavXml.D + "resourcetype"] = new XElement(DavXml.D + "resourcetype"),
            [DavXml.D + "getetag"] = new XElement(DavXml.D + "getetag", resource.ETag),
            [DavXml.D + "getcontenttype"] = new XElement(DavXml.D + "getcontenttype", EventContentType),
            [DavXml.D + "getcontentlength"] = new XElement(
                DavXml.D + "getcontentlength", Encoding.UTF8.GetByteCount(resource.Body).ToString(CultureInfo.InvariantCulture)),
        };
        if (withData)
        {
            props[DavXml.C + "calendar-data"] = new XElement(DavXml.C + "calendar-data", resource.Body);
        }

        return props;
    }

    /// <summary>求められたプロパティを、見つかったもの（200）と無いもの（404）に分けて足す。</summary>
    private static void Add(MultiStatus multi, string href, Dictionary<XName, XElement> available, PropfindRequest request)
    {
        if (request.PropNameOnly)
        {
            multi.Add(href, available.Keys.Select(n => new XElement(n)));
            return;
        }

        if (request.AllProp)
        {
            multi.Add(href, available.Where(p => p.Key != DavXml.C + "calendar-data").Select(p => p.Value));
            return;
        }

        multi.Add(
            href,
            request.Props.Where(available.ContainsKey).Select(n => available[n]),
            request.Props.Where(n => !available.ContainsKey(n)));
    }

    // ---- 応答 ----------------------------------------------------------------------------------

    private static IResult MultiStatusResult(MultiStatus multi) =>
        new CalDavResult(StatusCodes.Status207MultiStatus, multi.ToXml(), "application/xml; charset=utf-8");

    private static async Task<string> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is > DavXml.MaxBodyBytes)
        {
            throw new DavXmlException("The request body is too large.");
        }

        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        var buffer = new char[DavXml.MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return read > DavXml.MaxBodyBytes ? throw new DavXmlException("The request body is too large.") : new string(buffer, 0, read);
    }

    /// <summary>状態・ヘッダ・本文を指定して返す。</summary>
    private sealed class CalDavResult(
        int status, string? body = null, string? contentType = null, IEnumerable<(string Name, string Value)>? headers = null) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            foreach (var (name, value) in headers ?? [])
            {
                httpContext.Response.Headers[name] = value;
            }

            if (body is not null)
            {
                httpContext.Response.ContentType = contentType ?? "text/plain; charset=utf-8";
                await httpContext.Response.WriteAsync(body, Encoding.UTF8);
            }
        }
    }
}
