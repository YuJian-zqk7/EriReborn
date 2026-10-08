namespace EriReborn.Core.Domain;

/// <summary>
/// Explicit install outcomes. There is no "success" that hides an
/// unimplemented platform path (spec 6, 68).
/// </summary>
public enum InstallState
{
    Succeeded,
    AlreadyInstalled,
    PlatformUnavailable,
    Unsupported,
    SourceUnavailable,
    ValidationFailed,
    PermissionDenied,
    DownloadFailed,
    IntegrityFailed,
    InstallerFailed,
    VerificationFailed,

    /// <summary>
    /// The vendor installer's Authenticode signature did not verify, so it was
    /// not executed.
    /// </summary>
    PublisherUntrusted,

    /// <summary>
    /// The definition came from catalog data whose signature did not verify, so
    /// it carries no authority and must not drive an automated install.
    /// </summary>
    UntrustedDefinition,

    /// <summary>
    /// The package was verified and handed to the OS installer, which requires
    /// an explicit user confirmation (Android). This is deliberately not
    /// "Succeeded" (spec 68).
    /// </summary>
    AwaitingUserConfirmation,

    Cancelled,
    Failed,
}

/// <summary>
/// How much UI the vendor installer is allowed to show. The distinction matters:
/// a silent install proves the package works unattended, while a graphical one
/// proves the vendor's wizard completes on this machine.
/// </summary>
public enum InstallerUiMode
{
    /// <summary>No UI at all (MSI <c>/qn</c>).</summary>
    Silent,

    /// <summary>Progress only, no questions (MSI <c>/qb</c>).</summary>
    Basic,

    /// <summary>The vendor's full wizard (MSI without a quiet flag).</summary>
    Interactive,
}

public sealed record InstallRequest
{
    public required SoftwareDefinition Software { get; init; }

    public required SoftwareSource Source { get; init; }

    /// <summary>Defaults to silent, the mode an unattended tool should use.</summary>
    public InstallerUiMode UiMode { get; init; } = InstallerUiMode.Silent;

    /// <summary>
    /// Runs an installer whose Authenticode signature did not verify. Off by
    /// default: executing an unsigned binary is the exact action an attacker
    /// wants, so it has to be an explicit choice.
    /// </summary>
    public bool AllowUnsignedInstaller { get; init; }

    /// <summary>Fully resolved install directory, or null for a package-managed install.</summary>
    public string? TargetDirectory { get; init; }

    /// <summary>
    /// Where the downloaded package lands before it is run. When null the platform picks its own
    /// staging directory; the engine fills this from the user's configured download directory so a
    /// big game archive does not have to live on the system drive (spec).
    /// </summary>
    public string? DownloadDirectory { get; init; }

    // There is deliberately no VerifyAfterInstall switch. Re-detecting after an install
    // is not optional: it is the only evidence that the install did anything, and a
    // switch that could turn it off would be a switch that turns "no fake success" off.
    // The field existed, defaulted to true, and was set to true in two places — and was
    // read by nothing, which made it a promise the API never kept.

    public bool AllowScriptExecution { get; init; }
}

public sealed record InstallResult(
    InstallState State,
    string Message,
    DetectionResult? Verified = null,
    string? InstalledPath = null,
    string? LogPath = null,
    Exception? Error = null)
{
    public bool IsSuccess => State is InstallState.Succeeded or InstallState.AlreadyInstalled;
}

public enum UpdateState
{
    UpToDate,
    Updated,
    UpdateAvailable,
    PlatformUnavailable,
    Unsupported,
    SourceUnavailable,
    ValidationFailed,
    DownloadFailed,
    IntegrityFailed,
    InstallerFailed,
    VerificationFailed,
    AwaitingUserConfirmation,
    Failed,
}

public sealed record UpdateResult(
    UpdateState State,
    string Message,
    string? FromVersion = null,
    string? ToVersion = null,
    DetectionResult? Verified = null,
    Exception? Error = null);

/// <summary>Live download telemetry taken from the real transfer (spec 32).</summary>
public sealed record DownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}
