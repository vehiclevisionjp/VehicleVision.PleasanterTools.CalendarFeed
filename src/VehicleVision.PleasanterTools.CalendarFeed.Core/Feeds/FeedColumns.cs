using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>マッピングに指定できる Pleasanter の列名の判定。</summary>
/// <remarks>
/// <para>
/// 型付きの列は、標準が <c>DateA</c>〜<c>DateZ</c> の 26 本、項目拡張で <c>Date001</c>〜<c>Date100</c> が増える
/// （Questionnaire <c>実機検証結果.md</c> 1 章。項目拡張の本数は Pleasanter の設定で変わる）。
/// 接尾辞は <c>[A-Z]</c> か 3 桁の数字の 2 通りだけで、他は受け付けない。
/// </para>
/// <para>
/// **ここで見るのは名前の形だけ。** そのサイトにその列があるか、列が有効かは <c>GetSite</c> が要るので見ない。
/// </para>
/// </remarks>
public static partial class FeedColumns
{
    /// <summary>型付きの列の接尾辞。標準の 1 文字か、項目拡張の 3 桁。</summary>
    private const string Suffix = "([A-Z]|[0-9]{3})";

    // **末尾は `$` ではなく `\z`。** `$` は末尾の改行の手前にも一致し、`DateA\n` を通してしまう
    [GeneratedRegex($"^Date{Suffix}\\z")]
    private static partial Regex TypedDate();

    [GeneratedRegex($"^(Class|Num|Description|Check){Suffix}\\z")]
    private static partial Regex TypedOther();

    [GeneratedRegex($"^Num{Suffix}\\z")]
    private static partial Regex TypedNumber();

    [GeneratedRegex($"^Class{Suffix}\\z")]
    private static partial Regex TypedClass();

    private static readonly HashSet<string> FixedDates =
        new(["StartTime", "CompletionTime", "CreatedTime", "UpdatedTime"], StringComparer.Ordinal);

    /// <summary>ID や数値で返り、Pleasanter に表示名へ解決させる固定の列。</summary>
    private static readonly HashSet<string> FixedResolved =
        new(["Status", "Manager", "Owner", "Creator", "Updator"], StringComparer.Ordinal);

    private static readonly HashSet<string> FixedTexts =
        new(["Title", "Body", ItemTitle], StringComparer.Ordinal);

    /// <summary>
    /// Pleasanter がサイト設定の「タイトル」（<c>TitleColumns</c> を <c>TitleSeparator</c> で連結）で組み上げた表示用タイトル。
    /// API 応答の <c>ItemTitle</c>（<c>_reference</c> の <c>ResultModel.cs:806</c> / <c>Title.cs:71</c>）。
    /// </summary>
    public const string ItemTitle = "ItemTitle";

    /// <summary>日付として読める列か（開始・終了に指定できる列）。</summary>
    public static bool IsDate(string column) =>
        FixedDates.Contains(column) || TypedDate().IsMatch(column);

    /// <summary>文字として読める列か（件名・説明・場所に指定できる列）。日付の列も文字として読める。</summary>
    public static bool IsText(string column) =>
        FixedTexts.Contains(column) || FixedDates.Contains(column) || FixedResolved.Contains(column)
        || TypedDate().IsMatch(column) || TypedOther().IsMatch(column);

    /// <summary>数値の列か（<c>NumA</c>〜<c>NumZ</c> / <c>Num001</c>〜）。</summary>
    public static bool IsNumber(string column) => TypedNumber().IsMatch(column);

    /// <summary>文字にするとき、**書式を与える必要がある列か**（日付と数値）。</summary>
    public static bool NeedsFormat(string column) => IsDate(column) || IsNumber(column);

    /// <summary>
    /// 既定の応答では生の値（選択肢のキー・ユーザー ID・状況の数値）で返るため、
    /// **Pleasanter に表示名へ解決させる必要がある列か**（Issue #5。選択肢・リンク・ユーザー）。
    /// </summary>
    public static bool NeedsResolution(string column) =>
        FixedResolved.Contains(column) || TypedClass().IsMatch(column);
}
