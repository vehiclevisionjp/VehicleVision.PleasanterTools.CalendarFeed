using System.Globalization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>Pleasanter のレコードを iCal の予定へ写す列の対応。</summary>
/// <remarks>
/// <para>
/// 列名は Pleasanter の列の内部名（<c>Title</c> / <c>StartTime</c> / <c>ClassA</c> / <c>DateA</c> など）。
/// 型付きの列（<c>ClassX</c> / <c>DateX</c> …）はレコードの <c>ClassHash</c> / <c>DateHash</c> …から読む。
/// </para>
/// <para>
/// **日付と数値の列を文字にするときは、.NET のカスタム書式文字列を必ず与える**（<see cref="Formats"/> か、
/// テンプレート中の <c>{DateA:yyyy/MM/dd}</c>）。値は既定の応答の生の値を本アプリが整形する
/// （Pleasanter の表示用の文字は、日付だけ・ロケール依存で使えない。実機検証結果 5 章）。
/// </para>
/// </remarks>
public sealed class FeedMapping
{
    /// <summary>件名にする列。既定は Pleasanter がサイト設定のタイトルで組み上げた <c>ItemTitle</c>。<see cref="SummaryTemplate"/> があればそちらが優先。</summary>
    public string SummaryColumn { get; init; } = FeedColumns.ItemTitle;

    /// <summary>
    /// 件名を列の値の組み合わせで作るテンプレート。<c>null</c> なら <see cref="SummaryColumn"/>。
    /// 例: <c>{ClassA}：{Title}（{DateA:yyyy/MM/dd}）</c>（<see cref="SummaryTemplate"/>）。
    /// </summary>
    public string? SummaryTemplate { get; init; }

    /// <summary>開始日時にする列。</summary>
    public string StartColumn { get; init; } = "StartTime";

    /// <summary>終了日時にする列。<c>null</c> なら終了を持たない予定にする。</summary>
    public string? EndColumn { get; init; } = "CompletionTime";

    /// <summary>説明にする列。</summary>
    public string? DescriptionColumn { get; init; } = "Body";

    /// <summary>場所にする列。</summary>
    public string? LocationColumn { get; init; }

    /// <summary>
    /// 日付・数値の列を文字にするときの書式（列名 → .NET のカスタム書式文字列）。
    /// 件名・説明・場所に日付・数値の列を指定したら必須。テンプレート中の <c>{列名:書式}</c> が優先。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Formats { get; init; }

    /// <summary>終日にするかの決め方。既定は <see cref="AllDayMode.Auto"/>。</summary>
    public AllDayMode AllDay { get; init; } = AllDayMode.Auto;

    /// <summary>終了が無い（または開始以前の）時間指定の予定に補う長さ（分）。</summary>
    public int DefaultDurationMinutes { get; init; } = 60;

    /// <summary>
    /// **Pleasanter に表示名へ解決させる必要がある列**（選択肢・リンク・ユーザー・状況）。
    /// 空なら 2 回目の取得は要らない。
    /// </summary>
    public IReadOnlyList<string> ResolvedColumns()
    {
        var columns = new List<string>();
        foreach (var column in TextColumns())
        {
            if (FeedColumns.NeedsResolution(column) && !columns.Contains(column))
            {
                columns.Add(column);
            }
        }

        return columns;
    }

    /// <summary>件名・説明・場所が読む列（テンプレートの列を含む）。解析できなければテンプレートの分は含めない。</summary>
    private IEnumerable<string> TextColumns()
    {
        if (SummaryTemplate is not null)
        {
            IReadOnlyList<TextTemplate.Part>? parts = null;
            try
            {
                parts = TextTemplate.Parse(SummaryTemplate);
            }
            catch (FormatException)
            {
                // 不正なテンプレートは Validate が弾く
            }

            foreach (var part in parts ?? [])
            {
                if (part.Column is not null)
                {
                    yield return part.Column;
                }
            }
        }
        else
        {
            yield return SummaryColumn;
        }

        if (DescriptionColumn is not null)
        {
            yield return DescriptionColumn;
        }

        if (LocationColumn is not null)
        {
            yield return LocationColumn;
        }
    }

    /// <summary>入力を検証する。問題があれば理由を、無ければ <c>null</c> を返す。</summary>
    public string? Validate()
    {
        if (SummaryTemplate is null && !FeedColumns.IsText(SummaryColumn))
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

        if (DefaultDurationMinutes is < 1 or > 1440)
        {
            return "DefaultDurationMinutes は 1〜1440 で指定してください。";
        }

        return ValidateFormats();
    }

    /// <summary>書式の指定を検証する（必須の有無・書式として使えるか）。</summary>
    private string? ValidateFormats()
    {
        if (Formats is { } formats)
        {
            if (formats.Count > 50)
            {
                return "Formats は 50 件までです。";
            }

            foreach (var (column, format) in formats)
            {
                if (!FeedColumns.NeedsFormat(column))
                {
                    return $"Formats に指定できるのは日付・数値の列だけです: {column}";
                }

                if (TryFormat(column, format) is { } error)
                {
                    return $"Formats[{column}]: {error}";
                }
            }
        }

        // テンプレート
        if (SummaryTemplate is not null)
        {
            IReadOnlyList<TextTemplate.Part> parts;
            try
            {
                parts = TextTemplate.Parse(SummaryTemplate);
            }
            catch (FormatException ex)
            {
                return $"SummaryTemplate: {ex.Message}";
            }

            if (parts.All(p => p.Column is null))
            {
                return "SummaryTemplate に列が 1 つもありません（{列名} で指定します）。";
            }

            foreach (var part in parts.Where(p => p.Column is not null))
            {
                if (!FeedColumns.IsText(part.Column!))
                {
                    return $"SummaryTemplate に指定できない列です: {part.Column}";
                }

                if (!FeedColumns.NeedsFormat(part.Column!))
                {
                    continue;
                }

                var format = part.Format ?? Formats?.GetValueOrDefault(part.Column!);
                if (format is null)
                {
                    return $"SummaryTemplate: 日付・数値の列には書式が要ります（{{{part.Column}:書式}} か Formats）: {part.Column}";
                }

                if (TryFormat(part.Column!, format) is { } error)
                {
                    return $"SummaryTemplate の {{{part.Column}:{format}}}: {error}";
                }
            }
        }
        else if (FeedColumns.NeedsFormat(SummaryColumn) && Formats?.ContainsKey(SummaryColumn) != true)
        {
            return $"SummaryColumn に日付・数値の列を指定するときは、Formats に書式が要ります: {SummaryColumn}";
        }

        foreach (var (name, column) in new[]
        {
            (nameof(DescriptionColumn), DescriptionColumn),
            (nameof(LocationColumn), LocationColumn),
        })
        {
            if (column is not null && FeedColumns.NeedsFormat(column) && Formats?.ContainsKey(column) != true)
            {
                return $"{name} に日付・数値の列を指定するときは、Formats に書式が要ります: {column}";
            }
        }

        return null;
    }

    /// <summary>書式として使えるか、実際に整形して確かめる。</summary>
    /// <remarks>
    /// 日付と数値で使える書式が違う。**カルチャはインバリアント**（書式文字列の解釈を環境に依存させない）。
    /// </remarks>
    public static string? TryFormat(string column, string format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 100 || format.Any(char.IsControl))
        {
            return "書式は 1〜100 文字（制御文字なし）で指定してください。";
        }

        try
        {
            _ = FeedColumns.IsNumber(column)
                ? 1234567.891m.ToString(format, CultureInfo.InvariantCulture)
                : new DateTime(2026, 3, 1, 9, 5, 7, DateTimeKind.Unspecified).ToString(format, CultureInfo.InvariantCulture);
            return null;
        }
        catch (FormatException)
        {
            return $"書式「{format}」は、{(FeedColumns.IsNumber(column) ? "数値" : "日付")}の書式として使えません。";
        }
    }
}
