namespace VehicleVision.PleasanterTools.CalendarFeed.Calendar;

/// <summary>iCal の予定（VEVENT）1 件。</summary>
public sealed record CalendarEvent
{
    /// <summary>予定の ID。**レコードごとに変わらない値**にする（購読側が更新と新規を見分ける）。</summary>
    public required string Uid { get; init; }

    /// <summary>件名。</summary>
    public required string Summary { get; init; }

    /// <summary>開始。終日の予定では、日付の部分だけを使う。</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>終了。終日の予定では、日付の部分だけを使い、**その日を含まない**（RFC 5545 の DTEND）。</summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>終日の予定か。</summary>
    public bool AllDay { get; init; }

    /// <summary>説明。</summary>
    public string? Description { get; init; }

    /// <summary>場所。</summary>
    public string? Location { get; init; }

    /// <summary>元のレコードへのリンク。</summary>
    public string? Url { get; init; }

    /// <summary>最終更新（UTC へ直して出す）。</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>更新の版（SEQUENCE）。</summary>
    public int Sequence { get; init; }
}
