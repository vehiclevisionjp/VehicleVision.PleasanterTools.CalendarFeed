using System.Globalization;
using VehicleVision.PleasanterTools.CalendarFeed.Data;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

/// <summary>共有のレート制限の判定。</summary>
/// <param name="Allowed">通してよいか。</param>
/// <param name="RetryAfter">断るとき、次の窓が始まるまでの時間。</param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// 複数台で数えを共有する固定窓のレート制限（Issue #49）。
/// </summary>
/// <remarks>
/// <para>
/// 標準のレート制限はメモリ内で数えるので、台ごとに別に数える（3 台なら 3 倍通る）。
/// ログインの試行や Pleasanter のログインの確認は、<b>DB の数え（<see cref="IRateLimitStore"/>）を共有</b>して、台をまたいで同じ枠で数える。
/// 外部の共有基盤（Redis など）は要らない。
/// </para>
/// <para>
/// <b>DB を読み書きできないときは、この台の中だけの数えへ落とす</b>（ログインの試行を止めないが、枠は掛け続ける）。
/// 購読 URL の枠（<c>feed</c>）は、要求が多く DB への書き込みが重いので、標準のまま台ごとの数えにする。
/// </para>
/// <para>
/// 標準の枠は、ミドルウェアが同期で判定するため、DB を非同期で呼ぶこの枠はエンドポイントフィルタ（<see cref="SharedRateLimitFilter"/>）で掛ける。
/// </para>
/// </remarks>
public sealed class SharedRateLimiter(IRateLimitStore store, TimeProvider time, ILogger<SharedRateLimiter> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Window, int Count)> _local = [];

    /// <summary>1 回の要求を数え、通してよいかを返す。</summary>
    public async ValueTask<RateLimitDecision> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var seconds = (long)window.TotalSeconds;
        var start = now.ToUnixTimeSeconds() / seconds * seconds;

        int count;
        try
        {
            count = await store.HitAsync(key, start, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "レート制限の数えを共有の DB に残せませんでした。この台の中だけで数えます");
            count = HitLocally(key, start);
        }

        var retryAfter = DateTimeOffset.FromUnixTimeSeconds(start) + window - now;
        return new RateLimitDecision(count <= limit, retryAfter);
    }

    /// <summary>この台の中だけの数え（DB が使えないとき）。</summary>
    private int HitLocally(string key, long start)
    {
        lock (_gate)
        {
            var count = _local.TryGetValue(key, out var entry) && entry.Window == start ? entry.Count + 1 : 1;
            _local[key] = (start, count);

            // 古い窓の分を捨てる（増え続けない）
            if (_local.Count > 10_000)
            {
                foreach (var stale in _local.Where(e => e.Value.Window < start).Select(e => e.Key).ToList())
                {
                    _local.Remove(stale);
                }
            }

            return count;
        }
    }
}

/// <summary>共有のレート制限を掛けるエンドポイントフィルタ。接続元（IP）ごとに、枠の名前ごとに数える。</summary>
/// <remarks>
/// <paramref name="policy"/> は数えの名前（DB の鍵に入る。変えると、数えが新しくなる）、<paramref name="parameter"/> は
/// <see cref="FixedParameters"/> の枠の名前（<c>RateLimit.json</c> の <c>Policies</c>）。回数と窓は、要求のたびにそこから引く。
/// </remarks>
public sealed class SharedRateLimitFilter(string policy, string parameter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = $"{policy}:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        var limit = http.RequestServices.GetRequiredService<FixedParameters>().Policy(parameter);
        var decision = await http.RequestServices.GetRequiredService<SharedRateLimiter>()
            .CheckAsync(key, limit.Limit, limit.Window, http.RequestAborted).ConfigureAwait(false);
        if (decision.Allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}

/// <summary>共有のレート制限の枠の定義と、エンドポイントへの掛け方。</summary>
public static class SharedRateLimits
{
    /// <summary>ログインの試行の枠（IP ごと 5 分に 10 回）。全体の枠に紛れさせると総当たりが通るので、別枠にする。</summary>
    public const string AdminLogin = "adminLogin";

    /// <summary>ログインの試行の枠を掛ける（回数と窓は <c>RateLimit.json</c> の <c>Policies:AdminLogin</c>）。</summary>
    public static TBuilder RequireAdminLoginRateLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new SharedRateLimitFilter(AdminLogin, FixedParameters.AdminLogin));

    /// <summary>Pleasanter のログインの確認の枠を掛ける（ログインの枠とは別）。</summary>
    public static TBuilder RequirePleasanterSsoRateLimit<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new SharedRateLimitFilter(
            Endpoints.AdminPleasanterSsoEndpoints.CheckRateLimitPolicy, FixedParameters.PleasanterSso));
}

/// <summary>古い数えの行を、定期的に消す。</summary>
public sealed class RateLimitPurgeService(IRateLimitStore store, TimeProvider time, ILogger<RateLimitPurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>窓が 1 日より前の行は要らない（最長の窓でも 5 分）。</summary>
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    /// <summary>古い行を消す。消した件数を返す。</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken) =>
        await store.PurgeAsync((time.GetUtcNow() - Keep).ToUnixTimeSeconds(), cancellationToken).ConfigureAwait(false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "レート制限の古い数えの掃除に失敗しました");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
