namespace VehicleVision.PleasanterTools.CalendarFeed.Localization;

/// <summary>
/// 設定の値の検証に落ちた例外。<b>文言の鍵と埋め込みの値を持つ</b>ので、管理 API が要求の言語で返せる。
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> は日本語（ログ・起動時の失敗の表示用。ログは日本語の規約）。
/// 画面へ返すときは <see cref="MessageKey"/> と <see cref="Args"/> から、要求の言語で組み立てる。
/// </remarks>
public sealed class SettingException : InvalidOperationException
{
    public SettingException(string messageKey, object[] args, Exception? innerException = null)
        : base(ServerMessages.Get(messageKey, "ja", args), innerException)
    {
        MessageKey = messageKey;
        Args = args;
    }

    /// <summary>文言の鍵（<see cref="ServerMessageKeys"/>）。</summary>
    public string MessageKey { get; }

    /// <summary>埋め込みの値（<c>{0}</c> から順）。</summary>
    public object[] Args { get; }
}
