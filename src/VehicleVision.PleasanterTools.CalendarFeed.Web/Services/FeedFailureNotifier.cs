using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>
/// フィードの取得の失敗が続いたら、フィードを編集できる管理者へメールで知らせる（Issue #48）。
/// </summary>
/// <remarks>
/// <para>
/// 購読側（カレンダークライアント）は、取得に失敗しても黙って古い予定を見せ続ける。API キーの失効や Pleasanter の停止に気づくのが遅れるので、
/// <b>連続の失敗が閾値に届いたとき 1 通</b>、<b>回復したとき 1 通</b>知らせる。続く間は、設定の間隔（既定 24 時間）が過ぎるまで再通知しない。
/// </para>
/// <para>
/// <b>通知するかどうかは、DB の 1 文の UPDATE で決める</b>（<see cref="IFeedStore.TryMarkFailureNotifiedAsync"/>）。複数台で同時に失敗を記録しても、通知は 1 回。
/// 送るのはアウトボックス経由（送信ワーカーが送る。複数台でも二重に送らない）。
/// </para>
/// <para>
/// 宛先は、有効で、フィードを編集する権限（<see cref="AdminPermissions.FeedsWrite"/>）を持つ管理者のうち、ログイン ID がメールアドレスの人。
/// <b>本文にフィードの URL（トークン）・API キー・Pleasanter の応答の文面は載せない</b>。
/// </para>
/// </remarks>
public sealed class FeedFailureNotifier(
    IFeedStore feeds,
    IAdminUserStore users,
    IMailOutbox outbox,
    IMailPayloadProtector protector,
    RuntimeSettings settings,
    TimeProvider time,
    ILogger<FeedFailureNotifier> logger)
{
    /// <summary>取得の結果を受けて、必要なら知らせる。<b>例外は投げない</b>（応答を落とさない）。</summary>
    public async Task OnRecordedAsync(FeedDefinition feed, FeedFetchResult result)
    {
        try
        {
            var config = settings.Current;
            if (!config.Feed.NotifyFailure || !config.Mail.IsReady)
            {
                return;
            }

            if (result.Succeeded)
            {
                // 失敗を知らせていたときだけ、回復を知らせる（印の有無で原子的に決める）
                if (await feeds.TryClearFailureNotifiedAsync(feed.Id).ConfigureAwait(false))
                {
                    await SendAsync(feed, result, recovered: true).ConfigureAwait(false);
                }

                return;
            }

            if (result.ConsecutiveFailures >= config.Feed.NotifyThreshold
                && await feeds.TryMarkFailureNotifiedAsync(feed.Id, time.GetUtcNow(), config.Feed.NotifyRepeat).ConfigureAwait(false))
            {
                await SendAsync(feed, result, recovered: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "フィードの取得の失敗の通知を処理できませんでした（フィード {FeedId}）", feed.Id);
        }
    }

    private async Task SendAsync(FeedDefinition feed, FeedFetchResult result, bool recovered)
    {
        var config = settings.Current;
        var kind = recovered ? MailKind.FeedFetchRecovered : MailKind.FeedFetchFailure;
        var adminUrl = config.PublicBaseUrl is { } baseUrl ? $"{baseUrl.TrimEnd('/')}/admin/" : string.Empty;
        // 件名に改行を通さない（フィードの名前は管理者が決める）
        var name = AuditText.Safe(feed.Name, 100) ?? string.Empty;
        var at = result.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var recipients = (await users.ListAsync().ConfigureAwait(false))
            .Where(u => !u.IsDisabled
                && AdminPermissions.Of(u.Role).Contains(AdminPermissions.FeedsWrite)
                && MailAddress.TryCreate(u.LoginId, out _))
            .ToList();
        if (recipients.Count == 0)
        {
            logger.LogWarning("フィードの取得の失敗を知らせる宛先（フィードを編集できる有効な管理者でメールアドレスの人）がいません（フィード {FeedId}）", feed.Id);
            return;
        }

        foreach (var user in recipients)
        {
            var texts = FeedFailureMailTexts.For(user.Language);
            var (subject, body) = recovered
                ? (texts.RecoverSubject, string.Format(CultureInfo.InvariantCulture, texts.RecoverBody, name, at, adminUrl))
                : (texts.FailSubject, string.Format(CultureInfo.InvariantCulture, texts.FailBody, name, result.ConsecutiveFailures, at, texts.HintFor(result.Status), adminUrl));
            var mail = new OutgoingMail(user.LoginId, string.Format(CultureInfo.InvariantCulture, subject, name), body);

            // 通知の 1 回ごとに識別子を変える（同じ人・同じフィード・同じ時刻の重複だけを防ぐ）
            var mailId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"feed-notify:{kind}:{feed.Id}:{user.Id}:{result.At.UtcTicks}")).AsSpan(0, 16)).ToString("D");
            await outbox.EnqueueAsync(mailId, (int)kind, protector.Protect(mail)).ConfigureAwait(false);
        }
    }
}

/// <summary>フィードの取得の失敗・回復のメールの文面（7 言語）。平文で送る。</summary>
public static class FeedFailureMailTexts
{
    /// <param name="FailSubject">件名。<c>{0}</c> がフィードの名前。</param>
    /// <param name="FailBody">本文。<c>{0}</c> 名前、<c>{1}</c> 連続の回数、<c>{2}</c> 最後の時刻（UTC）、<c>{3}</c> 対処、<c>{4}</c> 管理画面の URL。</param>
    /// <param name="RecoverSubject">回復の件名。<c>{0}</c> がフィードの名前。</param>
    /// <param name="RecoverBody">回復の本文。<c>{0}</c> 名前、<c>{1}</c> 時刻（UTC）、<c>{2}</c> 管理画面の URL。</param>
    public sealed record Text(
        string FailSubject,
        string FailBody,
        string RecoverSubject,
        string RecoverBody,
        string HintTransient,
        string HintUnauthorized,
        string HintPermanent,
        string HintKeyError)
    {
        /// <summary>取得結果の種別に合う、対処の案内。</summary>
        public string HintFor(string status) => status switch
        {
            FeedFetchStatus.Unauthorized => HintUnauthorized,
            FeedFetchStatus.KeyError => HintKeyError,
            FeedFetchStatus.Permanent => HintPermanent,
            _ => HintTransient,
        };
    }

    private static readonly Dictionary<string, Text> ByLanguage = new(StringComparer.Ordinal)
    {
        ["ja"] = new(
            "[カレンダー購読フィード] フィード「{0}」の取得が失敗しています",
            "フィード「{0}」の Pleasanter からの取得が、{1} 回続けて失敗しています（最後: {2} UTC）。\n購読しているカレンダーには、古い予定が表示されたままになります。\n\n{3}\n\n管理画面: {4}",
            "[カレンダー購読フィード] フィード「{0}」の取得が回復しました",
            "フィード「{0}」の Pleasanter からの取得が回復しました（{1} UTC）。\n\n管理画面: {2}",
            "Pleasanter が停止していないか、本アプリから届くかを確認してください。",
            "フィードの API キーが失効・変更されていないか確認し、必要なら登録し直してください。",
            "フィードの設定（サイト ID・絞り込み・列の対応）と、API キーの利用者の権限を確認してください。",
            "API キーを復号できません。秘密の鍵（CALENDARFEED_SECRET_KEY）が変わっていないか確認し、API キーを登録し直してください。"),
        ["en"] = new(
            "[Calendar feed] Fetching feed \"{0}\" is failing",
            "Fetching feed \"{0}\" from Pleasanter has failed {1} times in a row (last: {2} UTC).\nSubscribed calendars keep showing old events.\n\n{3}\n\nAdmin screen: {4}",
            "[Calendar feed] Fetching feed \"{0}\" has recovered",
            "Fetching feed \"{0}\" from Pleasanter has recovered ({1} UTC).\n\nAdmin screen: {2}",
            "Check that Pleasanter is running and reachable from this app.",
            "Check whether the feed's API key was revoked or changed, and register it again if needed.",
            "Check the feed settings (site ID, filters, column mapping) and the permissions of the API key's user.",
            "The API key cannot be decrypted. Check that the secret key (CALENDARFEED_SECRET_KEY) has not changed, and register the API key again."),
        ["zh"] = new(
            "[日历订阅源] 订阅源“{0}”获取失败",
            "订阅源“{0}”从 Pleasanter 获取已连续失败 {1} 次（最近一次：{2} UTC）。\n已订阅的日历会一直显示旧的日程。\n\n{3}\n\n管理页面：{4}",
            "[日历订阅源] 订阅源“{0}”已恢复获取",
            "订阅源“{0}”从 Pleasanter 获取已恢复（{1} UTC）。\n\n管理页面：{2}",
            "请确认 Pleasanter 是否在运行，以及本应用能否访问它。",
            "请确认订阅源的 API 密钥是否已失效或被更改，必要时请重新登记。",
            "请确认订阅源设置（站点 ID、筛选条件、列的对应）以及 API 密钥所属用户的权限。",
            "无法解密 API 密钥。请确认密钥（CALENDARFEED_SECRET_KEY）未被更改，并重新登记 API 密钥。"),
        ["de"] = new(
            "[Kalender-Feed] Abruf des Feeds „{0}“ schlägt fehl",
            "Der Abruf des Feeds „{0}“ von Pleasanter ist {1} Mal in Folge fehlgeschlagen (zuletzt: {2} UTC).\nAbonnierte Kalender zeigen weiterhin alte Termine an.\n\n{3}\n\nVerwaltung: {4}",
            "[Kalender-Feed] Abruf des Feeds „{0}“ funktioniert wieder",
            "Der Abruf des Feeds „{0}“ von Pleasanter funktioniert wieder ({1} UTC).\n\nVerwaltung: {2}",
            "Prüfen Sie, ob Pleasanter läuft und von dieser Anwendung erreichbar ist.",
            "Prüfen Sie, ob der API-Schlüssel des Feeds widerrufen oder geändert wurde, und registrieren Sie ihn gegebenenfalls neu.",
            "Prüfen Sie die Feed-Einstellungen (Website-ID, Filter, Spaltenzuordnung) und die Berechtigungen des Benutzers des API-Schlüssels.",
            "Der API-Schlüssel kann nicht entschlüsselt werden. Prüfen Sie, ob sich der geheime Schlüssel (CALENDARFEED_SECRET_KEY) geändert hat, und registrieren Sie den API-Schlüssel neu."),
        ["ko"] = new(
            "[캘린더 구독 피드] 피드 \"{0}\" 가져오기에 실패하고 있습니다",
            "피드 \"{0}\"를 Pleasanter에서 가져오는 데 {1}회 연속 실패했습니다(마지막: {2} UTC).\n구독 중인 캘린더에는 오래된 일정이 계속 표시됩니다.\n\n{3}\n\n관리 화면: {4}",
            "[캘린더 구독 피드] 피드 \"{0}\" 가져오기가 복구되었습니다",
            "피드 \"{0}\"를 Pleasanter에서 가져오는 작업이 복구되었습니다({1} UTC).\n\n관리 화면: {2}",
            "Pleasanter가 실행 중인지, 이 앱에서 접근할 수 있는지 확인하세요.",
            "피드의 API 키가 폐기·변경되지 않았는지 확인하고, 필요하면 다시 등록하세요.",
            "피드 설정(사이트 ID, 필터, 열 대응)과 API 키 사용자의 권한을 확인하세요.",
            "API 키를 복호화할 수 없습니다. 비밀 키(CALENDARFEED_SECRET_KEY)가 바뀌지 않았는지 확인하고 API 키를 다시 등록하세요."),
        ["es"] = new(
            "[Feed de calendario] Falla la obtención del feed «{0}»",
            "La obtención del feed «{0}» desde Pleasanter ha fallado {1} veces seguidas (última: {2} UTC).\nLos calendarios suscritos siguen mostrando eventos antiguos.\n\n{3}\n\nPantalla de administración: {4}",
            "[Feed de calendario] La obtención del feed «{0}» se ha recuperado",
            "La obtención del feed «{0}» desde Pleasanter se ha recuperado ({1} UTC).\n\nPantalla de administración: {2}",
            "Compruebe que Pleasanter está en funcionamiento y es accesible desde esta aplicación.",
            "Compruebe si la clave de API del feed se ha revocado o cambiado y, si es necesario, vuelva a registrarla.",
            "Compruebe la configuración del feed (ID del sitio, filtros, correspondencia de columnas) y los permisos del usuario de la clave de API.",
            "No se puede descifrar la clave de API. Compruebe que la clave secreta (CALENDARFEED_SECRET_KEY) no ha cambiado y vuelva a registrar la clave de API."),
        ["vi"] = new(
            "[Nguồn cấp lịch] Việc lấy nguồn cấp \"{0}\" đang thất bại",
            "Việc lấy nguồn cấp \"{0}\" từ Pleasanter đã thất bại {1} lần liên tiếp (lần cuối: {2} UTC).\nLịch đã đăng ký vẫn hiển thị các sự kiện cũ.\n\n{3}\n\nTrang quản lý: {4}",
            "[Nguồn cấp lịch] Việc lấy nguồn cấp \"{0}\" đã hoạt động trở lại",
            "Việc lấy nguồn cấp \"{0}\" từ Pleasanter đã hoạt động trở lại ({1} UTC).\n\nTrang quản lý: {2}",
            "Hãy kiểm tra Pleasanter có đang chạy và ứng dụng này có truy cập được hay không.",
            "Hãy kiểm tra khóa API của nguồn cấp có bị thu hồi hoặc thay đổi không, và đăng ký lại nếu cần.",
            "Hãy kiểm tra cài đặt nguồn cấp (ID trang, bộ lọc, ánh xạ cột) và quyền của người dùng khóa API.",
            "Không thể giải mã khóa API. Hãy kiểm tra khóa bí mật (CALENDARFEED_SECRET_KEY) có bị thay đổi không và đăng ký lại khóa API."),
    };

    /// <summary>言語に合う文面。対応外は日本語。</summary>
    public static Text For(string? language) => ByLanguage[FeedLanguages.Normalize(language)];
}
