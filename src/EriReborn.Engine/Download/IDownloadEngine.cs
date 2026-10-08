using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Download;

/// <summary>
/// Something that can actually move bytes. Native HTTP is one; an aria2
/// integration or a browser-assisted engine would be others, and adding one must
/// not require touching the software engine (spec 61/136/137).
/// </summary>
public interface IDownloadEngine
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// Whether this engine can carry out the route. The decision must be based on
    /// what the route declares, never on which provider it came from.
    /// </summary>
    bool CanHandle(DownloadRoute route);

    Task<DownloadResult> DownloadAsync(
        DownloadRoute route,
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Picks the engine for a route.
///
/// The ordering is by capability, and deliberately not by provider: a provider
/// must never be able to demand a particular engine (spec 136).
/// </summary>
public sealed class DownloadEngineSelector(IEnumerable<IDownloadEngine> engines, IAppLogger log)
{
    private readonly List<IDownloadEngine> _engines = engines.ToList();
    private readonly IAppLogger _log = log;

    public IReadOnlyList<IDownloadEngine> Engines => _engines;

    /// <summary>Adds an engine at runtime, e.g. one contributed by an extension.</summary>
    public void Add(IDownloadEngine engine)
    {
        if (engine is not null)
        {
            _engines.Add(engine);
        }
    }

    /// <summary>
    /// Adds an engine ahead of the built-ins. A contributed accelerator has to be tried
    /// first: the built-in HTTP engine claims every route that carries a URL, so an
    /// engine appended after it would never be selected and the accelerator would never
    /// run. A preferred engine still has to decline routes it cannot carry (aria2
    /// declines when aria2c is not installed, and the built-in engine then wins).
    /// </summary>
    public void AddPreferred(IDownloadEngine engine)
    {
        if (engine is not null)
        {
            _engines.Insert(0, engine);
        }
    }

    public IDownloadEngine? Select(DownloadRoute route)
    {
        var engine = _engines.FirstOrDefault(candidate => candidate.CanHandle(route));

        if (engine is null)
        {
            // Saying so beats silently falling back to an engine that cannot do it.
            _log.Warn("download.engine", $"No engine can handle a {route.Kind} route.");
            return null;
        }

        _log.Info("download.engine", $"Route {route.Kind} -> engine '{engine.Id}'.");
        return engine;
    }
}
