using System.Globalization;
using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Localization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>
/// フィードに出す予定の期間（Issue #67）。<b>今日を基準に、過去と未来を何日までとるか。</b>
/// どちらかを <c>null</c>（無制限）にすると<b>開区間</b>になる。
/// </summary>
/// <remarks>
/// <para>
/// <b>期間と重なる予定を出す。</b>開始が期間の終わりより前で、終了が期間の始まりより後なら出す
/// （期間の前に始まって今も続く長い予定が、消えない）。終了の無い予定は、開始が期間の中にあれば出す。
/// </para>
/// <para>
/// <b>日の単位。</b>過去 <c>N</c> 日は「今日の 0 時の N 日前から」、未来 <c>M</c> 日は「今日から M 日後の日の終わりまで」。
/// 今日は、<b>API キー利用者のタイムゾーン</b>の今日（予定の時刻と同じ壁時計）。0 日なら、今日の始まり（過去）・終わり（未来）まで。
/// </para>
/// <para>
/// <b>Pleasanter への絞り込みは、取り過ぎない範囲の粗いもの</b>（日付の列だけ。実機で確かめた書式 <c>["from,to"]</c>。
/// どちらかを空にすると開区間。実機検証結果 12 章）。<b>正確な判定は取得した後で本アプリが行う</b>（<see cref="Overlaps"/>）。
/// 日付が文字の列に入っているときは、Pleasanter では絞れないので、取得した後の判定だけが効く。
/// </para>
/// </remarks>
/// <param name="PastDays">今日から過去へ何日までか。<c>null</c> なら無制限。</param>
/// <param name="FutureDays">今日から未来へ何日までか。<c>null</c> なら無制限。</param>
public sealed record FeedRange(int? PastDays, int? FutureDays)
{
    /// <summary>新しいフィードの既定の過去（365 日）。</summary>
    public const int DefaultPastDays = 365;

    /// <summary>指定できる最大の日数（100 年）。</summary>
    public const int MaxDays = 36500;

    /// <summary>新しいフィードの既定: 過去 365 日・未来は無制限。</summary>
    public static FeedRange Default { get; } = new(DefaultPastDays, null);

    /// <summary>両側とも無制限（絞り込まない）。</summary>
    public static FeedRange Unbounded { get; } = new(null, null);

    private const string FilterFormat = "yyyy/MM/dd HH:mm:ss";

    /// <summary>両側とも無制限か。</summary>
    public bool IsUnbounded => PastDays is null && FutureDays is null;

    /// <summary>値の範囲を検証する。問題があれば理由を返す。</summary>
    public string? Validate(string? language = null) =>
        PastDays is < 0 or > MaxDays || FutureDays is < 0 or > MaxDays
            ? ServerMessages.Get(ServerMessageKeys.FeedRangeInvalid, language, MaxDays)
            : null;

    /// <summary>
    /// 期間（今日が <paramref name="today"/>）。<b>始まりは含み、終わりは含まない</b>（終わりは、未来の最後の日の翌日の 0 時）。
    /// 無制限の側は <c>null</c>。
    /// </summary>
    public (DateTime? Start, DateTime? End) Window(DateTime today)
    {
        var day = today.Date;
        return (
            PastDays is { } past ? day.AddDays(-past) : null,
            FutureDays is { } future ? day.AddDays(future + 1) : null);
    }

    /// <summary>
    /// 予定が期間と重なるか。<paramref name="start"/>・<paramref name="end"/> は、予定の壁時計（API キー利用者のタイムゾーン）。
    /// <paramref name="end"/> が <c>null</c>（または開始と同じ）の予定は、開始が期間の中にあるか。
    /// </summary>
    public bool Overlaps(DateTime today, DateTime start, DateTime? end)
    {
        var (windowStart, windowEnd) = Window(today);
        if (windowEnd is { } limit && start >= limit)
        {
            return false;
        }

        if (windowStart is not { } from)
        {
            return true;
        }

        return end is { } finish && finish > start ? finish > from : start >= from;
    }

    /// <summary>
    /// Pleasanter への絞り込み（<c>View.ColumnFilterHash</c>）に、期間の粗い絞り込みを足して返す。
    /// <b>利用者が同じ列へ書いた絞り込みは、上書きしない</b>（正確な判定は取得した後で行う）。
    /// </summary>
    public IReadOnlyDictionary<string, string> ApplyTo(
        IReadOnlyDictionary<string, string> filters, FeedMapping mapping, DateTime today)
    {
        var (windowStart, windowEnd) = Window(today);
        if (windowStart is null && windowEnd is null)
        {
            return filters;
        }

        var merged = new Dictionary<string, string>(filters, StringComparer.Ordinal);
        var startIsDate = FeedColumns.IsDate(mapping.StartColumn);
        var endIsDate = mapping.EndColumn is not null && FeedColumns.IsDate(mapping.EndColumn);

        // 開始 ≦ 期間の終わり。端（翌日の 0 時ちょうど）は粗く含める。正確には取得した後で外す
        // 終了 ≧ 期間の始まり。終了の列が無いときは、既定の長さ（最長 1 日）ぶん手前から含める
        DateTime? startFrom = null;
        DateTime? endFrom = null;
        if (windowStart is { } from)
        {
            if (mapping.EndColumn is null)
            {
                startFrom = from.AddDays(-1);
            }
            else if (endIsDate)
            {
                endFrom = from;
            }
        }

        if (startIsDate && (startFrom is not null || windowEnd is not null) && !merged.ContainsKey(mapping.StartColumn))
        {
            merged[mapping.StartColumn] = Range(startFrom, windowEnd);
        }

        if (endIsDate && endFrom is not null && !merged.ContainsKey(mapping.EndColumn!))
        {
            merged[mapping.EndColumn!] = Range(endFrom, null);
        }

        return merged;
    }

    /// <summary>Pleasanter の日付の絞り込みの値（JSON の配列を文字列にしたもの）。どちらかが <c>null</c> なら開区間。</summary>
    private static string Range(DateTime? from, DateTime? to) =>
        JsonSerializer.Serialize(new[]
        {
            $"{from?.ToString(FilterFormat, CultureInfo.InvariantCulture)},{to?.ToString(FilterFormat, CultureInfo.InvariantCulture)}",
        });
}
