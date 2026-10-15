namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>フィードを作るときに渡す、Pleasanter への接続の指定。</summary>
/// <param name="ApiKey">Pleasanter の API キー。**暗号化して保管し、以後は返さない。** <c>null</c> なら全フィード共通の既定を使う。</param>
/// <param name="ApiKeyUserTimeZoneId">API キーの利用者のタイムゾーン。<c>null</c> なら既定のタイムゾーン。</param>
/// <param name="Language">フィードの言語。</param>
/// <summary>フィードの定義の更新内容。<b>全項目を置き換える</b>（PUT）。</summary>
/// <param name="Name">管理上の名前。</param>
/// <param name="SiteId">予定の元になる Pleasanter のサイト ID。</param>
/// <param name="Mapping">列の対応。</param>
/// <param name="Filters">絞り込み。</param>
/// <param name="Language">フィードの言語。</param>
/// <param name="ApiKeyUserTimeZoneId">API キーの利用者のタイムゾーン。<c>null</c> なら既定のタイムゾーン。</param>
public sealed record FeedUpdate(
    string Name,
    long SiteId,
    FeedMapping Mapping,
    IReadOnlyDictionary<string, string> Filters,
    string Language,
    string? ApiKeyUserTimeZoneId,
    FeedRange? Range = null);

public sealed record FeedAccess(
    string? ApiKey = null,
    string? ApiKeyUserTimeZoneId = null,
    string Language = FeedLanguages.Default,
    FeedRange? Range = null);
