namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>フィード定義の保管先。</summary>
public interface IFeedStore
{
    /// <summary>フィードを作り、購読 URL に載せるトークンを返す。**トークンはこの戻り値でしか得られない。**</summary>
    Task<(FeedDefinition Feed, string Token)> CreateAsync(
        string name,
        long siteId,
        FeedMapping mapping,
        IReadOnlyDictionary<string, string> filters,
        FeedAccess? access = null,
        CancellationToken cancellationToken = default);

    /// <summary>トークンからフィードを引く。無い・失効済みなら <c>null</c>。**API キーは含まない。**</summary>
    Task<FeedDefinition?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>失効していないフィードを新しい順に返す。**API キーは含まない。**</summary>
    Task<IReadOnlyList<FeedDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>トークンを作り直す。旧トークンはその場で使えなくなる。フィードが無ければ <c>null</c>。</summary>
    Task<string?> RotateTokenAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>フィードを失効させる。失効させたら <c>true</c>。</summary>
    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>フィードの API キーを復号して返す。未登録・失効済みなら <c>null</c>。**呼び出しのたびにサーバ内だけで使い、ログへ出さない。**</summary>
    Task<string?> GetApiKeyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>ID から引く。失効済みは <c>null</c>。</summary>
    Task<FeedDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 定義を更新する（名前・サイト・列の対応・絞り込み・言語・API キー利用者のタイムゾーン）。
    /// **トークン（購読 URL）と API キーは変えない。** フィードが無い・失効済みなら <c>false</c>。
    /// </summary>
    Task<bool> UpdateAsync(Guid id, FeedUpdate update, CancellationToken cancellationToken = default);

    /// <summary>API キー（と利用者のタイムゾーン）を差し替える。フィードが無い・失効済みなら <c>false</c>。</summary>
    Task<bool> SetApiKeyAsync(
        Guid id,
        string apiKey,
        string? apiKeyUserTimeZoneId,
        CancellationToken cancellationToken = default);
}
