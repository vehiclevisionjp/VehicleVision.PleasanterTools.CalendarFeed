using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>Azure Communication Services で 1 通送る（Issue #198）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **接続文字列を受け取らない。** Entra ID のマネージド ID
/// （<c>DefaultAzureCredential</c>）だけを使う。**鍵を置ける口を作らない**のが、
/// この経路を足した理由そのもの。
/// </para>
/// <para>
/// **本文は平文だけ**（<c>OutgoingMail</c>）。ACS は HTML も送れるが、
/// **経路によって送れるものが変わらない**ようにする。
/// </para>
/// <para>
/// ⚠️ **送るのを待たない。** ACS の送信は非同期で、完了まで待つと 1 通ごとに
/// 数十秒掛かることがある。**受け付けられた時点で「送れた」とする**
/// （SMTP でリレーが受け取った時点と同じ扱い）。届かなかったことは
/// ACS 側の記録で追う。
/// </para>
/// </remarks>
public sealed class AcsMailTransport : IMailTransport
{
    private readonly MailOptions _options;
    private readonly ILogger<AcsMailTransport> _logger;
    private readonly EmailClient _client;

    public AcsMailTransport(MailOptions options, ILogger<AcsMailTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;

        // **窓口だけを設定から取る。** 資格情報はマネージド ID
        _client = new EmailClient(options.AcsEndpoint, new DefaultAzureCredential());
    }

    /// <summary>試験から入れ替えるための口。</summary>
    internal AcsMailTransport(MailOptions options, ILogger<AcsMailTransport> logger, EmailClient client)
    {
        _options = options;
        _logger = logger;
        _client = client;
    }

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mail);

        if (!_options.IsReady)
        {
            throw new MailDeliveryException(
                "メールの設定が有効になっていない（CALENDARFEED_MAIL_*）", isTransient: false);
        }

        mail.Validate();

        try
        {
            var headers = MailMessageFactory.ResolveHeaders(_options, mail);
            MailMessageFactory.ValidateHeaders(headers);
            var recipients = new EmailRecipients([new EmailAddress(headers.ToAddress)]);
            if (headers.BccAddress is not null)
            {
                recipients.BCC.Add(new EmailAddress(headers.BccAddress));
            }

            var message = new EmailMessage(
                headers.FromAddress,
                recipients,
                new EmailContent(mail.Subject) { PlainText = mail.Body });
            if (headers.ReplyToAddress is not null)
            {
                message.ReplyTo.Add(new EmailAddress(headers.ReplyToAddress));
            }

            // **待たない**（WaitUntil.Started）。受け付けられた時点で送れたとする
            await _client.SendAsync(
                WaitUntil.Started,
                message,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("メールを 1 通送った（Azure Communication Services）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Classify(exception);
        }
    }

    /// <summary>失敗を、再送する価値があるかどうかに翻訳する。</summary>
    /// <remarks>
    /// ⚠️ **ACS には送信の流量の上限があり、既定はとくに低い。**
    /// 上限に当たったとき（429）は**必ず一時**として扱い、間を置いて送り直す。
    /// </remarks>
    internal static MailDeliveryException Classify(Exception exception) => exception switch
    {
        MailDeliveryException delivery => delivery,

        RequestFailedException failed => failed.Status switch
        {
            // **投げ過ぎ。** 間を置けば通る
            429 => new MailDeliveryException(
                "ACS の流量の上限に達した", isTransient: true, exception),

            // **資格情報が通らない。** マネージド ID の割り当て漏れは人が直す
            401 or 403 => new MailDeliveryException(
                "ACS の認証が通らなかった（マネージド ID の割り当てを確かめること）",
                isTransient: false,
                exception),

            >= 500 => new MailDeliveryException(
                $"ACS が応答しなかった（{failed.Status}）", isTransient: true, exception),

            _ => new MailDeliveryException(
                $"ACS が要求を拒否した（{failed.Status}）", isTransient: false, exception),
        },

        // **資格情報そのものを取れない。** 設定の誤り
        AuthenticationFailedException => new MailDeliveryException(
            "ACS の資格情報を取得できなかった（マネージド ID の設定を確かめること）",
            isTransient: false,
            exception),

        HttpRequestException or TimeoutException => new MailDeliveryException(
            "ACS へ繋がらなかった", isTransient: true, exception),

        _ => new MailDeliveryException("メールを送れなかった", isTransient: true, exception),
    };
}
