using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

public sealed class WindowsNetworkService : INetworkService
{
    public WindowsNetworkService(HttpClient client)
    {
        Client = client;
    }

    public HttpClient Client { get; }

    /// <summary>
    /// A client with its own cookie jar. The shared client has cookies off on
    /// purpose, so a platform that needs a session gets one here rather than
    /// turning them on for every provider at once.
    /// </summary>
    public HttpClient CreateSession() => NetworkSessions.CreateCookieSession(Client);

    public static HttpClient CreateDefaultClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = false,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) EriReborn/3.0");
        return client;
    }
}
