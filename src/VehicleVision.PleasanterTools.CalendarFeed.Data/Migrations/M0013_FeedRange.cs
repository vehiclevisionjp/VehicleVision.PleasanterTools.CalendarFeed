using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>フィードに出す予定の期間（今日から過去・未来へ何日まで。Issue #67）。</summary>
/// <remarks>
/// 列が <c>NULL</c> の側は<b>無制限</b>（開区間）。**既存のフィードは、新しいフィードの既定と同じ「過去 365 日・未来は無制限」にそろえる**
/// （1 年より前の予定は、購読中のカレンダーから消える。画面から、フィードごとに変えられる）。
/// </remarks>
[Migration(13, "フィードの予定の期間")]
public sealed class M0013_FeedRange : Migration
{
    public override void Up()
    {
        Alter.Table("Feeds")
            .AddColumn("RangePastDays").AsInt32().Nullable()
            .AddColumn("RangeFutureDays").AsInt32().Nullable();

        Update.Table("Feeds").Set(new { RangePastDays = 365 }).AllRows();
    }

    public override void Down()
    {
        Delete.Column("RangeFutureDays").FromTable("Feeds");
        Delete.Column("RangePastDays").FromTable("Feeds");
    }
}
