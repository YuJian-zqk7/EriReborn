using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Download;

/// <summary>How a resolution ended. Distinct states, because they need different words.</summary>
public enum ResolveStatus
{
    Resolved,

    /// <summary>Nothing can turn this source into a download; it is not a failure.</summary>
    NotADownload,

    /// <summary>A real failure during resolution.</summary>
    Failed,

    /// <summary>The source needs credentials that are missing or expired.</summary>
    AuthRequired,

    /// <summary>The provider could not be asked, or answered with something unusable.</summary>
    ProviderError,
}

public sealed record ResolveOutcome(
    ResolveStatus Status,
    DownloadRoute? Route,
    string Message,
    CloudErrorKind Error = CloudErrorKind.None)
{
    public bool IsResolved => Status == ResolveStatus.Resolved && Route is not null;

    public static ResolveOutcome Ok(DownloadRoute route, string message)
        => new(ResolveStatus.Resolved, route, message);

    /// <summary>
    /// The source is legitimate but simply does not need a download route.
    /// Saying this is not the same as failing.
    /// </summary>
    public static ResolveOutcome NotADownload(string message)
        => new(ResolveStatus.NotADownload, null, message);

    public static ResolveOutcome Fail(ResolveStatus status, string message, CloudErrorKind error = CloudErrorKind.None)
        => new(status, null, message, error);
}

/// <summary>
/// Turns a <see cref="SoftwareSource"/> into a concrete <see cref="DownloadRoute"/>.
///
/// A share link is not a download URL; something has to bridge the two, and that
/// something is this layer. Adding a new way to obtain a download must mean
/// adding a resolver, never touching the download engine or the software engine
/// (spec 62/165/201).
/// </summary>
public interface IDownloadResolver
{
    string Id { get; }

    /// <summary>
    /// Whether this resolver understands the source. The decision is made from
    /// the source's own kind, never from a provider id (spec 135).
    /// </summary>
    bool CanResolve(SoftwareSource source);

    Task<ResolveOutcome> ResolveAsync(SoftwareSource source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Picks the resolver for a source and reports honestly when there is none.
/// </summary>
public sealed class DownloadResolverRegistry(IEnumerable<IDownloadResolver> resolvers, IAppLogger log)
{
    private readonly IReadOnlyList<IDownloadResolver> _resolvers = resolvers.ToList();
    private readonly IAppLogger _log = log;

    public IReadOnlyList<IDownloadResolver> Resolvers => _resolvers;

    public IDownloadResolver? ResolverFor(SoftwareSource source)
        => _resolvers.FirstOrDefault(resolver => resolver.CanResolve(source));

    public async Task<ResolveOutcome> ResolveAsync(
        SoftwareSource source,
        CancellationToken cancellationToken = default)
    {
        var resolver = ResolverFor(source);

        if (resolver is null)
        {
            // "No resolver" must not look like "resolution failed": the first is a
            // gap in this build, the second is the world's fault.
            _log.Warn("download.resolve", $"No resolver handles a {source.Kind} source.");
            return ResolveOutcome.Fail(
                ResolveStatus.NotADownload,
                $"本版本没有能处理「{source.Kind}」来源的解析器。",
                CloudErrorKind.Unsupported);
        }

        var outcome = await resolver.ResolveAsync(source, cancellationToken).ConfigureAwait(false);
        _log.Info("download.resolve", $"{source.Kind} -> {resolver.Id}: {outcome.Status} ({outcome.Message})");
        return outcome;
    }
}
