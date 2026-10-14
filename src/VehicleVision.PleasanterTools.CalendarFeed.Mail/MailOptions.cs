using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>SMTP の暗号化の掛け方。</summary>
public enum SmtpSecurity
{
    /// <summary>平文で繋いでから <c>STARTTLS</c> で上げる。**既定。587 番はこれ。**</summary>
    StartTls,

    /// <summary>最初から TLS で繋ぐ。**465 番はこれ。**</summary>
    ImplicitTls,

    /// <summary>暗号化しない。⚠️ **検証用。本番で使わない。**</summary>
    None,
}

/// <summary>メールの送信経路（Issue #198）。</summary>
/// <remarks>
/// <para>
/// **どれでも同じ 1 通が出る。** 違うのは**資格情報をどこに置くか**だけ。
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="Smtp"/></term>
///     <description>**既定。** どの送信元にも届く代わりに、**パスワードを 1 つ保管する**</description>
///   </item>
///   <item>
///     <term><see cref="AmazonSes"/></term>
///     <description>IAM ロール（マネージド ID）で通る。**保管する秘密は 0**</description>
///   </item>
///   <item>
///     <term><see cref="AzureCommunicationServices"/></term>
///     <description>Entra ID のマネージド ID で通る。**保管する秘密は 0**</description>
///   </item>
/// </list>
public enum MailTransportKind
{
    /// <summary>認証付き SMTP リレー。**既定。**</summary>
    Smtp,

    /// <summary>Amazon SES（HTTPS）。**IAM ロールで通る。**</summary>
    AmazonSes,

    /// <summary>Azure Communication Services（HTTPS）。**マネージド ID で通る。**</summary>
    AzureCommunicationServices,
}

/// <summary>メール送信の設定（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **既定は無効。** 設定しなければメールは 1 通も出ない。
/// インターネットへ出られないイントラでも動かせる状態を既定にする
/// （外部の CAPTCHA と同じ考え方。<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// <para>
/// **送信経路は SMTP だけ**（Issue #189 の決定）。**認証付きリレーの 587 番は
/// Azure でブロックされない**ので、SendGrid・Amazon SES・
/// Azure Communication Services・Microsoft 365 のどれにもこれで届く。
/// マネージド ID で秘密を持たない経路は Issue #198。
/// </para>
/// <para>
/// ⚠️ **パスワードを画面へ返さない。** 設定は環境変数で与え、管理画面から読めるようにしない。
/// </para>
/// </remarks>
public sealed record MailOptions
{
    /// <summary>メールを送るか。**既定は無効。**</summary>
    public bool Enabled { get; init; }

    /// <summary>使う送信経路（Issue #198）。**既定は SMTP。**</summary>
    public MailTransportKind Transport { get; init; } = MailTransportKind.Smtp;

    /// <summary>Amazon SES の地域（例 <c>ap-northeast-1</c>）。</summary>
    /// <remarks>
    /// ⚠️ **資格情報は設定に書かない。** AWS SDK の既定の探索順
    /// （App Service のマネージド ID → IAM ロール → 環境変数）に任せる。
    /// **鍵を設定へ書けるようにしない**のが、この経路を足した理由そのもの。
    /// </remarks>
    public string? SesRegion { get; init; }

    /// <summary>Azure Communication Services の窓口（例 <c>https://xxx.communication.azure.com</c>）。</summary>
    /// <remarks>
    /// ⚠️ **接続文字列を受け取らない。** Entra ID のマネージド ID
    /// （<c>DefaultAzureCredential</c>）だけを使う。**鍵を置ける口を作らない。**
    /// </remarks>
    public Uri? AcsEndpoint { get; init; }

    /// <summary>SMTP サーバ。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>ポート。**既定は 587**（認証付きリレー）。</summary>
    /// <remarks>
    /// ⚠️ **25 番を既定にしない。** Azure は 25 番の外向き接続を塞ぐ
    /// （サブスクリプション種別による）。**587 は塞がらない。**
    /// </remarks>
    public int Port { get; init; } = 587;

    /// <summary>暗号化の掛け方。</summary>
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;

    /// <summary>認証のユーザ名。**空なら認証しない。**</summary>
    public string? UserName { get; init; }

    /// <summary>認証のパスワード。**ログにも画面にも出さない。**</summary>
    public string? Password { get; init; }

    /// <summary>差出人のアドレス。</summary>
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>差出人の表示名。**空なら付けない。**</summary>
    public string? FromName { get; init; }

    /// <summary>返信先。**空なら差出人と同じ。**</summary>
    /// <remarks>
    /// 自動返信メールは**送りっぱなしにしない。** 回答者が返信できる先を運用側で決められること。
    /// </remarks>
    public string? ReplyToAddress { get; init; }

    /// <summary>1 通あたりの待ち時間の上限。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>メールに載せる URL の起点（例: <c>https://survey.example.jp</c>）。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **要求の <c>Host</c> ヘッダから組み立てない。** 受け取った名前で URL を作ると、
    /// **偽の宛先を載せたメールを、こちらの名前で送らせられる**
    /// （host header injection）。設定に無ければ**リンクを載せるメールを送らない。**
    /// </para>
    /// <para>
    /// **末尾の <c>/</c> は落として持つ**（<see cref="Link"/>）。
    /// </para>
    /// </remarks>
    public string? BaseUrl { get; init; }

    /// <summary>起点からの絶対 URL を作る。**起点が無ければ <c>null</c>。**</summary>
    /// <param name="path">先頭に <c>/</c> を付けた経路。</param>
    public string? Link(string path) =>
        string.IsNullOrWhiteSpace(BaseUrl) ? null : BaseUrl.TrimEnd('/') + path;

    /// <summary>送れる形になっているか。</summary>
    /// <remarks>**半端な設定で送り始めない。** 有効なのに欠けていれば、起動時に落とす。</remarks>
    public bool IsReady =>
        Enabled
        && !string.IsNullOrWhiteSpace(FromAddress)
        && Transport switch
        {
            MailTransportKind.Smtp =>
                !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535,
            MailTransportKind.AmazonSes => !string.IsNullOrWhiteSpace(SesRegion),
            MailTransportKind.AzureCommunicationServices => AcsEndpoint is not null,
            _ => false,
        };

    /// <summary>設定の接頭辞。</summary>
    public const string Prefix = "CALENDARFEED_MAIL_";

    /// <summary>設定から読む。</summary>
    /// <remarks>
    /// **読めない値を黙って既定へ落とさない**（<c>ResponseSenderOptions</c> と同じ）。
    /// 設定したつもりが効いていない状態を作らない。
    /// </remarks>
    public static MailOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ReadBool(configuration, Prefix + "ENABLED");
        if (!enabled)
        {
            // **無効なら他の値を読まない。** 使わない設定の不備で起動を止めない
            return new MailOptions();
        }

        var transport = ReadTransport(configuration, Prefix + "TRANSPORT");

        var options = new MailOptions
        {
            Enabled = true,
            Transport = transport,
            SesRegion = configuration[Prefix + "SES_REGION"],
            AcsEndpoint = ReadUri(configuration, Prefix + "ACS_ENDPOINT"),
            Host = configuration[Prefix + "SMTP_HOST"] ?? string.Empty,
            Port = ReadInt(configuration, Prefix + "SMTP_PORT", 587),
            Security = ReadSecurity(configuration, Prefix + "SMTP_SECURITY"),
            UserName = configuration[Prefix + "SMTP_USER"],
            Password = configuration[Prefix + "SMTP_PASSWORD"],
            FromAddress = configuration[Prefix + "FROM_ADDRESS"] ?? string.Empty,
            FromName = configuration[Prefix + "FROM_NAME"],
            ReplyToAddress = configuration[Prefix + "REPLYTO_ADDRESS"],
            BaseUrl = configuration[Prefix + "BASEURL"],
            Timeout = TimeSpan.FromSeconds(ReadInt(configuration, Prefix + "TIMEOUT_SECONDS", 30)),
        };

        if (options.Transport is MailTransportKind.Smtp
            && string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException(
                $"{Prefix}ENABLED が有効なのに {Prefix}SMTP_HOST が設定されていない");
        }

        if (options.Transport is MailTransportKind.AmazonSes
            && string.IsNullOrWhiteSpace(options.SesRegion))
        {
            throw new InvalidOperationException(
                $"{Prefix}TRANSPORT=AmazonSes のときは {Prefix}SES_REGION が要る"
                + "（例: ap-northeast-1）");
        }

        if (options.Transport is MailTransportKind.AzureCommunicationServices
            && options.AcsEndpoint is null)
        {
            throw new InvalidOperationException(
                $"{Prefix}TRANSPORT=AzureCommunicationServices のときは "
                + $"{Prefix}ACS_ENDPOINT が要る（例: https://xxx.communication.azure.com）");
        }

        if (string.IsNullOrWhiteSpace(options.FromAddress))
        {
            throw new InvalidOperationException(
                $"{Prefix}ENABLED が有効なのに {Prefix}FROM_ADDRESS が設定されていない");
        }

        if (options.Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"{Prefix}SMTP_PORT は 1〜65535 で指定する（今の値: {options.Port}）");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{Prefix}TIMEOUT_SECONDS は 1 以上で指定する");
        }

        if (!string.IsNullOrWhiteSpace(options.BaseUrl)
            && (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("http" or "https")))
        {
            // **半端な値でリンクを作らない。** 起動時に気付ける方がよい
            throw new InvalidOperationException(
                $"{Prefix}BASEURL は http:// か https:// から始まる絶対 URL で指定する"
                + $"（今の値: {options.BaseUrl}）");
        }

        return options;
    }

    private static bool ReadBool(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は true か false で指定する（今の値: {raw}）");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{key} は整数で指定する（今の値: {raw}）");
    }

    private static MailTransportKind ReadTransport(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MailTransportKind.Smtp;
        }

        return Enum.TryParse<MailTransportKind>(raw, ignoreCase: true, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は Smtp / AmazonSes / AzureCommunicationServices "
                + $"のいずれかで指定する（今の値: {raw}）");
    }

    private static Uri? ReadUri(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidOperationException(
                $"{key} は https の絶対 URL で指定する（今の値: {raw}）");
    }

    private static SmtpSecurity ReadSecurity(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SmtpSecurity.StartTls;
        }

        return Enum.TryParse<SmtpSecurity>(raw, ignoreCase: true, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は StartTls / ImplicitTls / None のいずれかで指定する（今の値: {raw}）");
    }
}
