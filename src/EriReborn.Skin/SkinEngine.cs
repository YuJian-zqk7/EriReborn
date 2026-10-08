using EriReborn.Core.Logging;

namespace EriReborn.Skin;

/// <summary>
/// Owns skin lifetime. Switching really unloads: the previous manifest and any
/// per-skin cache are dropped before the next skin is applied, so no colour,
/// font, asset or persona leaks across skins (spec 47).
/// </summary>
public sealed class SkinEngine(IAppLogger log)
{
    private readonly IAppLogger _log = log;
    private readonly List<SkinManifest> _available = new();

    /// <summary>
    /// What discovery found, before any override was layered on. Kept so a deleted override can
    /// be undone in a running session: without the original, the edited manifest registered
    /// earlier would linger and the app would keep showing text the user just deleted.
    /// </summary>
    private readonly List<SkinManifest> _discovered = new();

    private readonly Dictionary<string, IDisposable> _skinScopedResources = new(StringComparer.Ordinal);

    public event EventHandler<SkinManifest>? SkinChanged;

    public IReadOnlyList<SkinManifest> Available => _available;

    public SkinManifest? Active { get; private set; }

    /// <summary>Discovers every skins/&lt;id&gt;/skin.json under a root directory.</summary>
    /// <remarks>
    /// Only the shipped packs are discovered. A user skin is a thin file that names a base skin
    /// and lists what it changes, so it cannot be read as a skin on its own — it is resolved over
    /// its base and registered with <see cref="Register"/> afterwards.
    /// </remarks>
    public async Task<IReadOnlyList<SkinManifest>> DiscoverAsync(
        string skinsRoot,
        CancellationToken cancellationToken = default)
    {
        _available.Clear();
        _discovered.Clear();

        await ScanAsync(skinsRoot, cancellationToken).ConfigureAwait(false);

        _discovered.AddRange(_available);
        _log.Info("skin.discover", $"Discovered {_available.Count} skin(s).");
        return _available;
    }

    private async Task ScanAsync(string skinsRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(skinsRoot))
        {
            _log.Warn("skin.discover", $"Skins directory not found: {skinsRoot}");
            return;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(skinsRoot, "skin.json", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                var manifest = SkinManifestParser.Parse(json, manifestPath);
                if (_available.Any(s => string.Equals(s.Id, manifest.Id, StringComparison.Ordinal)))
                {
                    _log.Warn("skin.duplicate", $"Duplicate skin id '{manifest.Id}' in '{manifestPath}' was ignored.");
                    continue;
                }

                _available.Add(manifest);

                // Reported rather than ignored: a misspelled slot is invisible by
                // construction — nothing reads it, so the skin simply shows nothing
                // where the author expected their art.
                var assets = SkinAssets.Validate(manifest);
                if (!assets.IsClean)
                {
                    _log.Warn("skin.asset_slot", $"Skin '{manifest.Id}' 的资源槽有问题：{assets.Describe()}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("skin.parse", $"Failed to read skin manifest '{manifestPath}'.", ex);
            }
        }
    }

    /// <summary>The skins discovery found, before any override was applied.</summary>
    public IReadOnlyList<SkinManifest> Discovered => _discovered;

    /// <summary>Drops every registered skin and returns to what discovery found.</summary>
    public void ResetToDiscovered()
    {
        _available.Clear();
        _available.AddRange(_discovered);
    }

    /// <summary>
    /// Adds a skin, or replaces the one with the same id.
    ///
    /// <para>
    /// Discovery reads the shipped tree, which is read-only. A skin the user created or
    /// renamed therefore arrives here afterwards — and it has to be indistinguishable from a
    /// shipped one, including to the picker, which is why it goes into the same list.
    /// </para>
    /// </summary>
    public void Register(SkinManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var index = _available.FindIndex(skin => string.Equals(skin.Id, manifest.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _available[index] = manifest;
        }
        else
        {
            _available.Add(manifest);
        }
    }

    /// <summary>Registers a resource that must be disposed when this skin is unloaded.</summary>
    public void RegisterScopedResource(string skinId, IDisposable resource)
    {
        if (_skinScopedResources.TryGetValue(skinId, out var existing))
        {
            existing.Dispose();
        }

        _skinScopedResources[skinId] = resource;
    }

    /// <summary>Unload → clear cache → load → notify (spec 47).</summary>
    public Task<SkinManifest?> ApplyAsync(SkinManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Unload();
        Active = manifest;
        var validation = SkinAssets.Validate(manifest);
        if (!validation.IsClean)
        {
            _log.Warn("skin.asset_slot", $"Skin '{manifest.Id}' 的资源槽有问题：{validation.Describe()}");
        }

        _log.Info("skin.apply", $"Applied skin '{manifest.Id}' ({manifest.Name}, layout={manifest.Layout}).");
        SkinChanged?.Invoke(this, manifest);
        return Task.FromResult<SkinManifest?>(manifest);
    }

    public Task<SkinManifest?> ApplyByIdAsync(string skinId, CancellationToken cancellationToken = default)
    {
        var manifest = _available.FirstOrDefault(s => string.Equals(s.Id, skinId, StringComparison.Ordinal));
        if (manifest is null)
        {
            _log.Warn("skin.apply", $"Skin '{skinId}' is not available.");
            return Task.FromResult<SkinManifest?>(null);
        }

        return ApplyAsync(manifest, cancellationToken);
    }

    /// <summary>Drops the active skin and everything cached for it.</summary>
    public void Unload()
    {
        if (Active is not null)
        {
            _log.Info("skin.unload", $"Unloading skin '{Active.Id}'.");
        }

        foreach (var pair in _skinScopedResources)
        {
            try
            {
                pair.Value.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("skin.unload", $"Failed to dispose resources for skin '{pair.Key}': {ex.Message}");
            }
        }

        _skinScopedResources.Clear();
        Active = null;
    }
}
