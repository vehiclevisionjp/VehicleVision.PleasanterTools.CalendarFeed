namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>フィードを作るときに渡す、Pleasanter への接続の指定。</summary>
/// <param name="ApiKey">Pleasanter の API キー。**暗号化して保管し、以後は返さない。** <c>null</c> なら全フィード共通の既定を使う。</param>
/// <param name="ApiKeyUserTimeZoneId">API キーの利用者のタイムゾーン。<c>null</c> なら既定のタイムゾーン。</param>
/// <param name="Language">フィードの言語。</param>
public sealed record FeedAccess(
    string? ApiKey = null,
    string? ApiKeyUserTimeZoneId = null,
    string Language = FeedLanguages.Default);
