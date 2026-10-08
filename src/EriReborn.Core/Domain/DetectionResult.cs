namespace EriReborn.Core.Domain;

/// <summary>
/// Outcome of a detection attempt. "Unknown" must never be collapsed into
/// "Missing" (spec 22).
/// </summary>
public enum DetectionOutcome
{
    Detected,
    NotDetected,
    Unknown,
    Unsupported,
    Error,
}

public sealed record DetectionResult(
    DetectionOutcome Outcome,
    string? Version = null,
    string? DisplayName = null,
    string? Detail = null,
    string? Source = null)
{
    public static DetectionResult Detected(string? version = null, string? displayName = null, string? source = null, string? detail = null)
        => new(DetectionOutcome.Detected, version, displayName, detail, source);

    public static DetectionResult NotDetected(string? source = null, string? detail = null)
        => new(DetectionOutcome.NotDetected, null, null, detail, source);

    public static DetectionResult Unknown(string? detail = null, string? source = null)
        => new(DetectionOutcome.Unknown, null, null, detail, source);

    public static DetectionResult Unsupported(string? detail = null, string? source = null)
        => new(DetectionOutcome.Unsupported, null, null, detail, source);

    public static DetectionResult Error(string detail, string? source = null)
        => new(DetectionOutcome.Error, null, null, detail, source);

    /// <summary>Maps a detection outcome onto the richer software status.</summary>
    public SoftwareStatus ToStatus() => Outcome switch
    {
        DetectionOutcome.Detected => SoftwareStatus.Installed,
        DetectionOutcome.NotDetected => SoftwareStatus.Missing,
        DetectionOutcome.Unsupported => SoftwareStatus.Unsupported,
        DetectionOutcome.Error => SoftwareStatus.Failed,
        _ => SoftwareStatus.Unknown,
    };
}
