namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>送信待ちに置く 1 通を、暗号化して守る口。</summary>
/// <remarks>
/// <para>
/// ⚠️ **回答者のメールアドレスと、回答の写しが入る。**
/// 本アプリは完全匿名を前提にしているのに、自動返信のために**一時的とはいえ
/// 個人を指す値を DB へ置くことになる**（Issue #189）。**平文で置かない。**
/// </para>
/// <para>
/// **実体は <c>.Web</c> 側**（TOTP の共有鍵と同じ鍵・同じ仕掛けを使う。
/// <c>_documents/データモデル設計.md</c> 2.6）。ここは口だけを持ち、
/// <c>.Worker</c> が鍵の扱いを知らずに済むようにする。
/// </para>
/// <para>
/// **送れたら行ごと消す**ので、残り続けるのは送れていない間だけ。
/// </para>
/// </remarks>
public interface IMailPayloadProtector
{
    /// <summary>暗号化して、保存する形にする。</summary>
    string Protect(OutgoingMail mail);

    /// <summary>戻す。**鍵が違う・壊れている・読めないなら <c>null</c>。**</summary>
    OutgoingMail? Unprotect(string protectedPayload);
}
