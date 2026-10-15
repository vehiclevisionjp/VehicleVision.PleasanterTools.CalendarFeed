using Microsoft.Extensions.Caching.Memory;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web;

/// <summary>フィードの表示に関する設定。</summary>
public sealed class FeedOptions
{
    /// <summary>生成した .ics を使い回す秒数。**Pleasanter の API 回数制限を守るため 0 にしない。**</summary>
    public int CacheSeconds { get; init; } = 300;

    /// <summary>1 フィードで出す予定の上限。超えた分は出さない（応答の大きさと API 呼び出しの上限）。</summary>
    public int MaxEvents { get; init; } = 5000;

    /// <summary>Pleasanter へ求める 1 ページの件数。サーバ側の上限で切られる。</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>取得の失敗が続いたら、フィードを編集できる管理者へメールで知らせるか。</summary>
    public bool NotifyFailure { get; init; }

    /// <summary>何回続けて失敗したら知らせるか。一時的な 1 回の失敗では知らせない。</summary>
    public int NotifyThreshold { get; init; } = 3;

    /// <summary>失敗が続くときに、同じフィードを再び知らせる間隔。</summary>
    public TimeSpan NotifyRepeat { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>Pleasanter から取って .ics を組み立てる。</summary>
public sealed class FeedService(
    PleasanterClient client,
    IFeedStore feeds,
    RuntimeSettings settings,
    IMemoryCache cache,
    FeedFailureNotifier notifier,
    TimeProvider time)
{
    private PleasanterOptions pleasanter => settings.Current.Pleasanter;

    private FeedOptions options => settings.Current.Feed;

    /// <summary>フィードの .ics を返す。キャッシュがあればそれを返す。</summary>
    public Task<string> GetCalendarAsync(FeedDefinition feed, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            feed.Id,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.CacheSeconds);
                return await BuildAndRecordAsync(feed, cancellationToken).ConfigureAwait(false);
            })!;

    /// <summary>
    /// 組み立てて、**Pleasanter へ取りに行った結果を残す**（管理画面の「最後の取得結果」）。
    /// 失敗は握りつぶさず、そのまま呼び出し側へ投げる（応答の状態の決め方は呼び出し側）。
    /// </summary>
    private async Task<string> BuildAndRecordAsync(FeedDefinition feed, CancellationToken cancellationToken)
    {
        try
        {
            var (body, count) = await BuildAsync(feed, cancellationToken).ConfigureAwait(false);
            await RecordAsync(feed, new FeedFetchResult(time.GetUtcNow(), FeedFetchStatus.Success, null, count)).ConfigureAwait(false);
            return body;
        }
        catch (PleasanterException ex)
        {
            await RecordAsync(feed, new FeedFetchResult(time.GetUtcNow(), ex.Kind.ToString(), ex.Message)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            // 秘密の鍵が未設定、または保管時と違う鍵。**メッセージは固定にする**（例外の文面を画面へ出さない）
            await RecordAsync(feed, new FeedFetchResult(time.GetUtcNow(), FeedFetchStatus.KeyError, "API キーを復号できません。")).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>記録の失敗で .ics の応答を落とさない。</summary>
    private async Task RecordAsync(FeedDefinition feed, FeedFetchResult result)
    {
        try
        {
            var failures = await feeds.RecordFetchAsync(feed.Id, result, CancellationToken.None).ConfigureAwait(false);

            // 失敗が続いたら知らせ、回復したら知らせる。**通知の失敗で .ics の応答を落とさない**
            await notifier.OnRecordedAsync(feed, result with { ConsecutiveFailures = failures }).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            // 次の取得でやり直す
        }
    }

    /// <summary>レコードに対応する、解決済みの表示名。</summary>
    private static IReadOnlyDictionary<string, string>? ResolvedOf(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> resolved,
        System.Text.Json.Nodes.JsonNode record)
    {
        var id = (record["IssueId"] ?? record["ResultId"])?.ToString();
        return id is not null && resolved.TryGetValue(id, out var texts) ? texts : null;
    }

    /// <summary>生成済みの .ics を捨てる。**API キーを差し替えたら呼ぶ**（古いキーで取った結果を返さない）。</summary>
    public void Invalidate(Guid feedId) => cache.Remove(feedId);

    private async Task<(string Body, int EventCount)> BuildAsync(FeedDefinition feed, CancellationToken cancellationToken)
    {
        // フィードごとの API キー。未登録なら全フィード共通の既定（任意）。どちらも無ければ止める
        var apiKey = await feeds.GetApiKeyAsync(feed.Id, cancellationToken).ConfigureAwait(false)
            ?? pleasanter.ApiKey
            ?? throw new PleasanterException(
                PleasanterErrorKind.Unauthorized, "フィードに API キーが登録されていません。");
        var apiUserZone = TimeZoneInfo.FindSystemTimeZoneById(
            feed.ApiKeyUserTimeZoneId ?? pleasanter.ApiKeyUserTimeZoneId);

        // **表示名への解決が要る列があるときだけ、2 回目の取得をする**（選択肢・リンク・ユーザー・状況。Issue #5）。
        // 日付・数値は 1 回目の生の値を本アプリが書式で整形する
        var resolvedColumns = feed.Mapping.ResolvedColumns();

        var events = new List<CalendarEvent>();
        var offset = 0;
        while (events.Count < options.MaxEvents)
        {
            var page = await client
                .GetRecordsAsync(apiKey, feed.SiteId, offset, options.PageSize, feed.Filters, cancellationToken)
                .ConfigureAwait(false);

            // 1 回目と同じ条件（位置・件数・絞り込み）で取ると、並びと件数が揃う。レコード ID で突き合わせる
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? resolved = null;
            if (resolvedColumns.Count > 0 && page.Records.Count > 0)
            {
                var idColumn = page.Records[0]["IssueId"] is not null ? "IssueId" : "ResultId";
                resolved = await client
                    .GetResolvedTextAsync(
                        apiKey, feed.SiteId, offset, options.PageSize, feed.Filters, idColumn, resolvedColumns, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var record in page.Records)
            {
                // 解決できなかったレコード（突き合わせられない・権限で読めない）は、生の値のまま出す
                var recordResolved = resolved is null ? null : ResolvedOf(resolved, record);
                if (EventMapper.Map(record, feed, apiUserZone, pleasanter.LinkBaseUrl, recordResolved) is { } mapped)
                {
                    events.Add(mapped);
                }
            }

            offset += page.Records.Count;
            if (page.Records.Count == 0 || offset >= page.TotalCount)
            {
                break;
            }
        }

        var output = events.Take(options.MaxEvents).ToList();
        return (
            ICalWriter.Write(feed.Name, output, time.GetUtcNow(), TimeSpan.FromSeconds(options.CacheSeconds)),
            output.Count);
    }
}
