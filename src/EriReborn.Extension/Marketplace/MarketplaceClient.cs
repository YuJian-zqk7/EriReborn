using System.Text.Json;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Extension.Marketplace;

/// <summary>
/// Reads the marketplace index from a remote URL with an on-disk cache, so a
/// network outage degrades to cached content instead of an empty page
/// (spec 35/62). The index URL is configuration, never hard-coded.
/// </summary>
public sealed class MarketplaceClient(INetworkService network, IAppLogger log)
{
    private readonly INetworkService _network = network;
    private readonly IAppLogger _log = log;

    public async Task<MarketplaceFetchResult> FetchAsync(
        string? indexUrl,
        string cacheFile,
        CancellationToken cancellationToken = default)
    {
        // The caching rule is identical for every marketplace, so it lives in one
        // place. This class keeps only what is specific to extensions: the document
        // shape and the wording.
        var fetch = await RemoteIndexCache
            .FetchAsync(
                _network.Client,
                _log,
                indexUrl,
                cacheFile,
                "商城",
                Parse,
                configurationHint: "可通过 ERIREBORN_MARKETPLACE_INDEX 或设置页指定",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return fetch.Success && fetch.Value is not null
            ? new MarketplaceFetchResult(
                true,
                fetch.Value,
                fetch.FromCache,
                $"{fetch.Message}（{fetch.Value.Entries.Count} 个扩展）。")
            : new MarketplaceFetchResult(false, MarketplaceIndex.Empty, false, fetch.Message);
    }

    public static MarketplaceIndex Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var schema = root.TryGetProperty("schema", out var schemaElement) && schemaElement.TryGetInt32(out var s) ? s : 1;
        DateTimeOffset? updatedAt = null;
        if (root.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updated.GetString(), out var parsed))
        {
            updatedAt = parsed;
        }

        var entries = new List<MarketplaceEntry>();
        if (root.TryGetProperty("extensions", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = Str(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                entries.Add(new MarketplaceEntry
                {
                    Id = id,
                    Name = Str(item, "name") ?? id,
                    Author = Str(item, "author"),
                    Version = Str(item, "version"),
                    Description = Str(item, "description"),
                    Categories = Array(item, "categories"),
                    Tags = Array(item, "tags"),
                    Permissions = Array(item, "permissions"),
                    Dependencies = Array(item, "dependencies"),
                    Changelog = Array(item, "changelog"),
                    Trust = ParseTrust(Str(item, "trust")),
                    Homepage = Str(item, "homepage"),
                    DownloadUrl = Str(item, "downloadUrl"),
                    Sha256 = Str(item, "sha256"),
                    SizeBytes = item.TryGetProperty("sizeBytes", out var size) && size.TryGetInt64(out var bytes) ? bytes : null,
                });
            }
        }

        return new MarketplaceIndex(schema, updatedAt, entries);
    }

    private static SoftwareTrust ParseTrust(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "official" => SoftwareTrust.Official,
        "verified" => SoftwareTrust.Verified,
        "community" => SoftwareTrust.Community,
        "invalid" => SoftwareTrust.Invalid,
        _ => SoftwareTrust.Unknown,
    };

    private static IReadOnlyList<string> Array(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<string>();
        }

        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
