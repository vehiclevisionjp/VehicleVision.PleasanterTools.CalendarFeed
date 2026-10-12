namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>Pleasanter のレコードを iCal の予定へ写す列の対応。</summary>
/// <remarks>
/// 列名は Pleasanter の列の内部名（<c>Title</c> / <c>StartTime</c> / <c>ClassA</c> / <c>DateA</c> など）。
/// 型付きの列（<c>ClassX</c> / <c>DateX</c> …）はレコードの <c>ClassHash</c> / <c>DateHash</c> …から読む。
/// </remarks>
public sealed class FeedMapping
{
    /// <summary>件名にする列。</summary>
    public string SummaryColumn { get; init; } = "Title";

    /// <summary>開始日時にする列。</summary>
    public string StartColumn { get; init; } = "StartTime";

    /// <summary>終了日時にする列。<c>null</c> なら終了を持たない予定にする。</summary>
    public string? EndColumn { get; init; } = "CompletionTime";

    /// <summary>説明にする列。</summary>
    public string? DescriptionColumn { get; init; } = "Body";

    /// <summary>場所にする列。</summary>
    public string? LocationColumn { get; init; }

    /// <summary>終日の予定として出すか。</summary>
    public bool AllDay { get; init; }

    /// <summary>終了が無い（または開始以前の）時間指定の予定に補う長さ（分）。</summary>
    public int DefaultDurationMinutes { get; init; } = 60;
}
