namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>送る 1 通。</summary>
/// <remarks>
/// <para>
/// **本文は平文（<c>text/plain</c>）だけにする**（Issue #189）。
/// 本文には**管理者が書いた文面と、回答者自身の回答**が入る。HTML にすると、
/// 逃がし漏れが即そのまま差し込みになる経路を自分で作ることになる。
/// **書式が要るようになったら、そのときに改めて決める。**
/// </para>
/// <para>
/// ⚠️ **宛先はログへ出さない。** 回答者のメールアドレスは、
/// 完全匿名の前提で扱う中でも特に秘密度が高い（<c>_documents/非機能設計.md</c>）。
/// </para>
/// </remarks>
/// <param name="ToAddress">宛先。**1 通 1 宛先。** 複数宛先を作らない（誰に届いたかを混ぜない）。</param>
/// <param name="Subject">件名。**改行を含めない**（<see cref="Validate"/> で弾く）。</param>
/// <param name="Body">本文。平文。</param>
/// <param name="FromName">差出人の表示名。未設定なら全体設定を使う。</param>
/// <param name="ReplyToAddress">返信先。未設定なら全体設定を使う。</param>
/// <param name="BccAddress">BCC。未設定なら付けない。</param>
public sealed record OutgoingMail(
    string ToAddress,
    string Subject,
    string Body,
    string? FromName = null,
    string? ReplyToAddress = null,
    string? BccAddress = null)
{
    /// <summary>送る前に形を確かめる。**おかしければ例外。**</summary>
    /// <remarks>
    /// **件名に改行を通さない。** ヘッダの途中で改行を差し込まれると、
    /// ヘッダを 1 本増やせてしまう（メールヘッダインジェクション）。
    /// MailKit 側でも弾かれるが、**届く前に、こちらの言葉で落とす。**
    /// </remarks>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ToAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(Subject);
        ArgumentNullException.ThrowIfNull(Body);

        if (Subject.AsSpan().ContainsAny('\r', '\n'))
        {
            throw new ArgumentException("件名に改行は含められない", nameof(Subject));
        }

        if (ToAddress.AsSpan().ContainsAny('\r', '\n'))
        {
            throw new ArgumentException("宛先に改行は含められない", nameof(ToAddress));
        }

        ThrowIfContainsNewLine(FromName, nameof(FromName), "差出人の表示名");
        ThrowIfContainsNewLine(ReplyToAddress, nameof(ReplyToAddress), "返信先");
        ThrowIfContainsNewLine(BccAddress, nameof(BccAddress), "BCC");
    }

    private static void ThrowIfContainsNewLine(string? value, string parameterName, string role)
    {
        if (value is not null && value.AsSpan().ContainsAny('\r', '\n'))
        {
            throw new ArgumentException($"{role}に改行は含められない", parameterName);
        }
    }
}
