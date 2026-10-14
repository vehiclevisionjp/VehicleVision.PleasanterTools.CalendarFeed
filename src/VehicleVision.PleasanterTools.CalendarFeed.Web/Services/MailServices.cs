using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Mail;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>送信待ちの中身（宛先・件名・本文）を暗号化する。</summary>
/// <remarks>**平文の列を作らない。** 鍵が無い・違うと復号できず、送信ワーカーはデッドレターへ回す（2 要素の共有鍵と同じ前提）。</remarks>
public sealed class MailPayloadProtector(ISecretProtector protector) : IMailPayloadProtector
{
    private const string Context = "calendarfeed:mail";

    public string Protect(OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        return protector.Protect(MailPayload.ToJson(mail), Context);
    }

    public OutgoingMail? Unprotect(string protectedPayload)
    {
        try
        {
            return MailPayload.FromJson(protector.Unprotect(protectedPayload, Context));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            // 鍵の変更・破損・未設定。何度やっても読めないので、呼び出し側が分離する
            return null;
        }
    }
}

/// <summary>設定の送信経路（SMTP / Amazon SES / Azure Communication Services）で送る。</summary>
public sealed class ConfiguredMailTransport(MailOptions options, ILoggerFactory loggerFactory) : IMailTransport
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        if (!options.IsReady)
        {
            throw new MailDeliveryException("メールの設定が有効になっていません（CALENDARFEED_MAIL_*）。", isTransient: true);
        }

        IMailTransport transport = options.Transport switch
        {
            MailTransportKind.AmazonSes => new SesMailTransport(options, loggerFactory.CreateLogger<SesMailTransport>()),
            MailTransportKind.AzureCommunicationServices => new AcsMailTransport(options, loggerFactory.CreateLogger<AcsMailTransport>()),
            _ => new SmtpMailTransport(options, loggerFactory.CreateLogger<SmtpMailTransport>()),
        };

        try
        {
            await transport.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }
}

/// <summary>送信ワーカーの設定。</summary>
public sealed class MailSenderOptions
{
    public const string MaxSendsPerMinuteKey = "CALENDARFEED_MAIL_SEND_PER_MINUTE";

    /// <summary>このインスタンスで送信ワーカーを動かすか（既定 true）。</summary>
    public const string WorkerKey = "CALENDARFEED_MAIL_WORKER";

    /// <summary>
    /// このインスタンスで送信ワーカーを動かすか。<b>複数台にするとき、送信を担う台を絞る</b>ために <c>false</c> にできる
    /// （確保は 1 文の UPDATE で原子的なので、全台で動かしても二重には送らない）。積む側（招待）はどの台でも動く。
    /// </summary>
    public bool WorkerEnabled { get; init; } = true;

    /// <summary>ワーカーの名前（確保の持ち主）。</summary>
    public string WorkerName { get; init; } = Environment.MachineName;

    /// <summary>確保の有効期間。落ちても、これが過ぎれば解放される。</summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>再送の上限。超えたらデッドレター。</summary>
    public int MaxRetryCount { get; init; } = 10;

    /// <summary>送るものが無いときの待ち。</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReleaseExpiredLocksInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>1 分あたりの送信数の上限（既定 60）。送信元の流量の上限を超えると弾かれ、再送で余計に混む。</summary>
    public int MaxSendsPerMinute { get; set; } = 60;

    /// <summary>通と通の最短の間隔。</summary>
    public TimeSpan MinSendInterval => MaxSendsPerMinute > 0
        ? TimeSpan.FromTicks(TimeSpan.TicksPerMinute / MaxSendsPerMinute)
        : TimeSpan.Zero;

    /// <summary>設定から読む。読めない値は起動時に落とす。</summary>
    public static MailSenderOptions FromConfiguration(IConfiguration configuration)
    {
        var workerRaw = configuration[WorkerKey];
        var workerEnabled = string.IsNullOrWhiteSpace(workerRaw)
            || (bool.TryParse(workerRaw, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"{WorkerKey} は true か false で指定してください（今の値: {workerRaw}）"));

        var raw = configuration[MaxSendsPerMinuteKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MailSenderOptions { WorkerEnabled = workerEnabled };
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value > 0
            ? new MailSenderOptions { MaxSendsPerMinute = value, WorkerEnabled = workerEnabled }
            : throw new InvalidOperationException($"{MaxSendsPerMinuteKey} は 1 以上の整数で指定してください（今の値: {raw}）");
    }

    /// <summary>次に試す時刻（指数的に伸ばし、上限で止める）。</summary>
    public DateTimeOffset NextAttemptAt(DateTimeOffset now, int retryCount)
    {
        var factor = Math.Pow(2, Math.Min(retryCount, 16));
        return now + TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
    }
}

/// <summary>1 回の送信の結果。</summary>
public enum SendOutcome
{
    /// <summary>送るものが無かった。</summary>
    Idle,

    /// <summary>送れた。</summary>
    Sent,

    /// <summary>再送待ちへ戻した。</summary>
    Rescheduled,

    /// <summary>デッドレターへ回した。</summary>
    DeadLettered,
}

/// <summary>送信待ちから 1 通取って送る。</summary>
public sealed class MailSender(
    IMailOutbox outbox,
    IMailTransport transport,
    IMailPayloadProtector protector,
    MailSenderOptions options,
    TimeProvider time,
    ILogger<MailSender> logger)
{
    /// <summary>1 通だけ送る。</summary>
    public async Task<SendOutcome> SendOnceAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await outbox.ClaimAsync(options.WorkerName, options.LockDuration, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return SendOutcome.Idle;
        }

        try
        {
            return await ProcessAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 確保したまま落ちても、期限切れで解放される。メールは失われない
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "メールの送信で想定外の失敗が起きました");
            return await RescheduleAsync(claimed, "想定外の失敗", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SendOutcome> ProcessAsync(PendingMail claimed, CancellationToken cancellationToken)
    {
        var mail = protector.Unprotect(claimed.PayloadProtected);
        if (mail is null)
        {
            // 鍵が変わった・行が壊れた。何度やっても読めないので分離する
            return await DeadLetterAsync(claimed, "送信待ちの中身を復号できません（鍵の変更か破損）", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await transport.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        catch (MailDeliveryException ex)
        {
            // 再送する価値があるかは送信経路が判定済み（4xx は一時、5xx は恒久）
            return ex.IsTransient
                ? await RescheduleAsync(claimed, ex.Message, cancellationToken).ConfigureAwait(false)
                : await DeadLetterAsync(claimed, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        // 送れたら消す。宛先も本文も残さない
        await outbox.CompleteAsync(claimed.MailId, cancellationToken).ConfigureAwait(false);
        return SendOutcome.Sent;
    }

    private async Task<SendOutcome> RescheduleAsync(PendingMail claimed, string error, CancellationToken cancellationToken)
    {
        if (claimed.RetryCount >= options.MaxRetryCount)
        {
            return await DeadLetterAsync(claimed, $"再送の上限に達しました（{claimed.RetryCount} 回）: {error}", cancellationToken).ConfigureAwait(false);
        }

        await outbox.RescheduleAsync(claimed.MailId, options.NextAttemptAt(time.GetUtcNow(), claimed.RetryCount), error, cancellationToken).ConfigureAwait(false);
        return SendOutcome.Rescheduled;
    }

    private async Task<SendOutcome> DeadLetterAsync(PendingMail claimed, string error, CancellationToken cancellationToken)
    {
        // 黙って溜め続けない。ログに残す（宛先・本文は入れない）。件数は GET /admin/api/mail/status で見られる
        logger.LogError("メールをデッドレターへ回しました: {Reason}", error);
        await outbox.DeadLetterAsync(claimed.MailId, error, cancellationToken).ConfigureAwait(false);
        return SendOutcome.DeadLettered;
    }
}

/// <summary>送信待ちを定期的に送る（アプリの中の BackgroundService）。</summary>
public sealed class MailSenderHostedService(
    MailSender sender,
    IMailOutbox outbox,
    MailSenderOptions options,
    TimeProvider time,
    ILogger<MailSenderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.WorkerEnabled)
        {
            logger.LogInformation("このインスタンスではメール送信ワーカーを動かしません（{Key}=false）", MailSenderOptions.WorkerKey);
            return;
        }

        logger.LogInformation("メール送信ワーカーを開始しました（{Worker}）", options.WorkerName);
        var nextRelease = time.GetUtcNow();
        var nextSend = time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 確保したまま落ちた行を戻す。ワーカーが落ちてもメールは失われない
                if (time.GetUtcNow() >= nextRelease)
                {
                    var released = await outbox.ReleaseExpiredLocksAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                    if (released > 0)
                    {
                        logger.LogWarning("期限切れの確保を {Count} 件解放しました（メール）", released);
                    }

                    nextRelease = time.GetUtcNow() + options.ReleaseExpiredLocksInterval;
                }

                // 送信元の流量の上限を超えない。今の時刻から数える（遅れを「借り」として溜め込まない）
                if (options.MinSendInterval > TimeSpan.Zero)
                {
                    var wait = nextSend - time.GetUtcNow();
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                    }

                    nextSend = time.GetUtcNow() + options.MinSendInterval;
                }

                if (await sender.SendOnceAsync(stoppingToken).ConfigureAwait(false) is SendOutcome.Idle)
                {
                    await Task.Delay(options.IdleDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // ここで落とさない。1 通の失敗でワーカーを止めない
                logger.LogError(ex, "メール送信ワーカーで失敗が起きました。間隔を置いて続けます");
                await Task.Delay(options.IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("メール送信ワーカーを停止しました");
    }
}

/// <summary>管理者の招待をメールで送る。</summary>
/// <remarks>
/// <para>
/// 本人へ直接送る（手渡しの途中で漏れる経路を減らす）。<b>画面へ URL を出すのはやめない</b>:
/// メールが送れない構成（設定していない・ログイン ID がメールアドレスでない）でも、招待は出せる。
/// 送れたかどうか（<c>mailSent</c>）を画面へ返すので、送れていなければ手で渡せる。
/// </para>
/// <para>
/// ⚠️ **招待の URL は、それだけで管理者になれる値。** 送信待ちの行では暗号化され、送れたら行ごと消える。
/// リンクは設定の公開 URL から作る（**要求の Host から組み立てない**: host header injection）。
/// </para>
/// </remarks>
public sealed class AdminInvitationMailer(
    IMailOutbox outbox,
    IMailPayloadProtector protector,
    MailOptions options,
    IConfiguration configuration,
    ILogger<AdminInvitationMailer> logger)
{
    /// <summary>画面へ出す招待の URL の経路（SPA が受ける）。</summary>
    public const string AcceptPath = "/admin/invitations/accept";

    /// <summary>招待のメールを積む。積めたら <c>true</c>。</summary>
    /// <param name="loginId">相手のログイン ID。<b>メールアドレスのときだけ送る。</b></param>
    /// <param name="token">招待のトークン。この 1 回しか受け取れない値。</param>
    /// <param name="expiresAt">期限。</param>
    /// <param name="language">招いた側が使っている言語。</param>
    public async Task<bool> TryEnqueueAsync(
        string loginId,
        string token,
        DateTimeOffset expiresAt,
        string? language,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsReady)
        {
            return false;
        }

        if (AppSettings.Text(configuration, AppSettings.PublicBaseUrl) is not { } baseUrl)
        {
            logger.LogWarning("招待メールを送れません（{Key} が未設定）。画面の URL を手で渡してください", AppSettings.PublicBaseUrl);
            return false;
        }

        if (!MailAddress.TryCreate(loginId, out _))
        {
            // ログイン ID がメールアドレスとは限らない。宛先が無いだけで、異常ではない
            logger.LogInformation("ログイン ID がメールアドレスではないので、招待メールは送りません");
            return false;
        }

        try
        {
            var url = $"{baseUrl.TrimEnd('/')}{AcceptPath}?token={Uri.EscapeDataString(token)}";
            var texts = InvitationMailTexts.For(language);
            var mail = new OutgoingMail(
                loginId,
                texts.Subject,
                string.Format(CultureInfo.InvariantCulture, texts.Body, url, expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

            // ここで初めて暗号化する。招待の URL が平文で DB に載る経路を作らない
            return await outbox.EnqueueAsync(MailIdOf(token), (int)MailKind.AdminInvitation, protector.Protect(mail), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 招待そのものはすでに出ている。メールを積めなかったことで失敗にしない
            logger.LogError(ex, "招待メールを積めませんでした。画面の URL を手で渡してください");
            return false;
        }
    }

    /// <summary>
    /// トークンから、送信待ちの識別子を決める。⚠️ **トークンそのものを識別子にしない**
    /// （送信待ちの行は管理画面から見える所にあり、招待の URL はそれだけで管理者になれる値）。出し直すたびに変わる。
    /// </summary>
    public static string MailIdOf(string token) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("invitation:" + token)).AsSpan(0, 16)).ToString("D");
}

/// <summary>招待メールの文面（7 言語）。平文で送る。</summary>
public static class InvitationMailTexts
{
    /// <summary>件名と本文。本文の <c>{0}</c> が URL、<c>{1}</c> が期限（UTC）。</summary>
    public sealed record Text(string Subject, string Body);

    private static readonly Dictionary<string, Text> ByLanguage = new(StringComparer.Ordinal)
    {
        ["ja"] = new(
            "カレンダー購読フィードの管理画面への招待",
            "カレンダー購読フィードの管理画面への招待が届いています。\n\n次の URL を開き、パスワードを決めてください。\n{0}\n\n期限: {1} (UTC)\n\nこのメールに心当たりが無ければ、URL を開かずに破棄してください。"),
        ["en"] = new(
            "You have been invited to the calendar feed admin",
            "You have been invited to the calendar feed admin screen.\n\nOpen the following URL and choose your password.\n{0}\n\nExpires: {1} (UTC)\n\nIf you were not expecting this, discard this message without opening the URL."),
        ["zh"] = new(
            "日历订阅源管理页面邀请",
            "您已收到日历订阅源管理页面的邀请。\n\n请打开以下 URL 并设置密码。\n{0}\n\n到期时间：{1} (UTC)\n\n如果您未曾预期收到此邮件，请勿打开 URL 并删除此邮件。"),
        ["de"] = new(
            "Sie wurden zur Verwaltung der Kalender-Feeds eingeladen",
            "Sie wurden zum Verwaltungsbildschirm für Kalender-Feeds eingeladen.\n\nÖffnen Sie die folgende URL und wählen Sie Ihr Passwort.\n{0}\n\nLäuft ab: {1} (UTC)\n\nWenn Sie dies nicht erwartet haben, löschen Sie diese Nachricht, ohne die URL zu öffnen."),
        ["ko"] = new(
            "캘린더 구독 피드 관리 화면 초대",
            "캘린더 구독 피드 관리 화면 초대가 도착했습니다.\n\n다음 URL을 열고 비밀번호를 정하세요.\n{0}\n\n만료: {1} (UTC)\n\n이 메일을 예상하지 못했다면 URL을 열지 말고 삭제하세요."),
        ["es"] = new(
            "Ha sido invitado a la administración de feeds de calendario",
            "Ha sido invitado a la pantalla de administración de feeds de calendario.\n\nAbra la siguiente URL y elija su contraseña.\n{0}\n\nCaduca: {1} (UTC)\n\nSi no esperaba este mensaje, descártelo sin abrir la URL."),
        ["vi"] = new(
            "Bạn được mời đến trang quản lý nguồn cấp lịch",
            "Bạn đã được mời đến trang quản lý nguồn cấp lịch.\n\nHãy mở URL sau và đặt mật khẩu.\n{0}\n\nHết hạn: {1} (UTC)\n\nNếu bạn không mong đợi email này, hãy xóa nó mà không mở URL."),
    };

    /// <summary>言語に合う文面。対応外は日本語。</summary>
    public static Text For(string? language) =>
        ByLanguage[Feeds.FeedLanguages.Normalize(language)];
}
