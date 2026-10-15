namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

/// <summary>
/// テンプレートの書き方（<c>{列名:書式}</c>）が不正なときの例外。<b>文言の鍵と埋め込みの値を持つ</b>ので、要求の言語で返せる。
/// </summary>
/// <remarks><see cref="Exception.Message"/> は日本語（ログ用）。</remarks>
public sealed class TemplateFormatException : FormatException
{
    public TemplateFormatException(string messageKey, params object[] args)
        : base(ServerMessages.Get(messageKey, "ja", args))
    {
        MessageKey = messageKey;
        Args = args;
    }

    /// <summary>文言の鍵（<see cref="ServerMessageKeys"/>）。</summary>
    public string MessageKey { get; }

    /// <summary>埋め込みの値（<c>{0}</c> から順）。</summary>
    public object[] Args { get; }
}
