using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>RDBMS ごとに書き方が違う部分を閉じ込める。</summary>
/// <remarks>**方言差はここだけに置く。** 呼び出し側へ散らさない。</remarks>
public static partial class SqlDialect
{
    /// <summary>SQL の中の識別子を、その RDBMS の引用符へ書き換える。</summary>
    /// <remarks>
    /// <para>
    /// SQL を SQL のまま書くためのもの。<c>Format(provider, "SELECT [Id] FROM [Feeds] WHERE [SiteId] = @SiteId")</c>。
    /// **角括弧で囲んだ所だけが書き換わる。** 素の識別子はそのまま残るので、囲み忘れが目で見て分かる。
    /// SQL Server は角括弧がそのまま引用符なので、書き換えは起きない。
    /// </para>
    /// <para>
    /// ⚠️ **文字列リテラルを含む SQL には使わないこと。** リテラルの中の <c>[</c> まで書き換えてしまう。
    /// 値はパラメータで渡すのが決まり。
    /// </para>
    /// </remarks>
    public static string Format(DatabaseProvider provider, string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        return provider is DatabaseProvider.SqlServer
            ? sql
            : IdentifierPattern().Replace(sql, match => Quote(provider, match.Groups[1].Value));
    }

    /// <summary>
    /// <c>ORDER BY</c> を含む SELECT の末尾へ、件数の上限を付ける（<paramref name="limit"/> は数値なので文字列連結で安全）。
    /// SQL Server は <c>OFFSET … FETCH</c>（<c>ORDER BY</c> が要る）、他は <c>LIMIT</c>。
    /// </summary>
    public static string Limit(DatabaseProvider provider, string sql, int limit)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return provider is DatabaseProvider.SqlServer
            ? $"{sql} OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY"
            : $"{sql} LIMIT {limit}";
    }

    /// <summary>識別子を引用する。</summary>
    public static string Quote(DatabaseProvider provider, string identifier) => provider switch
    {
        DatabaseProvider.SqlServer => $"[{identifier}]",
        DatabaseProvider.PostgreSql or DatabaseProvider.Sqlite => $"\"{identifier}\"",
        DatabaseProvider.MySql => $"`{identifier}`",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>角括弧で囲まれた識別子。識別子として妥当な形だけを拾う。</summary>
    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
