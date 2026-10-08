using EriReborn.Core.Domain;

namespace EriReborn.Engine.Download;

/// <summary>
/// Official and plain-HTTP sources are already download URLs: nothing has to be
/// resolved, only normalised into a route so everything downstream sees one
/// shape.
/// </summary>
public sealed class DirectUrlResolver : IDownloadResolver
{
    public string Id => "direct-url";

    /// <summary>
    /// Claims the kind, not the individual source. A missing URL is a defect in
    /// the source, and reporting it as "no resolver for this kind" would send the
    /// reader looking in the wrong place.
    /// </summary>
    public bool CanResolve(SoftwareSource source)
        => source.Kind is SourceKind.Official or SourceKind.HttpUrl;

    public Task<ResolveOutcome> ResolveAsync(SoftwareSource source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            return Task.FromResult(ResolveOutcome.Fail(
                ResolveStatus.Failed,
                "该来源没有可用的直链。"));
        }

        var route = new DownloadRoute(
            DownloadRouteKind.ProviderDirect,
            source.Url,
            FileName: source.FileName,
            ContentLength: null,
            Note: source.Sha256 is null ? null : "来源自带 SHA-256");

        return Task.FromResult(ResolveOutcome.Ok(route, "直链来源，无需解析。"));
    }
}
