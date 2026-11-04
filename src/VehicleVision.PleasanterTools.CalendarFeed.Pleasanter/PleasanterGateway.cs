using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dnc = VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;

/// <summary>接続先 Pleasanter の設定。</summary>
/// <remarks>**API キーを設定ファイルへ直書きしないこと。** 環境変数かローカル上書きで渡す。</remarks>
public sealed class PleasanterOptions
{
    /// <summary>
    /// Pleasanter の URL。<b>未設定（空）でも起動する</b>（管理画面から設定できる）。未設定のまま取りに行くと失敗する。
    /// 末尾のスラッシュは付けない。<b>https のみ（ループバックだけ http 可）。資格情報・クエリ・フラグメントは付けない</b>
    /// （API キーを本文で送るため。DotNetClient が通信の前に検査し、満たさなければ <see cref="PleasanterErrorKind.Permanent"/> で失敗する）。
    /// </summary>
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

    /// <summary>
    /// <b>API の回数の上限に達した</b>（HTTP 429。サイトごと・1 日ごとの <c>LimitPerSite</c>。実機検証結果 13 章）。
    /// 上限は、API キーの利用者のタイムゾーンの日付が変わるまで戻らないので、<see cref="Transient"/> のように短く待っても直らない。
    /// </summary>
    RateLimited,
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

/// <summary>接続設定の出どころ。<b>呼び出すたびに読む</b>（管理画面で編集した設定が、再起動なしで効く）。</summary>
public interface IPleasanterOptionsSource
{
    /// <summary>いまの接続設定。</summary>
    PleasanterOptions Current { get; }
}

/// <summary>
/// DotNetClient の <see cref="Dnc.PleasanterApiClient"/> を、本アプリの呼び方（一覧の取得と、CalDAV の書き込みのためのレコードの取得・作成・更新・削除。失敗の分類）で包む。
/// </summary>
/// <remarks>
/// <para>
/// **再試行しない。** 失敗の扱いは呼び出し側が決める。失敗は <see cref="PleasanterException"/>（<see cref="PleasanterErrorKind"/>）で投げる。
/// 例外のメッセージは固定の文言だけで、<b>API キー・BaseUrl・Pleasanter が返した文言を含めない</b>
/// （購読側へは分類だけが 502 / 503 として伝わる）。
/// </para>
/// <para>
/// 通信の経路（<see cref="HttpClient"/>）は呼び出し側が作る。<b>リダイレクトを辿らない
/// <see cref="Dnc.PleasanterHttp.CreateHandler"/> を使うこと</b>（API キーは本文に載り、307・308 は本文を転送先へ再送するため。
/// DI では <c>ConfigurePrimaryHttpMessageHandler(PleasanterHttp.CreateHandler)</c>）。
/// <c>BaseUrl</c> の検査（https のみ・ループバックだけ http・資格情報／クエリ／フラグメント拒否）は、DotNetClient が通信の前に呼び出しごとに行う。
/// </para>
/// <para>
/// <b>API キーは呼び出しごとに渡される</b>（フィードごと・利用者ごとに違う）ので、<see cref="Dnc.PleasanterApiClient"/> は呼び出しごとに作る
/// （軽い。<see cref="HttpClient"/> は使い回す）。型付きのメソッド（<c>client.Items.*</c>）は使わず、
/// <see cref="Dnc.PleasanterApiClient.PostJsonAsync"/> だけを使う。型付きは最初の呼び出しの前に版の問い合わせ（<c>GetLicenseInfo</c>）を行い、
/// 本アプリが数えない API の呼び出しが増える（Pleasanter 1.5.6.0 からはレート制限の対象）うえ、本アプリは応答を型へ読まず <c>JsonNode</c> のまま使うため。
/// <c>DetectServerVersion = false</c> も指定している。
/// </para>
/// <para>
/// <b>DotNetClient 0.2.0 に無いものを、ここで補っている。</b> 該当の変更を含む版へ上げたら、不要になった分を消す。
/// </para>
/// <list type="bullet">
/// <item><description>HTTP が 200 でも、本文の <c>StatusCode</c> が 200 以外なら失敗として分類する
/// （DotNetClient #95・PR #96 は develop にあるが 0.2.0 には無い。<b>DotNetClient #95 を含む版へ上げたら、この補いは消せる</b>）</description></item>
/// <item><description>HTTP 429 を <see cref="PleasanterErrorKind.RateLimited"/> として分ける（DotNetClient は <c>Transient</c>）。
/// 日次の上限は日付が変わるまで戻らず、購読側へ返す <c>Retry-After</c> の決め方が違うため。
/// 本アプリは Pleasanter の <c>Retry-After</c> ヘッダを読まない（購読側へ返す待ち時間は、分類と API キーの利用者のタイムゾーンの 0 時で決める）</description></item>
/// </list>
/// <para>ここに書いた応答の形は設計文書の推奨と参照ソースの確認に基づく。**実機の Pleasanter では未検証**（フェイクの通信とループバックのみ）。</para>
/// </remarks>
public sealed class PleasanterGateway(HttpClient httpClient, IPleasanterOptionsSource optionsSource)
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
        var body = new JsonObject
        {
            ["Offset"] = offset,
            ["PageSize"] = pageSize,
        };
        if (view.Count > 0)
        {
            body["View"] = JsonSerializer.SerializeToNode(view, SerializerOptions);
        }

        var node = await PostAsync($"api/items/{siteId}/Get", apiKey, body, cancellationToken).ConfigureAwait(false);
        if (node["Response"] is not JsonObject payload || payload["Data"] is not JsonArray data)
        {
            throw new PleasanterException(
                PleasanterErrorKind.Permanent, "Pleasanter の応答に Response.Data がありません。");
        }

        return new PleasanterPage(
            payload["TotalCount"]?.GetValue<int>() ?? data.Count,
            payload["PageSize"]?.GetValue<int>() ?? pageSize,
            data.OfType<JsonNode>().ToList());
    }

    /// <summary>
    /// レコード 1 件を<b>最新の状態で</b>取る（CalDAV の書き込みが、競合の検出に使う。Issue #85）。
    /// 無い・読めないときは <see cref="PleasanterException"/>（<c>StatusCode</c> が 404 なら無い）。
    /// </summary>
    public async Task<JsonNode> GetRecordAsync(string apiKey, long recordId, CancellationToken cancellationToken = default)
    {
        var node = await PostAsync($"api/items/{recordId}/Get", apiKey, new JsonObject(), cancellationToken).ConfigureAwait(false);
        return node["Response"]?["Data"] is JsonArray { Count: > 0 } data && data[0] is { } record
            ? record
            : throw new PleasanterException(PleasanterErrorKind.Permanent, "Pleasanter の応答にレコードがありません。", 404);
    }

    /// <summary>レコードを作る。作ったレコードの ID を返す（CalDAV の書き込み。Issue #85）。</summary>
    /// <param name="fields">レコードの項目（<c>Title</c> など。<c>ClassHash</c> 等の入れ子も可）。</param>
    public async Task<long> CreateRecordAsync(string apiKey, long siteId, JsonObject fields, CancellationToken cancellationToken = default)
    {
        var node = await PostAsync($"api/items/{siteId}/Create", apiKey, fields, cancellationToken).ConfigureAwait(false);
        return node["Id"]?.GetValue<long>()
            ?? throw new PleasanterException(PleasanterErrorKind.Permanent, "Pleasanter の応答に作成したレコードの ID がありません。");
    }

    /// <summary>レコードを更新する。<paramref name="fields"/> に書いた項目だけが変わる（CalDAV の書き込み。Issue #85）。</summary>
    public async Task UpdateRecordAsync(string apiKey, long recordId, JsonObject fields, CancellationToken cancellationToken = default) =>
        await PostAsync($"api/items/{recordId}/Update", apiKey, fields, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// レコードを削除する（CalDAV の書き込み。Issue #85）。<b>Pleasanter のごみ箱へ移る</b>（実機検証結果 14 章）ので、Pleasanter の画面から戻せる。
    /// </summary>
    public async Task DeleteRecordAsync(string apiKey, long recordId, CancellationToken cancellationToken = default) =>
        await PostAsync($"api/items/{recordId}/Delete", apiKey, new JsonObject(), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 標準 API へ POST し、状態を見て応答の JSON を返す。<b>API キーは本文にしか載せず（DotNetClient が <c>ApiKey</c>・<c>ApiVersion</c> を足す）、例外・ログへ出さない。</b>
    /// HTTP が 200 でも、本文の <c>StatusCode</c> が 200 でなければ失敗として扱う。
    /// </summary>
    private async Task<JsonObject> PostAsync(string path, string apiKey, JsonObject body, CancellationToken cancellationToken)
    {
        var current = options;

        // 画面から編集できる設定なので、未設定のまま使われうる。分かるメッセージで止める（再試行しても直らない）
        if (string.IsNullOrWhiteSpace(current.BaseUrl))
        {
            throw new PleasanterException(PleasanterErrorKind.Permanent, "Pleasanter の URL が設定されていません。管理画面の設定で指定してください。");
        }

        var dncOptions = new Dnc.PleasanterOptions
        {
            BaseUrl = current.BaseUrl.TrimEnd('/'),
            ApiKey = apiKey,
            ApiVersion = current.ApiVersion,
            Timeout = TimeSpan.FromSeconds(current.TimeoutSeconds),
            DetectServerVersion = false,
        };

        Dnc.PleasanterResponse response;
        try
        {
            // 呼び出し側の取り消しは伝わる。それ以外の通信の失敗（接続・タイムアウト）は、DotNetClient が応答なし（StatusCode なし）で返す
            response = await new Dnc.PleasanterApiClient(httpClient, dncOptions)
                .PostJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // BaseUrl が使えない形（http の非ループバック・資格情報／クエリ／フラグメント付きなど）。
            // DotNetClient の例外の文言は URL を含まない理由だけだが、画面へ出す文言はここで固定にする
            throw new PleasanterException(
                PleasanterErrorKind.Permanent,
                "Pleasanter の URL が使えません。https（ループバックだけ http 可）で、資格情報・クエリ・フラグメントを含めずに、管理画面の設定で指定してください。");
        }

        if (response.StatusCode is not { } status)
        {
            // 接続できない・タイムアウト。待てば直る見込みがある（従来と同じ分類）。例外の内容（URL を含み得る）は出さない
            throw new PleasanterException(PleasanterErrorKind.Transient, "Pleasanter へ接続できませんでした。");
        }

        if (status == (int)HttpStatusCode.TooManyRequests)
        {
            throw new PleasanterException(PleasanterErrorKind.RateLimited, $"Pleasanter が HTTP {status} を返しました。", status);
        }

        if (!response.IsSuccess)
        {
            throw new PleasanterException(
                response.ErrorKind switch
                {
                    Dnc.PleasanterErrorKind.Unauthorized => PleasanterErrorKind.Unauthorized,
                    Dnc.PleasanterErrorKind.Transient or Dnc.PleasanterErrorKind.Unknown => PleasanterErrorKind.Transient,
                    _ => PleasanterErrorKind.Permanent,
                },
                $"Pleasanter が HTTP {status} を返しました。",
                status);
        }

        if (response.Body is not JsonObject node)
        {
            // JSON でない（HTML・空など）。経路の問題とみなし、待てば直る見込みとして扱う（従来と同じ）
            throw new PleasanterException(PleasanterErrorKind.Transient, "Pleasanter の応答が JSON ではありません。", status);
        }

        // 応答本文の StatusCode も見る（HTTP が 200 でも本文でエラーを返す場合に備える）。
        // DotNetClient #95 を含む版へ上げたら消せる（DotNetClient 0.2.0 は HTTP のステータスだけで分類する）
        if (node["StatusCode"] is JsonValue statusNode && statusNode.TryGetValue<int>(out var bodyStatus) && bodyStatus != 200)
        {
            throw new PleasanterException(
                Classify((HttpStatusCode)bodyStatus),
                $"Pleasanter が StatusCode {bodyStatus} を返しました。",
                bodyStatus);
        }

        return node;
    }

    private static PleasanterErrorKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,
        HttpStatusCode.TooManyRequests => PleasanterErrorKind.RateLimited,
        HttpStatusCode.RequestTimeout => PleasanterErrorKind.Transient,
        _ => (int)statusCode >= 500 ? PleasanterErrorKind.Transient : PleasanterErrorKind.Permanent,
    };
}
