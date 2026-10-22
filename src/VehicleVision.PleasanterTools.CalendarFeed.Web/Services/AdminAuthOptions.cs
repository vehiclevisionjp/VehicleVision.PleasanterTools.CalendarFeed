using VehicleVision.PleasanterTools.CalendarFeed.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>2 要素の方針。</summary>
/// <remarks>Questionnaire の <c>QUESTIONNAIRE_ADMIN_TWOFACTOR</c> と同じ（既定は <see cref="Optional"/>）。</remarks>
public enum TwoFactorMode
{
    /// <summary>**登録するまでログインが完了しない。** 登録済みなら毎回求める。自分では解除できない。</summary>
    Required,

    /// <summary>未登録ならそのまま入れる。登録済みなら毎回求める。自分で解除できる（パスワードの再確認つき）。</summary>
    Optional,

    /// <summary>未登録ならそのまま入れ、**登録の口を閉じる**。⚠️ **登録済みの人からは外さない**（設定 1 つで既存の保護が消えるのは危ない）。</summary>
    Disabled,
}

/// <summary>管理者認証の設定。</summary>
public sealed record AdminAuthOptions(TwoFactorMode TwoFactor, AdminPasswordPolicy PasswordPolicy)
{
    /// <summary>連続して何回失敗したら締め出すか。</summary>
    public int LockoutThreshold { get; init; } = 5;

    /// <summary>締め出しの長さ。**恒久的には締め出さない**（正規の利用者を狙って締め出す嫌がらせが成立する）。</summary>
    public TimeSpan Lockout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>パスワードは通ったが 2 要素がまだ、の段階の有効期間。この間は何も操作させない。</summary>
    public TimeSpan PendingLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>ログイン後にパスワードを再確認した、2 要素の登録途中の有効期間。</summary>
    public TimeSpan ReenrollLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>ログイン済みセッションの有効期間。</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);

    /// <summary>招待の有効期間（既定 48 時間。Questionnaire と同じ）。**短すぎると届く前に切れ、長すぎると URL が残る。** 切れたら出し直す。</summary>
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromHours(48);

    /// <summary>棚卸しで「長期間使われていない」とみなす日数。</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(90);

    /// <summary>認証アプリに出す発行元の名前。</summary>
    public string Issuer { get; init; } = "CalendarFeed";
}

/// <summary>指定の権限を持つ役割だけを通す。<b>ログイン済みの確認（<see cref="AdminSessionFilter"/>）の後ろに付ける。</b></summary>
/// <remarks>
/// **認可は本アプリの役割だけで行う**（Pleasanter のグループ・組織では判定しない）。権限は役割から、その都度対応表を引く
/// （cookie へ焼かない。焼くと、役割を変えても再ログインまで効かない）。
/// </remarks>
public sealed class RequirePermissionFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        AdminPermissions.Has(AdminContext.Of(context.HttpContext).User.Role, permission)
            ? await next(context).ConfigureAwait(false)
            : ApiError.Of(StatusCodes.Status403Forbidden, ServerMessageKeys.PermissionRequired, permission);

    /// <summary>エンドポイントへ付けるフィルタを作る。</summary>
    public static RequirePermissionFilter Of(string permission) => new(permission);
}

/// <summary>認証済みの管理者（エンドポイントフィルタが <c>HttpContext.Items</c> へ置く）。</summary>
public sealed record AdminContext(AdminUser User, string SessionId, string Kind)
{
    public const string ItemKey = "calendarfeed.admin";

    public static AdminContext Of(HttpContext http) => (AdminContext)http.Items[ItemKey]!;
}
