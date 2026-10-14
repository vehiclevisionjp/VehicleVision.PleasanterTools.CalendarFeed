using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Mail;

/// <summary>送信待ちの行へ入れる形と、そこから戻す形。</summary>
/// <remarks>
/// **1 通ぶんを 1 つの文字列にまとめる。** 宛先・件名・本文を別々の列に持つと、
/// **暗号化の掛け忘れが列ごとに起き得る**（<see cref="IMailPayloadProtector"/>）。
/// まとめておけば、守るものが 1 つで済む。
/// </remarks>
public static class MailPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>1 通ぶんを文字列にする。</summary>
    public static string ToJson(OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        return JsonSerializer.Serialize(mail, Options);
    }

    /// <summary>文字列から戻す。**壊れていれば <c>null</c>。**</summary>
    /// <remarks>
    /// **例外にしない。** 読めない行は送れないので、呼ぶ側がデッドレターへ回す。
    /// </remarks>
    public static OutgoingMail? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var mail = JsonSerializer.Deserialize<OutgoingMail>(json, Options);
            return mail is null || string.IsNullOrWhiteSpace(mail.ToAddress) ? null : mail;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
