using EriReborn.Core.Domain;

namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Detection is a first-class capability, not a file-exists check (spec 21).
/// Implementations must return Unsupported rather than guessing (spec 22).
/// </summary>
public interface ISoftwareDetector
{
    Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default);
}
