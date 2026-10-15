using System.Globalization;
using Microsoft.Net.Http.Headers;
using VehicleVision.PleasanterTools.CalendarFeed.Localization;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Localization;

/// <summary>要求の言語（<c>Accept-Language</c>）。サーバが返す文言の言語を決める。</summary>
/// <remarks>
/// 管理画面が、自分の言語を <c>Accept-Language</c> で明示して送る（ブラウザの言語設定ではなく、画面で選んだ言語）。
/// 対応する言語がなければ <c>null</c>（<see cref="ServerMessages"/> が <c>en</c> → <c>ja</c> の順で落とす）。地域（<c>en-US</c>）は見ない。
/// </remarks>
public static class RequestLanguage
{
    /// <summary>要求の言語。品質値（<c>q</c>）の高い順に見て、最初に対応する言語を返す。</summary>
    public static string? Of(HttpContext? http)
    {
        if (http is null || !http.Request.Headers.TryGetValue(HeaderNames.AcceptLanguage, out var values))
        {
            return null;
        }

        var candidates = new List<(string Code, double Quality, int Order)>();
        var order = 0;
        foreach (var value in values)
        {
            foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split(';', StringSplitOptions.TrimEntries);
                var quality = 1.0;
                foreach (var parameter in pieces.Skip(1))
                {
                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(parameter[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var q))
                    {
                        quality = q;
                    }
                }

                candidates.Add((pieces[0].Split('-', '_')[0].ToLowerInvariant(), quality, order++));
            }
        }

        return candidates
            .Where(c => c.Quality > 0 && ServerMessages.Languages.Contains(c.Code))
            .OrderByDescending(c => c.Quality)
            .ThenBy(c => c.Order)
            .Select(c => c.Code)
            .FirstOrDefault();
    }
}

/// <summary>
/// 言語ごとの文言で返すエラー応答。<b>応答を書く時点で要求の言語を決める</b>ので、呼ぶ側は <c>HttpContext</c> を持ち回らなくてよい。
/// 本文は <c>{ "error": "文言" }</c>（従来と同じ形）。
/// </summary>
public sealed class ApiError(int statusCode, string key, object[] args) : IResult
{
    /// <summary>エラー応答を作る。</summary>
    public static IResult Of(int statusCode, string key, params object[] args) => new ApiError(statusCode, key, args);

    public Task ExecuteAsync(HttpContext httpContext)
    {
        var message = ServerMessages.Get(key, RequestLanguage.Of(httpContext), args);
        return Results.Json(new { error = message }, statusCode: statusCode).ExecuteAsync(httpContext);
    }
}
