using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>
/// CalendarFeed の <see cref="ISecretProtector"/> を、Core.Data の <see cref="ISecretCipher"/> に合わせる。
/// 暗号の実装と鍵の管理は CalendarFeed に残し、Core.Data（管理者の TOTP の共有鍵）へは窓口だけを渡す。
/// </summary>
/// <remarks>暗号化の文脈（<c>calendarfeed:totp:{id}</c>）は Core.Data が組んで渡す。ここでは何も足さない。</remarks>
public sealed class SecretCipherAdapter(ISecretProtector protector) : ISecretCipher
{
    public string Protect(string plaintext, string context) => protector.Protect(plaintext, context);

    public string Unprotect(string protectedValue, string context) => protector.Unprotect(protectedValue, context);
}
