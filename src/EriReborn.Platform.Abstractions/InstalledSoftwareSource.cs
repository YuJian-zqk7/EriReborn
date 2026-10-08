namespace EriReborn.Platform.Abstractions;

/// <summary>One program the platform reports as installed.</summary>
public sealed record InstalledSoftwareInfo(
    string Name,
    string? Version = null,
    string? Publisher = null,
    string? Source = null);

/// <summary>
/// Optional capability: enumerate what is installed on this machine. It exists
/// so catalog entries that declare no detector can be given one from real
/// evidence, instead of the product inventing detection data (spec 22).
///
/// A platform that cannot enumerate installed software simply does not
/// implement this interface; callers must treat that as "no suggestions
/// available", not as "nothing is installed".
/// </summary>
public interface IInstalledSoftwareSource
{
    /// <summary>How the list was obtained, shown verbatim in the UI.</summary>
    string SourceDescription { get; }

    Task<IReadOnlyList<InstalledSoftwareInfo>> ListInstalledAsync(CancellationToken cancellationToken = default);
}
