namespace VehicleVision.PleasanterTools.CalendarFeed.Admin;

/// <summary>管理者。<b>パスワードのハッシュ・TOTP の共有鍵は含まない。</b></summary>
public sealed record AdminUser(
    string Id,
    string LoginId,
    string Role,
    bool IsDisabled,
    int FailedLoginCount,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    bool TotpEnabled,
    string Language)
{
    /// <summary>締め出し中か。</summary>
    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;
}

/// <summary>役割。認可はこれだけで判定する（Pleasanter のグループ・組織では判定しない）。</summary>
public static class AdminRoles
{
    /// <summary>すべてを許す。</summary>
    public const string Administrator = "Administrator";
}

/// <summary>サーバ側セッションの段階。</summary>
public enum AdminSessionKind
{
    /// <summary>パスワードは通ったが、2 要素がまだ。<b>何も操作させない。</b>（5 分）</summary>
    Pending,

    /// <summary>ログイン済み。（8 時間）</summary>
    Session,
}

/// <summary>サーバ側のセッション（DB には ID のハッシュだけを持つ）。</summary>
public sealed record AdminSession(string UserId, AdminSessionKind Kind, DateTimeOffset ExpiresAt);

/// <summary>ログイン失敗の記録の結果。</summary>
/// <param name="FailedCount">いまの連続失敗の回数（締め出したら 0 に戻す）。</param>
/// <param name="LockedUntil">締め出したなら、その解除の時刻。</param>
public sealed record AdminFailure(int FailedCount, DateTimeOffset? LockedUntil);

/// <summary>管理者の保管先。</summary>
public interface IAdminUserStore
{
    /// <summary>管理者の人数。</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// **最初の管理者を作る。管理者が 1 人も居ないときだけ**成功する（原子的に 1 回だけ）。
    /// すでに居れば <c>null</c>。
    /// </summary>
    Task<AdminUser?> CreateFirstAsync(
        string loginId, string passwordHash, string language, CancellationToken cancellationToken = default);

    /// <summary>ログイン ID（大文字小文字を区別しない）から引く。パスワードのハッシュも返す。</summary>
    Task<(AdminUser User, string PasswordHash)?> FindForLoginAsync(
        string loginId, CancellationToken cancellationToken = default);

    /// <summary>ID から引く。</summary>
    Task<AdminUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// ログイン失敗を記録する。<paramref name="threshold"/> 回目で <paramref name="lockout"/> の間だけ締め出す。
    /// **締め出しは DB に持つ**（プロセス内の記憶だと、インスタンスの数だけ試せる）。
    /// </summary>
    Task<AdminFailure> RecordFailureAsync(
        string id, int threshold, TimeSpan lockout, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>ログイン成功を記録する（失敗の回数を戻し、最終ログインを残す）。</summary>
    Task RecordSuccessAsync(string id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>パスワードのハッシュを差し替える。</summary>
    Task SetPasswordHashAsync(string id, string passwordHash, CancellationToken cancellationToken = default);

    /// <summary>TOTP の共有鍵を復号して返す。未登録なら <c>null</c>。</summary>
    Task<string?> GetTotpSecretAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>登録の途中の共有鍵を置く（<see cref="EnableTotpAsync"/> で確定するまで有効にならない）。</summary>
    Task SetTotpPendingAsync(string id, string secret, CancellationToken cancellationToken = default);

    /// <summary>登録の途中の共有鍵を復号して返す。無ければ <c>null</c>。</summary>
    Task<string?> GetTotpPendingAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// TOTP を有効にする。共有鍵を確定し、**前の復旧コードを消して新しいもの（ハッシュ）に置き換える。**
    /// </summary>
    Task EnableTotpAsync(
        string id,
        string secret,
        long timeStep,
        IReadOnlyList<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default);

    /// <summary>TOTP を解除する（共有鍵と復旧コードを消す）。</summary>
    Task DisableTotpAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// **前回より新しい時間枠のときだけ**使用済みにして <c>true</c>。
    /// 同じ枠の数字は二度通さない（読んでから書くと、同時に来た 2 つが両方通る）。
    /// </summary>
    Task<bool> TryUseTotpStepAsync(string id, long timeStep, CancellationToken cancellationToken = default);

    /// <summary>復旧コードを使い切りにして <c>true</c>。**使用済みの印を条件に含めた UPDATE の件数で判断する。**</summary>
    Task<bool> TryUseRecoveryCodeAsync(
        string id, string codeHash, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>未使用の復旧コードの本数。</summary>
    Task<int> CountRecoveryCodesAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>サーバ側セッションの保管先。</summary>
public interface IAdminSessionStore
{
    /// <summary>セッションを作り、cookie に載せる ID を返す。**ID は推測できない 256 bit。DB には SHA-256 だけを保管する。**</summary>
    Task<string> CreateAsync(
        string userId,
        AdminSessionKind kind,
        TimeSpan lifetime,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>ID からセッションを引く。無い・期限切れなら <c>null</c>。</summary>
    Task<AdminSession?> FindAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>セッションを消す。</summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 利用者のセッションを消す。<paramref name="exceptSessionId"/> があればそれだけ残す
    /// （パスワード変更で、いまの端末以外を失効させる）。
    /// </summary>
    Task DeleteForUserAsync(
        string userId, string? exceptSessionId = null, CancellationToken cancellationToken = default);

    /// <summary>期限切れを消す。</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
