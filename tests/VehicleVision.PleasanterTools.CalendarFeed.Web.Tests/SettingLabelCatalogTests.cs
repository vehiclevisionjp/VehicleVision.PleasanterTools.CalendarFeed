using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>
/// 設定の定義（<see cref="AppSettingDefinitions"/>）の鍵ごとに、画面の項目名（<c>settingLabel{鍵}</c>）が 7 言語のカタログにあること（Issue #65）。
/// </summary>
/// <remarks>
/// 設定を足したときの足し忘れ（画面に項目名の代わりに鍵が出る）と、消し忘れ（定義に無い項目名が残る）を検出する。
/// 画面のカタログは Frontend にあるので、同じリポジトリのファイルを読む（<c>ParameterFilesTests</c> と同じやり方）。
/// </remarks>
public sealed partial class SettingLabelCatalogTests
{
    private static readonly string[] Languages = ["ja", "en", "zh", "de", "ko", "es", "vi"];

    private static readonly string Messages = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src",
        "VehicleVision.PleasanterTools.CalendarFeed.Frontend", "src", "lib", "i18n", "messages"));

    [GeneratedRegex(@"^\s*settingLabel(CALENDARFEED_[A-Z0-9_]+):", RegexOptions.Multiline)]
    private static partial Regex LabelKey();

    private static HashSet<string> LabelsOf(string language) =>
        [.. LabelKey().Matches(File.ReadAllText(Path.Combine(Messages, $"{language}.ts"))).Select(m => m.Groups[1].Value)];

    [Fact]
    public void 定義の鍵ごとに_項目名が7言語すべてにある()
    {
        var missing = from language in Languages
                      let labels = LabelsOf(language)
                      from definition in AppSettingDefinitions.All
                      where !labels.Contains(definition.Key)
                      select $"{language}:settingLabel{definition.Key}";

        Assert.Empty(missing);
    }

    [Fact]
    public void 定義に無い項目名がカタログに残っていない()
    {
        var keys = AppSettingDefinitions.All.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var stale = from language in Languages
                    from label in LabelsOf(language)
                    where !keys.Contains(label)
                    select $"{language}:settingLabel{label}";

        Assert.Empty(stale);
    }

    [Fact]
    public void カタログの場所が正しい_検査が空振りしない()
    {
        // パスを間違えると、どちらのテストも「空で通る」ので、読めていることを確かめる
        Assert.All(Languages, language => Assert.NotEmpty(LabelsOf(language)));
    }
}
