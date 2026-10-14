using System.Net;
using System.Text;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>Pleasanter の利用者・グループ・セッションの API を真似る（Pleasanter のログインで入る機能の検証用）。</summary>
internal sealed class FakePleasanterSso : HttpMessageHandler
{
    public const string AuthCookie = ".AspNetCore.Cookies";
    public const string ApiKey = "global-api-key";

    /// <summary>Pleasanter の利用者。</summary>
    public sealed record User(int TenantId, int UserId, string LoginId, string Name, int DeptId = 1);

    /// <summary>Pleasanter のグループ。</summary>
    public sealed record Group(int GroupId, bool Disabled, string[] Members, string[] Children, int TenantId = 1);

    /// <summary>送られた問い合わせ。</summary>
    public sealed record Call(string Path, string? Cookie, string Body);

    /// <summary>cookie の値 → その cookie でログインしている利用者。</summary>
    public Dictionary<string, User> Sessions { get; } = [];

    /// <summary>API キーで引ける利用者。</summary>
    public Dictionary<int, User> Users { get; } = [];

    public Dictionary<int, Group> Groups { get; } = [];

    /// <summary>cookie で呼ぶ <c>/api/users/get</c> に、利用者の API 利用の禁止（403）を返すか。</summary>
    public bool ApiForbidden { get; set; }

    /// <summary>この状態を返す（<c>null</c> なら本物らしく答える）。</summary>
    public HttpStatusCode? Fail { get; set; }

    /// <summary>JSON でない応答（ログイン画面の HTML など）を返すか。</summary>
    public bool ReturnHtml { get; set; }

    public List<Call> Calls { get; } = [];

    public User Login(string cookie, User user)
    {
        Sessions[cookie] = user;
        Users[user.UserId] = user;
        return user;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var cookie = request.Headers.TryGetValues("Cookie", out var values) && string.Join("; ", values) is { Length: > 0 } joined ? joined : null;
        var path = request.RequestUri!.AbsolutePath;
        Calls.Add(new Call(path, cookie, body));

        if (Fail is { } status)
        {
            return new HttpResponseMessage(status) { Content = new StringContent("fail") };
        }

        if (ReturnHtml)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") };
        }

        var sessionUser = SessionUserOf(cookie);
        var keyed = body.Contains($"\"ApiKey\":\"{ApiKey}\"", StringComparison.Ordinal);

        if (path == "/api/users/get")
        {
            if (sessionUser is null)
            {
                return Business(401, null);
            }

            return ApiForbidden ? Business(403, null, HttpStatusCode.Forbidden) : Ok(UserJson(sessionUser));
        }

        if (path == "/api/sessions/set")
        {
            return sessionUser is null ? Business(401, null) : Ok(null, $$"""{ "UserId": {{sessionUser.UserId}} }""");
        }

        if (path.StartsWith("/api/users/", StringComparison.Ordinal) && path.EndsWith("/get", StringComparison.Ordinal))
        {
            if (!keyed)
            {
                return Business(401, null);
            }

            var id = int.Parse(path.Split('/')[3]);
            return Users.TryGetValue(id, out var user) ? Ok(UserJson(user)) : Ok(null, null, empty: true);
        }

        if (path.StartsWith("/api/groups/", StringComparison.Ordinal) && path.EndsWith("/get", StringComparison.Ordinal))
        {
            if (!keyed)
            {
                return Business(401, null);
            }

            var id = int.Parse(path.Split('/')[3]);
            return Groups.TryGetValue(id, out var group) ? Ok(GroupJson(group)) : Ok(null, null, empty: true);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private User? SessionUserOf(string? cookie)
    {
        if (cookie is null)
        {
            return null;
        }

        foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith(AuthCookie + "=", StringComparison.Ordinal)
                && Sessions.TryGetValue(part[(AuthCookie.Length + 1)..], out var user))
            {
                return user;
            }
        }

        return null;
    }

    private static string UserJson(User u) =>
        $$"""{ "TenantId": {{u.TenantId}}, "UserId": {{u.UserId}}, "LoginId": {{JsonSerializer.Serialize(u.LoginId)}}, "Name": {{JsonSerializer.Serialize(u.Name)}}, "DeptId": {{u.DeptId}} }""";

    private static string GroupJson(Group g) =>
        $$"""{ "TenantId": {{g.TenantId}}, "GroupId": {{g.GroupId}}, "Disabled": {{(g.Disabled ? "true" : "false")}}, "GroupMembers": {{JsonSerializer.Serialize(g.Members)}}, "GroupChildren": {{JsonSerializer.Serialize(g.Children)}} }""";

    private static HttpResponseMessage Ok(string? row, string? response = null, bool empty = false) =>
        Json(HttpStatusCode.OK, response is not null
            ? $$"""{ "StatusCode": 200, "Response": {{response}} }"""
            : $$"""{ "StatusCode": 200, "Response": { "Offset": 0, "PageSize": 200, "TotalCount": {{(empty ? 0 : 1)}}, "Data": [ {{row}} ] } }""");

    private static HttpResponseMessage Business(int status, string? _, HttpStatusCode http = HttpStatusCode.OK) =>
        Json(http, $$"""{ "StatusCode": {{status}}, "Message": "x" }""");

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
