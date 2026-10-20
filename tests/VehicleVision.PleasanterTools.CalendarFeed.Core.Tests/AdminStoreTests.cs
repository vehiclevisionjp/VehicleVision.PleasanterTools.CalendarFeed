using System.Security.Cryptography;
using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>管理者・セッション・復旧コードの保管（4 種の RDBMS）。</summary>
[Collection(TestDatabases.CollectionName)]
public sealed class AdminStoreTests : IDisposable
{
    private static readonly string TestKey = SecretProtector.GenerateKey();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-admin-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    private string ConnectionStringFor(DatabaseProvider provider) =>
        provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!;

    private DbConnectionFactory FactoryFor(DatabaseProvider provider) => new(provider, ConnectionStringFor(provider));

    /// <summary>ストアを用意する。**管理者の表は共有の DB なので、毎回空にする**（クラス内は直列に走る）。</summary>
    private async Task<(AdminUserStore Users, AdminSessionStore Sessions, DbConnectionFactory Factory)> SetUpAsync(
        DatabaseProvider provider)
    {
        var factory = FactoryFor(provider);
        TestDatabases.Migrate(provider, ConnectionStringFor(provider));
        await using (var connection = factory.Create())
        {
            foreach (var table in new[] { "AdminRecoveryCodes", "AdminSessions", "AdminUsers", "AdminSetup" })
            {
                await connection.ExecuteAsync(SqlDialect.Format(provider, $"DELETE FROM [{table}]"));
            }
        }

        return (new AdminUserStore(factory, new SecretProtector(TestKey)), new AdminSessionStore(factory), factory);
    }

    private static Task<AdminUser?> FirstAsync(AdminUserStore users, string loginId = "Admin") =>
        users.CreateFirstAsync(loginId, PasswordHasher.Hash("correct horse battery", iterations: 1000), "ja");

    [Theory]
    [EnabledDatabases]
    public async Task 最初の管理者を作りログイン_ID_は大文字小文字を区別せず引ける(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);

        var admin = await FirstAsync(users, "Admin");

        Assert.NotNull(admin);
        Assert.Equal("Admin", admin.LoginId);
        Assert.Equal(AdminRoles.Administrator, admin.Role);
        Assert.False(admin.TotpEnabled);
        var found = await users.FindForLoginAsync("  aDMIN ");
        Assert.Equal(admin.Id, found!.Value.User.Id);
        Assert.True(PasswordHasher.Verify("correct horse battery", found.Value.PasswordHash));
        Assert.Null(await users.FindForLoginAsync("nobody"));
        Assert.Equal(1, await users.CountAsync());
    }

    [Theory]
    [EnabledDatabases]
    public async Task 最初の管理者は1度しか作れず同時に来ても1人だけ(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => FirstAsync(users, $"admin{i}")));

        Assert.Single(results, r => r is not null);
        Assert.Equal(1, await users.CountAsync());
        Assert.Null(await FirstAsync(users, "another"));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 失敗を数えて閾値で締め出し成功で戻す(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;

        var first = await users.RecordFailureAsync(admin.Id, 3, TimeSpan.FromMinutes(15), Now);
        var second = await users.RecordFailureAsync(admin.Id, 3, TimeSpan.FromMinutes(15), Now);
        var third = await users.RecordFailureAsync(admin.Id, 3, TimeSpan.FromMinutes(15), Now);

        Assert.Equal(1, first.FailedCount);
        Assert.Null(second.LockedUntil);
        Assert.Equal(Now.AddMinutes(15), third.LockedUntil);
        var locked = (await users.FindByIdAsync(admin.Id))!;
        Assert.True(locked.IsLocked(Now.AddMinutes(14)));
        Assert.False(locked.IsLocked(Now.AddMinutes(16)));
        Assert.Equal(0, locked.FailedLoginCount);

        await users.RecordSuccessAsync(admin.Id, Now.AddMinutes(20));
        var ok = (await users.FindByIdAsync(admin.Id))!;
        Assert.Null(ok.LockedUntil);
        Assert.Equal(Now.AddMinutes(20), ok.LastLoginAt);
    }

    [Theory]
    [EnabledDatabases]
    public async Task パスワードのハッシュを差し替えられる(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;

        await users.SetPasswordHashAsync(admin.Id, PasswordHasher.Hash("a brand new password", iterations: 1000));

        var found = (await users.FindForLoginAsync("admin"))!.Value;
        Assert.True(PasswordHasher.Verify("a brand new password", found.PasswordHash));
        Assert.False(PasswordHasher.Verify("correct horse battery", found.PasswordHash));
    }

    [Theory]
    [EnabledDatabases]
    public async Task TOTPの共有鍵は暗号化して保管し_確定は登録の途中の鍵からだけ(DatabaseProvider provider)
    {
        var (users, _, factory) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        var secret = TotpService.GenerateSecret();

        await users.SetTotpPendingAsync(admin.Id, secret);
        Assert.Equal(secret, await users.GetTotpPendingAsync(admin.Id));
        Assert.False((await users.FindByIdAsync(admin.Id))!.TotpEnabled);
        Assert.Null(await users.GetTotpSecretAsync(admin.Id));

        await users.EnableTotpAsync(admin.Id, secret, 100, [RecoveryCodes.Hash("AAAAA-BBBBB")]);

        Assert.True((await users.FindByIdAsync(admin.Id))!.TotpEnabled);
        Assert.Equal(secret, await users.GetTotpSecretAsync(admin.Id));
        Assert.Null(await users.GetTotpPendingAsync(admin.Id));

        await using var connection = factory.Create();
        var cipher = await connection.ExecuteScalarAsync<string>(
            SqlDialect.Format(provider, "SELECT [TotpSecretCipher] FROM [AdminUsers] WHERE [Id] = @Id"), new { admin.Id });
        Assert.StartsWith("v1:", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, cipher, StringComparison.Ordinal);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 共有鍵の暗号文を別の管理者の行へ差し替えても復号できない(DatabaseProvider provider)
    {
        var (users, _, factory) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 1, []);

        await using var connection = factory.Create();
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, "UPDATE [AdminUsers] SET [TotpSecretCipher] = [TotpSecretCipher] WHERE [Id] = @Id"),
            new { admin.Id });
        var other = new AdminUserStore(factory, new SecretProtector(SecretProtector.GenerateKey()));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => other.GetTotpSecretAsync(admin.Id));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同じ時間枠の数字は二度通さない(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 100, []);

        Assert.False(await users.TryUseTotpStepAsync(admin.Id, 100));
        Assert.False(await users.TryUseTotpStepAsync(admin.Id, 99));
        Assert.True(await users.TryUseTotpStepAsync(admin.Id, 101));
        Assert.False(await users.TryUseTotpStepAsync(admin.Id, 101));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同時に来た同じ時間枠は1つだけ通る(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 100, []);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => users.TryUseTotpStepAsync(admin.Id, 200)));

        Assert.Single(results, r => r);
    }

    [Theory]
    [EnabledDatabases]
    public async Task 復旧コードは使い切りで_登録し直すと前のものは消える(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        var codes = RecoveryCodes.Generate();
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 1, codes.Select(RecoveryCodes.Hash).ToList());
        Assert.Equal(10, await users.CountRecoveryCodesAsync(admin.Id));

        Assert.True(await users.TryUseRecoveryCodeAsync(admin.Id, RecoveryCodes.Hash(codes[0].ToLowerInvariant()), Now));
        Assert.False(await users.TryUseRecoveryCodeAsync(admin.Id, RecoveryCodes.Hash(codes[0]), Now));
        Assert.False(await users.TryUseRecoveryCodeAsync(admin.Id, RecoveryCodes.Hash("ZZZZZ-ZZZZZ"), Now));
        Assert.Equal(9, await users.CountRecoveryCodesAsync(admin.Id));

        var fresh = RecoveryCodes.Generate();
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 1, fresh.Select(RecoveryCodes.Hash).ToList());

        Assert.False(await users.TryUseRecoveryCodeAsync(admin.Id, RecoveryCodes.Hash(codes[1]), Now));
        Assert.True(await users.TryUseRecoveryCodeAsync(admin.Id, RecoveryCodes.Hash(fresh[0]), Now));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同時に来た同じ復旧コードは1つだけ通る(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        var codes = RecoveryCodes.Generate();
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 1, codes.Select(RecoveryCodes.Hash).ToList());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => users.TryUseRecoveryCodeAsync(admin.Id, RecoveryCodes.Hash(codes[0]), Now)));

        Assert.Single(results, r => r);
    }

    [Theory]
    [EnabledDatabases]
    public async Task TOTPを解除すると共有鍵と復旧コードが消える(DatabaseProvider provider)
    {
        var (users, _, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        await users.EnableTotpAsync(admin.Id, TotpService.GenerateSecret(), 1, [RecoveryCodes.Hash("AAAAA-BBBBB")]);

        await users.DisableTotpAsync(admin.Id);

        Assert.False((await users.FindByIdAsync(admin.Id))!.TotpEnabled);
        Assert.Null(await users.GetTotpSecretAsync(admin.Id));
        Assert.Equal(0, await users.CountRecoveryCodesAsync(admin.Id));
    }

    [Theory]
    [EnabledDatabases]
    public async Task セッションは推測できないIDで作られハッシュだけが保管される(DatabaseProvider provider)
    {
        var (users, sessions, factory) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;

        var id = await sessions.CreateAsync(admin.Id, AdminSessionKind.Session, TimeSpan.FromHours(8), Now);
        var other = await sessions.CreateAsync(admin.Id, AdminSessionKind.Pending, TimeSpan.FromMinutes(5), Now);

        Assert.NotEqual(id, other);
        Assert.True(id.Length >= 40);
        var found = await sessions.FindAsync(id, Now.AddHours(1));
        Assert.Equal(admin.Id, found!.UserId);
        Assert.Equal(AdminSessionKind.Session, found.Kind);
        Assert.Equal(AdminSessionKind.Pending, (await sessions.FindAsync(other, Now.AddMinutes(1)))!.Kind);
        Assert.Null(await sessions.FindAsync("unknown-session-id", Now));

        await using var connection = factory.Create();
        var ids = await connection.QueryAsync<string>(SqlDialect.Format(provider, "SELECT [Id] FROM [AdminSessions]"));
        Assert.DoesNotContain(id, ids);
        Assert.All(ids, hash => Assert.Equal(64, hash.Length));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 期限切れのセッションは引けず削除できる(DatabaseProvider provider)
    {
        var (users, sessions, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        var id = await sessions.CreateAsync(admin.Id, AdminSessionKind.Pending, TimeSpan.FromMinutes(5), Now);

        Assert.NotNull(await sessions.FindAsync(id, Now.AddMinutes(4)));
        Assert.Null(await sessions.FindAsync(id, Now.AddMinutes(5)));
        Assert.Equal(1, await sessions.DeleteExpiredAsync(Now.AddMinutes(6)));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 利用者のセッションをいまの端末だけ残して消せる(DatabaseProvider provider)
    {
        var (users, sessions, _) = await SetUpAsync(provider);
        var admin = (await FirstAsync(users))!;
        var keep = await sessions.CreateAsync(admin.Id, AdminSessionKind.Session, TimeSpan.FromHours(8), Now);
        var drop = await sessions.CreateAsync(admin.Id, AdminSessionKind.Session, TimeSpan.FromHours(8), Now);

        await sessions.DeleteForUserAsync(admin.Id, keep);

        Assert.NotNull(await sessions.FindAsync(keep, Now));
        Assert.Null(await sessions.FindAsync(drop, Now));

        await sessions.DeleteAsync(keep);
        Assert.Null(await sessions.FindAsync(keep, Now));
    }
}
