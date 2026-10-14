using System.Net.Mail;
using MimeKit;
using MimeKit.Text;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>送る 1 通を、実際のメールの形へ組み立てる。</summary>
/// <remarks>
/// **通信を含まない。** ここだけなら網無しで試験できる。
/// </remarks>
public static class MailMessageFactory
{
    /// <summary>
    /// 自動で作ったメールであることを示すヘッダ（RFC 3834）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **自動返信に自動返信が返ると無限に往復する。**
    /// まともな不在通知はこのヘッダを見て返信を止める。**全通に必ず付ける。**
    /// </remarks>
    public const string AutoSubmittedHeader = "Auto-Submitted";

    /// <summary>全体設定と 1 通ごとの指定から、実際に使うヘッダを決める。</summary>
    public static ResolvedMailHeaders ResolveHeaders(MailOptions options, OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mail);

        var fromName = string.IsNullOrWhiteSpace(mail.FromName) ? options.FromName : mail.FromName;
        var replyTo = string.IsNullOrWhiteSpace(mail.ReplyToAddress)
            ? options.ReplyToAddress
            : mail.ReplyToAddress;
        var bcc = string.IsNullOrWhiteSpace(mail.BccAddress) ? null : mail.BccAddress;

        return new ResolvedMailHeaders(
            options.FromAddress,
            fromName,
            mail.ToAddress,
            replyTo,
            bcc);
    }

    /// <summary>組み立てる。</summary>
    /// <exception cref="MailDeliveryException">
    /// アドレスの形が壊れている。**再送しても直らないので恒久の失敗として投げる。**
    /// </exception>
    public static MimeMessage Build(MailOptions options, OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mail);
        mail.Validate();
        var headers = ResolveHeaders(options, mail);
        ValidateHeaders(headers);

        var message = new MimeMessage();
        message.From.Add(Parse(headers.FromAddress, headers.FromName));
        message.To.Add(Parse(headers.ToAddress, name: null));

        if (headers.ReplyToAddress is not null)
        {
            message.ReplyTo.Add(Parse(headers.ReplyToAddress, name: null));
        }

        if (headers.BccAddress is not null)
        {
            message.Bcc.Add(Parse(headers.BccAddress, name: null));
        }

        message.Subject = mail.Subject;

        // **自動生成であることを明示する**（RFC 3834）。不在通知との往復を止める
        message.Headers.Add(AutoSubmittedHeader, "auto-generated");

        message.Body = new TextPart(TextFormat.Plain) { Text = mail.Body };

        return message;
    }

    private static MailboxAddress Parse(string address, string? name)
    {
        try
        {
            return string.IsNullOrWhiteSpace(name)
                ? MailboxAddress.Parse(address)
                : new MailboxAddress(name, address);
        }
        catch (ParseException exception)
        {
            // ⚠️ **アドレスそのものを文言へ入れない。** この文言はデッドレターに残る
            throw new MailDeliveryException(
                "メールアドレスの形が正しくない", isTransient: false, exception);
        }
    }

    private static void ValidateAddress(string address, string role)
    {
        // ⚠️ **MimeKit の解析は緩く、`@` が無い文字列も通す。**
        if (!MailAddress.TryCreate(address, out _))
        {
            throw new MailDeliveryException(
                $"{role}のアドレスの形が正しくない", isTransient: false);
        }
    }

    internal static void ValidateHeaders(ResolvedMailHeaders headers)
    {
        ValidateAddress(headers.FromAddress, "差出人");
        ValidateAddress(headers.ToAddress, "宛先");
        if (headers.ReplyToAddress is not null)
        {
            ValidateAddress(headers.ReplyToAddress, "返信先");
        }

        if (headers.BccAddress is not null)
        {
            ValidateAddress(headers.BccAddress, "BCC");
        }
    }
}

/// <summary>全体設定へのフォールバックを済ませたメールヘッダ。</summary>
public sealed record ResolvedMailHeaders(
    string FromAddress,
    string? FromName,
    string ToAddress,
    string? ReplyToAddress,
    string? BccAddress);
