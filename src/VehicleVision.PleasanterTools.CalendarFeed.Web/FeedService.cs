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
    PleasanterOptions pleasanter,
    FeedOptions options,
    IMemoryCache cache,
    TimeProvider time)
{
    private readonly TimeZoneInfo _apiUserZone = TimeZoneInfo.FindSystemTimeZoneById(pleasanter.ApiKeyUserTimeZoneId);

    /// <summary>フィードの .ics を返す。キャッシュがあればそれを返す。</summary>
    public Task<string> GetCalendarAsync(FeedDefinition feed, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            feed.Id,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.CacheSeconds);
                return await BuildAsync(feed, cancellationToken).ConfigureAwait(false);
            })!;

    private async Task<string> BuildAsync(FeedDefinition feed, CancellationToken cancellationToken)
    {
        var events = new List<CalendarEvent>();
        var offset = 0;
        while (events.Count < options.MaxEvents)
        {
            var page = await client
                .GetRecordsAsync(feed.SiteId, offset, options.PageSize, feed.Filters, cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in page.Records)
            {
                if (EventMapper.Map(record, feed, _apiUserZone, pleasanter.LinkBaseUrl) is { } mapped)
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
