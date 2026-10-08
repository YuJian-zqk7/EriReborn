using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Download;

/// <summary>
/// The built-in engine: plain resumable HTTP. Everything it needs comes from the
/// route, and it never inspects the provider id.
/// </summary>
public sealed class NativeHttpDownloadEngine(HttpDownloader downloader, IAppLogger log) : IDownloadEngine
{
    private readonly HttpDownloader _downloader = downloader;
    private readonly IAppLogger _log = log;

    public string Id => "native-http";

    public string DisplayName => "内置 HTTP 下载器";

    /// <summary>
    /// Handles every route that carries a URL. A route needing a browser session
    /// belongs to an engine that has one, so it declines those rather than
    /// silently attempting a request that is certain to be rejected.
    /// </summary>
    public bool CanHandle(DownloadRoute route)
    {
        if (string.IsNullOrWhiteSpace(route.Url))
        {
            return false;
        }

        if (route.Kind == DownloadRouteKind.BrowserAssisted && route.RequiresSession)
        {
            return false;
        }

        return true;
    }

    public Task<DownloadResult> DownloadAsync(
        DownloadRoute route,
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _log.Info(
            "download.route",
            $"Downloading {request.FileName ?? request.Url} via {route.Kind}"
            + (route.SupportsResume ? " (resume declared)" : string.Empty));

        // The route's headers and provider travel with the request, so the
        // downloader sees one uniform shape whatever the source was.
        var merged = request with
        {
            Url = route.Url,
            Headers = route.BuildHeaders(),
            ProviderId = request.ProviderId ?? route.ProviderId,
            FileName = request.FileName ?? route.FileName,
        };

        return _downloader.DownloadAsync(merged, progress, cancellationToken);
    }
}
