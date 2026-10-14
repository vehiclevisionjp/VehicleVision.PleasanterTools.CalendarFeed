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
    /// <summary>Pleasanter の URL。<b>未設定（空）でも起動する</b>（管理画面から設定できる）。未設定のまま取りに行くと失敗する。</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// 全フィード共通の既定の API キー（任意）。**フィードごとの API キーが未登録のときだけ使う。**
    /// サーバ側だけが持つ。購読側へ渡さない。リクエストボディに載せる方式。
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// 既定のタイムゾーン ID。フィードごとの <c>ApiKeyUserTimeZoneId</c> が未設定のときに、
    /// API キーに紐づく Pleasanter 利用者のタイムゾーンとして使う。日時はこのタイムゾーンで返る。
    /// </summary>
    /// <remarks>**運用開始後に変更してはならない。** 変えると既存の予定の時刻がずれる。</remarks>
    public required string ApiKeyUserTimeZoneId { get; init; }

    /// <summary>予定に付けるレコードへのリンクの基点。利用者のブラウザから届く URL を指定する。</summary>
    public string? LinkBaseUrl { get; init; }

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
/// <summary>接続設定の出どころ。<b>呼び出すたびに読む</b>（管理画面で編集した設定が、再起動なしで効く）。</summary>
public interface IPleasanterOptionsSource
{
    /// <summary>いまの接続設定。</summary>
    PleasanterOptions Current { get; }
}

public sealed class PleasanterClient(HttpClient httpClient, IPleasanterOptionsSource optionsSource)
{
    private PleasanterOptions options => optionsSource.Current;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>サイトのレコードを 1 ページ取り出す。</summary>
    /// <param name="apiKey">この呼び出しに使う API キー。**ログ・例外へ出さない。**</param>
    /// <param name="siteId">サイト ID。</param>
    /// <param name="offset">取り出し始める位置。</param>
    /// <param name="pageSize">1 ページの件数。サーバ側の上限を超えると上限へ切られる。</param>
    /// <param name="filters">列名と絞り込み値（<c>View.ColumnFilterHash</c>）。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    public async Task<PleasanterPage> GetRecordsAsync(
        string apiKey,
        long siteId,
        int offset,
        int pageSize,
        IReadOnlyDictionary<string, string>? filters,
        CancellationToken cancellationToken = default)
    {
        var view = new Dictionary<string, object?>();
        if (filters is { Count: > 0 })
        {
            view["ColumnFilterHash"] = filters;
        }

        return await PostGetAsync(apiKey, siteId, offset, pageSize, view, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// **選択肢・リンク・ユーザー・状況の列を、Pleasanter に表示名へ解決させて取り出す**（Issue #5）。
    /// 返すのは（レコード ID → 列名 → 表示名）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>View.ApiDataType = KeyValues</c> ＋ <c>ApiColumnValueDisplayType = Text</c> は、<c>GridColumns</c> に指定した列だけを
    /// 表示用の文字で返す（実機検証結果 5 章）。**日時には使えない**（日付だけ・ロケール依存）ので、日時は
    /// <see cref="GetRecordsAsync"/> の生の値を使う。
    /// </para>
    /// <para>
    /// **<paramref name="offset"/> / <paramref name="pageSize"/> / <paramref name="filters"/> は
    /// <see cref="GetRecordsAsync"/> と同じ値を渡す。** 同じ条件なら並びと件数が揃うことを実機で確認した。
    /// 呼び出し側はレコード ID で突き合わせる。
    /// </para>
    /// <para>解決できる範囲は API キーの利用者の権限で決まる（読めない列は返らない）。</para>
    /// </remarks>
    /// <param name="idColumn">レコード ID の列（<c>IssueId</c> / <c>ResultId</c>）。</param>
    /// <param name="columns">解決させる列。</param>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> GetResolvedTextAsync(
        string apiKey,
        long siteId,
        int offset,
        int pageSize,
        IReadOnlyDictionary<string, string>? filters,
        string idColumn,
        IReadOnlyCollection<string> columns,
        CancellationToken cancellationToken = default)
    {
        var view = new Dictionary<string, object?>
        {
            ["ApiDataType"] = 1, // KeyValues
            ["ApiColumnKeyDisplayType"] = 1, // 列の内部名をキーにする
            ["ApiColumnValueDisplayType"] = 2, // Text（表示名）
            ["GridColumns"] = new[] { idColumn }.Concat(columns).Distinct().ToArray(),
        };
        if (filters is { Count: > 0 })
        {
            view["ColumnFilterHash"] = filters;
        }

        var page = await PostGetAsync(apiKey, siteId, offset, pageSize, view, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in page.Records.OfType<JsonObject>())
        {
            if (row[idColumn] is not { } id)
            {
                continue;
            }

            // ID は Text でも文字列で返る。数値で返っても同じ文字にする
            var key = id is JsonValue v && v.TryGetValue<string>(out var text) ? text : id.ToJsonString();
            result[key] = columns.ToDictionary(
                c => c,
                c => row[c] is { } node ? (node is JsonValue cv && cv.TryGetValue<string>(out var t) ? t : node.ToJsonString()) : string.Empty,
                StringComparer.Ordinal);
        }

        return result;
    }

    private async Task<PleasanterPage> PostGetAsync(
        string apiKey,
        long siteId,
        int offset,
        int pageSize,
        Dictionary<string, object?> view,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["ApiKey"] = apiKey,
            ["ApiVersion"] = options.ApiVersion,
            ["Offset"] = offset,
            ["PageSize"] = pageSize,
        };
        if (view.Count > 0)
        {
            body["View"] = view;
        }

        // 画面から編集できる設定なので、未設定のまま使われうる。分かるメッセージで止める（再試行しても直らない）
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new PleasanterException(PleasanterErrorKind.Permanent, "Pleasanter の URL が設定されていません。管理画面の設定で指定してください。");
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
