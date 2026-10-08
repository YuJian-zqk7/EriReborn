namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Builds the cookie-carrying clients a few platform handshakes need.
///
/// <para>
/// Both platform services need exactly the same thing here, and the reason is the
/// same: the shared client has cookies turned off on purpose, so a handshake that
/// depends on a session cookie gets its own jar rather than turning them on for
/// every provider at once. Writing that twice is how the two platforms quietly
/// drift apart.
/// </para>
/// </summary>
public static class NetworkSessions
{
    /// <summary>
    /// A client with its own cookie container, copying the shared client's default
    /// headers so the platform cannot tell the two apart.
    /// </summary>
    /// <param name="shared">The service's shared client, used as the identity template.</param>
    /// <param name="configure">Platform-specific handler tuning, if any.</param>
    public static HttpClient CreateCookieSession(HttpClient shared, Action<HttpClientHandler>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(shared);

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
        };

        configure?.Invoke(handler);

        var client = new HttpClient(handler) { Timeout = shared.Timeout };

        foreach (var header in shared.DefaultRequestHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        return client;
    }
}
