namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>
/// メールの種類（メールのアウトボックスの <c>Kind</c> 列へ整数で入る。<b>値を入れ替えない</b>）。
/// Core.Data のアウトボックスは値の意味を持たないので、CalendarFeed が決める。
/// </summary>
public enum MailKind
{
    /// <summary>管理者の招待。<b>本文の URL が、それだけで管理者になれる値。</b></summary>
    AdminInvitation = 2,

    /// <summary>メール設定の試し送信。</summary>
    SettingsTest = 3,

    /// <summary>フィードの取得の失敗が続いている通知。</summary>
    FeedFetchFailure = 4,

    /// <summary>フィードの取得が回復した通知。</summary>
    FeedFetchRecovered = 5,
}
