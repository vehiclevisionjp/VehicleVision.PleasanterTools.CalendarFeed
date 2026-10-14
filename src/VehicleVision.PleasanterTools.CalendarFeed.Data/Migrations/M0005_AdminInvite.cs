using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>管理者の招待と、端末（セッション）の識別に使う接続元。</summary>
/// <remarks>
/// <para>
/// 招待は <c>AdminUsers</c> に印を持たせる。<b>トークンの現物は置かない</b>（SHA-256 の 16 進だけ）。
/// 招待中の管理者は <c>PasswordHash</c> が空で、パスワードを決めるまでログインできない。
/// </para>
/// <para>
/// セッションの <c>IpAddress</c> / <c>UserAgent</c> は、端末の一覧で「どれが自分の端末か」を見分けるための表示用。認可には使わない。
/// </para>
/// </remarks>
[Migration(5, "管理者の招待・セッションの接続元")]
public sealed class M0005_AdminInvite : Migration
{
    public override void Up()
    {
        Alter.Table("AdminUsers")
            .AddColumn("InviteHash").AsString(64).Nullable()
            .AddColumn("InviteExpiresAt").AsString(32).Nullable();
        Create.Index("IX_AdminUsers_InviteHash").OnTable("AdminUsers").OnColumn("InviteHash");

        Alter.Table("AdminSessions")
            .AddColumn("IpAddress").AsString(45).Nullable()
            .AddColumn("UserAgent").AsString(200).Nullable();
    }

    public override void Down()
    {
        Delete.Column("UserAgent").FromTable("AdminSessions");
        Delete.Column("IpAddress").FromTable("AdminSessions");
        Delete.Index("IX_AdminUsers_InviteHash").OnTable("AdminUsers");
        Delete.Column("InviteExpiresAt").FromTable("AdminUsers");
        Delete.Column("InviteHash").FromTable("AdminUsers");
    }
}
