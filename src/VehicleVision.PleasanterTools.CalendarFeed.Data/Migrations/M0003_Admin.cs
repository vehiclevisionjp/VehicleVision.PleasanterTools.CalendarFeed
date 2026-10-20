using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>管理者・サーバ側セッション・復旧コード。</summary>
/// <remarks>
/// <para>
/// M0001 と同じく **4 種の RDBMS で通じる型だけ**を使う（ID は文字列、日時は <see cref="DbTime"/> の固定幅の ISO 文字列、
/// 真偽は 0/1）。日時は**固定幅の UTC の文字列**にして、SQL の文字列比較が時刻の前後と一致するようにしている。
/// </para>
/// <para>
/// **秘密は平文で置かない。** パスワードは PBKDF2 のハッシュ、TOTP の共有鍵は暗号文、セッション ID・復旧コードは SHA-256。
/// </para>
/// </remarks>
[Migration(3, "管理者・セッション・復旧コード")]
public sealed class M0003_Admin : Migration
{
    public override void Up()
    {
        Create.Table("AdminUsers")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("LoginId").AsString(100).NotNullable()
            // 大文字小文字を区別しない一意性（RDBMS の照合順序に頼らない）。小文字へ直した値
            .WithColumn("LoginIdKey").AsString(100).NotNullable()
            .WithColumn("PasswordHash").AsString(300).NotNullable()
            .WithColumn("Role").AsString(32).NotNullable().WithDefaultValue("Administrator")
            .WithColumn("IsDisabled").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("FailedLoginCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("LockedUntil").AsDateTime2().Nullable()
            .WithColumn("LastLoginAt").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("TotpSecretCipher").AsString(int.MaxValue).Nullable()
            .WithColumn("TotpPendingCipher").AsString(int.MaxValue).Nullable()
            .WithColumn("TotpLastTimeStep").AsInt64().Nullable()
            .WithColumn("Language").AsString(8).NotNullable().WithDefaultValue("ja");
        Create.Index("IX_AdminUsers_LoginIdKey").OnTable("AdminUsers").OnColumn("LoginIdKey").Unique();

        // **最初の管理者を 1 度だけ作るための印。** 主キーの一意性で排他する
        // （「居なければ INSERT」の 1 文は、PostgreSQL の既定の分離水準では同時に来た複数が通ってしまう。実機で確認）
        Create.Table("AdminSetup")
            .WithColumn("Id").AsInt32().NotNullable().PrimaryKey();

        Create.Table("AdminSessions")
            // セッション ID の SHA-256（16 進）。cookie の現物は保管しない
            .WithColumn("Id").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("UserId").AsString(36).NotNullable()
            .WithColumn("Kind").AsString(16).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable();
        Create.Index("IX_AdminSessions_UserId").OnTable("AdminSessions").OnColumn("UserId");

        Create.Table("AdminRecoveryCodes")
            .WithColumn("UserId").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("CodeHash").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("UsedAt").AsDateTime2().Nullable();

        // M0001 と同じ理由（MySQL は NVARCHAR が utf8mb3 になる）。表ごと utf8mb4 へ直す
        foreach (var table in new[] { "AdminUsers", "AdminSessions", "AdminRecoveryCodes" })
        {
            IfDatabase("MySql").Execute.Sql(
                $"ALTER TABLE `{table}` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
        }
    }

    public override void Down()
    {
        Delete.Table("AdminRecoveryCodes");
        Delete.Table("AdminSessions");
        Delete.Table("AdminSetup");
        Delete.Table("AdminUsers");
    }
}
