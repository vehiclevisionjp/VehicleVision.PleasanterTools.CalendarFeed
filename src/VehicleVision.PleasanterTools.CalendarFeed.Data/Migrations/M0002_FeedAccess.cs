using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>フィードごとの Pleasanter への接続（API キー・利用者のタイムゾーン）と言語。</summary>
/// <remarks>
/// <c>ApiKeyCipher</c> は <c>SecretProtector</c> の暗号文（ASCII）。**平文の API キーは DB に置かない。**
/// <c>ApiKeyHint</c> は登録した API キーの末尾（見分けるため。値そのものではない）。
/// </remarks>
[Migration(2, "フィードごとの API キーと言語")]
public sealed class M0002_FeedAccess : Migration
{
    public override void Up()
    {
        Alter.Table("Feeds")
            .AddColumn("ApiKeyCipher").AsString(int.MaxValue).Nullable()
            .AddColumn("ApiKeyHint").AsString(16).Nullable()
            .AddColumn("ApiKeyUserTimeZoneId").AsString(64).Nullable()
            .AddColumn("Language").AsString(8).NotNullable().WithDefaultValue("ja");

        // M0001 と同じ理由（MySQL は NVARCHAR が utf8mb3 になる）。足した列も utf8mb4 へ揃える
        IfDatabase("MySql").Execute.Sql(
            "ALTER TABLE `Feeds` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
    }

    public override void Down()
    {
        Delete.Column("ApiKeyCipher").FromTable("Feeds");
        Delete.Column("ApiKeyHint").FromTable("Feeds");
        Delete.Column("ApiKeyUserTimeZoneId").FromTable("Feeds");
        Delete.Column("Language").FromTable("Feeds");
    }
}
