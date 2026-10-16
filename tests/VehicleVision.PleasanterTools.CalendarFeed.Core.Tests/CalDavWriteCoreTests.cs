using System.Text.Json.Nodes;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.CalDav;
using VehicleVision.PleasanterTools.CalendarFeed.Calendar;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>書き込まれた iCalendar の読み取り（Issue #85）。</summary>
public sealed class ICalReaderTests
{
    private static string Event(params string[] lines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//EN\r\nBEGIN:VEVENT\r\nUID:abc-1\r\n"
        + string.Join("\r\n", lines) + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void UTCの日時と_件名_説明_場所を読む_エスケープを戻す()
    {
        var e = ICalReader.ParseEvent(Event(
            "DTSTART:20260310T090000Z", "DTEND:20260310T100000Z", @"SUMMARY:会議\, 第1回", @"DESCRIPTION:一行目\n二行目\;終", "LOCATION:会議室"));

        Assert.Equal("abc-1", e.Uid);
        Assert.Equal("会議, 第1回", e.Summary);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), e.Start);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), e.End);
        Assert.False(e.AllDay);
        Assert.False(e.Floating);
        Assert.Equal("一行目\n二行目;終", e.Description);
        Assert.Equal("会議室", e.Location);
    }

    [Fact]
    public void TZIDつきの時刻は_そのタイムゾーンからUTCへ直す()
    {
        var e = ICalReader.ParseEvent(Event("DTSTART;TZID=Asia/Tokyo:20260310T180000", "DTEND;TZID=Asia/Tokyo:20260310T190000", "SUMMARY:x"));

        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), e.Start);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), e.End);
    }

    [Fact]
    public void 知らないTZIDは受け付けない()
    {
        var ex = Assert.Throws<ICalException>(() => ICalReader.ParseEvent(Event("DTSTART;TZID=Mars/Olympus:20260310T180000", "SUMMARY:x")));

        Assert.Equal(ICalProblem.InvalidData, ex.Problem);
    }

    [Fact]
    public void 浮動時間は壁時計のまま_終日は日付()
    {
        var floating = ICalReader.ParseEvent(Event("DTSTART:20260310T090000", "SUMMARY:x"));
        var allDay = ICalReader.ParseEvent(Event("DTSTART;VALUE=DATE:20260310", "DTEND;VALUE=DATE:20260312", "SUMMARY:x"));

        Assert.True(floating.Floating);
        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0), floating.Start);
        Assert.True(allDay.AllDay);
        Assert.Equal(new DateTime(2026, 3, 10), allDay.Start);
        Assert.Equal(new DateTime(2026, 3, 12), allDay.End); // DTEND のまま（その日を含まない）
    }

    [Fact]
    public void DURATIONから終了を求める()
    {
        var e = ICalReader.ParseEvent(Event("DTSTART:20260310T090000Z", "DURATION:PT1H30M", "SUMMARY:x"));

        Assert.Equal(new DateTime(2026, 3, 10, 10, 30, 0, DateTimeKind.Utc), e.End);
    }

    [Fact]
    public void 折り返された行を戻す()
    {
        var e = ICalReader.ParseEvent(Event("DTSTART:20260310T090000Z", "SUMMARY:とても長い件", " 名の続き"));

        Assert.Equal("とても長い件名の続き", e.Summary);
    }

    [Fact]
    public void アラームとタイムゾーンの定義は読み飛ばす()
    {
        var text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTIMEZONE\r\nTZID:Asia/Tokyo\r\nBEGIN:STANDARD\r\nTZOFFSETTO:+0900\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:u\r\nDTSTART:20260310T090000Z\r\nSUMMARY:本体\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nSUMMARY:アラーム\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var e = ICalReader.ParseEvent(text);

        Assert.Equal("本体", e.Summary);
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY")]
    [InlineData("RDATE:20260401T000000Z")]
    [InlineData("EXDATE:20260401T000000Z")]
    [InlineData("RECURRENCE-ID:20260401T000000Z")]
    public void 繰り返しは受け付けない(string line)
    {
        var ex = Assert.Throws<ICalException>(() => ICalReader.ParseEvent(Event("DTSTART:20260310T090000Z", "SUMMARY:x", line)));

        Assert.Equal(ICalProblem.InvalidResource, ex.Problem);
    }

    [Fact]
    public void VEVENT以外の部品_VEVENTが複数_UIDなし_壊れたデータは受け付けない()
    {
        const string head = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n";
        const string tail = "END:VCALENDAR\r\n";

        Assert.Equal(
            ICalProblem.UnsupportedComponent,
            Assert.Throws<ICalException>(() => ICalReader.ParseEvent(head + "BEGIN:VTODO\r\nUID:t\r\nEND:VTODO\r\n" + tail)).Problem);
        const string one = "BEGIN:VEVENT\r\nUID:a\r\nDTSTART:20260310T090000Z\r\nEND:VEVENT\r\n";
        Assert.Equal(ICalProblem.InvalidResource, Assert.Throws<ICalException>(() => ICalReader.ParseEvent(head + one + one + tail)).Problem);
        Assert.Equal(
            ICalProblem.InvalidResource,
            Assert.Throws<ICalException>(() => ICalReader.ParseEvent(head + "BEGIN:VEVENT\r\nDTSTART:20260310T090000Z\r\nEND:VEVENT\r\n" + tail)).Problem);
        Assert.Equal(ICalProblem.InvalidData, Assert.Throws<ICalException>(() => ICalReader.ParseEvent("hello")).Problem);
        Assert.Equal(ICalProblem.InvalidData, Assert.Throws<ICalException>(() => ICalReader.ParseEvent(head + "BEGIN:VEVENT\r\n" + tail)).Problem);
        Assert.Throws<ICalException>(() => ICalReader.ParseEvent(new string('x', ICalReader.MaxLength + 1)));
    }

    [Fact]
    public void 開始が無い_終了の型が違う_日時が壊れているものは受け付けない()
    {
        Assert.Throws<ICalException>(() => ICalReader.ParseEvent(Event("SUMMARY:x")));
        Assert.Throws<ICalException>(() => ICalReader.ParseEvent(Event("DTSTART:20260310T090000Z", "DTEND;VALUE=DATE:20260311", "SUMMARY:x")));
        Assert.Throws<ICalException>(() => ICalReader.ParseEvent(Event("DTSTART:2026-03-10", "SUMMARY:x")));
        Assert.Throws<ICalException>(() => ICalReader.ParseEvent(Event("DTSTART:20260310T090000Z", "DURATION:1 hour", "SUMMARY:x")));
    }
}

/// <summary>予定を、列の対応の逆で Pleasanter の本文へ直す（Issue #85）。</summary>
public sealed class RecordWriterTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static ParsedEvent Timed(bool floating = false) => new(
        "u", "会議", new DateTime(2026, 3, 10, 9, 0, 0, floating ? DateTimeKind.Unspecified : DateTimeKind.Utc),
        new DateTime(2026, 3, 10, 10, 0, 0, floating ? DateTimeKind.Unspecified : DateTimeKind.Utc), false, "議題", "会議室", floating);

    [Fact]
    public void 既定の対応は_Title_Body_StartTime_CompletionTime_へ書く_壁時計はAPI利用者のタイムゾーン()
    {
        var body = RecordWriter.ToBody(Timed(), new FeedMapping(), Tokyo);

        Assert.Equal("会議", (string?)body["Title"]);
        Assert.Equal("議題", (string?)body["Body"]);
        Assert.Equal("2026-03-10T18:00:00", (string?)body["StartTime"]);
        Assert.Equal("2026-03-10T19:00:00", (string?)body["CompletionTime"]);
        Assert.Null(body["ItemTitle"]);
    }

    [Fact]
    public void 浮動時間は変換しない()
    {
        var body = RecordWriter.ToBody(Timed(floating: true), new FeedMapping(), Tokyo);

        Assert.Equal("2026-03-10T09:00:00", (string?)body["StartTime"]);
    }

    [Fact]
    public void 終日は日付の0時で_終了はその日を含む日へ戻す()
    {
        var allDay = new ParsedEvent("u", "休み", new DateTime(2026, 3, 10), new DateTime(2026, 3, 12), true, null, null, false);

        var body = RecordWriter.ToBody(allDay, new FeedMapping(), Tokyo);

        Assert.Equal("2026-03-10T00:00:00", (string?)body["StartTime"]);
        Assert.Equal("2026-03-11T00:00:00", (string?)body["CompletionTime"]);
        Assert.Equal(string.Empty, (string?)body["Body"]); // 説明が無ければ空にする（消されたものを残さない）
    }

    [Fact]
    public void 日付の型付き列と説明の型付き列は_Hashへ書く()
    {
        var mapping = new FeedMapping { SummaryColumn = "Title", StartColumn = "DateA", EndColumn = "DateB", DescriptionColumn = "DescriptionA", LocationColumn = "DescriptionB" };

        var body = RecordWriter.ToBody(Timed(), mapping, TimeZoneInfo.Utc);

        Assert.Equal("2026-03-10T09:00:00", (string?)body["DateHash"]!["DateA"]);
        Assert.Equal("2026-03-10T10:00:00", (string?)body["DateHash"]!["DateB"]);
        Assert.Equal("議題", (string?)body["DescriptionHash"]!["DescriptionA"]);
        Assert.Equal("会議室", (string?)body["DescriptionHash"]!["DescriptionB"]);
    }

    [Fact]
    public void 文字の列の日付は_読み取りと同じ書式で書く()
    {
        var mapping = new FeedMapping { StartColumn = "DescriptionC", StartFormat = "yyyy/MM/dd HH:mm", EndColumn = null };

        var body = RecordWriter.ToBody(Timed(), mapping, TimeZoneInfo.Utc);

        Assert.Equal("2026/03/10 09:00", (string?)body["DescriptionHash"]!["DescriptionC"]);
    }

    [Fact]
    public void 終了の列が無ければ終了は書かない_終了が無い予定も書かない()
    {
        var noEnd = new ParsedEvent("u", "x", new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), null, false, null, null, false);

        var body = RecordWriter.ToBody(noEnd, new FeedMapping(), TimeZoneInfo.Utc);

        Assert.Null(body["CompletionTime"]);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("class-summary")]
    [InlineData("class-description")]
    [InlineData("created-start")]
    [InlineData("text-start-without-format")]
    public void 書き戻せない対応は_理由つきで断る(string kind)
    {
        var mapping = kind switch
        {
            "template" => new FeedMapping { SummaryTemplate = "{ClassA}:{Title}" },
            "class-summary" => new FeedMapping { SummaryColumn = "ClassA" },
            "class-description" => new FeedMapping { DescriptionColumn = "ClassB" },
            "created-start" => new FeedMapping { StartColumn = "CreatedTime" },
            _ => new FeedMapping { StartColumn = "DescriptionA", StartFormat = null },
        };

        var ex = Assert.Throws<NotWritableException>(() => RecordWriter.ToBody(Timed(), mapping, TimeZoneInfo.Utc));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(ex.Message, RecordWriter.WritableProblem(mapping));
    }

    [Fact]
    public void 書ける対応は_問題なし()
    {
        Assert.Null(RecordWriter.WritableProblem(new FeedMapping()));
    }
}

/// <summary>UID の対応表と、更新の排他の貸し出し（Issue #85）。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class CalDavStoreTests : IDisposable
{
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    private async Task<CalDavStore> StoreAsync(DatabaseProvider provider)
    {
        var factory = new DbConnectionFactory(
            provider,
            provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!);
        TestDatabases.Migrate(provider, factory.ConnectionString);
        await using var connection = factory.Create();
        await connection.ExecuteAsync(SqlDialect.Format(provider, "DELETE FROM [CalDavResources]"));
        await connection.ExecuteAsync(SqlDialect.Format(provider, "DELETE FROM [CalDavLocks]"));
        return new CalDavStore(factory);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 対応を足して_名前とUIDで引け_重なる名前とUIDは断る(DatabaseProvider provider)
    {
        var store = await StoreAsync(provider);
        var feed = Guid.NewGuid();
        var resource = new CalDavResource(feed, "ABC-123.ics", "client-uid-日本語", 42);

        Assert.True(await store.TryAddAsync(resource));

        Assert.Equal(resource, await store.FindByNameAsync(feed, "ABC-123.ics"));
        Assert.Equal(resource, await store.FindByUidAsync(feed, "client-uid-日本語"));
        Assert.Null(await store.FindByNameAsync(Guid.NewGuid(), "ABC-123.ics")); // 別のフィードのものは引けない
        Assert.False(await store.TryAddAsync(resource with { Uid = "other", RecordId = 43 }));        // 名前が重なる
        Assert.False(await store.TryAddAsync(resource with { Name = "other.ics", RecordId = 43 }));  // UID が重なる
        Assert.Single(await store.ListAsync(feed));
    }

    [Theory]
    [EnabledDatabases]
    public async Task レコードごと_フィードごとに消せる(DatabaseProvider provider)
    {
        var store = await StoreAsync(provider);
        var feed = Guid.NewGuid();
        await store.TryAddAsync(new CalDavResource(feed, "a.ics", "a", 1));
        await store.TryAddAsync(new CalDavResource(feed, "b.ics", "b", 2));

        await store.DeleteByRecordAsync(feed, 1);
        Assert.Equal(["b.ics"], (await store.ListAsync(feed)).Select(r => r.Name));

        await store.DeleteFeedAsync(feed);
        Assert.Empty(await store.ListAsync(feed));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 貸し出しは_同時に1つだけ_返すか期限が切れれば取れる(DatabaseProvider provider)
    {
        var store = await StoreAsync(provider);
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var lease = TimeSpan.FromSeconds(30);

        Assert.True(await store.TryAcquireLockAsync("k", "owner-1", now, lease));
        Assert.False(await store.TryAcquireLockAsync("k", "owner-2", now.AddSeconds(5), lease));
        await store.ReleaseLockAsync("k", "owner-2");                                            // 他人のものは返せない
        Assert.False(await store.TryAcquireLockAsync("k", "owner-2", now.AddSeconds(6), lease));
        await store.ReleaseLockAsync("k", "owner-1");
        Assert.True(await store.TryAcquireLockAsync("k", "owner-2", now.AddSeconds(7), lease));  // 返されたら取れる
        Assert.True(await store.TryAcquireLockAsync("k", "owner-3", now.AddSeconds(60), lease)); // 期限が切れたら取れる
    }
}
