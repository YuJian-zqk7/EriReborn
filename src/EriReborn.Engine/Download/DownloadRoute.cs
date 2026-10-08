namespace EriReborn.Engine.Download;

/// <summary>
/// How a download will actually be performed. A share link is not a download
/// URL: something has to turn one into the other, and the result is a route
/// (spec 62/63).
/// </summary>
public enum DownloadRouteKind
{
    /// <summary>Straight from the provider's own CDN.</summary>
    ProviderDirect,

    /// <summary>Obtained through a logged-in browser session.</summary>
    BrowserAssisted,

    /// <summary>Produced by a resolver from a share link.</summary>
    Resolver,

    /// <summary>A legitimate third-party mirror.</summary>
    Mirror,
}

/// <summary>
/// Everything the download engine needs, and nothing that would force it to
/// know which provider it is talking to.
///
/// The engine must not decide things from the provider id: it must decide from
/// what the route declares it supports (spec 136).
/// </summary>
public sealed record DownloadRoute(
    DownloadRouteKind Kind,
    string Url,
    string? ProviderId = null,
    string? Referer = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool RequiresSession = false,
    bool SupportsRange = false,
    bool SupportsResume = false,
    long? ContentLength = null,
    DateTimeOffset? ExpiresAt = null,
    string? FileName = null,
    string? Note = null)
{
    /// <summary>True when the URL is known to have expired and must be re-resolved.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    /// <summary>Header set for the request, including the referer when one is needed.</summary>
    public Dictionary<string, string> BuildHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (Headers is not null)
        {
            foreach (var (key, value) in Headers)
            {
                headers[key] = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(Referer) && !headers.ContainsKey("Referer"))
        {
            headers["Referer"] = Referer;
        }

        return headers;
    }
}
