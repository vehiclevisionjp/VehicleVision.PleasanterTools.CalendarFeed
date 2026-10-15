namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>フィードが Pleasanter から最後に取りに行った結果（管理画面に出す）。</summary>
/// <remarks>
/// **キャッシュに当たった要求は数えない**（Pleasanter へ取りに行ったときだけ残す）。
/// メッセージに API キー・トークンは入れない（呼び出し側が資格情報を渡さない）。
/// </remarks>
/// <param name="At">取りに行った時刻。</param>
/// <param name="Status"><see cref="FeedFetchStatus"/> のいずれか。</param>
/// <param name="Message">失敗の理由（短い文字列）。成功なら <c>null</c>。</param>
/// <param name="EventCount">成功したときの予定の件数。</param>
/// <param name="ConsecutiveFailures">いまの連続の失敗の回数（成功で 0 に戻る。失敗の通知の閾値に使う）。</param>
public sealed record FeedFetchResult(
    DateTimeOffset At, string Status, string? Message = null, int? EventCount = null, int ConsecutiveFailures = 0)
{
    /// <summary>成功したか。</summary>
    public bool Succeeded => Status == FeedFetchStatus.Success;
}

/// <summary>取得結果の種別。画面の表示と絞り込みに使うので、名前を変えると過去の記録と食い違う。</summary>
public static class FeedFetchStatus
{
    public const string Success = "Success";

    /// <summary>Pleasanter に接続できない・一時的な不調。待てば直る見込みがある。</summary>
    public const string Transient = "Transient";

    /// <summary>API キーが無い・拒否された。<b>人が直す必要がある。</b></summary>
    public const string Unauthorized = "Unauthorized";

    /// <summary>送った内容か権限の問題。再試行しても通らない。</summary>
    public const string Permanent = "Permanent";

    /// <summary>API キーを復号できない（秘密の鍵を失った・変えた）。API キーを登録し直す必要がある。</summary>
    public const string KeyError = "KeyError";
}
