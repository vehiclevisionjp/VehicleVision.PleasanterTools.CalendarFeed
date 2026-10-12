using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>日付が文字の列に入っているときの開始・終了（Issue #14）。</summary>
public sealed class TextDateTests
{
    private static CalendarEvent? Map(string json, FeedMapping mapping) =>
        EventMapper.Map(
            JsonNode.Parse(json)!,
            new FeedDefinition(
                Guid.NewGuid(), "t", 1, mapping, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, false),
            TimeZoneInfo.Utc);

    private static FeedMapping Text(string startFormat, string? endColumn = null, string? endFormat = null) => new()
    {
        StartColumn = "DescriptionA",
        StartFormat = startFormat,
        EndColumn = endColumn,
        EndFormat = endFormat,
        AllDay = AllDayMode.Never,
    };

    [Fact]
    public void 文字の列の日付を書式で読む()
    {
        var e = Map(
            """{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "2026/03/05 14:30" } }""",
            Text("yyyy/MM/dd HH:mm"));

        Assert.Equal(new DateTime(2026, 3, 5, 14, 30, 0), e!.Start.DateTime);
    }

    [Fact]
    public void 和暦ではない日本語の書式も読める()
    {
        var e = Map(
            """{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "2026年3月5日" } }""",
            Text("yyyy年M月d日"));

        Assert.Equal(new DateTime(2026, 3, 5), e!.Start.DateTime);
    }

    [Fact]
    public void 開始と終了を別の文字の列から読める()
    {
        var e = Map(
            """
            { "IssueId": 1, "Title": "t",
              "DescriptionHash": { "DescriptionA": "2026/03/05 09:00", "DescriptionB": "2026/03/05 17:30" } }
            """,
            new FeedMapping
            {
                StartColumn = "DescriptionA",
                StartFormat = "yyyy/MM/dd HH:mm",
                EndColumn = "DescriptionB",
                EndFormat = "yyyy/MM/dd HH:mm",
            });

        Assert.Equal(TimeSpan.FromMinutes(510), e!.End - e.Start);
    }

    [Fact]
    public void 前後の空白は無視する()
    {
        var e = Map(
            """{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "  2026/03/05  " } }""",
            Text("yyyy/MM/dd"));

        Assert.NotNull(e);
    }

    [Theory]
    [InlineData("")]
    [InlineData("未定")]
    [InlineData("2026-03-05")]
    [InlineData("2026/13/45")]
    public void 開始が空か読めなければ予定を出さない(string value)
    {
        var e = Map(
            $$"""{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "{{value}}" } }""",
            Text("yyyy/MM/dd"));

        Assert.Null(e);
    }

    [Fact]
    public void 終了が空なら既定の長さを補う()
    {
        var e = Map(
            """{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "2026/03/05 09:00", "DescriptionB": "" } }""",
            Text("yyyy/MM/dd HH:mm", "DescriptionB", "yyyy/MM/dd HH:mm"));

        Assert.Equal(TimeSpan.FromMinutes(60), e!.End - e.Start);
    }

    [Fact]
    public void 終了が空でないのに読めなければ予定を出さない_黙って別の日付にしない()
    {
        var e = Map(
            """{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "2026/03/05 09:00", "DescriptionB": "来週" } }""",
            Text("yyyy/MM/dd HH:mm", "DescriptionB", "yyyy/MM/dd HH:mm"));

        Assert.Null(e);
    }

    [Fact]
    public void 日付だけの文字は終日の自動判定が効く()
    {
        var e = Map(
            """{ "IssueId": 1, "Title": "t", "DescriptionHash": { "DescriptionA": "2026/03/05" } }""",
            new FeedMapping { StartColumn = "DescriptionA", StartFormat = "yyyy/MM/dd", EndColumn = null });

        Assert.True(e!.AllDay);
    }

    [Fact]
    public void 選択肢の列にも書式つきで指定できタイトルや本文も使える()
    {
        Assert.Null(new FeedMapping { StartColumn = "ClassA", StartFormat = "yyyyMMdd", EndColumn = null }.Validate());
        Assert.Null(new FeedMapping { StartColumn = "Title", StartFormat = "yyyy/MM/dd", EndColumn = null }.Validate());
        Assert.Null(new FeedMapping { StartColumn = "Description001", StartFormat = "yyyy/MM/dd", EndColumn = null }.Validate());
    }

    [Fact]
    public void 日付の列に書式を付けると弾く()
    {
        Assert.NotNull(new FeedMapping { StartColumn = "DateA", StartFormat = "yyyy/MM/dd" }.Validate());
        Assert.NotNull(new FeedMapping { EndColumn = "CompletionTime", EndFormat = "yyyy/MM/dd" }.Validate());
    }

    [Fact]
    public void 文字の列に書式が無ければ弾く()
    {
        Assert.NotNull(new FeedMapping { StartColumn = "DescriptionA" }.Validate());
        Assert.NotNull(new FeedMapping { EndColumn = "DescriptionB" }.Validate());
    }

    [Fact]
    public void 開始に使えない列を弾く()
    {
        Assert.NotNull(new FeedMapping { StartColumn = "NumA", StartFormat = "yyyy" }.Validate());
        Assert.NotNull(new FeedMapping { StartColumn = "CheckA", StartFormat = "yyyy" }.Validate());
        Assert.NotNull(new FeedMapping { StartColumn = "Status", StartFormat = "yyyy" }.Validate());
        Assert.NotNull(new FeedMapping { StartColumn = "Description;", StartFormat = "yyyy" }.Validate());
    }

    [Fact]
    public void 終了が無いのに終了の書式があれば弾く()
    {
        Assert.NotNull(new FeedMapping { EndColumn = null, EndFormat = "yyyy" }.Validate());
    }

    [Theory]
    [InlineData("%")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yyyy\nMM")]
    public void 日付の書式として使えないものを弾く(string format) =>
        Assert.NotNull(FeedMapping.TryParseFormat(format));

    [Theory]
    [InlineData("yyyy/MM/dd")]
    [InlineData("yyyy年M月d日")]
    [InlineData("yyyy/MM/dd HH:mm:ss")]
    [InlineData("yyyyMMdd")]
    [InlineData("d")]
    [InlineData("dd MMM yyyy")]
    public void 日付の書式として使えるもの(string format) => Assert.Null(FeedMapping.TryParseFormat(format));

    [Theory]
    [InlineData("ddd")]
    [InlineData("dddd")]
    public void 整形できても読み戻せない書式は弾く(string format) =>
        Assert.NotNull(FeedMapping.TryParseFormat(format));
}
