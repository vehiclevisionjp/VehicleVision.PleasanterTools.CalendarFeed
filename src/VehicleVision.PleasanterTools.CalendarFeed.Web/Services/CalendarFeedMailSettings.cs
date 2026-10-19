namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>
/// このアプリのメール設定の名前。送信の部品は Core.Mail にあり、<b>環境変数の接頭辞はアプリごとに決める</b>
/// ので、ここで持つ。
/// </summary>
public static class CalendarFeedMailSettings
{
    /// <summary>メールの環境変数の接頭辞（例: <c>CALENDARFEED_MAIL_ENABLED</c>）。</summary>
    public const string Prefix = "CALENDARFEED_MAIL_";
}
