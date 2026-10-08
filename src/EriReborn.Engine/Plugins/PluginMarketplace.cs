using System.Text.Json;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// One published resource plugin.
///
/// Deliberately a different shape from the extension marketplace entry: a plugin
/// offers resources, an extension offers capability, and the two must not be
/// modelled as the same thing (spec 176/182).
/// </summary>
public sealed record PluginMarketplaceEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>How many resources the plugin carries.</summary>
    public int ResourceCount { get; init; }

    /// <summary>Which cloud platforms its sources use.</summary>
    public IReadOnlyList<string> Providers { get; init; } = Array.Empty<string>();

    public string? Homepage { get; init; }

    public string? DownloadUrl { get; init; }

    public string? SignatureUrl { get; init; }

    public string? Sha256 { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>True when the index says this plugin is signed.</summary>
    public bool IsSigned { get; init; }
}

public sealed record PluginMarketplaceIndex(
    int Schema,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<PluginMarketplaceEntry> Entries)
{
    public static PluginMarketplaceIndex Empty { get; } = new(1, null, Array.Empty<PluginMarketplaceEntry>());
}

/// <summary>Reads the plugin marketplace index, with the shared cache fallback.</summary>
public sealed class PluginMarketplaceClient(INetworkService network, IAppLogger log)
{
    public async Task<(PluginMarketplaceIndex Index, bool FromCache, string Message)> FetchAsync(
        string? indexUrl,
        string cacheFile,
        CancellationToken cancellationToken = default)
    {
        // Parsed by the shared cache before it writes anything, so a malformed index
        // can no longer replace a good cached copy with itself.
        var fetch = await RemoteIndexCache
            .FetchAsync(
                network.Client,
                log,
                indexUrl,
                cacheFile,
                "插件商城",
                Parse,
                configurationHint: "可通过 ERIREBORN_PLUGIN_INDEX 或设置页指定",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!fetch.Success || fetch.Value is null)
        {
            return (PluginMarketplaceIndex.Empty, false, fetch.Message);
        }

        return (fetch.Value, fetch.FromCache, $"{fetch.Message}（{fetch.Value.Entries.Count} 个插件）");
    }

    public static PluginMarketplaceIndex Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var schema = root.TryGetProperty("schema", out var schemaElement) && schemaElement.TryGetInt32(out var value)
            ? value
            : 1;

        DateTimeOffset? updatedAt = null;
        if (root.TryGetProperty("updatedAt", out var updated)
            && updated.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updated.GetString(), out var parsed))
        {
            updatedAt = parsed;
        }

        var entries = new List<PluginMarketplaceEntry>();

        // "plugins" rather than "extensions": the document shape is part of the
        // distinction, not an accident.
        if (root.TryGetProperty("plugins", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = Str(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                entries.Add(new PluginMarketplaceEntry
                {
                    Id = id,
                    Name = Str(item, "name") ?? id,
                    Author = Str(item, "author"),
                    Version = Str(item, "version"),
                    Description = Str(item, "description"),
                    Categories = Strings(item, "categories"),
                    Tags = Strings(item, "tags"),
                    ResourceCount = item.TryGetProperty("resourceCount", out var count) && count.TryGetInt32(out var n) ? n : 0,
                    Providers = Strings(item, "providers"),
                    Homepage = Str(item, "homepage"),
                    DownloadUrl = Str(item, "downloadUrl"),
                    SignatureUrl = Str(item, "signatureUrl"),
                    Sha256 = Str(item, "sha256"),
                    SizeBytes = item.TryGetProperty("sizeBytes", out var size) && size.TryGetInt64(out var bytes) ? bytes : null,
                    IsSigned = item.TryGetProperty("signed", out var signed) && signed.ValueKind == JsonValueKind.True,
                });
            }
        }

        return new PluginMarketplaceIndex(schema, updatedAt, entries);
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
