using VehicleVision.PleasanterTools.Core.Web;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>
/// Pleasanter のログインの本人確認（Core.Web の <see cref="PleasanterSessionVerifier"/>）へ、
/// 本アプリの Pleasanter 接続設定（URL と API キー）を渡す。
/// </summary>
/// <remarks>
/// <para>
/// **使うたびに <see cref="RuntimeSettings.Current"/> を読む**（管理画面からの保存や、他のインスタンスの保存が再起動なしで効くように）。
/// 置き換える前の検証器（Issue #137 まで本アプリにあったもの）と同じ読み方。
/// </para>
/// <para>
/// URL と API キーの検証（空・http(s) でない URL を「キー無し」と扱う）は Core.Web 側が行う。ここでは値をそのまま渡す。
/// </para>
/// </remarks>
public sealed class RuntimePleasanterApiConnectionSource(RuntimeSettings runtime) : IPleasanterApiConnectionSource
{
    public Task<PleasanterApiConnectionSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var pleasanter = runtime.Current.Pleasanter;
        return Task.FromResult(new PleasanterApiConnectionSettings(pleasanter.BaseUrl, pleasanter.ApiKey));
    }
}
