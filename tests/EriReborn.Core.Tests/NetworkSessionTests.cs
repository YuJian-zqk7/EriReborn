using EriReborn.Core.Tests.TestSupport;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A cookie session has to actually carry cookies, and it has to be its own jar.
/// The shared client has them off deliberately, so "it works on my provider" must
/// not be the only evidence that a handshake depending on one can function.
/// </summary>
public sealed class NetworkSessionTests
{
    private static HttpClient SharedClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EriReborn-Test/1.0");
        return client;
    }

    [Fact]
    public void A_session_is_a_different_client_that_keeps_the_identity()
    {
        var shared = SharedClient();

        var session = NetworkSessions.CreateCookieSession(shared);

        Assert.NotSame(shared, session);
        Assert.Equal("EriReborn-Test/1.0", string.Join(" ", session.DefaultRequestHeaders.GetValues("User-Agent")));
        Assert.Equal(shared.Timeout, session.Timeout);
    }

    [Fact]
    public void Two_sessions_do_not_share_a_jar()
    {
        var shared = SharedClient();

        var first = NetworkSessions.CreateCookieSession(shared);
        var second = NetworkSessions.CreateCookieSession(shared);

        // A shared jar is how one platform's login leaks into another's request.
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task A_cookie_set_by_the_server_is_sent_back()
    {
        using var server = new TestHttpServer(request =>
            request.CookieHeader is null
                ? new TestResponse(200, System.Text.Encoding.UTF8.GetBytes("first"))
                {
                    ExtraHeaders = new Dictionary<string, string> { ["Set-Cookie"] = "sid=abc123; Path=/" },
                }
                : new TestResponse(200, System.Text.Encoding.UTF8.GetBytes("carried:" + request.CookieHeader)));

        using var shared = SharedClient();
        using var session = NetworkSessions.CreateCookieSession(shared);

        var first = await session.GetStringAsync(server.Url("/a"));
        var second = await session.GetStringAsync(server.Url("/b"));

        Assert.Equal("first", first);
        Assert.Contains("sid=abc123", second);

        // And the session actually sent it, rather than the server inventing one.
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task The_shared_client_still_does_not_keep_cookies()
    {
        using var server = new TestHttpServer(request =>
            new TestResponse(200, System.Text.Encoding.UTF8.GetBytes(request.CookieHeader ?? "none"))
            {
                ExtraHeaders = new Dictionary<string, string> { ["Set-Cookie"] = "sid=abc123; Path=/" },
            });

        using var shared = SharedClient();

        await shared.GetStringAsync(server.Url("/a"));
        var second = await shared.GetStringAsync(server.Url("/b"));

        // Proves the sessions exist for a reason: turning cookies on for everyone
        // would leak sessions between providers.
        Assert.Equal("none", second);
    }
}
