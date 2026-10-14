namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>メールを 1 通送る口。</summary>
/// <remarks>
/// <para>
/// **実体は差し替えられる**（Issue #189）。今は SMTP（<see cref="SmtpMailTransport"/>）だけ。
/// マネージド ID で秘密を持たない HTTPS の経路は Issue #198 で足す。
/// </para>
/// <para>
/// **再送はここでしない。** 送信待ちの表とワーカーが持つ
/// （回答の送信と同じ構造。<c>_documents/アーキテクチャ方針.md</c> 10 章）。
/// **ここは「1 回試して、駄目なら理由を付けて投げる」だけ。**
/// </para>
/// </remarks>
public interface IMailTransport
{
    /// <summary>1 通送る。</summary>
    /// <exception cref="MailDeliveryException">
    /// 送れなかった。**再送する価値があるかは <see cref="MailDeliveryException.IsTransient"/> で判る。**
    /// </exception>
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default);
}
