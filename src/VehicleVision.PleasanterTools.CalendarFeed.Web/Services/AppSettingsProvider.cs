using System.Collections.Frozen;
using VehicleVision.PleasanterTools.CalendarFeed.Data;
using VehicleVision.PleasanterTools.CalendarFeed.Security;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>ある時点のアプリケーション設定の値。</summary>
/// <param name="Values">全キーの値。<b>秘密も平文でここに持つ</b>（サーバ内だけ。画面へは返さない）。</param>
/// <param name="FixedKeys">外部設定（環境変数・Parameters）で固定されたキー。<b>画面からは変えられない。</b></param>
/// <param name="UnreadableSecrets">保管した秘密を復号できなかったキー（鍵を失った・変えた）。値は空として扱い、再入力が要る。</param>
public sealed record AppSettingsSnapshot(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> FixedKeys,
    IReadOnlySet<string> UnreadableSecrets)
{
    public string this[string key] => Values[key];
}

/// <summary>アプリケーション設定の読み書き。</summary>
public interface IAppSettingsProvider
{
    /// <summary>いまの設定（短い間キャッシュする）。</summary>
    Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>キャッシュを使わずに読み直す（他のインスタンスの保存を取り込む）。</summary>
    Task<AppSettingsSnapshot> ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 設定を保存する。<b>値が <c>null</c> なら既定へ戻す</b>（保管した値を消す）。<b>秘密は空文字なら変えない</b>
    /// （画面は秘密の現在値を返さないので、空のまま送られてくる）。
    /// </summary>
    /// <returns>保存後の設定と、実際に変わったキー。</returns>
    Task<(AppSettingsSnapshot Snapshot, IReadOnlyList<string> ChangedKeys)> SaveAsync(
        IReadOnlyDictionary<string, string?> values,
        string? updatedByAdminUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// アプリケーション設定の読み書き（Questionnaire の <c>AppSettingsProvider</c> に則る）。
/// <b>優先順位は 外部設定（固定）＞ DB ＞ 既定値。</b>
/// </summary>
/// <remarks>
/// <para>
/// 外部設定の値は<b>範囲を課さずに</b>正規化する（検証環境の設定で起動できなくならないように）。画面からの入力は範囲を課す。
/// </para>
/// <para>
/// **秘密は <see cref="ISecretProtector"/> で暗号化して保管**し、設定名に束縛する（別の設定へ貼り替えられない）。
/// </para>
/// </remarks>
public sealed class AppSettingsProvider(
    IConfiguration configuration,
    IAppSettingStore store,
    ISecretProtector protector,
    TimeProvider time,
    ILogger<AppSettingsProvider> logger) : IAppSettingsProvider
{
    /// <summary>キャッシュの有効期間。<b>他のインスタンスの保存は、この間隔で反映される。</b></summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheEntry? _cache;

    private static string ContextOf(string key) => $"calendarfeed:setting:{key}";

    /// <summary>
    /// 起動直後（DB を読む前）の設定。外部設定と既定値だけ。
    /// <b>外部設定の値をここで直に範囲検査しない</b>（上限・下限を課すと、検証環境の設定で起動できなくなる）。
    /// </summary>
    public static AppSettingsSnapshot InitialSnapshot(IConfiguration configuration)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var fixedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in AppSettingDefinitions.All)
        {
            if (configuration[definition.Key] is { } external && external.Length > 0)
            {
                values[definition.Key] = definition.NormalizeExternal(external);
                fixedKeys.Add(definition.Key);
            }
            else
            {
                values[definition.Key] = definition.DefaultValue;
            }
        }

        return new AppSettingsSnapshot(
            values.ToFrozenDictionary(StringComparer.Ordinal),
            fixedKeys.ToFrozenSet(StringComparer.Ordinal),
            FrozenSet<string>.Empty);
    }

    public async Task<AppSettingsSnapshot> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Cache(await LoadAsync(cancellationToken).ConfigureAwait(false), time.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (Volatile.Read(ref _cache) is { } current && now < current.ExpiresAt)
        {
            return current.Snapshot;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = time.GetUtcNow();
            if (Volatile.Read(ref _cache) is { } cached && now < cached.ExpiresAt)
            {
                return cached.Snapshot;
            }

            return Cache(await LoadAsync(cancellationToken).ConfigureAwait(false), now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(AppSettingsSnapshot Snapshot, IReadOnlyList<string> ChangedKeys)> SaveAsync(
        IReadOnlyDictionary<string, string?> values,
        string? updatedByAdminUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);

            // ---- 検証（1 つでも不正なら、何も保存しない） ----
            var toSave = new Dictionary<string, string>(StringComparer.Ordinal);
            var toReset = new List<string>();
            foreach (var (key, requested) in values)
            {
                if (!AppSettingDefinitions.ByKey.TryGetValue(key, out var definition))
                {
                    throw new AppSettingValidationException($"管理対象ではない設定です: {key}");
                }

                if (requested is null)
                {
                    // 既定へ戻す（保管した値を消す）。外部設定で固定されたものは戻せない
                    if (current.FixedKeys.Contains(key))
                    {
                        throw new AppSettingValidationException($"{definition.Label}は外部設定で固定されているため、画面から変更できません。");
                    }

                    toReset.Add(key);
                    continue;
                }

                // 秘密は、空なら変えない（画面は現在値を返さないので、空のまま送られてくる）
                if (definition.IsSecret && string.IsNullOrWhiteSpace(requested))
                {
                    continue;
                }

                var value = definition.Normalize(requested);
                if (current.FixedKeys.Contains(key))
                {
                    if (string.Equals(value, current[key], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new AppSettingValidationException($"{definition.Label}は外部設定で固定されているため、画面から変更できません。");
                }

                toSave[key] = value;
            }

            var merged = new Dictionary<string, string>(current.Values, StringComparer.Ordinal);
            foreach (var key in toReset)
            {
                merged[key] = AppSettingDefinitions.ByKey[key].DefaultValue;
            }

            foreach (var (key, value) in toSave)
            {
                merged[key] = value;
            }

            AppSettingDefinitions.ValidateCombination(merged);

            // ---- 変わったキー（値が同じなら保存しない） ----
            var changed = new List<string>();
            foreach (var (key, value) in toSave)
            {
                var secret = AppSettingDefinitions.ByKey[key].IsSecret;
                if (secret ? true : !string.Equals(value, current[key], StringComparison.Ordinal))
                {
                    changed.Add(key);
                }
            }

            changed.AddRange(toReset.Where(k => !string.Equals(current[k], AppSettingDefinitions.ByKey[k].DefaultValue, StringComparison.Ordinal)
                || current.UnreadableSecrets.Contains(k)));

            // ---- 保存 ----
            var now = time.GetUtcNow();
            foreach (var (key, value) in toSave)
            {
                var definition = AppSettingDefinitions.ByKey[key];
                await store.SaveAsync(
                    key, definition.IsSecret ? protector.Protect(value, ContextOf(key)) : value, definition.IsSecret,
                    updatedByAdminUserId, now, cancellationToken).ConfigureAwait(false);
            }

            foreach (var key in toReset)
            {
                await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }

            var unreadable = current.UnreadableSecrets.Where(k => !toSave.ContainsKey(k) && !toReset.Contains(k)).ToFrozenSet(StringComparer.Ordinal);
            var snapshot = current with { Values = merged.ToFrozenDictionary(StringComparer.Ordinal), UnreadableSecrets = unreadable };
            return (Cache(snapshot, now), changed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AppSettingsSnapshot Cache(AppSettingsSnapshot loaded, DateTimeOffset now)
    {
        Volatile.Write(ref _cache, new CacheEntry(loaded, now + CacheLifetime));
        return loaded;
    }

    private async Task<AppSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var records = (await store.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.SettingKey, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var fixedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in AppSettingDefinitions.All)
        {
            if (configuration[definition.Key] is { } external && external.Length > 0)
            {
                values[definition.Key] = definition.NormalizeExternal(external);
                fixedKeys.Add(definition.Key);
                continue;
            }

            if (!records.TryGetValue(definition.Key, out var record))
            {
                values[definition.Key] = definition.DefaultValue;
                continue;
            }

            if (record.IsSecret != definition.IsSecret)
            {
                logger.LogError("設定の秘密区分が定義と一致しません。既定へ戻して扱います: {Key}", definition.Key);
                values[definition.Key] = definition.DefaultValue;
                continue;
            }

            string stored;
            if (definition.IsSecret)
            {
                try
                {
                    stored = protector.Unprotect(record.Value, ContextOf(definition.Key));
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
                {
                    // 鍵を失った・変えた（または未設定）。**起動を止めず、値は空として扱い、画面で再入力を求める**
                    logger.LogError("保管した秘密の設定を復号できません。再入力してください: {Key}", definition.Key);
                    values[definition.Key] = string.Empty;
                    unreadable.Add(definition.Key);
                    continue;
                }
            }
            else
            {
                stored = record.Value;
            }

            try
            {
                values[definition.Key] = definition.NormalizeExternal(stored);
            }
            catch (AppSettingValidationException ex)
            {
                // DB を直接いじって壊れた値。**起動を止めず、既定で動く**
                logger.LogError("保管した設定の値が不正です。既定で扱います: {Key}（{Reason}）", definition.Key, ex.Message);
                values[definition.Key] = definition.DefaultValue;
            }
        }

        return new AppSettingsSnapshot(
            values.ToFrozenDictionary(StringComparer.Ordinal),
            fixedKeys.ToFrozenSet(StringComparer.Ordinal),
            unreadable.ToFrozenSet(StringComparer.Ordinal));
    }

    private sealed record CacheEntry(AppSettingsSnapshot Snapshot, DateTimeOffset ExpiresAt);
}
