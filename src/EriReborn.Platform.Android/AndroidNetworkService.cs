using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

public sealed class AndroidNetworkService : INetworkService
{
    public AndroidNetworkService(HttpClient client)
    {
        Client = client;
    }

    public HttpClient Client { get; }

    /// <summary>
    /// A client with its own cookie jar. Android had the same gap as Windows here:
    /// the shared client cannot carry a session, and a handshake that needs one then
    /// fails on a platform where the code looks identical.
    /// </summary>
    public HttpClient CreateSession() => NetworkSessions.CreateCookieSession(
        Client,
        handler => handler.AutomaticDecompression = System.Net.DecompressionMethods.All);

    public static HttpClient CreateDefaultClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android) EriReborn/3.0");
        return client;
    }
}
