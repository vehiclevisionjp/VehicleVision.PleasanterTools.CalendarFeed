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
        CancellationToken cancellationToken = default);

    /// <summary>トークンからフィードを引く。無い・失効済みなら <c>null</c>。</summary>
    Task<FeedDefinition?> FindByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>失効していないフィードを新しい順に返す。</summary>
    Task<IReadOnlyList<FeedDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>トークンを作り直す。旧トークンはその場で使えなくなる。フィードが無ければ <c>null</c>。</summary>
    Task<string?> RotateTokenAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>フィードを失効させる。失効させたら <c>true</c>。</summary>
    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default);
}
