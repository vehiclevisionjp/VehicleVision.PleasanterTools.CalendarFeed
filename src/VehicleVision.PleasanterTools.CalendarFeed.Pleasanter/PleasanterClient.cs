using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;

/// <summary>接続先 Pleasanter の設定。</summary>
/// <remarks>**API キーを設定ファイルへ直書きしないこと。** 環境変数かローカル上書きで渡す。</remarks>
public sealed class PleasanterOptions
{
    /// <summary>API の呼び出し先。末尾のスラッシュは付けない。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API キー。**サーバ側だけが持つ。購読側へ渡さない。** リクエストボディに載せる方式。</summary>
    public required string ApiKey { get; init; }

    /// <summary>API キーに紐づく Pleasanter 利用者のタイムゾーン ID。日時はこのタイムゾーンで返る。</summary>
    /// <remarks>**運用開始後に変更してはならない。** 変えると既存の予定の時刻がずれる。</remarks>
    public required string ApiKeyUserTimeZoneId { get; init; }

    /// <summary>予定に付けるレコードへのリンクの基点。利用者のブラウザから届く URL を指定する。</summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>API のバージョン。リクエストボディの <c>ApiVersion</c> に載せる。</summary>
    public decimal ApiVersion { get; init; } = 1.1m;

    /// <summary>1 回の呼び出しの上限（秒）。</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Pleasanter 呼び出しの失敗の種類。</summary>
public enum PleasanterErrorKind
{
    /// <summary>一時的。待てば直る見込みがある。</summary>
    Transient,

    /// <summary>API キーが拒否された。**人が直す必要がある。**</summary>
    Unauthorized,

    /// <summary>送った内容か権限の問題。再試行しても通らない。</summary>
    Permanent,
}

/// <summary>Pleasanter 呼び出しの失敗。</summary>
/// <remarks>メッセージに API キーやリクエスト本文を含めないこと。</remarks>
public sealed class PleasanterException(PleasanterErrorKind kind, string message, int? statusCode = null)
    : Exception(message)
{
    public PleasanterErrorKind Kind { get; } = kind;

    public int? StatusCode { get; } = statusCode;
}

/// <summary>一覧取得の 1 ページ分。</summary>
/// <param name="TotalCount">条件に合う件数の全体。</param>
/// <param name="PageSize">Pleasanter が実際に適用したページサイズ（サーバ側の上限で切られる）。</param>
/// <param name="Records">このページのレコード。</param>
public sealed record PleasanterPage(int TotalCount, int PageSize, IReadOnlyList<JsonNode> Records);

/// <summary>Pleasanter の標準 API を叩く（このアプリで要るのは一覧取得だけ）。</summary>
/// <remarks>**再試行しない。** 失敗の扱いは呼び出し側が決める。</remarks>
public sealed class PleasanterClient(HttpClient httpClient, PleasanterOptions options)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>サイトのレコードを 1 ページ取り出す。</summary>
    /// <param name="siteId">サイト ID。</param>
    /// <param name="offset">取り出し始める位置。</param>
    /// <param name="pageSize">1 ページの件数。サーバ側の上限を超えると上限へ切られる。</param>
    /// <param name="filters">列名と絞り込み値（<c>View.ColumnFilterHash</c>）。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    public async Task<PleasanterPage> GetRecordsAsync(
        long siteId,
        int offset,
        int pageSize,
        IReadOnlyDictionary<string, string>? filters,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["ApiKey"] = options.ApiKey,
            ["ApiVersion"] = options.ApiVersion,
            ["Offset"] = offset,
            ["PageSize"] = pageSize,
        };
        if (filters is { Count: > 0 })
        {
            body["View"] = new Dictionary<string, object?> { ["ColumnFilterHash"] = filters };
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}/api/items/{siteId}/Get")
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // ex.Message に URL は載るが、API キーは本文にしか無いので漏れない
            throw new PleasanterException(PleasanterErrorKind.Transient, "Pleasanter へ接続できませんでした。");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw new PleasanterException(
                    Classify(response.StatusCode),
                    $"Pleasanter が HTTP {status} を返しました。",
                    status);
            }

            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                throw new PleasanterException(
                    PleasanterErrorKind.Transient, "Pleasanter の応答が JSON ではありません。", status);
            }

            // 応答本文の StatusCode も見る（HTTP が 200 でも本文でエラーを返す場合に備える）
            var bodyStatus = node?["StatusCode"]?.GetValue<int?>();
            if (bodyStatus is { } s and not 200)
            {
                throw new PleasanterException(
                    Classify((HttpStatusCode)s),
                    $"Pleasanter が StatusCode {s} を返しました。",
                    s);
            }

            if (node?["Response"] is not JsonObject payload || payload["Data"] is not JsonArray data)
            {
                throw new PleasanterException(
                    PleasanterErrorKind.Permanent, "Pleasanter の応答に Response.Data がありません。", status);
            }

            return new PleasanterPage(
                payload["TotalCount"]?.GetValue<int>() ?? data.Count,
                payload["PageSize"]?.GetValue<int>() ?? pageSize,
                data.OfType<JsonNode>().ToList());
        }
    }

    private static PleasanterErrorKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => PleasanterErrorKind.Transient,
        _ => (int)statusCode >= 500 ? PleasanterErrorKind.Transient : PleasanterErrorKind.Permanent,
    };
}
