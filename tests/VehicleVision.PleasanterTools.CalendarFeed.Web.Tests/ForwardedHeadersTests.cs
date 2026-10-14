using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.CalendarFeed.Web.Services;

namespace VehicleVision.PleasanterTools.CalendarFeed.Web.Tests;

/// <summary>プロキシの背後でクライアントの IP を見る（転送ヘッダ。Issue #31）。</summary>
public sealed class ForwardedHeadersTests
{
    private const string Proxy = "10.0.0.5";

    private static HttpRequestMessage LoginRequest(string remoteIp, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/login")
        {
            Content = JsonContent.Create(new { loginId = "nobody", password = "wrong password!!" }),
        };
        request.Headers.Add(RemoteIpStartupFilter.Header, remoteIp);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return request;
    }

    /// <summary>10 回使い切ったあとの 11 回目の応答。</summary>
    private static async Task<HttpStatusCode> ExhaustAsync(HttpClient client, Func<int, HttpRequestMessage> make)
    {
        for (var i = 0; i < 10; i++)
        {
            using var _ = await client.SendAsync(make(i));
        }

        using var last = await client.SendAsync(make(10));
        return last.StatusCode;
    }

    [Fact]
    public async Task 信頼するネットワークからの転送ヘッダでクライアントごとに枠が分かれる()
    {
        using var app = new TestApp(forwardedNetworks: "10.0.0.0/24");
        var client = app.Client();

        // クライアント A が 10 回使い切って 429 になっても、クライアント B は別の枠
        Assert.Equal(HttpStatusCode.TooManyRequests, await ExhaustAsync(client, _ => LoginRequest(Proxy, "203.0.113.1")));
        using var other = await client.SendAsync(LoginRequest(Proxy, "203.0.113.2"));
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task 信頼しない相手の転送ヘッダは無視する()
    {
        using var app = new TestApp(forwardedNetworks: "10.0.0.0/24");
        var client = app.Client();

        // 信頼しない相手が X-Forwarded-For を変えて枠を逃れようとしても、接続元の IP の枠で数える
        var status = await ExhaustAsync(client, i => LoginRequest("198.51.100.9", $"203.0.113.{i}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task 設定が無ければ誰の転送ヘッダも信頼しない()
    {
        using var app = new TestApp();
        var client = app.Client();

        var status = await ExhaustAsync(client, i => LoginRequest(Proxy, $"203.0.113.{i}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task ループバックも設定に書かなければ信頼しない()
    {
        using var app = new TestApp(forwardedNetworks: "10.0.0.0/24");
        var client = app.Client();

        var status = await ExhaustAsync(client, i => LoginRequest("127.0.0.1", $"203.0.113.{i}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public void カンマ区切りのCIDRを読み取る()
    {
        var networks = AppSettings.ReadForwardedProxyNetworks(Configure("10.244.0.0/16, 192.0.2.10/32"));

        Assert.Equal(["10.244.0.0/16", "192.0.2.10/32"], networks.Select(n => n.ToString()));
    }

    [Fact]
    public void 未設定なら空() =>
        Assert.Empty(AppSettings.ReadForwardedProxyNetworks(Configure(null)));

    [Fact]
    public void CIDRでない値は設定名つきで落とす()
    {
        var e = Assert.Throws<InvalidOperationException>(
            () => AppSettings.ReadForwardedProxyNetworks(Configure("10.0.0.1")));

        Assert.Contains(AppSettings.ForwardedProxyNetworks, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CIDRの書き間違いは起動時に落ちる()
    {
        using var app = new TestApp(forwardedNetworks: "not-a-cidr");

        Assert.Throws<InvalidOperationException>(() => app.Client());
    }

    private static IConfiguration Configure(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AppSettings.ForwardedProxyNetworks] = value })
            .Build();
}
