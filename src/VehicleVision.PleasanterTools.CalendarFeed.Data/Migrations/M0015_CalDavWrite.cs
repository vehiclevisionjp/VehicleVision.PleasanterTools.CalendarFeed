using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>CalDAV の書き込みの記録（Issue #85。設計方針 5・6 章）。</summary>
/// <remarks>
/// <b>CalDavResources</b>: カレンダーアプリが付けた予定の UID と資源の名前を、Pleasanter のレコード ID と結ぶ。
/// UID が変わると別の予定と見なされて重複するので、一度返した UID と名前を残す（Pleasanter の列は使わない）。
/// <b>CalDavLocks</b>: 同じレコードへの同時の更新を直列にする短い貸し出し（複数台でも効く。期限つき）。
/// </remarks>
[Migration(15, "CalDAV の書き込み（UID の対応表・更新の排他）")]
public sealed class M0015_CalDavWrite : Migration
{
    public override void Up()
    {
        Create.Table("CalDavResources")
            .WithColumn("FeedId").AsString(36).NotNullable().PrimaryKey("PK_CalDavResources")
            .WithColumn("Name").AsString(255).NotNullable().PrimaryKey("PK_CalDavResources")
            .WithColumn("Uid").AsString(255).NotNullable()
            .WithColumn("RecordId").AsInt64().NotNullable();

        Create.Index("UX_CalDavResources_Uid").OnTable("CalDavResources")
            .OnColumn("FeedId").Ascending().OnColumn("Uid").Ascending().WithOptions().Unique();
        Create.Index("IX_CalDavResources_Record").OnTable("CalDavResources")
            .OnColumn("FeedId").Ascending().OnColumn("RecordId").Ascending();

        Create.Table("CalDavLocks")
            .WithColumn("LockKey").AsString(100).NotNullable().PrimaryKey()
            .WithColumn("Owner").AsString(36).NotNullable()
            .WithColumn("ExpiresAt").AsInt64().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("CalDavLocks");
        Delete.Table("CalDavResources");
    }
}
