namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>Pleasanter のレコードを iCal の予定へ写す列の対応。</summary>
/// <remarks>
/// 列名は Pleasanter の列の内部名（<c>Title</c> / <c>StartTime</c> / <c>ClassA</c> / <c>DateA</c> など）。
/// 型付きの列（<c>ClassX</c> / <c>DateX</c> …）はレコードの <c>ClassHash</c> / <c>DateHash</c> …から読む。
/// </remarks>
public sealed class FeedMapping
{
    /// <summary>件名にする列。既定は Pleasanter がサイト設定のタイトルで組み上げた <c>ItemTitle</c>。</summary>
    public string SummaryColumn { get; init; } = FeedColumns.ItemTitle;

    /// <summary>開始日時にする列。</summary>
    public string StartColumn { get; init; } = "StartTime";

    /// <summary>終了日時にする列。<c>null</c> なら終了を持たない予定にする。</summary>
    public string? EndColumn { get; init; } = "CompletionTime";

    /// <summary>説明にする列。</summary>
    public string? DescriptionColumn { get; init; } = "Body";

    /// <summary>場所にする列。</summary>
    public string? LocationColumn { get; init; }

    /// <summary>終日にするかの決め方。既定は <see cref="AllDayMode.Auto"/>。</summary>
    public AllDayMode AllDay { get; init; } = AllDayMode.Auto;

    /// <summary>終了が無い（または開始以前の）時間指定の予定に補う長さ（分）。</summary>
    public int DefaultDurationMinutes { get; init; } = 60;

    /// <summary>入力を検証する。問題があれば理由を、無ければ <c>null</c> を返す。</summary>
    public string? Validate()
    {
        if (!FeedColumns.IsText(SummaryColumn))
        {
            return $"SummaryColumn に指定できない列です: {SummaryColumn}";
        }

        if (!FeedColumns.IsDate(StartColumn))
        {
            return $"StartColumn は日付の列（StartTime / CompletionTime / DateA〜DateZ / Date001〜Date100 など）を指定してください: {StartColumn}";
        }

        if (EndColumn is not null && !FeedColumns.IsDate(EndColumn))
        {
            return $"EndColumn は日付の列を指定してください: {EndColumn}";
        }

        foreach (var (name, column) in new[]
        {
            (nameof(DescriptionColumn), DescriptionColumn),
            (nameof(LocationColumn), LocationColumn),
        })
        {
            if (column is not null && !FeedColumns.IsText(column))
            {
                return $"{name} に指定できない列です: {column}";
            }
        }

        return DefaultDurationMinutes is < 1 or > 1440
            ? "DefaultDurationMinutes は 1〜1440 で指定してください。"
            : null;
    }
}
