using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>フィードが Pleasanter から最後に取りに行った結果（管理画面の表示用）。</summary>
[Migration(8, "フィードの最後の取得結果")]
public sealed class M0008_FeedLastFetch : Migration
{
    public override void Up()
    {
        Alter.Table("Feeds")
            .AddColumn("LastFetchAt").AsDateTime2().Nullable()
            .AddColumn("LastFetchStatus").AsString(32).Nullable()
            .AddColumn("LastFetchMessage").AsString(500).Nullable()
            .AddColumn("LastFetchEventCount").AsInt32().Nullable();

        // M0001 と同じ理由（MySQL は NVARCHAR が utf8mb3 になる）。足した列も utf8mb4 へ揃える
        IfDatabase("MySql").Execute.Sql(
            "ALTER TABLE `Feeds` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
    }

    public override void Down()
    {
        Delete.Column("LastFetchEventCount").FromTable("Feeds");
        Delete.Column("LastFetchMessage").FromTable("Feeds");
        Delete.Column("LastFetchStatus").FromTable("Feeds");
        Delete.Column("LastFetchAt").FromTable("Feeds");
    }
}
