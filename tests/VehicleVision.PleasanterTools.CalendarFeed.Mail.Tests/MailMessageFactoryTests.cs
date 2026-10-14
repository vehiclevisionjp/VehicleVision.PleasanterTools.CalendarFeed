using MimeKit;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail.Tests;

public class MailMessageFactoryTests
{
    private static readonly MailOptions Options = new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        FromAddress = "noreply@example.test",
        FromName = "アンケート",
    };

    private static MimeMessage Build(OutgoingMail mail) => MailMessageFactory.Build(Options, mail);

    private static OutgoingMail Sample(string subject = "件名", string body = "本文") =>
        new("respondent@example.test", subject, body);

    [Fact]
    public void 差出人と宛先を載せる()
    {
        var message = Build(Sample());

        Assert.Equal("noreply@example.test", Assert.IsType<MailboxAddress>(message.From.Single()).Address);
        Assert.Equal("アンケート", Assert.IsType<MailboxAddress>(message.From.Single()).Name);
        Assert.Equal("respondent@example.test", Assert.IsType<MailboxAddress>(message.To.Single()).Address);
    }

    [Fact]
    public void 自動生成であることを必ず示す()
    {
        // ⚠️ **不在通知との往復を止める**（RFC 3834）
        var message = Build(Sample());

        Assert.Equal("auto-generated", message.Headers[MailMessageFactory.AutoSubmittedHeader]);
    }

    [Fact]
    public void 本文は平文にする()
    {
        // **HTML にしない。** 管理者の文面と回答者の回答がそのまま入る
        var body = Assert.IsType<TextPart>(Build(Sample(body: "こんにちは")).Body);

        Assert.True(body.IsPlain);
        Assert.Equal("こんにちは", body.Text);
    }

    [Fact]
    public void 返信先は設定したときだけ載せる()
    {
        Assert.Empty(Build(Sample()).ReplyTo);

        var message = MailMessageFactory.Build(
            Options with { ReplyToAddress = "toiawase@example.test" }, Sample());

        Assert.Equal("toiawase@example.test", Assert.IsType<MailboxAddress>(message.ReplyTo.Single()).Address);
    }

    [Fact]
    public void 完了メールのヘッダ指定は全体設定より優先する()
    {
        var message = MailMessageFactory.Build(
            Options with { ReplyToAddress = "default@example.test" },
            Sample() with
            {
                FromName = "満足度調査事務局",
                ReplyToAddress = "survey@example.test",
                BccAddress = "archive@example.test",
            });

        var from = Assert.IsType<MailboxAddress>(message.From.Single());
        Assert.Equal("noreply@example.test", from.Address);
        Assert.Equal("満足度調査事務局", from.Name);
        Assert.Equal(
            "survey@example.test",
            Assert.IsType<MailboxAddress>(message.ReplyTo.Single()).Address);
        Assert.Equal(
            "archive@example.test",
            Assert.IsType<MailboxAddress>(message.Bcc.Single()).Address);
    }

    [Fact]
    public void 未設定のヘッダは全体設定へ戻りBCCは付けない()
    {
        var message = MailMessageFactory.Build(
            Options with { ReplyToAddress = "default@example.test" },
            Sample());

        Assert.Equal("アンケート", Assert.IsType<MailboxAddress>(message.From.Single()).Name);
        Assert.Equal(
            "default@example.test",
            Assert.IsType<MailboxAddress>(message.ReplyTo.Single()).Address);
        Assert.Empty(message.Bcc);
    }

    [Theory]
    [InlineData("FromName")]
    [InlineData("ReplyToAddress")]
    [InlineData("BccAddress")]
    public void 利用者が指定するヘッダの改行は弾く(string property)
    {
        var mail = property switch
        {
            "FromName" => Sample() with { FromName = "事務局\r\nBcc: injected@example.test" },
            "ReplyToAddress" => Sample() with
            {
                ReplyToAddress = "reply@example.test\r\nBcc: injected@example.test",
            },
            _ => Sample() with { BccAddress = "bcc@example.test\r\nTo: injected@example.test" },
        };

        Assert.Throws<ArgumentException>(() => Build(mail));
    }

    [Theory]
    [InlineData("ReplyToAddress")]
    [InlineData("BccAddress")]
    public void 返信先とBCCの形が壊れていれば恒久の失敗にする(string property)
    {
        var mail = property == "ReplyToAddress"
            ? Sample() with { ReplyToAddress = "返信先ではない" }
            : Sample() with { BccAddress = "BCCではない" };

        Assert.False(Assert.Throws<MailDeliveryException>(() => Build(mail)).IsTransient);
    }

    [Fact]
    public void 件名の改行は弾く()
    {
        // **ヘッダを 1 本増やされない**（メールヘッダインジェクション）
        Assert.Throws<ArgumentException>(() => Build(Sample(subject: "件名\r\nBcc: dare@example.test")));
    }

    [Fact]
    public void 宛先の形が壊れていれば恒久の失敗にする()
    {
        // **再送しても直らない。** 一時の失敗にすると延々と送り直すことになる
        var exception = Assert.Throws<MailDeliveryException>(() =>
            Build(new OutgoingMail("これはアドレスではない", "件名", "本文")));

        Assert.False(exception.IsTransient);
    }

    [Fact]
    public void 失敗の文言にアドレスを入れない()
    {
        // ⚠️ **この文言はデッドレターの行と管理画面に残る**
        var exception = Assert.Throws<MailDeliveryException>(() =>
            Build(new OutgoingMail("koreha-address-dehanai", "件名", "本文")));

        Assert.DoesNotContain("koreha-address-dehanai", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 件名と本文はそのまま載る()
    {
        var message = Build(Sample(subject: "ご回答ありがとうございました"));

        Assert.Equal("ご回答ありがとうございました", message.Subject);
    }
}
