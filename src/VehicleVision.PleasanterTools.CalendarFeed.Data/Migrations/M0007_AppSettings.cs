using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>アプリケーション設定（管理画面から編集する設定）。Questionnaire の <c>AppSettings</c> 表に則る。</summary>
/// <remarks>
/// <para>
/// 優先順位は <b>外部設定（環境変数・Parameters。画面からは変えられない）＞ この表 ＞ 既定値</b>。
/// <b>秘密（<c>IsSecret</c>）は暗号文</b>で置き、画面へは返さない。
/// </para>
/// <para>起動に要る値（DB 接続・秘密の鍵など）はここへ置かない。</para>
/// </remarks>
[Migration(7, "アプリケーション設定")]
public sealed class M0007_AppSettings : Migration
{
    public override void Up()
    {
        Create.Table("AppSettings")
            .WithColumn("SettingKey").AsString(256).NotNullable().PrimaryKey()
            .WithColumn("Value").AsString(int.MaxValue).NotNullable()
            .WithColumn("IsSecret").AsInt32().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedBy").AsString(36).Nullable();

        // M0001 と同じ理由（MySQL は NVARCHAR が utf8mb3 になる）。表ごと utf8mb4 へ直す
        IfDatabase("MySql").Execute.Sql(
            "ALTER TABLE `AppSettings` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
    }

    public override void Down() => Delete.Table("AppSettings");
}
