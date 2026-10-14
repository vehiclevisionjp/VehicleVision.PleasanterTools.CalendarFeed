using System.Net;
using Amazon;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>Amazon SES で 1 通送る（Issue #198）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **資格情報を設定に持たない。** AWS SDK の既定の探索順
/// （App Service や EC2 のマネージド ID → IAM ロール → 環境変数）に任せる。
/// **鍵を設定へ書ける口を作らない**のが、この経路を足した理由そのもの
/// （SMTP なら必ずパスワードを 1 つ保管することになる）。
/// </para>
/// <para>
/// **SMTP と同じ 1 通を送る。** 組み立ては <see cref="MailMessageFactory"/> で共通、
/// ここは**出来上がった生のメールをそのまま渡す**（<c>SendEmail</c> の Raw）。
/// 差出人や件名の扱いが経路ごとにずれない。
/// </para>
/// <para>
/// ⚠️ **宛先・本文をログへ出さない**（SMTP の経路と同じ決まり）。
/// </para>
/// </remarks>
public sealed class SesMailTransport : IMailTransport, IDisposable
{
    private readonly MailOptions _options;
    private readonly ILogger<SesMailTransport> _logger;
    private readonly IAmazonSimpleEmailServiceV2 _client;

    public SesMailTransport(MailOptions options, ILogger<SesMailTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger = logger;

        // **地域だけを設定から取る。** 資格情報は SDK が探す
        _client = new AmazonSimpleEmailServiceV2Client(
            new AmazonSimpleEmailServiceV2Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(options.SesRegion),
            });
    }

    /// <summary>試験から入れ替えるための口。</summary>
    internal SesMailTransport(
        MailOptions options,
        ILogger<SesMailTransport> logger,
        IAmazonSimpleEmailServiceV2 client)
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

        var message = MailMessageFactory.Build(_options, mail);

        using var raw = new MemoryStream();
        await message.WriteToAsync(raw, cancellationToken).ConfigureAwait(false);

        try
        {
            await _client.SendEmailAsync(
                new SendEmailRequest
                {
                    // **生のメールをそのまま渡す。** 組み立ては経路で分けない
                    Content = new EmailContent
                    {
                        Raw = new RawMessage { Data = new MemoryStream(raw.ToArray()) },
                    },
                },
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("メールを 1 通送った（Amazon SES / {Region}）", _options.SesRegion);
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
    /// **止まっているのか、直らないのかを分ける**（SMTP の経路と同じ考え方）。
    /// ⚠️ **倒す先を間違えると、直らない失敗を延々と再送するか、直る失敗を捨てる。**
    /// </remarks>
    internal static MailDeliveryException Classify(Exception exception) => exception switch
    {
        MailDeliveryException delivery => delivery,

        // **送信元や宛先の登録がまだ。** 人が直すまで通らない
        MailFromDomainNotVerifiedException => new MailDeliveryException(
            "SES の送信ドメインが検証されていない", isTransient: false, exception),

        AccountSuspendedException => new MailDeliveryException(
            "SES のアカウントが停止されている", isTransient: false, exception),

        SendingPausedException => new MailDeliveryException(
            "SES の送信が止められている", isTransient: false, exception),

        // **投げ過ぎ。** 間を置けば通る
        TooManyRequestsException or LimitExceededException => new MailDeliveryException(
            "SES の流量の上限に達した", isTransient: true, exception),

        // **入力そのものが通らない。** 送り直しても同じ
        BadRequestException => new MailDeliveryException(
            "SES が要求を受け付けなかった", isTransient: false, exception),

        AmazonSimpleEmailServiceV2Exception ses => new MailDeliveryException(
            $"SES が要求を拒否した（{(int)ses.StatusCode}）",
            // **5xx は向こう側の不調。** 4xx は直らない
            isTransient: (int)ses.StatusCode >= (int)HttpStatusCode.InternalServerError,
            exception),

        HttpRequestException or TimeoutException => new MailDeliveryException(
            "SES へ繋がらなかった", isTransient: true, exception),

        _ => new MailDeliveryException("メールを送れなかった", isTransient: true, exception),
    };

    public void Dispose() => _client.Dispose();
}
