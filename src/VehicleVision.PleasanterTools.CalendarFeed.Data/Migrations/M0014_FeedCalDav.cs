using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>フィードの CalDAV（カレンダーアプリからの接続）を有効にするか（Issue #85）。</summary>
/// <remarks>既存のフィードは<b>無効</b>のまま。管理画面から、フィードごとに明示的に有効にする（既定は無効）。</remarks>
[Migration(14, "フィードの CalDAV の有効化")]
public sealed class M0014_FeedCalDav : Migration
{
    public override void Up() =>
        Alter.Table("Feeds").AddColumn("CalDavEnabled").AsInt32().NotNullable().WithDefaultValue(0);

    public override void Down() => Delete.Column("CalDavEnabled").FromTable("Feeds");
}
