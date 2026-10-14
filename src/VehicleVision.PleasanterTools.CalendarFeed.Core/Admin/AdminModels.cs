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

    /// <summary>
    /// 長い間使われていないか（棚卸し用）。**招待中・無効のアカウントは対象にしない**（使っていないのではなく、使えない）。
    /// 一度もログインしていなければ、作られた日から数える。
    /// </summary>
    public bool IsStale(DateTimeOffset now, TimeSpan threshold, bool invitationPending) =>
        !invitationPending && !IsDisabled && now - (LastLoginAt ?? CreatedAt) > threshold;
}

/// <summary>役割。**認可は本アプリの役割だけで行う**（Pleasanter のグループ・組織では判定しない）。権限の割り当ては <see cref="AdminPermissions"/>。</summary>
/// <remarks>Questionnaire の役割（Administrator / SurveyAdministrator / UserAdministrator / Editor / Auditor）に則る。DB には名前で入る。</remarks>
public static class AdminRoles
{
    /// <summary>特権管理者。すべてを許す。</summary>
    public const string Administrator = "Administrator";

    /// <summary>フィード管理者。フィードの運用を任せる相手。<b>人には触れない。</b></summary>
    public const string FeedAdministrator = "FeedAdministrator";

    /// <summary>ユーザ管理者。人の出入りと、その記録。<b>フィードには触れない。</b></summary>
    public const string UserAdministrator = "UserAdministrator";

    /// <summary>フィード編集者。作るところまで。<b>失効はできない。</b></summary>
    public const string Editor = "Editor";

    /// <summary>監査担当。見るだけ。</summary>
    public const string Auditor = "Auditor";

    /// <summary>すべての役割。</summary>
    public static readonly IReadOnlyList<string> All =
        [Administrator, FeedAdministrator, UserAdministrator, Editor, Auditor];

    /// <summary>知っている役割か。</summary>
    public static bool IsValid(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}

/// <summary>管理者の変更の結果。</summary>
public enum AdminChangeResult
{
    /// <summary>変えた。</summary>
    Updated,

    /// <summary>その管理者が居ない。</summary>
    NotFound,

    /// <summary><b>有効な Administrator が 0 人になるので断った。</b>締め出しを防ぐ。</summary>
    LastAdministrator,
}

/// <summary>ログイン中の端末 1 つ分（サーバ側セッション）。</summary>
/// <param name="Handle">端末を指す識別子（セッション ID の SHA-256。cookie の現物ではない）。</param>
public sealed record AdminSessionInfo(
    string Handle,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? IpAddress,
    string? UserAgent);

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

    /// <summary>管理者の一覧（作成の古い順）。</summary>
    Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 管理者を作る（招待用）。<paramref name="passwordHash"/> には**誰も知らない値**のハッシュを渡す
    /// （既定のパスワードを配らないための埋め草。資格情報ではない）。ログイン ID がすでにあれば <c>null</c>。
    /// </summary>
    Task<AdminUser?> CreateAsync(
        string loginId,
        string passwordHash,
        string role,
        string language,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 無効化・有効化。**最後の有効な Administrator は無効化できない。**
    /// 「有効な Administrator」は、無効でなく、<b>一度でもログインした</b>人（招待しただけの人は、誰も知らないパスワードしか持たないので数えない）。
    /// </summary>
    Task<AdminChangeResult> SetDisabledAsync(string id, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>役割を変える。**最後の有効な Administrator は降格できない。**</summary>
    Task<AdminChangeResult> SetRoleAsync(string id, string role, CancellationToken cancellationToken = default);

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
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null);

    /// <summary>セッション ID から端末を指す識別子を作る（一覧に出してよい値。cookie の現物は作れない）。</summary>
    string HandleOf(string sessionId);

    /// <summary>ログイン中（<c>Session</c>・期限内）の端末の一覧。新しい順。</summary>
    Task<IReadOnlyList<AdminSessionInfo>> ListForUserAsync(
        string userId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>端末を 1 つ失効させる。<b>持ち主の組で消す</b>（他人の端末の識別子を渡されても消えない）。</summary>
    Task<bool> DeleteByHandleAsync(string userId, string handle, CancellationToken cancellationToken = default);

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
