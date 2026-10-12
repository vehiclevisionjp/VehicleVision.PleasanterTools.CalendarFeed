namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>購読フィード 1 本の定義。</summary>
/// <param name="Id">フィードの ID。</param>
/// <param name="Name">管理上の名前。カレンダー名（X-WR-CALNAME）にも使う。</param>
/// <param name="SiteId">予定の元になる Pleasanter のサイト ID。</param>
/// <param name="Mapping">列の対応。</param>
/// <param name="Filters">Pleasanter の <c>View.ColumnFilterHash</c> へ渡す絞り込み。</param>
/// <param name="CreatedAt">作成日時（UTC）。</param>
/// <param name="IsRevoked">失効済みか。失効したフィードは 404 を返す。</param>
public sealed record FeedDefinition(
    Guid Id,
    string Name,
    long SiteId,
    FeedMapping Mapping,
    IReadOnlyDictionary<string, string> Filters,
    DateTimeOffset CreatedAt,
    bool IsRevoked);
