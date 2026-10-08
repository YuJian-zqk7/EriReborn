namespace EriReborn.Core.Paths;

/// <summary>
/// Everything needed to compute a concrete install path (spec 17/18).
/// The user's explicit override always wins.
/// </summary>
public sealed record EnvironmentContext
{
    /// <summary>Root directory the user picked, e.g. E:\EriReborn.</summary>
    public required string RootPath { get; init; }

    /// <summary>Per-software explicit path chosen by the user.</summary>
    public IReadOnlyDictionary<string, string> SoftwarePathOverrides { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// When true the resolved path contains the second-level category folder.
    /// </summary>
    public bool IncludeSubcategoryFolder { get; init; } = true;

    public string? OverrideFor(string softwareId)
        => SoftwarePathOverrides.TryGetValue(softwareId, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
