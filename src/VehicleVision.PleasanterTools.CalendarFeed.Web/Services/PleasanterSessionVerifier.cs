using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>Pleasanter へ問い合わせた結果の種類。</summary>
public enum PleasanterSessionStatus
{
    /// <summary>Pleasanter にログインしている（2 要素まで済んでいる）。</summary>
    Authenticated,

    /// <summary>Pleasanter にログインしていない。</summary>
    Unauthenticated,

    /// <summary>本人だが、許可した組織・グループに所属していない。</summary>
    NotAllowed,

    /// <summary>
    /// 問い合わせが成り立たなかった（接続できない・時間切れ・想定外の応答）。
    /// **成功とは決して扱わない。**
    /// </summary>
    UpstreamError,
}

/// <summary>Pleasanter が返した本人。</summary>
public sealed record PleasanterIdentity(int TenantId, int UserId, string LoginId, string? Name, int? DeptId = null);

/// <summary>Pleasanter へ問い合わせた結果。</summary>
/// <param name="Reason">
/// ログと操作の記録に残す短い理由（英小文字とハイフン）。**画面へは出さない。**
/// </param>
public sealed record PleasanterSessionResult(
    PleasanterSessionStatus Status,
    PleasanterIdentity? Identity = null,
    string Reason = "")
{
    public static PleasanterSessionResult Unauthenticated(string reason) =>
        new(PleasanterSessionStatus.Unauthenticated, Reason: reason);

    public static PleasanterSessionResult Error(string reason) =>
        new(PleasanterSessionStatus.UpstreamError, Reason: reason);
}

/// <summary>ブラウザが持つ Pleasanter の cookie で、Pleasanter に誰がログインしているかを聞く（Issue #28）。</summary>
public interface IPleasanterSessionVerifier
{
    /// <summary>要求の cookie を転送して、Pleasanter に本人を聞く。</summary>
    /// <param name="cookieHeaders">ブラウザから届いた <c>Cookie</c> ヘッダの生値。</param>
    Task<PleasanterSessionResult> VerifyAsync(
        StringValues cookieHeaders,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Pleasanter の API へ cookie を転送して本人を聞く。</summary>
/// <remarks>
/// <para>
/// **cookie を転送して <c>POST /api/users/get</c> を利用者 ID <c>Own</c> で絞って 1 回呼ぶ。**
/// 本人は cookie の認証で Pleasanter に決めさせる
/// （<c>_reference/Implem.Pleasanter/Implem.Pleasanter/Libraries/Requests/Context.cs</c>）。
/// Pleasanter は <c>Own</c> をログイン中の利用者 ID に置き換える
/// （<c>Libraries/Settings/View.cs</c> の <c>ConvertedOwn</c>）。読むだけで書き込みは無い。
/// </para>
/// <para>
/// 利用者の API 利用が禁止されていて 403 が返ったときだけ、本アプリの API キーを使う代わりの経路へ回る
/// （<see cref="VerifyWithApiKeyAsync"/>）。**本アプリは回答を Pleasanter へ書くために API キーを必ず持つ。**
/// 複数テナントでもテナントと本アプリの配置は 1 対 1 なので、キーの持ち主と利用者のテナントは一致する。
/// </para>
/// <para>
/// ⚠️ **成功と判断するのは、JSON の業務ステータスが 200 で、本人がちょうど 1 行、
/// TenantId・UserId・LoginId がきっちり読めたときだけ。** 応答を正しく読めたことを確かめるため。
/// 転送（3xx）・HTML・時間切れ・JSON でないもの・5xx はすべて <see cref="PleasanterSessionStatus.UpstreamError"/>。
/// **cookie を転送した問い合わせの 401 だけが「ログインしていない」。**
/// </para>
/// <para>
/// ⚠️ **cookie の値と API キーはログへ出さない。** 名前の数と応答の状態だけを残す。
/// **cookie を転送する問い合わせに API キーを載せない。API キーで問い合わせるときは cookie を送らない。**
/// </para>
/// <para>
/// **HttpClient は cookie を覚えない・転送を追わない設定で作る**（<c>Program.cs</c> の
/// <see cref="HttpClientName"/>）。cookie は要求ごとに <c>Cookie</c> ヘッダで付ける。
/// 覚える設定だと、ある管理者の cookie が別の管理者の問い合わせへ混ざる。
/// </para>
/// </remarks>
public sealed partial class PleasanterSessionVerifier(
    IHttpClientFactory clientFactory,
    RuntimeSettings runtime,
    ILogger<PleasanterSessionVerifier> logger,
    TimeProvider? timeProvider = null) : IPleasanterSessionVerifier
{
    /// <summary>問い合わせに使う HttpClient の名前。</summary>
    public const string HttpClientName = "PleasanterSso";

    /// <summary>代わりの経路で <c>/api/sessions/set</c> に書く鍵。</summary>
    /// <remarks>
    /// **Pleasanter のセッションごとに保存され、セッションと一緒に消える**（<c>SavePerUser</c> を付けない）。
    /// 値に意味は無い。応答から利用者 ID を読むためだけに書く。
    /// </remarks>
    public const string SessionProbeKey = "VehicleVision.CalendarFeed.SsoProbe";

    /// <summary>読む応答の上限。**本人 1 行なので小さい。**</summary>
    private const int MaxResponseBytes = 64 * 1024;

    /// <summary>Pleasanter の API へ渡す版。**Pleasanter の API の版で、本アプリの設定とは別。**</summary>
    private const decimal ApiVersion = 1.1m;

    /// <summary>ログイン中の本人だけに絞る <c>/api/users/get</c> の本文。</summary>
    private static readonly string OwnUserQuery = JsonSerializer.Serialize(new
    {
        ApiVersion,
        View = new { ColumnFilterHash = new { UserId = "[\"Own\"]" } },
    });

    public async Task<PleasanterSessionResult> VerifyAsync(
        StringValues cookieHeaders,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || options.InternalBaseUrl is null)
        {
            return PleasanterSessionResult.Error("disabled");
        }

        var (cookieHeader, forwardedCount) = BuildCookieHeader(cookieHeaders, options);
        if (forwardedCount == 0)
        {
            // **送るものが無ければ聞くまでもない。** Pleasanter へ無駄な要求を出さない
            return PleasanterSessionResult.Unauthenticated("no-cookie");
        }

        // **時間切れは問い合わせ全体に掛ける。** 代わりの経路で 3 回呼んでも、待つ長さは設定の 1 回分
        using var deadline = new CancellationTokenSource(options.Timeout, timeProvider ?? TimeProvider.System);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var stopwatch = Stopwatch.StartNew();

        PleasanterSessionResult result;
        try
        {
            result = await VerifyOwnUserAsync(options, cookieHeader, timeout.Token).ConfigureAwait(false);
            if (result.Status == PleasanterSessionStatus.Authenticated && options.HasMembershipRestriction)
            {
                result = await VerifyMembershipAsync(result.Identity!, options, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Pleasanter への本人確認が時間内に終わりませんでした（{Timeout} 秒）。",
                options.Timeout.TotalSeconds);
            return PleasanterSessionResult.Error("timeout");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Pleasanter へ本人確認を問い合わせられませんでした。");
            return PleasanterSessionResult.Error("unreachable");
        }

        LogOutcome(result, forwardedCount, stopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>転送してよい cookie だけを並べ直す。</summary>
    /// <remarks>
    /// **生のヘッダから切り出す。** <c>Request.Cookies</c> は値を URL デコードして返すため、
    /// 組み立て直すと元と違う値になり得る。**受け取った形のまま渡す。**
    /// </remarks>
    public static (string Header, int Count) BuildCookieHeader(
        StringValues cookieHeaders,
        PleasanterSsoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new StringBuilder();
        var count = 0;

        foreach (var header in cookieHeaders)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            foreach (var part in header.Split(';'))
            {
                var pair = part.Trim();
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var name = pair[..separator].Trim();
                if (!options.ShouldForwardCookie(name))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

                builder.Append(name).Append('=').Append(pair[(separator + 1)..].Trim());
                count++;
            }
        }

        return (builder.ToString(), count);
    }

    // ---- 問い合わせの流れ --------------------------------------------------

    /// <summary><c>/api/users/get</c> を <c>Own</c> で絞って本人を聞く。403 なら代わりの経路へ回る。</summary>
    private async Task<PleasanterSessionResult> VerifyOwnUserAsync(
        PleasanterSsoOptions options,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var (failure, document) = await PostAsync(
            new Uri(options.InternalBaseUrl!, "api/users/get"),
            OwnUserQuery,
            cookieHeader,
            reasonPrefix: string.Empty,
            cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            using (document)
            {
                return ParseUsers(document.RootElement, expectedUserId: null, reasonPrefix: string.Empty);
            }
        }

        if (failure!.Reason is not ("http-403" or "status-403"))
        {
            return failure;
        }

        // **403 は、この利用者に API の利用が許されていないとき**（User.json の DisableApi や
        // 利用者ごとの API 禁止。Libraries/General/Validators.cs の ValidateApi）。
        // 本アプリの API キーがあれば、利用者 ID だけを cookie で得て、API キーで引き直す
        var connection = await GetApiKeyConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            logger.LogWarning(
                "Pleasanter が利用者の API 利用を許していないため（{Reason}）、本人を読めませんでした。"
                + "User.json の DisableApi や利用者ごとの API 禁止を確認してください。API を禁止したまま使うには、"
                + "本アプリの Pleasanter 接続設定（{BaseUrlKey} と {ApiKeyKey}）を設定してください。",
                failure.Reason,
                AppSettingDefinitions.PleasanterBaseUrl,
                AppSettingDefinitions.PleasanterApiKey);
            return failure;
        }

        logger.LogInformation(
            "Pleasanter が利用者の API 利用を許していないため、本アプリの API キーで本人を引き直します。");
        return await VerifyWithApiKeyAsync(options, cookieHeader, connection.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>代わりの経路。cookie で利用者 ID を得て、本アプリの API キーで利用者を引く。</summary>
    /// <remarks>
    /// <para>
    /// **1 回目**: cookie を転送して <c>/api/sessions/set</c> を呼ぶ。応答に利用者 ID が載る
    /// （<c>Models/Sessions/SessionUtilities.cs</c> の <c>SetByApi</c>）。sessions の API は
    /// API 利用の禁止を見ない。**401 は「ログインしていない」。**
    /// </para>
    /// <para>
    /// **2 回目**: 本アプリの API キーで <c>/api/users/{id}/get</c> を呼ぶ。**cookie は送らない。**
    /// 宛先は本アプリの Pleasanter 接続設定の URL（API キーをいつも送っている先）。
    /// **API キーを別の宛先へ送らない**（内部 URL を書き換えられてもキーが漏れない）。
    /// ここでの 401・403 は設定の誤りであって、利用者のログアウトではない。
    /// </para>
    /// </remarks>
    private async Task<PleasanterSessionResult> VerifyWithApiKeyAsync(
        PleasanterSsoOptions options,
        string cookieHeader,
        (Uri BaseUrl, string ApiKey) connection,
        CancellationToken cancellationToken)
    {
        var (failure, document) = await PostAsync(
            new Uri(options.InternalBaseUrl!, "api/sessions/set"),
            JsonSerializer.Serialize(new
            {
                ApiVersion,
                SessionKey = SessionProbeKey,
                SessionValue = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            }),
            cookieHeader,
            reasonPrefix: "session-",
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return failure!;
        }

        int userId;
        using (document)
        {
            if (!TryGetObject(document.RootElement, "Response", out var response)
                || !TryGetPositiveInt(response, "UserId", out userId))
            {
                return PleasanterSessionResult.Error("session-no-user-id");
            }
        }

        (failure, document) = await PostAsync(
            new Uri(connection.BaseUrl, string.Create(CultureInfo.InvariantCulture, $"api/users/{userId}/get")),
            JsonSerializer.Serialize(new { ApiVersion, ApiKey = connection.ApiKey }),
            cookieHeader: null,
            reasonPrefix: "user-",
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return failure!;
        }

        using (document)
        {
            return ParseUsers(document.RootElement, expectedUserId: userId, reasonPrefix: "user-");
        }
    }

    /// <summary>本アプリの Pleasanter 接続設定から URL と API キーを取る。**揃っていなければ <c>null</c>。**</summary>
    private Task<(Uri BaseUrl, string ApiKey)?> GetApiKeyConnectionAsync(CancellationToken cancellationToken)
    {
        var pleasanter = runtime.Current.Pleasanter;
        return Task.FromResult(ConnectionOf(pleasanter));
    }

    private static (Uri BaseUrl, string ApiKey)? ConnectionOf(Pleasanter.PleasanterOptions pleasanter)
    {
        if (string.IsNullOrWhiteSpace(pleasanter.ApiKey)
            || !Uri.TryCreate(pleasanter.BaseUrl?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return (baseUrl, pleasanter.ApiKey.Trim());
    }

    // ---- 問い合わせと応答の読み取り ------------------------------------------

    /// <summary>JSON を POST し、業務ステータス 200 の JSON だけを返す。**それ以外は理由を付けて返す。**</summary>
    /// <param name="cookieHeader">
    /// 転送する cookie。**cookie を転送した問い合わせだけ、401 を「ログインしていない」と読む。**
    /// cookie を付けない（API キーで問い合わせる）ときの 401 は設定の誤り。
    /// </param>
    /// <returns>成功なら <c>Document</c>（呼び出し元が破棄する）、失敗なら <c>Failure</c>。</returns>
    private async Task<(PleasanterSessionResult? Failure, JsonDocument? Document)> PostAsync(
        Uri url,
        string json,
        string? cookieHeader,
        string reasonPrefix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await clientFactory.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        PleasanterSessionResult Fail(string reason) => PleasanterSessionResult.Error(reasonPrefix + reason);

        PleasanterSessionResult Unauthorized(string reason) => cookieHeader is not null
            ? PleasanterSessionResult.Unauthenticated(reasonPrefix + reason)
            : Fail(reason);

        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (Unauthorized("http-401"), null);
        }

        if (status is >= 300 and < 400)
        {
            // **転送は追わない。** ログイン画面への転送などは「成り立たなかった」扱い
            return (Fail("redirect"), null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (Fail($"http-{status}"), null);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null
            || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning(
                "Pleasanter の応答が JSON ではありませんでした（{MediaType}）。",
                AuditText.Safe(mediaType, 100));
            return (Fail("not-json"), null);
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return (Fail("too-large"), null);
        }

        byte[]? body;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            body = await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (body is not { Length: > 0 })
        {
            return (Fail("empty-or-too-large"), null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (Fail("malformed-json"), null);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("StatusCode", out var statusCode)
            || statusCode.ValueKind != JsonValueKind.Number
            || !statusCode.TryGetInt32(out var businessStatus))
        {
            document.Dispose();
            return (Fail("no-status"), null);
        }

        if (businessStatus != 200)
        {
            document.Dispose();
            return (businessStatus == 401 ? Unauthorized("status-401") : Fail($"status-{businessStatus}"), null);
        }

        return (null, document);
    }

    /// <summary>上限まで読む。**超えたら <c>null</c>。**</summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    /// <summary><c>/api/users/get</c> の応答（業務ステータス 200）を読む。</summary>
    /// <remarks>
    /// 期待する形（2026-09-25 に Pleasanter 1.5.8.1 で実測）:
    /// <c>{"StatusCode":200,"Response":{"Offset":0,"PageSize":200,"TotalCount":1,"Data":[{"TenantId":1,"UserId":2,"LoginId":"...","Name":"...", ...}]}}</c>。
    /// **<c>TotalCount</c> が 1 で、行もちょうど 1 つのときだけ成功にする。** 応答を正しく読めたことを確かめるため。
    /// </remarks>
    private static PleasanterSessionResult ParseUsers(JsonElement root, int? expectedUserId, string reasonPrefix)
    {
        if (!TryGetObject(root, "Response", out var response)
            || !response.TryGetProperty("Data", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return PleasanterSessionResult.Error(reasonPrefix + "no-data");
        }

        if (!response.TryGetProperty("TotalCount", out var totalCount)
            || totalCount.ValueKind != JsonValueKind.Number
            || !totalCount.TryGetInt32(out var total))
        {
            return PleasanterSessionResult.Error(reasonPrefix + "no-total-count");
        }

        if (total != 1)
        {
            return PleasanterSessionResult.Error(reasonPrefix + (total == 0 ? "no-row" : "multiple-rows"));
        }

        var count = rows.GetArrayLength();
        if (count != 1)
        {
            return PleasanterSessionResult.Error(reasonPrefix + (count == 0 ? "no-row" : "multiple-rows"));
        }

        var row = rows[0];
        if (row.ValueKind != JsonValueKind.Object
            || !TryGetPositiveInt(row, "TenantId", out var tenantId)
            || !TryGetPositiveInt(row, "UserId", out var userId)
            || !row.TryGetProperty("LoginId", out var loginIdElement)
            || loginIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(loginIdElement.GetString()))
        {
            return PleasanterSessionResult.Error(reasonPrefix + "invalid-row");
        }

        if (expectedUserId is { } expected && userId != expected)
        {
            return PleasanterSessionResult.Error(reasonPrefix + "mismatch");
        }

        string? name = row.TryGetProperty("Name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

        int? deptId = row.TryGetProperty("DeptId", out var dept)
            && dept.ValueKind == JsonValueKind.Number && dept.TryGetInt32(out var id) && id >= 0 ? id : null;

        return new PleasanterSessionResult(
            PleasanterSessionStatus.Authenticated,
            new PleasanterIdentity(tenantId, userId, loginIdElement.GetString()!.Trim(), name, deptId));
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetPositiveInt(JsonElement row, string name, out int value)
    {
        value = 0;
        return row.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value > 0;
    }

    // ---- ログ -------------------------------------------------------------

    private void LogOutcome(PleasanterSessionResult result, int cookieCount, long elapsedMs)
    {
        switch (result.Status)
        {
            case PleasanterSessionStatus.Authenticated:
                logger.LogInformation(
                    "Pleasanter が本人を返しました（TenantId {TenantId}、UserId {UserId}、{ElapsedMs} ms）。",
                    result.Identity!.TenantId,
                    result.Identity.UserId,
                    elapsedMs);
                break;
            case PleasanterSessionStatus.NotAllowed:
                logger.LogInformation("Pleasanter SSO の許可した所属に該当しないため拒否しました（{Reason}）。", result.Reason);
                break;
            case PleasanterSessionStatus.Unauthenticated:
                logger.LogInformation(
                    "Pleasanter は未ログインと答えました（{Reason}、cookie {CookieCount} 個、{ElapsedMs} ms）。",
                    result.Reason,
                    cookieCount,
                    elapsedMs);
                break;
            default:
                logger.LogWarning(
                    "Pleasanter の本人確認が成り立ちませんでした（{Reason}）。{Hint}",
                    result.Reason,
                    Hint(result.Reason));
                break;
        }
    }

    /// <summary>失敗の理由ごとに、運用者が見るべき所を返す。</summary>
    private static string Hint(string reason) =>
        reason switch
        {
            "redirect" or "session-redirect" => "内部 URL が Pleasanter を指しているか確認してください。",
            "http-403" or "status-403" =>
                "利用者の API 利用が禁止されています。本アプリの Pleasanter 接続設定の API キーを設定してください。",
            "user-http-401" or "user-status-401" or "user-http-403" or "user-status-403" =>
                "本アプリの Pleasanter 接続設定の API キーが正しいか、キーの持ち主の API 利用が許されているかを確認してください。",
            "user-no-row" =>
                "API キーの持ち主と同じテナントの利用者しか引けません。本アプリの Pleasanter 接続設定の URL が内部 URL と同じ Pleasanter を指しているかも確認してください。",
            "user-mismatch" or "user-multiple-rows" =>
                "本アプリの Pleasanter 接続設定の URL が内部 URL と同じ Pleasanter を指しているか確認してください。",
            _ => string.Empty,
        };
}
