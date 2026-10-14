using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>管理者の招待と、メールの送信待ち（アウトボックス）。Questionnaire の <c>AdminInvitations</c> / <c>MailOutbox</c> に則る。</summary>
/// <remarks>
/// <para>
/// 招待は <b>トークンの SHA-256 だけ</b>を持つ。使い切り（<c>UsedAt</c>）・期限つき。使用済みの行は、使われた事実として残す。
/// </para>
/// <para>
/// 送信待ちは <b>宛先・件名・本文を 1 つにまとめて暗号化した列（<c>PayloadProtected</c>）だけ</b>を持つ。
/// 平文の列を作らないので、後から平文が入る余地も無い。送れたら行ごと消える。
/// </para>
/// </remarks>
[Migration(6, "管理者の招待・メールの送信待ち")]
public sealed class M0006_AdminInvitationsAndMailOutbox : Migration
{
    public override void Up()
    {
        Create.Table("AdminInvitations")
            .WithColumn("InvitationId").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("AdminUserId").AsString(36).NotNullable()
            // トークンの SHA-256（16 進）。現物は保管しない
            .WithColumn("TokenHash").AsString(64).NotNullable()
            .WithColumn("ExpiresAt").AsString(32).NotNullable()
            .WithColumn("UsedAt").AsString(32).Nullable()
            .WithColumn("CreatedAt").AsString(32).NotNullable()
            .WithColumn("CreatedBy").AsString(36).Nullable();
        Create.Index("IX_AdminInvitations_TokenHash").OnTable("AdminInvitations").OnColumn("TokenHash").Unique();
        Create.Index("IX_AdminInvitations_AdminUserId").OnTable("AdminInvitations").OnColumn("AdminUserId");

        Create.Table("MailOutbox")
            .WithColumn("MailId").AsString(36).NotNullable().PrimaryKey()
            // 種類（Data/MailKind）。整数で持つ
            .WithColumn("Kind").AsInt32().NotNullable()
            // ⚠️ 宛先・件名・本文を 1 つにまとめて暗号化したもの。列を分けると、暗号化の掛け忘れが列ごとに起き得る
            .WithColumn("PayloadProtected").AsString(int.MaxValue).NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("RetryCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("NextAttemptAt").AsString(32).NotNullable()
            // 理由だけを入れる。宛先も本文も入れない（画面とデッドレターに残る）
            .WithColumn("LastError").AsString(1024).Nullable()
            .WithColumn("LockedBy").AsString(64).Nullable()
            .WithColumn("LockedUntil").AsString(32).Nullable()
            .WithColumn("CreatedAt").AsString(32).NotNullable()
            .WithColumn("UpdatedAt").AsString(32).NotNullable();

        // 送信ワーカーが「送るべき行」を探す索引
        Create.Index("IX_MailOutbox_Status_NextAttemptAt")
            .OnTable("MailOutbox")
            .OnColumn("Status").Ascending()
            .OnColumn("NextAttemptAt").Ascending();

        // M0001 と同じ理由（MySQL は NVARCHAR が utf8mb3 になる）。表ごと utf8mb4 へ直す
        foreach (var table in new[] { "AdminInvitations", "MailOutbox" })
        {
            IfDatabase("MySql").Execute.Sql(
                $"ALTER TABLE `{table}` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
        }
    }

    public override void Down()
    {
        Delete.Table("MailOutbox");
        Delete.Table("AdminInvitations");
    }
}
