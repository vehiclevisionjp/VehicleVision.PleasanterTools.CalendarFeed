using System.Text;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

public sealed class ICalWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    private static string Write(params CalendarEvent[] events) =>
        ICalWriter.Write("テスト", events, Now);

    [Fact]
    public void 時間指定の予定はUTCで出す()
    {
        var text = Write(new CalendarEvent
        {
            Uid = "u1",
            Summary = "会議",
            Start = new DateTimeOffset(2026, 3, 1, 9, 0, 0, Jst),
            End = new DateTimeOffset(2026, 3, 1, 10, 0, 0, Jst),
        });

        Assert.Contains("DTSTART:20260301T000000Z\r\n", text);
        Assert.Contains("DTEND:20260301T010000Z\r\n", text);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", text);
        Assert.EndsWith("END:VCALENDAR\r\n", text);
    }

    [Fact]
    public void 終日の予定は日付だけを出す()
    {
        var text = Write(new CalendarEvent
        {
            Uid = "u1",
            Summary = "休暇",
            AllDay = true,
            Start = new DateTimeOffset(2026, 3, 1, 0, 0, 0, Jst),
            End = new DateTimeOffset(2026, 3, 3, 0, 0, 0, Jst),
        });

        Assert.Contains("DTSTART;VALUE=DATE:20260301\r\n", text);
        Assert.Contains("DTEND;VALUE=DATE:20260303\r\n", text);
    }

    [Fact]
    public void 改行や区切り文字をエスケープし別のプロパティを差し込めない()
    {
        var text = Write(new CalendarEvent
        {
            Uid = "u1",
            Summary = "a,b;c\\d",
            Start = Now,
            Description = "1行目\r\n2行目\nEND:VEVENT\nSUMMARY:偽",
        });

        Assert.Contains("SUMMARY:a\\,b\\;c\\\\d\r\n", text);
        Assert.Contains("DESCRIPTION:1行目\\n2行目\\nEND:VEVENT\\nSUMMARY:偽\r\n", text);
        Assert.Single(text.Split("\r\n"), l => l == "END:VEVENT");
    }

    [Fact]
    public void 長い行は75オクテットで折り返し文字を割らない()
    {
        var text = Write(new CalendarEvent { Uid = "u1", Summary = new string('あ', 100), Start = Now });

        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
        }

        // 折り返しを戻すと元の件名になる（UTF-8 として壊れていない）
        var unfolded = text.Replace("\r\n ", string.Empty);
        Assert.Contains($"SUMMARY:{new string('あ', 100)}\r\n", unfolded);
    }

    [Fact]
    public void すべての行がCRLFで終わる()
    {
        var text = Write(new CalendarEvent { Uid = "u1", Summary = "x", Start = Now });
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
    }
}
