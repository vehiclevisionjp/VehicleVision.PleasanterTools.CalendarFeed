using VehicleVision.PleasanterTools.CalendarFeed.Localization;
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

    /// <summary>
    /// 開始日時にする列。日付の列（<c>StartTime</c> / <c>CompletionTime</c> / <c>DateA</c>…）は書式が要らない。
    /// **日付が文字として入っている列（<c>Class*</c> / <c>Description*</c> / <c>Title</c> / <c>Body</c>）は <see cref="StartFormat"/> が必須。**
    /// </summary>
    public string StartColumn { get; init; } = "StartTime";

    /// <summary>
    /// 開始が文字の列のときの、日付の書式（.NET のカスタム書式文字列。<c>DateTime.ParseExact</c>。例 <c>yyyy/MM/dd HH:mm</c>）。
    /// 日付の列には指定しない。
    /// </summary>
    public string? StartFormat { get; init; }

    /// <summary>終了日時にする列。<c>null</c> なら終了を持たない予定にする。文字の列なら <see cref="EndFormat"/> が必須。</summary>
    public string? EndColumn { get; init; } = "CompletionTime";

    /// <summary>終了が文字の列のときの、日付の書式（<see cref="StartFormat"/> と同じ）。</summary>
    public string? EndFormat { get; init; }

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
    public string? Validate(string? language = null)
    {
        if (SummaryTemplate is null && !FeedColumns.IsText(SummaryColumn))
        {
            return ServerMessages.Get(ServerMessageKeys.MappingSummaryColumnInvalid, language, SummaryColumn);
        }

        if (ValidateDateColumn(nameof(StartColumn), StartColumn, StartFormat, language) is { } startError)
        {
            return startError;
        }

        if (EndColumn is null)
        {
            if (EndFormat is not null)
            {
                return ServerMessages.Get(ServerMessageKeys.MappingEndFormatWithoutEnd, language);
            }
        }
        else if (ValidateDateColumn(nameof(EndColumn), EndColumn, EndFormat, language) is { } endError)
        {
            return endError;
        }

        foreach (var (name, column) in new[]
        {
            (nameof(DescriptionColumn), DescriptionColumn),
            (nameof(LocationColumn), LocationColumn),
        })
        {
            if (column is not null && !FeedColumns.IsText(column))
            {
                return ServerMessages.Get(ServerMessageKeys.MappingColumnInvalid, language, name, column);
            }
        }

        if (DefaultDurationMinutes is < 1 or > 1440)
        {
            return ServerMessages.Get(ServerMessageKeys.MappingDurationInvalid, language);
        }

        return ValidateFormats(language);
    }

    /// <summary>開始・終了の列と書式の組み合わせを検証する。</summary>
    private static string? ValidateDateColumn(string name, string column, string? format, string? language)
    {
        if (FeedColumns.IsDate(column))
        {
            return format is null
                ? null
                : ServerMessages.Get(ServerMessageKeys.MappingDateColumnNoFormat, language, name, column);
        }

        if (!FeedColumns.IsParsableText(column))
        {
            return ServerMessages.Get(ServerMessageKeys.MappingDateColumnKind, language, name, column);
        }

        if (format is null)
        {
            return ServerMessages.Get(ServerMessageKeys.MappingTextColumnNeedsFormat, language, name, name.Replace("Column", "Format", StringComparison.Ordinal), column);
        }

        return TryParseFormat(format, language) is { } error ? $"{name.Replace("Column", "Format", StringComparison.Ordinal)}: {error}" : null;
    }

    /// <summary>日付を読む書式として使えるか。**整形してから同じ書式で読み戻せること**を確かめる。</summary>
    public static string? TryParseFormat(string format, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 100 || format.Any(char.IsControl))
        {
            return ServerMessages.Get(ServerMessageKeys.MappingFormatLengthInvalid, language);
        }

        try
        {
            // 曜日だけの書式（ddd）は、読み戻すと「実行した日」の曜日と食い違うかどうかで結果が変わる（日付の無い書式は今日の日付で補われる）。
            // 曜日の違う 2 日を両方とも読み戻せることを求め、実行した曜日によって通ったり弾かれたりしないようにする
            var samples = new[]
            {
                new DateTime(2026, 3, 5, 14, 30, 45, DateTimeKind.Unspecified),
                new DateTime(2026, 3, 6, 14, 30, 45, DateTimeKind.Unspecified),
            };
            return samples.All(sample => DateTime.TryParseExact(sample.ToString(format, CultureInfo.InvariantCulture), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                ? null
                : ServerMessages.Get(ServerMessageKeys.MappingDateFormatNotRoundTrip, language, format);
        }
        catch (FormatException)
        {
            return ServerMessages.Get(ServerMessageKeys.MappingDateFormatUnusable, language, format);
        }
    }

    /// <summary>書式の指定を検証する（必須の有無・書式として使えるか）。</summary>
    private string? ValidateFormats(string? language)
    {
        if (Formats is { } formats)
        {
            if (formats.Count > 50)
            {
                return ServerMessages.Get(ServerMessageKeys.MappingFormatsTooMany, language);
            }

            foreach (var (column, format) in formats)
            {
                if (!FeedColumns.NeedsFormat(column))
                {
                    return ServerMessages.Get(ServerMessageKeys.MappingFormatsOnlyDateNumber, language, column);
                }

                if (TryFormat(column, format, language) is { } error)
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
            catch (TemplateFormatException ex)
            {
                return $"SummaryTemplate: {ServerMessages.Get(ex.MessageKey, language, ex.Args)}";
            }

            if (parts.All(p => p.Column is null))
            {
                return ServerMessages.Get(ServerMessageKeys.MappingSummaryTemplateNoColumns, language);
            }

            foreach (var part in parts.Where(p => p.Column is not null))
            {
                if (!FeedColumns.IsText(part.Column!))
                {
                    return ServerMessages.Get(ServerMessageKeys.MappingSummaryTemplateColumnInvalid, language, part.Column!);
                }

                if (!FeedColumns.NeedsFormat(part.Column!))
                {
                    continue;
                }

                var format = part.Format ?? Formats?.GetValueOrDefault(part.Column!);
                if (format is null)
                {
                    return ServerMessages.Get(ServerMessageKeys.MappingSummaryTemplateNeedsFormat, language, part.Column!);
                }

                if (TryFormat(part.Column!, format, language) is { } error)
                {
                    return ServerMessages.Get(ServerMessageKeys.MappingSummaryTemplateFormatError, language, part.Column!, format, error);
                }
            }
        }
        else if (FeedColumns.NeedsFormat(SummaryColumn) && Formats?.ContainsKey(SummaryColumn) != true)
        {
            return ServerMessages.Get(ServerMessageKeys.MappingSummaryColumnNeedsFormat, language, SummaryColumn);
        }

        foreach (var (name, column) in new[]
        {
            (nameof(DescriptionColumn), DescriptionColumn),
            (nameof(LocationColumn), LocationColumn),
        })
        {
            if (column is not null && FeedColumns.NeedsFormat(column) && Formats?.ContainsKey(column) != true)
            {
                return ServerMessages.Get(ServerMessageKeys.MappingColumnNeedsFormat, language, name, column);
            }
        }

        return null;
    }

    /// <summary>書式として使えるか、実際に整形して確かめる。</summary>
    /// <remarks>
    /// 日付と数値で使える書式が違う。**カルチャはインバリアント**（書式文字列の解釈を環境に依存させない）。
    /// </remarks>
    public static string? TryFormat(string column, string format, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 100 || format.Any(char.IsControl))
        {
            return ServerMessages.Get(ServerMessageKeys.MappingFormatLengthInvalid, language);
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
            return ServerMessages.Get(FeedColumns.IsNumber(column) ? ServerMessageKeys.MappingNumberFormatUnusable : ServerMessageKeys.MappingDateFormatUnusable, language, format);
        }
    }
}
