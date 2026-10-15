using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using System.Text;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>件名（などの文字）を、列の値を組み合わせて作るテンプレート。</summary>
/// <remarks>
/// <para>
/// 書き方: <c>{ClassA}：{Title}（{DateA:yyyy/MM/dd HH:mm}）</c>。<c>{列名}</c> か <c>{列名:書式}</c>。
/// <c>{{</c> と <c>}}</c> は波括弧そのもの。
/// </para>
/// <para>
/// **書式は .NET のカスタム書式文字列**（日付は <c>yyyy/MM/dd</c>、数値は <c>#,##0.00</c> など）。
/// 日付と数値の列は**書式が必須**（既定は持たない）。書式を要さない列（文字・選択肢など）に付けても読み捨てる。
/// </para>
/// </remarks>
public static partial class TextTemplate
{
    /// <summary>テンプレートの長さの上限。</summary>
    public const int MaxLength = 500;

    /// <summary>1 つの部品。<see cref="Column"/> が <c>null</c> なら固定の文字。</summary>
    /// <param name="Literal">固定の文字。</param>
    /// <param name="Column">列名。</param>
    /// <param name="Format">この場で指定された書式。</param>
    public sealed record Part(string? Literal, string? Column = null, string? Format = null);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,63}\z")]
    private static partial Regex ColumnName();

    /// <summary>テンプレートを部品に分ける。書式が不正なら <see cref="FormatException"/>。</summary>
    public static IReadOnlyList<Part> Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Length > MaxLength)
        {
            throw new TemplateFormatException(ServerMessageKeys.TemplateTooLong, MaxLength);
        }

        var parts = new List<Part>();
        var literal = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            switch (c)
            {
                case '{' when i + 1 < template.Length && template[i + 1] == '{':
                    literal.Append('{');
                    i++;
                    break;
                case '}' when i + 1 < template.Length && template[i + 1] == '}':
                    literal.Append('}');
                    i++;
                    break;
                case '{':
                    {
                        var close = template.IndexOf('}', i + 1);
                        if (close < 0)
                        {
                            throw new TemplateFormatException(ServerMessageKeys.TemplateBraceUnclosed);
                        }

                        if (literal.Length > 0)
                        {
                            parts.Add(new Part(literal.ToString()));
                            literal.Clear();
                        }

                        var body = template[(i + 1)..close];
                        var colon = body.IndexOf(':', StringComparison.Ordinal);
                        var column = colon < 0 ? body : body[..colon];
                        var format = colon < 0 ? null : body[(colon + 1)..];
                        if (!ColumnName().IsMatch(column))
                        {
                            throw new TemplateFormatException(ServerMessageKeys.TemplateColumnInvalid, column);
                        }

                        if (format is { Length: 0 })
                        {
                            throw new TemplateFormatException(ServerMessageKeys.TemplateFormatEmpty, column);
                        }

                        parts.Add(new Part(null, column, format));
                        i = close;
                        break;
                    }

                case '}':
                    throw new TemplateFormatException(ServerMessageKeys.TemplateBraceStray);
                default:
                    literal.Append(c);
                    break;
            }
        }

        if (literal.Length > 0)
        {
            parts.Add(new Part(literal.ToString()));
        }

        return parts;
    }

    /// <summary>部品を文字にする。<paramref name="valueOf"/> は（列名, その場の書式）から値を返す。</summary>
    public static string Render(IEnumerable<Part> parts, Func<string, string?, string> valueOf)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(valueOf);

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(part.Column is null ? part.Literal : valueOf(part.Column, part.Format));
        }

        return sb.ToString().Trim();
    }
}
