using System.Text.Json;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>フィードに出す予定の期間（Issue #67）。</summary>
public sealed class FeedRangeTests
{
    // 今日は 2026-10-15 の昼
    private static readonly DateTime Today = new(2026, 10, 15, 12, 30, 0);

    [Fact]
    public void 既定は_過去365日_未来は無制限()
    {
        Assert.Equal(new FeedRange(365, null), FeedRange.Default);
        Assert.False(FeedRange.Default.IsUnbounded);
        Assert.True(FeedRange.Unbounded.IsUnbounded);
    }

    [Fact]
    public void 期間は_今日の0時から数える_未来は最後の日の翌日の0時が終わり()
    {
        var (start, end) = new FeedRange(90, 30).Window(Today);

        Assert.Equal(new DateTime(2026, 7, 17), start);   // 今日 2026-10-15 の 90 日前
        Assert.Equal(new DateTime(2026, 11, 15), end);    // 30 日後（11-14）の翌日の 0 時（含まない）
    }

    [Fact]
    public void 無制限の側は_null_開区間()
    {
        Assert.Equal((new DateTime(2026, 10, 15), null), new FeedRange(0, null).Window(Today));
        Assert.Equal((null, new DateTime(2026, 10, 16)), new FeedRange(null, 0).Window(Today));
        Assert.Equal(((DateTime?)null, (DateTime?)null), FeedRange.Unbounded.Window(Today));
    }

    [Theory]
    // 窓は 2026-07-17 〜 2026-11-15（含まない）
    [InlineData("2026-07-16 10:00", "2026-07-16 11:00", false)] // 窓の前
    [InlineData("2026-07-16 23:00", "2026-07-17 00:00", false)] // 窓の始まりちょうどに終わる（重ならない）
    [InlineData("2026-07-16 23:00", "2026-07-17 00:30", true)]  // 窓の始まりをまたぐ
    [InlineData("2026-01-01 09:00", "2027-01-01 09:00", true)]  // 窓を丸ごと含む長い予定
    [InlineData("2026-10-15 09:00", "2026-10-15 10:00", true)]  // 窓の中
    [InlineData("2026-11-14 23:00", "2026-11-15 02:00", true)]  // 窓の終わりをまたいで始まる
    [InlineData("2026-11-15 00:00", "2026-11-15 01:00", false)] // 窓の終わりちょうどに始まる（含まない）
    [InlineData("2027-01-01 09:00", "2027-01-01 10:00", false)] // 窓の後
    public void 期間と重なる予定だけを出す(string start, string end, bool expected)
    {
        Assert.Equal(expected, new FeedRange(90, 30).Overlaps(Today, DateTime.Parse(start), DateTime.Parse(end)));
    }

    [Theory]
    [InlineData("2026-07-17 00:00", true)]
    [InlineData("2026-07-16 23:59", false)]
    [InlineData("2026-11-14 23:59", true)]
    [InlineData("2026-11-15 00:00", false)]
    public void 終了の無い予定は_開始が期間の中にあるか(string start, bool expected)
    {
        var range = new FeedRange(90, 30);

        Assert.Equal(expected, range.Overlaps(Today, DateTime.Parse(start), null));
        Assert.Equal(expected, range.Overlaps(Today, DateTime.Parse(start), DateTime.Parse(start))); // 開始と同じ終了も、点として扱う
    }

    [Fact]
    public void 開区間は_その側で判定しない()
    {
        var pastOnly = new FeedRange(30, null);
        Assert.True(pastOnly.Overlaps(Today, new DateTime(2099, 1, 1), new DateTime(2099, 1, 2)));
        Assert.False(pastOnly.Overlaps(Today, new DateTime(2020, 1, 1), new DateTime(2020, 1, 2)));

        var futureOnly = new FeedRange(null, 30);
        Assert.True(futureOnly.Overlaps(Today, new DateTime(1999, 1, 1), new DateTime(1999, 1, 2)));
        Assert.False(futureOnly.Overlaps(Today, new DateTime(2099, 1, 1), new DateTime(2099, 1, 2)));

        Assert.True(FeedRange.Unbounded.Overlaps(Today, new DateTime(1900, 1, 1), null));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(36500, 36500, null)]
    [InlineData(null, null, null)]
    [InlineData(-1, 0, "PastDays")]
    [InlineData(0, 36501, "PastDays")]
    public void 値の範囲を検証する(int? past, int? future, string? expectedContains)
    {
        var error = new FeedRange(past, future).Validate("en");

        if (expectedContains is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(expectedContains, error);
            Assert.Contains("36500", error);
        }
    }

    // ---- Pleasanter への粗い絞り込み ----

    private static string[] Ranges(IReadOnlyDictionary<string, string> filters, string column) =>
        JsonSerializer.Deserialize<string[]>(filters[column])!;

    [Fact]
    public void 日付の列には_開始は終わりまで_終了は始まりから_を足す()
    {
        var mapping = new FeedMapping { StartColumn = "StartTime", EndColumn = "CompletionTime" };

        var filters = new FeedRange(90, 30).ApplyTo(new Dictionary<string, string>(), mapping, Today);

        Assert.Equal([",2026/11/15 00:00:00"], Ranges(filters, "StartTime"));
        Assert.Equal(["2026/07/17 00:00:00,"], Ranges(filters, "CompletionTime"));
    }

    [Fact]
    public void 無制限の側は_空にして開区間にする()
    {
        var mapping = new FeedMapping { StartColumn = "StartTime", EndColumn = "CompletionTime" };

        var pastOnly = new FeedRange(90, null).ApplyTo(new Dictionary<string, string>(), mapping, Today);
        Assert.False(pastOnly.ContainsKey("StartTime"));
        Assert.Equal(["2026/07/17 00:00:00,"], Ranges(pastOnly, "CompletionTime"));

        var futureOnly = new FeedRange(null, 30).ApplyTo(new Dictionary<string, string>(), mapping, Today);
        Assert.Equal([",2026/11/15 00:00:00"], Ranges(futureOnly, "StartTime"));
        Assert.False(futureOnly.ContainsKey("CompletionTime"));

        Assert.Empty(FeedRange.Unbounded.ApplyTo(new Dictionary<string, string>(), mapping, Today));
    }

    [Fact]
    public void 終了の列が無いときは_開始の列へ両側を足す_既定の長さぶん手前から()
    {
        var mapping = new FeedMapping { StartColumn = "DateA", EndColumn = null };

        var filters = new FeedRange(90, 30).ApplyTo(new Dictionary<string, string>(), mapping, Today);

        Assert.Equal(["2026/07/16 00:00:00,2026/11/15 00:00:00"], Ranges(filters, "DateA"));
    }

    [Fact]
    public void 日付が文字の列は_Pleasanterでは絞れない_絞り込みを足さない()
    {
        var mapping = new FeedMapping { StartColumn = "ClassA", StartFormat = "yyyyMMdd", EndColumn = null };

        Assert.Empty(new FeedRange(90, 30).ApplyTo(new Dictionary<string, string>(), mapping, Today));
    }

    [Fact]
    public void 利用者が同じ列へ書いた絞り込みは_上書きしない_元の絞り込みも残る()
    {
        var mapping = new FeedMapping { StartColumn = "StartTime", EndColumn = "CompletionTime" };
        var own = new Dictionary<string, string> { ["StartTime"] = "[\"2026/01/01,\"]", ["ClassA"] = "[\"1\"]" };

        var filters = new FeedRange(90, 30).ApplyTo(own, mapping, Today);

        Assert.Equal("[\"2026/01/01,\"]", filters["StartTime"]);
        Assert.Equal("[\"1\"]", filters["ClassA"]);
        Assert.Equal(["2026/07/17 00:00:00,"], Ranges(filters, "CompletionTime"));
        Assert.Equal(2, own.Count); // 渡した辞書は変えない
    }
}
