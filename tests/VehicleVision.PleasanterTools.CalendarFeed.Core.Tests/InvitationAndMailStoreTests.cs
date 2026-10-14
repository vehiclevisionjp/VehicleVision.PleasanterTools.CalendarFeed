using Dapper;
using VehicleVision.PleasanterTools.CalendarFeed.Admin;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Core.Tests;

/// <summary>
/// 管理者の招待・管理・端末、メールの送信待ちの保管（4 種の RDBMS）。
/// <b>方言差が出やすい SQL</b>（最後の Administrator を守る副問い合わせ、送信待ちの原子的な確保）を実機に当てる。
/// </summary>
[Collection(TestDatabases.CollectionName)]
public sealed class InvitationAndMailStoreTests : IDisposable
{
    private static readonly string TestKey = SecretProtector.GenerateKey();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _sqlitePath = Path.Combine(Path.GetTempPath(), $"calendarfeed-invmail-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new(Now);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_sqlitePath);
    }

    /// <summary>進められる時計。</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Stores(
        AdminUserStore Users,
        AdminSessionStore Sessions,
        AdminInvitationStore Invitations,
        MailOutbox Outbox);

    private async Task<Stores> SetUpAsync(DatabaseProvider provider)
    {
        var factory = new DbConnectionFactory(
            provider,
            provider is DatabaseProvider.Sqlite ? $"Data Source={_sqlitePath};Pooling=False" : TestDatabases.ConnectionString(provider)!);
        TestDatabases.Migrate(provider, factory.ConnectionString);
        await using (var connection = factory.Create())
        {
            foreach (var table in new[] { "AdminInvitations", "MailOutbox", "AdminRecoveryCodes", "AdminSessions", "AdminUsers", "AdminSetup" })
            {
                await connection.ExecuteAsync(SqlDialect.Format(provider, $"DELETE FROM [{table}]"));
            }
        }

        return new Stores(
            new AdminUserStore(factory, new SecretProtector(TestKey)),
            new AdminSessionStore(factory),
            new AdminInvitationStore(factory, _clock),
            new MailOutbox(factory, _clock));
    }

    /// <summary>ログインしたことのある（有効な）管理者。</summary>
    private async Task<AdminUser> ActiveAsync(Stores s, string loginId, string role)
    {
        var created = await s.Users.CreateAsync(loginId, PasswordHasher.Hash("correct horse battery", iterations: 1000), role, "ja", Now);
        await s.Users.RecordSuccessAsync(created!.Id, Now);
        return (await s.Users.FindByIdAsync(created.Id))!;
    }

    private static AdminInvitation Invitation(string userId, string tokenHash, DateTimeOffset? expires = null) =>
        new(Guid.NewGuid().ToString("D"), userId, tokenHash, expires ?? Now.AddHours(48), CreatedBy: userId);

    // ---- 管理者の作成 ------------------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task 管理者を作り_同じログインIDは大文字小文字を区別せず断る(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);

        var created = await s.Users.CreateAsync("Bob", PasswordHasher.Hash("x-placeholder", iterations: 1000), AdminRoles.Editor, "en", Now);

        Assert.NotNull(created);
        Assert.Equal(AdminRoles.Editor, created.Role);
        Assert.Equal("en", created.Language);
        Assert.Null(created.LastLoginAt);
        Assert.Null(await s.Users.CreateAsync("bOB", PasswordHasher.Hash("x-placeholder", iterations: 1000), AdminRoles.Editor, "ja", Now));
        Assert.Single(await s.Users.ListAsync());
    }

    // ---- 招待 ------------------------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task 招待を置き_使い切りにし_出し直すと前の未使用は消え使用済みは残る(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var user = await ActiveAsync(s, "bob", AdminRoles.Editor);
        var first = Invitation(user.Id, InviteTokens.Hash("first"));
        await s.Invitations.ReplaceAsync(first);

        var found = await s.Invitations.FindByTokenHashAsync(first.TokenHash);
        Assert.Equal(first.InvitationId, found!.InvitationId);
        Assert.Null(found.UsedAt);
        Assert.Equal(first.ExpiresAt, found.ExpiresAt);
        Assert.Equal([user.Id], await s.Invitations.ListPendingAdminUserIdsAsync(Now));
        Assert.Empty(await s.Invitations.ListPendingAdminUserIdsAsync(Now.AddHours(49)));

        Assert.True(await s.Invitations.TryConsumeAsync(first.InvitationId, Now));
        Assert.False(await s.Invitations.TryConsumeAsync(first.InvitationId, Now));
        Assert.NotNull((await s.Invitations.FindByTokenHashAsync(first.TokenHash))!.UsedAt);
        Assert.Empty(await s.Invitations.ListPendingAdminUserIdsAsync(Now));

        // 出し直す: 使用済みは残り、未使用だけが差し替わる
        var second = Invitation(user.Id, InviteTokens.Hash("second"));
        var third = Invitation(user.Id, InviteTokens.Hash("third"));
        await s.Invitations.ReplaceAsync(second);
        await s.Invitations.ReplaceAsync(third);
        Assert.NotNull(await s.Invitations.FindByTokenHashAsync(first.TokenHash));
        Assert.Null(await s.Invitations.FindByTokenHashAsync(second.TokenHash));
        Assert.NotNull(await s.Invitations.FindByTokenHashAsync(third.TokenHash));

        await s.Invitations.RevokeUnusedAsync(user.Id);
        Assert.Null(await s.Invitations.FindByTokenHashAsync(third.TokenHash));
        Assert.NotNull(await s.Invitations.FindByTokenHashAsync(first.TokenHash));
        Assert.Null(await s.Invitations.FindByTokenHashAsync(InviteTokens.Hash("unknown")));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 同時に使い切ろうとしても1回だけ通る(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var user = await ActiveAsync(s, "bob", AdminRoles.Editor);
        var invitation = Invitation(user.Id, InviteTokens.Hash("race"));
        await s.Invitations.ReplaceAsync(invitation);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.Invitations.TryConsumeAsync(invitation.InvitationId, Now)));

        Assert.Single(results, r => r);
    }

    // ---- 最後の Administrator ----------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task 最後の有効なAdministratorは無効化も降格もできない_一度もログインしていない人は頭数に入れない(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var only = await ActiveAsync(s, "only", AdminRoles.Administrator);

        Assert.Equal(AdminChangeResult.LastAdministrator, await s.Users.SetDisabledAsync(only.Id, true));
        Assert.Equal(AdminChangeResult.LastAdministrator, await s.Users.SetRoleAsync(only.Id, AdminRoles.Editor));
        Assert.Equal(AdminChangeResult.NotFound, await s.Users.SetDisabledAsync(Guid.NewGuid().ToString(), true));
        Assert.Equal(AdminChangeResult.NotFound, await s.Users.SetRoleAsync(Guid.NewGuid().ToString(), AdminRoles.Editor));

        // 招待しただけ（一度もログインしていない）の Administrator は数えない
        var pending = await s.Users.CreateAsync("pending", PasswordHasher.Hash("x-placeholder", iterations: 1000), AdminRoles.Administrator, "ja", Now);
        Assert.Equal(AdminChangeResult.LastAdministrator, await s.Users.SetDisabledAsync(only.Id, true));
        Assert.False((await s.Users.FindByIdAsync(only.Id))!.IsDisabled);

        // ログインしたなら数える
        await s.Users.RecordSuccessAsync(pending!.Id, Now);
        Assert.Equal(AdminChangeResult.Updated, await s.Users.SetDisabledAsync(only.Id, true));
        Assert.True((await s.Users.FindByIdAsync(only.Id))!.IsDisabled);
        Assert.Equal(AdminChangeResult.LastAdministrator, await s.Users.SetRoleAsync(pending.Id, AdminRoles.Auditor));
        Assert.Equal(AdminChangeResult.Updated, await s.Users.SetDisabledAsync(only.Id, false));
        Assert.Equal(AdminChangeResult.Updated, await s.Users.SetRoleAsync(pending.Id, AdminRoles.Auditor));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 互いを同時に外そうとしても有効なAdministratorは1人残る(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var a = await ActiveAsync(s, "a", AdminRoles.Administrator);
        var b = await ActiveAsync(s, "b", AdminRoles.Administrator);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => s.Users.SetDisabledAsync(a.Id, true))
                .Concat(Enumerable.Range(0, 4).Select(_ => s.Users.SetDisabledAsync(b.Id, true))));

        var active = (await s.Users.ListAsync()).Count(u => u is { IsDisabled: false, Role: AdminRoles.Administrator });
        Assert.True(active >= 1, $"有効な Administrator が {active} 人。結果: {string.Join(',', results)}");
    }

    // ---- 端末 ------------------------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task 端末の一覧と持ち主の組での失効(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var a = await ActiveAsync(s, "a", AdminRoles.Administrator);
        var b = await ActiveAsync(s, "b", AdminRoles.Editor);

        var first = await s.Sessions.CreateAsync(a.Id, AdminSessionKind.Session, TimeSpan.FromHours(8), Now, default, "203.0.113.1", "Edge/155");
        var second = await s.Sessions.CreateAsync(a.Id, AdminSessionKind.Session, TimeSpan.FromHours(8), Now.AddMinutes(1));
        await s.Sessions.CreateAsync(a.Id, AdminSessionKind.Pending, TimeSpan.FromMinutes(5), Now);
        await s.Sessions.CreateAsync(b.Id, AdminSessionKind.Session, TimeSpan.FromHours(8), Now);

        var list = await s.Sessions.ListForUserAsync(a.Id, Now.AddMinutes(2));

        Assert.Equal(2, list.Count);
        Assert.Equal(s.Sessions.HandleOf(second), list[0].Handle);
        Assert.Equal("203.0.113.1", list.Single(x => x.Handle == s.Sessions.HandleOf(first)).IpAddress);
        Assert.Equal("Edge/155", list.Single(x => x.Handle == s.Sessions.HandleOf(first)).UserAgent);
        Assert.Empty(await s.Sessions.ListForUserAsync(a.Id, Now.AddHours(9)));

        // 他人の端末の識別子では消えない
        var bHandle = (await s.Sessions.ListForUserAsync(b.Id, Now)).Single().Handle;
        Assert.False(await s.Sessions.DeleteByHandleAsync(a.Id, bHandle));
        Assert.True(await s.Sessions.DeleteByHandleAsync(a.Id, s.Sessions.HandleOf(first)));
        Assert.Single(await s.Sessions.ListForUserAsync(a.Id, Now.AddMinutes(2)));
    }

    // ---- メールの送信待ち --------------------------------------------------------------

    [Theory]
    [EnabledDatabases]
    public async Task 送信待ちを積み_同じ識別子は二重に積まず_確保して消せる(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var id = Guid.NewGuid().ToString("D");

        Assert.True(await s.Outbox.EnqueueAsync(id, (int)MailKind.AdminInvitation, "protected-payload"));
        Assert.False(await s.Outbox.EnqueueAsync(id, (int)MailKind.AdminInvitation, "protected-payload"));

        var claimed = await s.Outbox.ClaimAsync("worker-a", TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        Assert.Equal(id, claimed.MailId);
        Assert.Equal((int)MailKind.AdminInvitation, claimed.Kind);
        Assert.Equal("protected-payload", claimed.PayloadProtected);
        Assert.Equal(0, claimed.RetryCount);

        // 確保中は他のワーカーが取れない
        Assert.Null(await s.Outbox.ClaimAsync("worker-b", TimeSpan.FromMinutes(5)));

        await s.Outbox.CompleteAsync(id);
        Assert.Equal(0, (await s.Outbox.GetStatusAsync()).PendingCount);
        Assert.Null(await s.Outbox.ClaimAsync("worker-a", TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [EnabledDatabases]
    public async Task 複数のワーカーが同時に確保しても同じ1通を取らない(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("D")).ToList();
        foreach (var id in ids)
        {
            Assert.True(await s.Outbox.EnqueueAsync(id, (int)MailKind.AdminInvitation, "payload"));
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => s.Outbox.ClaimAsync($"worker-{i}", TimeSpan.FromMinutes(5))));

        var taken = claims.Where(c => c is not null).Select(c => c!.MailId).ToList();
        Assert.Equal(ids.Order(), taken.Order());
        Assert.Equal(taken.Count, taken.Distinct().Count());
    }

    [Theory]
    [EnabledDatabases]
    public async Task 再送待ちは時刻まで確保されず_デッドレターは確保されない_期限切れの確保は戻る(DatabaseProvider provider)
    {
        var s = await SetUpAsync(provider);
        var retry = Guid.NewGuid().ToString("D");
        var dead = Guid.NewGuid().ToString("D");
        await s.Outbox.EnqueueAsync(retry, (int)MailKind.AdminInvitation, "payload");
        await s.Outbox.EnqueueAsync(dead, (int)MailKind.AdminInvitation, "payload");

        // 再送待ち: 次の時刻まで取れず、回数が増える
        Assert.NotNull(await s.Outbox.ClaimAsync("w", TimeSpan.FromMinutes(5)));
        Assert.NotNull(await s.Outbox.ClaimAsync("w", TimeSpan.FromMinutes(5)));
        await s.Outbox.RescheduleAsync(retry, Now.AddMinutes(10), "一時的に送れない");
        await s.Outbox.DeadLetterAsync(dead, new string('x', 2000));

        Assert.Null(await s.Outbox.ClaimAsync("w", TimeSpan.FromMinutes(5)));
        _clock.Now = Now.AddMinutes(11);
        var again = await s.Outbox.ClaimAsync("w", TimeSpan.FromMinutes(5));
        Assert.Equal(retry, again!.MailId);
        Assert.Equal(1, again.RetryCount);

        var status = await s.Outbox.GetStatusAsync();
        Assert.Equal(1, status.PendingCount);
        Assert.Equal(1, status.DeadLetterCount);
        Assert.NotNull(status.OldestPendingAt);

        // 確保したまま落ちた行は、期限が切れると解放される
        Assert.Equal(0, await s.Outbox.ReleaseExpiredLocksAsync(_clock.Now));
        Assert.Equal(1, await s.Outbox.ReleaseExpiredLocksAsync(_clock.Now.AddMinutes(6)));
        _clock.Now = Now.AddMinutes(20);
        Assert.Equal(retry, (await s.Outbox.ClaimAsync("w", TimeSpan.FromMinutes(5)))!.MailId);
    }
}
