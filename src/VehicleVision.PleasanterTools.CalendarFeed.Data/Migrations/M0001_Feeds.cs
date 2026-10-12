using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>購読フィードの表。</summary>
/// <remarks>
/// **4 種の RDBMS で通じる型だけを使う。** ID は文字列（GUID の "D" 形式）、日時は ISO 8601 の文字列、
/// 真偽は 0/1 の整数。型ごとの差を持ち込まないため。
/// </remarks>
[Migration(1, "購読フィードの表")]
public sealed class M0001_Feeds : Migration
{
    public override void Up()
    {
        Create.Table("Feeds")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("SiteId").AsInt64().NotNullable()
            .WithColumn("MappingJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("FiltersJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("TokenHash").AsString(64).NotNullable()
            .WithColumn("CreatedAt").AsString(40).NotNullable()
            .WithColumn("IsRevoked").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("IX_Feeds_TokenHash").OnTable("Feeds").OnColumn("TokenHash").Unique();

        // ⚠️ **MySQL は文字列の列が utf8mb3（3 バイト）で作られる。** FluentMigrator は AsString を NVARCHAR にし、
        // MySQL の NVARCHAR は utf8mb3 固定のため、DB の既定が utf8mb4 でも絵文字が入らない（実機で確認）。
        // 表ごと utf8mb4 へ直す。照合順序は大文字小文字を区別するものを明示する（既定は RDBMS で違う）
        IfDatabase("MySql").Execute.Sql(
            "ALTER TABLE `Feeds` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
    }

    public override void Down() => Delete.Table("Feeds");
}
