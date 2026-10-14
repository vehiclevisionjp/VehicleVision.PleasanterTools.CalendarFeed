using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>SMTP で 1 通送る（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **認証付きリレーの 587 番は Azure でブロックされない**ので、これ 1 本で
/// SendGrid・Amazon SES・Azure Communication Services・Microsoft 365 に届く
/// （Microsoft Learn「Troubleshoot outbound SMTP connectivity problems in Azure」2026-09-13 参照）。
/// </para>
/// <para>
/// **1 通ごとに繋いで切る。** 送信ワーカーは 1 件ずつ順に送る作りで、
/// 接続を持ち回っても得が少ない一方、**持ち回ると切れた接続の扱いが増える。**
/// </para>
/// <para>
/// ⚠️ **宛先・本文・パスワードをログへ出さない。**
/// </para>
/// </remarks>
public sealed class SmtpMailTransport(
    MailOptions options,
    ILogger<SmtpMailTransport> logger)
    : IMailTransport
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mail);

        if (!options.IsReady)
        {
            // **黙って捨てない。** 送れない設定のまま「送った」ことにすると、
            // 届いていないことに誰も気付けない
            throw new MailDeliveryException(
                "メールの設定が有効になっていない（CALENDARFEED_MAIL_*）", isTransient: false);
        }

        var message = MailMessageFactory.Build(options, mail);

        using var client = new SmtpClient
        {
            Timeout = (int)options.Timeout.TotalMilliseconds,
        };

        try
        {
            await client.ConnectAsync(
                options.Host, options.Port, ToSocketOptions(options.Security), cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                await client.AuthenticateAsync(options.UserName, options.Password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            // **件名も宛先も出さない。** 送れたことだけ残す
            logger.LogInformation("メールを 1 通送った（{Host}:{Port}）", options.Host, options.Port);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // **停止の合図は失敗ではない。** そのまま抜けて、次回また拾わせる
            throw;
        }
        catch (Exception exception)
        {
            throw Classify(exception);
        }
    }

    /// <summary>失敗を、再送する価値があるかどうかに翻訳する。</summary>
    /// <remarks>
    /// **4xx は一時、5xx は恒久**（RFC 5321）。認証と TLS の失敗は設定の誤りなので、
    /// **何度送っても同じ**として恒久に倒す。**倒す先を間違えると、
    /// 直らない failure を延々と再送し続けるか、直る failure を捨てるかのどちらかになる。**
    /// </remarks>
    private static MailDeliveryException Classify(Exception exception) => exception switch
    {
        MailDeliveryException delivery => delivery,

        // **TLS が握れない**。証明書か暗号化の指定の誤り。**AuthenticationException より先に見る**
        SslHandshakeException => new MailDeliveryException(
            "SMTP の TLS を確立できなかった（証明書か暗号化の設定を確かめること）",
            isTransient: false,
            exception),

        // **認証が通らない**。利用者名かパスワードの誤り。
        // **MailKit の型ではなく基底で受ける**（MailKit.Security.AuthenticationException は
        // System.Security.Authentication.AuthenticationException を継承していて、
        // using を両方入れると名前が衝突する）
        System.Security.Authentication.AuthenticationException => new MailDeliveryException(
            "SMTP の認証が通らなかった（利用者名とパスワードを確かめること）",
            isTransient: false,
            exception),

        SmtpCommandException command => new MailDeliveryException(
            $"SMTP が要求を拒否した（{(int)command.StatusCode}）",
            isTransient: (int)command.StatusCode < 500,
            exception),

        // 手順の不一致。**相手側の一時的な不調でも起きる**ので一時に倒す
        SmtpProtocolException => new MailDeliveryException(
            "SMTP の手順が噛み合わなかった", isTransient: true, exception),

        SocketException or IOException or TimeoutException => new MailDeliveryException(
            "SMTP サーバへ繋がらなかった", isTransient: true, exception),

        // **時間切れ**。cancellationToken による中止はここへ来ない（先に再送出している）
        OperationCanceledException => new MailDeliveryException(
            "SMTP の応答が時間内に返らなかった", isTransient: true, exception),

        _ => new MailDeliveryException("メールを送れなかった", isTransient: true, exception),
    };

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
