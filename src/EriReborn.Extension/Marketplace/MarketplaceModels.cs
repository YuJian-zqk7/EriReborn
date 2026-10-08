using EriReborn.Core.Domain;

namespace EriReborn.Extension.Marketplace;

/// <summary>One published extension, as described by the marketplace index (spec 35).</summary>
public sealed record MarketplaceEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Changelog { get; init; } = Array.Empty<string>();

    public SoftwareTrust Trust { get; init; } = SoftwareTrust.Unknown;

    public string? Homepage { get; init; }

    public string? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public long? SizeBytes { get; init; }

    public string TrustText => Trust.ToString();
}

public sealed record MarketplaceIndex(
    int Schema,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<MarketplaceEntry> Entries)
{
    public static MarketplaceIndex Empty { get; } = new(1, null, Array.Empty<MarketplaceEntry>());
}

public sealed record MarketplaceFetchResult(
    bool Success,
    MarketplaceIndex Index,
    bool FromCache,
    string Message);
