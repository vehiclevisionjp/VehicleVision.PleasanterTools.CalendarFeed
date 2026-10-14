using System.Net;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Azure;
using Azure.Identity;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail.Tests;

/// <summary>HTTPS の送信経路の、失敗の切り分け（Issue #198）。</summary>
/// <remarks>
/// <para>
/// **ここで見たいのは「再送する価値があるか」の判定だけ。**
/// ⚠️ **倒す先を間違えると、直らない失敗を延々と再送し続けるか、
/// 直る失敗を捨てるかのどちらかになる。**
/// </para>
/// <para>
/// ⚠️ **実機（AWS / Azure）では未検証。** 資格情報を要するため、
/// **手元でも CI でも当てられない**（<c>_documents/導入-更新運用手順書.md</c> に明記）。
/// </para>
/// </remarks>
public class CloudMailTransportTests
{
    // ---- Amazon SES ---------------------------------------------------------

    [Fact]
    public void SESの流量の上限は一時の失敗にする()
    {
        // **間を置けば通る。** 捨ててはいけない
        Assert.True(SesMailTransport.Classify(new TooManyRequestsException("多すぎる")).IsTransient);
    }

    [Fact]
    public void SESのドメイン未検証は恒久の失敗にする()
    {
        // **人が直すまで通らない。** 再送しても同じ
        Assert.False(
            SesMailTransport.Classify(new MailFromDomainNotVerifiedException("未検証")).IsTransient);
    }

    [Fact]
    public void SESの停止は恒久の失敗にする()
    {
        Assert.False(SesMailTransport.Classify(new AccountSuspendedException("停止")).IsTransient);
        Assert.False(SesMailTransport.Classify(new SendingPausedException("停止")).IsTransient);
    }

    [Fact]
    public void SESの5xxは一時の失敗にする()
    {
        var exception = new AmazonSimpleEmailServiceV2Exception("向こうの不調")
        {
            StatusCode = HttpStatusCode.InternalServerError,
        };

        Assert.True(SesMailTransport.Classify(exception).IsTransient);
    }

    [Fact]
    public void SESの4xxは恒久の失敗にする()
    {
        var exception = new AmazonSimpleEmailServiceV2Exception("受け付けない")
        {
            StatusCode = HttpStatusCode.BadRequest,
        };

        Assert.False(SesMailTransport.Classify(exception).IsTransient);
    }

    [Fact]
    public void SESへ繋がらないのは一時の失敗にする()
    {
        Assert.True(SesMailTransport.Classify(new HttpRequestException("繋がらない")).IsTransient);
    }

    [Fact]
    public void SESの失敗の文言に宛先を入れない()
    {
        // ⚠️ **この文言はデッドレターの行と管理画面に残る**
        var message = SesMailTransport.Classify(new TooManyRequestsException("多すぎる")).Message;

        Assert.DoesNotContain("@", message, StringComparison.Ordinal);
    }

    // ---- Azure Communication Services ---------------------------------------

    [Fact]
    public void ACSの流量の上限は一時の失敗にする()
    {
        // ⚠️ **ACS の既定の上限はとくに低い。** 必ず送り直す側へ倒す
        Assert.True(
            AcsMailTransport.Classify(new RequestFailedException(429, "多すぎる")).IsTransient);
    }

    [Fact]
    public void ACSの認証の失敗は恒久にする()
    {
        // **マネージド ID の割り当て漏れは人が直す**
        Assert.False(
            AcsMailTransport.Classify(new RequestFailedException(403, "権限が無い")).IsTransient);
        Assert.False(
            AcsMailTransport.Classify(new AuthenticationFailedException("取れない")).IsTransient);
    }

    [Fact]
    public void ACSの5xxは一時の失敗にする()
    {
        Assert.True(
            AcsMailTransport.Classify(new RequestFailedException(503, "落ちている")).IsTransient);
    }

    [Fact]
    public void ACSの4xxは恒久の失敗にする()
    {
        Assert.False(
            AcsMailTransport.Classify(new RequestFailedException(400, "受け付けない")).IsTransient);
    }

    [Fact]
    public void ACSへ繋がらないのは一時の失敗にする()
    {
        Assert.True(AcsMailTransport.Classify(new HttpRequestException("繋がらない")).IsTransient);
    }

    [Fact]
    public void 分からない失敗は一時にする()
    {
        // **分からないものは、まず送り直す。** 捨てる側へ倒さない
        Assert.True(SesMailTransport.Classify(new InvalidOperationException("不明")).IsTransient);
        Assert.True(AcsMailTransport.Classify(new InvalidOperationException("不明")).IsTransient);
    }
}
