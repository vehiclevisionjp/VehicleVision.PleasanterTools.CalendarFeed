using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Feeds;

/// <summary>予定を終日にするかの決め方。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AllDayMode
{
    /// <summary>**自動。** 開始が 0:00 で、終了が無いか 0:00 のレコードを終日にする。</summary>
    /// <remarks>0:00 ちょうどに始まり 0:00 ちょうどに終わる時間つきの予定は終日になる。気になるなら <see cref="Never"/>。</remarks>
    Auto,

    /// <summary>常に時間指定。日付だけのレコードも 0:00 開始の予定になる。</summary>
    Never,

    /// <summary>常に終日。時刻は捨てる。</summary>
    Always,
}
