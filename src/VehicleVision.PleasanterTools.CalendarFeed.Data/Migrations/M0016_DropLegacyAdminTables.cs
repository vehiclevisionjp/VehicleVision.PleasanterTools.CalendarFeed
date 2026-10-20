using FluentMigrator;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data.Migrations;

/// <summary>
/// 管理者・セッション・監査ログ・メールのアウトボックス・招待・アプリ設定・レート制限の保管を、Core.Data の共有ストア
/// （<c>Core_</c> 接頭辞の表。Core の移行 M10001〜M10007）へ切り替えたので、CalendarFeed 自前の旧表を落とす。
/// </summary>
/// <remarks>
/// <para>
/// <b>稼働データは無い</b>ので、中身は移さない。旧表を作る M0003〜M0007・M0009・M0010・M0012 は、
/// 公開済みの番号なので書き換えない（新しい DB では、それらが先に走って旧表を作り、ここで落とす）。
/// </para>
/// <para>
/// <b>存在すれば</b>落とす（<c>IfExists</c>）。すでに無い DB でも通る。<c>Feeds</c> 表は触らない。
/// どの表にも外部キーは無いので、落とす順序に制約はない。
/// </para>
/// </remarks>
[Migration(16, "旧の管理者・監査ログ・メール・設定・レート制限の表を落とす（Core.Data の共有ストアへ移行）")]
public sealed class M0016_DropLegacyAdminTables : Migration
{
    /// <summary>落とす旧表（M0003〜M0012 が作ったもの）。</summary>
    private static readonly string[] LegacyTables =
    [
        "AdminRecoveryCodes",
        "AdminSessions",
        "AdminSetup",
        "AdminInvitations",
        "AdminUsers",
        "AuditLogs",
        "MailOutbox",
        "AppSettings",
        "RateLimits",
    ];

    public override void Up()
    {
        foreach (var table in LegacyTables)
        {
            Delete.Table(table).IfExists();
        }
    }

    /// <summary>戻さない（旧表は、もう使われない。移行は上へ進める運用で、巻き戻しは対応しない）。</summary>
    public override void Down()
    {
    }
}
