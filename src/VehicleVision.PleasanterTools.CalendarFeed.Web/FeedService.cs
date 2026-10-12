using Microsoft.Extensions.Caching.Memory;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Pleasanter;

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
}

/// <summary>Pleasanter から取って .ics を組み立てる。</summary>
public sealed class FeedService(
    PleasanterClient client,
    IFeedStore feeds,
    PleasanterOptions pleasanter,
    FeedOptions options,
    IMemoryCache cache,
    TimeProvider time)
{
    /// <summary>フィードの .ics を返す。キャッシュがあればそれを返す。</summary>
    public Task<string> GetCalendarAsync(FeedDefinition feed, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            feed.Id,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.CacheSeconds);
                return await BuildAsync(feed, cancellationToken).ConfigureAwait(false);
            })!;

    /// <summary>生成済みの .ics を捨てる。**API キーを差し替えたら呼ぶ**（古いキーで取った結果を返さない）。</summary>
    public void Invalidate(Guid feedId) => cache.Remove(feedId);

    private async Task<string> BuildAsync(FeedDefinition feed, CancellationToken cancellationToken)
    {
        // フィードごとの API キー。未登録なら全フィード共通の既定（任意）。どちらも無ければ止める
        var apiKey = await feeds.GetApiKeyAsync(feed.Id, cancellationToken).ConfigureAwait(false)
            ?? pleasanter.ApiKey
            ?? throw new PleasanterException(
                PleasanterErrorKind.Unauthorized, "フィードに API キーが登録されていません。");
        var apiUserZone = TimeZoneInfo.FindSystemTimeZoneById(
            feed.ApiKeyUserTimeZoneId ?? pleasanter.ApiKeyUserTimeZoneId);

        var events = new List<CalendarEvent>();
        var offset = 0;
        while (events.Count < options.MaxEvents)
        {
            var page = await client
                .GetRecordsAsync(apiKey, feed.SiteId, offset, options.PageSize, feed.Filters, cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in page.Records)
            {
                if (EventMapper.Map(record, feed, apiUserZone, pleasanter.LinkBaseUrl) is { } mapped)
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

        return ICalWriter.Write(
            feed.Name,
            events.Take(options.MaxEvents),
            time.GetUtcNow(),
            TimeSpan.FromSeconds(options.CacheSeconds));
    }
}
