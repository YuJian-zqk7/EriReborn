using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Cloud;

/// <summary>
/// Resolves providers by id. Lookups are uniform; there is no special-cased
/// provider anywhere in the engine (spec 27).
/// </summary>
public sealed class CloudProviderRegistry
{
    private readonly Dictionary<string, ICloudProvider> _byId;

    public CloudProviderRegistry(IEnumerable<ICloudProvider> providers, IAppLogger log)
    {
        _byId = new Dictionary<string, ICloudProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (_byId.ContainsKey(provider.Id))
            {
                log.Warn("cloud.registry", $"Duplicate cloud provider id '{provider.Id}' was ignored.");
                continue;
            }

            _byId[provider.Id] = provider;
        }

        log.Info("cloud.registry", $"Registered {_byId.Count} cloud provider(s): {string.Join(", ", _byId.Keys)}.");
    }

    public IReadOnlyCollection<ICloudProvider> Providers => _byId.Values;

    /// <summary>Adds a provider at runtime, e.g. one contributed by an extension.</summary>
    public void Add(ICloudProvider provider)
    {
        if (provider is not null)
        {
            _byId[provider.Id] = provider;
        }
    }

    /// <summary>Removes a provider previously registered by id.</summary>
    public bool Remove(string id)
        => !string.IsNullOrWhiteSpace(id) && _byId.Remove(id);

    /// <summary>Ordered the same way for every consumer.</summary>
    public IReadOnlyList<ICloudProvider> InDisplayOrder()
        => CloudProviderIds.All
            .Where(id => _byId.ContainsKey(id))
            .Select(id => _byId[id])
            .Concat(_byId.Values.Where(p => !CloudProviderIds.All.Contains(p.Id)))
            .ToList();

    public ICloudProvider? Resolve(string? providerId)
        => string.IsNullOrWhiteSpace(providerId) ? null : _byId.GetValueOrDefault(providerId);

    /// <summary>Resolves the provider that hosts a given software source.</summary>
    public ICloudProvider? Resolve(SoftwareSource source)
        => source.Kind == SourceKind.CloudShare ? Resolve(source.ProviderId) : null;
}
