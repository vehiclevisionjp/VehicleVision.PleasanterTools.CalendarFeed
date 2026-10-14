using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>管理操作の記録（監査ログ）。</summary>
/// <remarks>M0001・M0003 と同じく 4 種の RDBMS で通じる型だけを使う（日時は <see cref="DbTime"/> の固定幅の UTC の文字列）。</remarks>
[Migration(4, "監査ログ")]
public sealed class M0004_AuditLog : Migration
{
    public override void Up()
    {
        Create.Table("AuditLogs")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("OccurredAt").AsString(32).NotNullable()
            .WithColumn("AdminUserId").AsString(36).Nullable()
            .WithColumn("LoginId").AsString(100).Nullable()
            .WithColumn("Action").AsString(64).NotNullable()
            .WithColumn("Succeeded").AsInt32().NotNullable()
            .WithColumn("TargetType").AsString(32).Nullable()
            .WithColumn("TargetId").AsString(64).Nullable()
            .WithColumn("Detail").AsString(500).Nullable()
            .WithColumn("IpAddress").AsString(45).Nullable();
        Create.Index("IX_AuditLogs_OccurredAt").OnTable("AuditLogs").OnColumn("OccurredAt").Descending();

        // M0001 と同じ理由（MySQL は NVARCHAR が utf8mb3 になる）。表ごと utf8mb4 へ直す
        IfDatabase("MySql").Execute.Sql(
            "ALTER TABLE `AuditLogs` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
    }

    public override void Down() => Delete.Table("AuditLogs");
}
