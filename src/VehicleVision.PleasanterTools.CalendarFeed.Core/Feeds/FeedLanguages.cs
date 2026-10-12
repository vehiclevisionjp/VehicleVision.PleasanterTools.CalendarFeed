namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>フィードの言語。</summary>
/// <remarks>
/// <c>.ics</c> は購読者の言語を知らないので、**フィードごとに言語を持たせる。**
/// 本アプリが出す文言は、件名が空のときの仮の件名だけ（予定の中身はデータなので翻訳しない）。
/// 7 言語・地域は見ない・対応外は例外にせず既定（<see cref="Default"/>）へ落とす（方針書 10 章）。
/// </remarks>
public static class FeedLanguages
{
    /// <summary>既定の言語。</summary>
    public const string Default = "ja";

    private static readonly Dictionary<string, string> UntitledByLanguage = new(StringComparer.Ordinal)
    {
        ["en"] = "(Untitled)",
        ["zh"] = "(无标题)",
        ["ja"] = "(無題)",
        ["de"] = "(Ohne Titel)",
        ["ko"] = "(제목 없음)",
        ["es"] = "(Sin título)",
        ["vi"] = "(Không có tiêu đề)",
    };

    /// <summary>対応する言語のコード。</summary>
    public static IReadOnlyCollection<string> Supported => UntitledByLanguage.Keys;

    /// <summary>対応する言語か。</summary>
    public static bool IsSupported(string? language) =>
        language is not null && UntitledByLanguage.ContainsKey(language);

    /// <summary>言語のコードを正規化する。地域（<c>en-US</c>）は落とし、対応外は既定へ落とす。</summary>
    public static string Normalize(string? language)
    {
        var code = language?.Trim().Split('-', '_')[0].ToLowerInvariant();
        return IsSupported(code) ? code! : Default;
    }

    /// <summary>件名が空のときの仮の件名。</summary>
    public static string Untitled(string? language) => UntitledByLanguage[Normalize(language)];
}
