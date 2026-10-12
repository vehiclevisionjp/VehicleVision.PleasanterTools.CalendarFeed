using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

public sealed class EventMapperTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone(
        "JST", TimeSpan.FromHours(9), "JST", "JST");

    private static FeedDefinition Feed(FeedMapping? mapping = null) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "テスト",
        42,
        mapping ?? new FeedMapping(),
        new Dictionary<string, string>(),
        DateTimeOffset.UnixEpoch,
        false);

    private static JsonNode Record(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void 標準の列から予定を作る()
    {
        var e = EventMapper.Map(
            Record("""
                { "IssueId": 7, "Ver": 3, "Title": "打合せ", "Body": "議題",
                  "StartTime": "2026-03-01T09:00:00", "CompletionTime": "2026-03-01T10:30:00",
                  "UpdatedTime": "2026-03-01T08:00:00" }
                """),
            Feed(),
            Tokyo,
            "https://pleasanter.example.com/");

        Assert.NotNull(e);
        Assert.Equal("42-7@11111111111111111111111111111111.calendarfeed", e.Uid);
        Assert.Equal("打合せ", e.Summary);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), e.Start.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 1, 30, 0, TimeSpan.Zero), e.End!.Value.ToUniversalTime());
        Assert.Equal("https://pleasanter.example.com/items/7", e.Url);
        Assert.Equal(2, e.Sequence);
    }

    [Fact]
    public void 型付きの列はHashから読む()
    {
        var mapping = new FeedMapping
        {
            StartColumn = "DateA",
            EndColumn = "DateB",
            LocationColumn = "ClassA",
            DescriptionColumn = null,
        };
        var e = EventMapper.Map(
            Record("""
                { "ResultId": 9, "Title": "t",
                  "DateHash": { "DateA": "2026-03-01T09:00:00", "DateB": "2026-03-01T11:00:00" },
                  "ClassHash": { "ClassA": "会議室" } }
                """),
            Feed(mapping),
            Tokyo);

        Assert.NotNull(e);
        Assert.Equal("会議室", e.Location);
        Assert.Equal(TimeSpan.FromHours(2), e.End - e.Start);
    }

    [Fact]
    public void 未設定の日付は予定にしない()
    {
        Assert.Null(EventMapper.Map(
            Record("""{ "IssueId": 1, "Title": "t", "StartTime": "1899-12-30T00:00:00" }"""), Feed(), Tokyo));
        Assert.Null(EventMapper.Map(Record("""{ "IssueId": 1, "Title": "t" }"""), Feed(), Tokyo));
    }

    [Fact]
    public void 終了が未設定なら既定の長さを補う()
    {
        var e = EventMapper.Map(
            Record("""
                { "IssueId": 1, "Title": "t", "StartTime": "2026-03-01T09:00:00",
                  "CompletionTime": "1899-12-30T00:00:00" }
                """),
            Feed(new FeedMapping { DefaultDurationMinutes = 30 }),
            Tokyo);

        Assert.Equal(TimeSpan.FromMinutes(30), e!.End - e.Start);
    }

    [Fact]
    public void 終日の予定は終了の日を含めて翌日をDTENDにする()
    {
        var e = EventMapper.Map(
            Record("""
                { "IssueId": 1, "Title": "t", "StartTime": "2026-03-01T00:00:00",
                  "CompletionTime": "2026-03-02T00:00:00" }
                """),
            Feed(new FeedMapping { AllDay = true }),
            Tokyo);

        Assert.True(e!.AllDay);
        Assert.Equal(new DateTime(2026, 3, 1), e.Start.Date);
        Assert.Equal(new DateTime(2026, 3, 3), e.End!.Value.Date);
    }

    [Fact]
    public void 件名が空なら仮の件名を付ける()
    {
        var e = EventMapper.Map(
            Record("""{ "IssueId": 1, "Title": "", "StartTime": "2026-03-01T09:00:00" }"""), Feed(), Tokyo);

        Assert.Equal("(無題)", e!.Summary);
    }

    [Fact]
    public void レコードIDが無ければ予定にしない()
    {
        Assert.Null(EventMapper.Map(
            Record("""{ "Title": "t", "StartTime": "2026-03-01T09:00:00" }"""), Feed(), Tokyo));
    }
}
