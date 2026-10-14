namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>メールを送れなかった。</summary>
/// <remarks>
/// <para>
/// **後で送り直せる失敗かどうかを、送信経路の側で判定してここへ載せる。**
/// 呼ぶ側（送信ワーカー）が SMTP の応答コードを解釈しなくて済むようにする。
/// </para>
/// <para>
/// ⚠️ **メッセージに宛先を入れないこと。** この文言はデッドレターの行と管理画面に残る。
/// </para>
/// </remarks>
/// <param name="message">失敗の理由。**宛先と本文を入れない。**</param>
/// <param name="isTransient">
/// 時間を置けば通る見込みがあるか。
/// <list type="bullet">
/// <item><c>true</c>: 繋がらない・時間切れ・4xx の一時拒否。**再送する**</item>
/// <item><c>false</c>: 宛先が無い・認証が通らない・5xx の恒久拒否。**再送しても同じ**</item>
/// </list>
/// </param>
/// <param name="innerException">元の失敗。</param>
public sealed class MailDeliveryException(
    string message,
    bool isTransient,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>時間を置けば通る見込みがあるか。</summary>
    public bool IsTransient { get; } = isTransient;
}
