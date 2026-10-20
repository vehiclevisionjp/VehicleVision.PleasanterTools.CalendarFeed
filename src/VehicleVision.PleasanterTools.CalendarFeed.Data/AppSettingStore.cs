using Dapper;

namespace VehicleVision.PleasanterTools.CalendarFeed.Data;

/// <summary>保管しているアプリケーション設定 1 件。</summary>
/// <param name="SettingKey">設定名（<c>CALENDARFEED_*</c>）。</param>
/// <param name="Value">値。<b>秘密（<paramref name="IsSecret"/>）は暗号文</b>。</param>
/// <param name="IsSecret">秘密か。</param>
/// <param name="UpdatedAt">更新した時刻。</param>
/// <param name="UpdatedByAdminUserId">更新した管理者。</param>
public sealed record AppSettingRecord(
    string SettingKey,
    string Value,
    bool IsSecret,
    DateTimeOffset UpdatedAt,
    string? UpdatedByAdminUserId);

/// <summary>アプリケーション設定の保管先（Questionnaire の <c>AppSettings</c> 表に則る）。</summary>
public interface IAppSettingStore
{
    /// <summary>すべての設定を読む。</summary>
    Task<IReadOnlyList<AppSettingRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>設定を置く（あれば置き換える）。</summary>
    Task SaveAsync(string settingKey, string value, bool isSecret, string? updatedByAdminUserId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>設定を消す（既定へ戻す）。</summary>
    Task DeleteAsync(string settingKey, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装（SQLite / SQL Server / PostgreSQL / MySQL）。</summary>
public sealed class AppSettingStore(IDbConnectionFactory connections) : IAppSettingStore
{
    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    public async Task<IReadOnlyList<AppSettingRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Sql("SELECT [SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedBy] FROM [AppSettings]"),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new AppSettingRecord(
            r.SettingKey, r.Value, r.IsSecret != 0, DbTime.AsUtc(r.UpdatedAt) ?? DateTimeOffset.MinValue, r.UpdatedBy))];
    }

    public async Task SaveAsync(
        string settingKey, string value, bool isSecret, string? updatedByAdminUserId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new
        {
            SettingKey = settingKey,
            Value = value,
            IsSecret = isSecret ? 1 : 0,
            UpdatedAt = DbTime.ForDb(now.UtcDateTime),
            UpdatedBy = updatedByAdminUserId,
        };

        // 置き換えは「消して入れる」を 1 つのトランザクションで（RDBMS ごとの UPSERT の書き方の差を持ち込まない）
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AppSettings] WHERE [SettingKey] = @SettingKey"), parameters, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [AppSettings] ([SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedBy]) "
                + "VALUES (@SettingKey, @Value, @IsSecret, @UpdatedAt, @UpdatedBy)"),
            parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string settingKey, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("DELETE FROM [AppSettings] WHERE [SettingKey] = @SettingKey"), new { SettingKey = settingKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class Row
    {
        public string SettingKey { get; set; } = "";

        public string Value { get; set; } = "";

        public int IsSecret { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public string? UpdatedBy { get; set; }
    }
}
