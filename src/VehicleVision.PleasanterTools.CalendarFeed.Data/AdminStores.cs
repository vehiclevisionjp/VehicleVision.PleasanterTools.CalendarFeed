using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Feeds;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>管理者を RDBMS へ保管する。</summary>
/// <remarks>
/// <para>
/// **TOTP の共有鍵は <see cref="ISecretProtector"/> で暗号化して保管し、利用者 ID に束縛する。** 平文は置かない。
/// </para>
/// <para>
/// **競合する書き込みは、条件を含めた 1 文の UPDATE / INSERT の件数で判断する**
/// （読んでから書くと、同時に来た 2 つが両方通る）。
/// </para>
/// </remarks>
public sealed class AdminUserStore(IDbConnectionFactory connections, ISecretProtector protector) : IAdminUserStore
{
    private const string SelectColumns =
        "SELECT [Id], [LoginId], [Role], [IsDisabled], [FailedLoginCount], [LockedUntil], [LastLoginAt], [CreatedAt], "
        + "[TotpSecretCipher], [Language], [PasswordHash] FROM [AdminUsers]";

    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    private static string KeyOf(string loginId) => loginId.Trim().ToLowerInvariant();

    private static string TotpContext(string id) => $"calendarfeed:totp:{id}";

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            Sql("SELECT COUNT(*) FROM [AdminUsers]"), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AdminUser?> CreateFirstAsync(
        string loginId, string passwordHash, string language, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("D");

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // **印の主キー（1 行だけ）で排他する。** 先に入れた 1 つだけが通り、他は一意制約で落ちる
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [AdminSetup] ([Id]) VALUES (1)"), transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            // 一意制約で落ちた（すでに最初の管理者が居る）なら null。それ以外の失敗は握りつぶさない
            if (await IsSetUpAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            throw;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [AdminUsers] ([Id], [LoginId], [LoginIdKey], [PasswordHash], [Role], [IsDisabled], "
                + "[FailedLoginCount], [CreatedAt], [Language]) "
                + "VALUES (@Id, @LoginId, @LoginIdKey, @PasswordHash, @Role, 0, 0, @CreatedAt, @Language)"),
            new
            {
                Id = id,
                LoginId = loginId.Trim(),
                LoginIdKey = KeyOf(loginId),
                PasswordHash = passwordHash,
                Role = AdminRoles.Administrator,
                CreatedAt = DbTime.Format(DateTimeOffset.UtcNow),
                Language = FeedLanguages.Normalize(language),
            },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>印が立っているか（別の接続で読む。落ちた接続・トランザクションの外から見る）。</summary>
    private async Task<bool> IsSetUpAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            Sql("SELECT COUNT(*) FROM [AdminSetup]"), cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<(AdminUser User, string PasswordHash)?> FindForLoginAsync(
        string loginId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            Sql($"{SelectColumns} WHERE [LoginIdKey] = @Key"), new { Key = KeyOf(loginId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : (row.ToUser(), row.PasswordHash);
    }

    public async Task<AdminUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            Sql($"{SelectColumns} WHERE [Id] = @Id"), new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToUser();
    }

    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<UserRow>(new CommandDefinition(
            Sql($"{SelectColumns} ORDER BY [CreatedAt], [Id]"), cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToUser())];
    }

    public async Task<AdminUser?> CreateAsync(
        string loginId,
        string passwordHash,
        string role,
        string language,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("D");
        await using var connection = connections.Create();
        try
        {
            // ログイン ID の一意性は LoginIdKey の一意索引が守る
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [AdminUsers] ([Id], [LoginId], [LoginIdKey], [PasswordHash], [Role], [IsDisabled], "
                    + "[FailedLoginCount], [CreatedAt], [Language]) "
                    + "VALUES (@Id, @LoginId, @LoginIdKey, @PasswordHash, @Role, 0, 0, @CreatedAt, @Language)"),
                new
                {
                    Id = id,
                    LoginId = loginId.Trim(),
                    LoginIdKey = KeyOf(loginId),
                    PasswordHash = passwordHash,
                    Role = role,
                    CreatedAt = DbTime.Format(now),
                    Language = FeedLanguages.Normalize(language),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (System.Data.Common.DbException)
        {
            // 一意制約で落ちた（同じログイン ID がある）なら null。それ以外の失敗は握りつぶさない
            if (await FindForLoginAsync(loginId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            throw;
        }

        return await FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task<AdminChangeResult> SetDisabledAsync(
        string id, bool disabled, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, "[IsDisabled] = @Value", disabled ? 1 : 0, leavesAdministrator: disabled, cancellationToken);

    public Task<AdminChangeResult> SetRoleAsync(string id, string role, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, "[Role] = @Value", role, leavesAdministrator: role != AdminRoles.Administrator, cancellationToken);

    /// <summary>
    /// 1 文で変える。<paramref name="leavesAdministrator"/> のときは、**ほかに有効な Administrator が居る場合だけ**通す
    /// （同時に 2 人が互いを降格しても、最後の 1 人は残る）。
    /// </summary>
    /// <remarks>
    /// 件数を数える副問い合わせは派生表で包む（MySQL は、更新する表をそのまま副問い合わせに使えない）。
    /// 「有効な Administrator」は、無効でなく、**一度でもログインした**人（招待しただけの人は、誰も知らないパスワードしか持たないので数えない。
    /// この条件は厳しい側に外れる＝まだ入っていない人が居る間は断るが、断るのは安全側）。
    /// </remarks>
    private async Task<AdminChangeResult> ChangeAsync(
        string id, string setClause, object value, bool leavesAdministrator, CancellationToken cancellationToken)
    {
        var guard = leavesAdministrator
            ? " AND (NOT ([Role] = @Admin AND [IsDisabled] = 0 AND [LastLoginAt] IS NOT NULL) OR (SELECT COUNT(*) FROM "
                + "(SELECT [Id] FROM [AdminUsers] WHERE [Role] = @Admin AND [IsDisabled] = 0 AND [LastLoginAt] IS NOT NULL "
                + "AND [Id] <> @Id) AS [Others]) > 0)"
            : string.Empty;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // ⚠️ **Administrator の行を先にロックして、同時の変更を 1 つずつにする。**
        // 1 文の UPDATE の副問い合わせは、PostgreSQL（READ COMMITTED）では文の開始時点の状態を見るので、
        // 2 人が同時に互いを外すと「相手が残る」と見て両方通り、有効な Administrator が 0 人になる（実機で再現）。
        // ロックの順序は主キー順に揃える（揃えないと MySQL でデッドロックする）。SQLite は書き込みが直列なので不要
        if (leavesAdministrator)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlDialect.LockAdministrators(connections.Provider),
                new { Admin = AdminRoles.Administrator }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            Sql($"UPDATE [AdminUsers] SET {setClause} WHERE [Id] = @Id{guard}"),
            new { Id = id, Value = value, Admin = AdminRoles.Administrator },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (updated > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AdminChangeResult.Updated;
        }

        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            Sql("SELECT COUNT(*) FROM [AdminUsers] WHERE [Id] = @Id"), new { Id = id },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return exists ? AdminChangeResult.LastAdministrator : AdminChangeResult.NotFound;
    }

    public async Task<AdminFailure> RecordFailureAsync(
        string id, int threshold, TimeSpan lockout, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        // 加算は 1 文で行う（読んでから書くと、並行した失敗が数えられない）
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [FailedLoginCount] = [FailedLoginCount] + 1 WHERE [Id] = @Id"),
            new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var count = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            Sql("SELECT [FailedLoginCount] FROM [AdminUsers] WHERE [Id] = @Id"),
            new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;

        if (count < threshold)
        {
            return new AdminFailure(count, null);
        }

        // 締め出す。**恒久的には締め出さない**（正規の利用者を狙って締め出す嫌がらせが成立する）。回数は戻す
        var until = now + lockout;
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [FailedLoginCount] = 0, [LockedUntil] = @Until WHERE [Id] = @Id"),
            new { Id = id, Until = DbTime.Format(until) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new AdminFailure(0, until);
    }

    public async Task RecordSuccessAsync(string id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [FailedLoginCount] = 0, [LockedUntil] = NULL, [LastLoginAt] = @Now WHERE [Id] = @Id"),
            new { Id = id, Now = DbTime.Format(now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SetPasswordHashAsync(string id, string passwordHash, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [PasswordHash] = @PasswordHash WHERE [Id] = @Id"),
            new { Id = id, PasswordHash = passwordHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<string?> GetTotpSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var cipher = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            Sql("SELECT [TotpSecretCipher] FROM [AdminUsers] WHERE [Id] = @Id"), new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return string.IsNullOrEmpty(cipher) ? null : protector.Unprotect(cipher, TotpContext(id));
    }

    public async Task SetTotpPendingAsync(string id, string secret, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [TotpPendingCipher] = @Cipher WHERE [Id] = @Id"),
            new { Id = id, Cipher = protector.Protect(secret, TotpContext(id)) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<string?> GetTotpPendingAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var cipher = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            Sql("SELECT [TotpPendingCipher] FROM [AdminUsers] WHERE [Id] = @Id"), new { Id = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return string.IsNullOrEmpty(cipher) ? null : protector.Unprotect(cipher, TotpContext(id));
    }

    public async Task EnableTotpAsync(
        string id,
        string secret,
        long timeStep,
        IReadOnlyList<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [TotpSecretCipher] = @Cipher, [TotpPendingCipher] = NULL, "
                + "[TotpLastTimeStep] = @TimeStep WHERE [Id] = @Id"),
            new { Id = id, Cipher = protector.Protect(secret, TotpContext(id)), TimeStep = timeStep },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // **登録し直すと前の復旧コードは消える。** 残すと配り直した意味が無い
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AdminRecoveryCodes] WHERE [UserId] = @Id"), new { Id = id },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var hash in recoveryCodeHashes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [AdminRecoveryCodes] ([UserId], [CodeHash]) VALUES (@Id, @Hash)"),
                new { Id = id, Hash = hash }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableTotpAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [TotpSecretCipher] = NULL, [TotpPendingCipher] = NULL, [TotpLastTimeStep] = NULL "
                + "WHERE [Id] = @Id"),
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AdminRecoveryCodes] WHERE [UserId] = @Id"), new { Id = id },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryUseTotpStepAsync(string id, long timeStep, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminUsers] SET [TotpLastTimeStep] = @TimeStep "
                + "WHERE [Id] = @Id AND ([TotpLastTimeStep] IS NULL OR [TotpLastTimeStep] < @TimeStep)"),
            new { Id = id, TimeStep = timeStep },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<bool> TryUseRecoveryCodeAsync(
        string id, string codeHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [AdminRecoveryCodes] SET [UsedAt] = @Now "
                + "WHERE [UserId] = @Id AND [CodeHash] = @Hash AND [UsedAt] IS NULL"),
            new { Id = id, Hash = codeHash, Now = DbTime.Format(now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<int> CountRecoveryCodesAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            Sql("SELECT COUNT(*) FROM [AdminRecoveryCodes] WHERE [UserId] = @Id AND [UsedAt] IS NULL"),
            new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class UserRow
    {
        public string Id { get; set; } = "";

        public string LoginId { get; set; } = "";

        public string Role { get; set; } = "";

        public int IsDisabled { get; set; }

        public int FailedLoginCount { get; set; }

        public string? LockedUntil { get; set; }

        public string? LastLoginAt { get; set; }

        public string CreatedAt { get; set; } = "";

        public string? TotpSecretCipher { get; set; }

        public string Language { get; set; } = FeedLanguages.Default;

        public string PasswordHash { get; set; } = "";

        public AdminUser ToUser() => new(
            Id,
            LoginId,
            Role,
            IsDisabled != 0,
            FailedLoginCount,
            DbTime.Parse(LockedUntil),
            DbTime.Parse(LastLoginAt),
            DbTime.Parse(CreatedAt)!.Value,
            !string.IsNullOrEmpty(TotpSecretCipher),
            Language);
    }
}

/// <summary>サーバ側セッションを RDBMS へ保管する。</summary>
/// <remarks>
/// cookie に載せるのは**推測できない 256 bit の ID** だけで、DB には <b>SHA-256 だけ</b>を保管する
/// （DB が漏れても、そこから有効な cookie は作れない）。
/// </remarks>
public sealed class AdminSessionStore(IDbConnectionFactory connections) : IAdminSessionStore
{
    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    private static string IdHash(string sessionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    public async Task<string> CreateAsync(
        string userId,
        AdminSessionKind kind,
        TimeSpan lifetime,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null,
        SamlSessionKeys? samlKeys = null)
    {
        var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [AdminSessions] ([Id], [UserId], [Kind], [CreatedAt], [ExpiresAt], [IpAddress], [UserAgent], "
                + "[SamlNameId], [SamlSessionIndex]) "
                + "VALUES (@Id, @UserId, @Kind, @CreatedAt, @ExpiresAt, @IpAddress, @UserAgent, @SamlNameId, @SamlSessionIndex)"),
            new
            {
                Id = IdHash(sessionId),
                UserId = userId,
                Kind = kind.ToString(),
                CreatedAt = DbTime.Format(now),
                ExpiresAt = DbTime.Format(now + lifetime),
                IpAddress = AuditText.Safe(ipAddress, 45),
                UserAgent = AuditText.Safe(userAgent, 200),
                SamlNameId = AuditText.Safe(samlKeys?.NameId, 256),
                SamlSessionIndex = AuditText.Safe(samlKeys?.SessionIndex, 256),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return sessionId;
    }

    public string HandleOf(string sessionId) => IdHash(sessionId);

    public async Task<int> DeleteBySamlKeysAsync(string nameId, string? sessionIndex, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            string.IsNullOrEmpty(sessionIndex)
                ? Sql("DELETE FROM [AdminSessions] WHERE [SamlNameId] = @NameId")
                : Sql("DELETE FROM [AdminSessions] WHERE [SamlNameId] = @NameId AND [SamlSessionIndex] = @SessionIndex"),
            new { NameId = nameId, SessionIndex = sessionIndex },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SamlSessionKeys?> GetSamlKeysAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<(string? NameId, string? SessionIndex)?>(new CommandDefinition(
            Sql("SELECT [SamlNameId], [SamlSessionIndex] FROM [AdminSessions] WHERE [Id] = @Id"),
            new { Id = IdHash(sessionId) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is { NameId: { Length: > 0 } nameId } r ? new SamlSessionKeys(nameId, r.SessionIndex) : null;
    }

    public async Task<IReadOnlyList<AdminSessionInfo>> ListForUserAsync(
        string userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<(string Id, string CreatedAt, string ExpiresAt, string? IpAddress, string? UserAgent)>(
            new CommandDefinition(
                Sql("SELECT [Id], [CreatedAt], [ExpiresAt], [IpAddress], [UserAgent] FROM [AdminSessions] "
                    + "WHERE [UserId] = @UserId AND [Kind] = @Kind AND [ExpiresAt] > @Now ORDER BY [CreatedAt] DESC"),
                new { UserId = userId, Kind = nameof(AdminSessionKind.Session), Now = DbTime.Format(now) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new AdminSessionInfo(
            r.Id, DbTime.Parse(r.CreatedAt)!.Value, DbTime.Parse(r.ExpiresAt)!.Value, r.IpAddress, r.UserAgent))];
    }

    public async Task<bool> DeleteByHandleAsync(
        string userId, string handle, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AdminSessions] WHERE [Id] = @Id AND [UserId] = @UserId"),
            new { Id = handle, UserId = userId }, cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<AdminSession?> FindAsync(
        string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<(string UserId, string Kind, string ExpiresAt)?>(
            new CommandDefinition(
                Sql("SELECT [UserId], [Kind], [ExpiresAt] FROM [AdminSessions] WHERE [Id] = @Id AND [ExpiresAt] > @Now"),
                new { Id = IdHash(sessionId), Now = DbTime.Format(now) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is { } r && Enum.TryParse<AdminSessionKind>(r.Kind, out var kind)
            ? new AdminSession(r.UserId, kind, DbTime.Parse(r.ExpiresAt)!.Value)
            : null;
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AdminSessions] WHERE [Id] = @Id"), new { Id = IdHash(sessionId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteForUserAsync(
        string userId, string? exceptSessionId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            exceptSessionId is null
                ? Sql("DELETE FROM [AdminSessions] WHERE [UserId] = @UserId")
                : Sql("DELETE FROM [AdminSessions] WHERE [UserId] = @UserId AND [Id] <> @Except"),
            new { UserId = userId, Except = exceptSessionId is null ? null : IdHash(exceptSessionId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AdminSessions] WHERE [ExpiresAt] <= @Now"), new { Now = DbTime.Format(now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
