namespace EriReborn.Persona;

/// <summary>
/// A voice: wording for the events the product reports. Persona is deliberately
/// separate from Skin (spec 57) — one is how the application looks, the other is
/// how it speaks, and either can change without the other.
///
/// A pack only supplies wording. Facts (counts, paths, versions) are never
/// rewritten by a persona.
/// </summary>
public sealed record PersonaPack
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Messages { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Returns this persona's wording, or the caller's own text when it has none.</summary>
    public string Say(string key, string fallback)
        => Messages.TryGetValue(key, out var message) && !string.IsNullOrWhiteSpace(message)
            ? message
            : fallback;
}

/// <summary>Message keys the application asks personas for.</summary>
public static class PersonaKeys
{
    public const string OverviewGreeting = "overview.greeting";
    public const string ScanIdle = "scan.idle";
    public const string ScanPreparing = "scan.preparing";
    public const string ScanDone = "scan.done";
    public const string ScanFailed = "scan.failed";
    public const string CatalogHealthy = "catalog.healthy";
    public const string CatalogRejected = "catalog.rejected";
    public const string MemoryUnknown = "memory.unknown";
    public const string EmptyCatalog = "catalog.empty";
}
