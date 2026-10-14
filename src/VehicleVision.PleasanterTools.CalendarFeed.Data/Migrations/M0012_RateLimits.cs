using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>複数台で共有するレート制限の数え（固定窓。Issue #49）。</summary>
/// <remarks>
/// 1 つの鍵（枠の名前と接続元）につき 1 行。窓（時間を窓の長さで割った整数）が変わったら、数えを 1 に戻す。
/// 古い行は、定期処理が消す。
/// </remarks>
[Migration(12, "レート制限の数え")]
public sealed class M0012_RateLimits : Migration
{
    public override void Up() =>
        Create.Table("RateLimits")
            .WithColumn("LimitKey").AsString(200).NotNullable().PrimaryKey()
            .WithColumn("Window").AsInt64().NotNullable()
            .WithColumn("HitCount").AsInt32().NotNullable();

    public override void Down() => Delete.Table("RateLimits");
}
