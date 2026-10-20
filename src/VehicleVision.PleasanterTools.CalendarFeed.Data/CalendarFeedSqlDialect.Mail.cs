using static VehicleVision.PleasanterTools.Core.Data.SqlDialect;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>メール・管理者の確保で、RDBMS ごとに書き方が違う部分。</summary>
public static partial class CalendarFeedSqlDialect
{
    /// <summary>
    /// 送れる 1 通を <b>1 文で原子的に</b>確保する SQL。複数の送信ワーカーが同じ 1 通を取らない。
    /// 識別子は各 RDBMS の引用符で書いてある（<see cref="Format"/> を通さなくてよい）。
    /// </summary>
    public static string ClaimPendingMail(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE TOP (1) [MailOutbox] " +
            "SET [Status] = @SendingStatus, [LockedBy] = @LockedBy, [LockedUntil] = @LockedUntil " +
            "OUTPUT inserted.[MailId], inserted.[Kind], inserted.[PayloadProtected], inserted.[RetryCount] " +
            "WHERE [Status] = @PendingStatus AND [NextAttemptAt] <= @Now",

        // 行ロックを取り、すでに取られている行は飛ばす（待たずに次の行へ）
        DatabaseProvider.PostgreSql =>
            "UPDATE \"MailOutbox\" AS m " +
            "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil " +
            "WHERE m.\"MailId\" = (" +
            "  SELECT c.\"MailId\" FROM \"MailOutbox\" AS c " +
            "  WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now " +
            "  ORDER BY c.\"NextAttemptAt\" FOR UPDATE SKIP LOCKED LIMIT 1) " +
            "RETURNING \"MailId\", \"Kind\", \"PayloadProtected\", \"RetryCount\"",

        // SQLite は書き込みが直列なので、副問い合わせで 1 行に絞れば足りる
        DatabaseProvider.Sqlite =>
            "UPDATE \"MailOutbox\" AS m " +
            "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil " +
            "WHERE m.rowid = (" +
            "  SELECT c.rowid FROM \"MailOutbox\" AS c " +
            "  WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now " +
            "  ORDER BY c.\"NextAttemptAt\" LIMIT 1) " +
            "RETURNING \"MailId\", \"Kind\", \"PayloadProtected\", \"RetryCount\"",

        // MySQL は UPDATE … ORDER BY … LIMIT で 1 行に絞る。RETURNING が無いので <see cref="ReadClaimedMailForMySql"/> で読み直す
        DatabaseProvider.MySql =>
            "UPDATE `MailOutbox` " +
            "SET `Status` = @SendingStatus, `LockedBy` = @LockedBy, `LockedUntil` = @LockedUntil " +
            "WHERE `Status` = @PendingStatus AND `NextAttemptAt` <= @Now " +
            "ORDER BY `NextAttemptAt` LIMIT 1",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>
    /// Administrator の行を、主キー順にロックする SELECT（<b>トランザクションの中で使う</b>）。
    /// 最後の Administrator を守る判定を、同時の変更の間で 1 つずつにするため。SQLite は書き込みが直列なのでロック句が要らない。
    /// </summary>
    public static string LockAdministrators(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "SELECT [Id] FROM [AdminUsers] WITH (UPDLOCK, HOLDLOCK) WHERE [Role] = @Admin ORDER BY [Id]",
        DatabaseProvider.PostgreSql =>
            "SELECT \"Id\" FROM \"AdminUsers\" WHERE \"Role\" = @Admin ORDER BY \"Id\" FOR UPDATE",
        DatabaseProvider.MySql =>
            "SELECT `Id` FROM `AdminUsers` WHERE `Role` = @Admin ORDER BY `Id` FOR UPDATE",
        DatabaseProvider.Sqlite =>
            "SELECT \"Id\" FROM \"AdminUsers\" WHERE \"Role\" = @Admin ORDER BY \"Id\"",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>MySQL で確保した行を、確保ごとに一意な <c>LockedBy</c> で読み直す。</summary>
    public const string ReadClaimedMailForMySql =
        "SELECT `MailId`, `Kind`, `PayloadProtected`, `RetryCount` FROM `MailOutbox` WHERE `LockedBy` = @LockedBy";
}
