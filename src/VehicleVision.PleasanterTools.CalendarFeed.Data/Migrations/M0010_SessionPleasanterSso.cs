using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>Pleasanter のログインで入ったセッションが持つ、再検証に要る値。</summary>
/// <remarks>
/// Pleasanter のテナント ID・利用者 ID と、最後に確かめた時刻。再検証で「同じ人か」を見るために持つ（認可には使わない）。
/// Pleasanter で入っていないセッションは空。
/// </remarks>
[Migration(10, "セッションの Pleasanter SSO 鍵")]
public sealed class M0010_SessionPleasanterSso : Migration
{
    public override void Up() =>
        Alter.Table("AdminSessions")
            .AddColumn("PleasanterTenantId").AsInt32().Nullable()
            .AddColumn("PleasanterUserId").AsInt32().Nullable()
            .AddColumn("PleasanterVerifiedAt").AsString(32).Nullable();

    public override void Down()
    {
        Delete.Column("PleasanterVerifiedAt").FromTable("AdminSessions");
        Delete.Column("PleasanterUserId").FromTable("AdminSessions");
        Delete.Column("PleasanterTenantId").FromTable("AdminSessions");
    }
}
