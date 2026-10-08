using EriReborn.Core.Domain;

namespace EriReborn.App.Shared.EnvironmentBuilder;

/// <summary>
/// A named target environment: the set of software / runtime / drivers / security
/// entries the machine should have. The user forms this instead of installing one
/// piece at a time (spec 146).
/// </summary>
public sealed record EnvironmentDefinition
{
    public required string Name { get; init; }

    public IReadOnlyList<SoftwareDefinition> Entries { get; init; } = Array.Empty<SoftwareDefinition>();

    /// <summary>Maps a catalog category to one of the four environment buckets (spec 146).</summary>
    public static string BucketFor(string? categoryId) => (categoryId ?? string.Empty).Trim() switch
    {
        "Runtime" => "Runtime",
        "System_Drivers" => "Drivers",
        "System_Security" => "Security",
        _ => "Software",
    };

    /// <summary>Entries grouped by bucket, preserving the spec 146 shape.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SoftwareDefinition>> Groups =>
        Entries
            .GroupBy(e => BucketFor(e.CategoryId))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SoftwareDefinition>)g.ToList());

    public static EnvironmentDefinition FromEntries(string name, IReadOnlyList<SoftwareDefinition> entries) =>
        new() { Name = name, Entries = entries };
}
