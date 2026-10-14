using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>端末（セッション）の識別に使う接続元。</summary>
/// <remarks>
/// セッションの <c>IpAddress</c> / <c>UserAgent</c> は、端末の一覧で「どれが自分の端末か」を見分けるための表示用。認可には使わない。
/// </remarks>
[Migration(5, "セッションの接続元")]
public sealed class M0005_SessionOrigin : Migration
{
    public override void Up()
    {
        Alter.Table("AdminSessions")
            .AddColumn("IpAddress").AsString(45).Nullable()
            .AddColumn("UserAgent").AsString(200).Nullable();
    }

    public override void Down()
    {
        Delete.Column("UserAgent").FromTable("AdminSessions");
        Delete.Column("IpAddress").FromTable("AdminSessions");
    }
}
