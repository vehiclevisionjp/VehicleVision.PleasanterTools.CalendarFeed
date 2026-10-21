using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>フィードの取得の失敗が続いたときの通知に要る値（連続の失敗の回数と、通知を出した時刻）。</summary>
[Migration(11, "フィードの連続失敗と通知の印")]
public sealed class M0011_FeedFailureNotify : Migration
{
    public override void Up() =>
        Alter.Table("Feeds")
            .AddColumn("ConsecutiveFailures").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("FailureNotifiedAt").AsDateTime2().Nullable();

    public override void Down()
    {
        Delete.Column("FailureNotifiedAt").FromTable("Feeds");
        Delete.Column("ConsecutiveFailures").FromTable("Feeds");
    }
}
