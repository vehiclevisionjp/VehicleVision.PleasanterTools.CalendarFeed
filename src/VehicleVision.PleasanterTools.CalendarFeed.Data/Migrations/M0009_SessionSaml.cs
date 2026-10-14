using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>SAML で入ったセッションが持つ、単一ログアウトに要る値。</summary>
/// <remarks>
/// IdP が付けた NameID とセッション索引。単一ログアウトの要求に載せるためだけに持つ（認可には使わない）。
/// SAML で入っていないセッションは空。
/// </remarks>
[Migration(9, "セッションの SAML 鍵")]
public sealed class M0009_SessionSaml : Migration
{
    public override void Up() =>
        Alter.Table("AdminSessions")
            .AddColumn("SamlNameId").AsString(256).Nullable()
            .AddColumn("SamlSessionIndex").AsString(256).Nullable();

    public override void Down()
    {
        Delete.Column("SamlSessionIndex").FromTable("AdminSessions");
        Delete.Column("SamlNameId").FromTable("AdminSessions");
    }
}
