namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>購読フィード 1 本の定義。</summary>
/// <param name="Id">フィードの ID。</param>
/// <param name="Name">管理上の名前。カレンダー名（X-WR-CALNAME）にも使う。</param>
/// <param name="SiteId">予定の元になる Pleasanter のサイト ID。</param>
/// <param name="Mapping">列の対応。</param>
/// <param name="Filters">Pleasanter の <c>View.ColumnFilterHash</c> へ渡す絞り込み。</param>
/// <param name="CreatedAt">作成日時（UTC）。</param>
/// <param name="IsRevoked">失効済みか。失効したフィードは 404 を返す。</param>
/// <param name="Language">フィードの言語（件名が空のときの仮の件名に使う）。</param>
/// <param name="ApiKeyUserTimeZoneId">API キーの利用者のタイムゾーン。<c>null</c> なら既定のタイムゾーン。</param>
/// <param name="LastFetch">Pleasanter から最後に取りに行った結果。<c>null</c> ならまだ取りに行っていない。</param>
/// <param name="Range">出す予定の期間（今日からの日数。無制限の側は <c>null</c>）。<c>null</c> なら既定（過去 365 日・未来は無制限）。</param>
/// <param name="ApiKeyHint">登録した API キーの末尾（見分けるため）。<c>null</c> なら API キーは未登録。**値そのものは持たない。**</param>
public sealed record FeedDefinition(
    Guid Id,
    string Name,
    long SiteId,
    FeedMapping Mapping,
    IReadOnlyDictionary<string, string> Filters,
    DateTimeOffset CreatedAt,
    bool IsRevoked,
    string Language = FeedLanguages.Default,
    string? ApiKeyUserTimeZoneId = null,
    string? ApiKeyHint = null,
    FeedFetchResult? LastFetch = null,
    FeedRange? Range = null)
{
    /// <summary>出す予定の期間。未指定なら既定。</summary>
    public FeedRange EffectiveRange => Range ?? FeedRange.Default;

    /// <summary>フィードごとの API キーが登録されているか。</summary>
    public bool HasApiKey => ApiKeyHint is not null;
}
