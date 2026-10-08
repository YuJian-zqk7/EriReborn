using EriReborn.Core.Domain;

namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Real installation pipeline (spec 19). A platform that cannot install must
/// report PlatformUnavailable/Unsupported, never Succeeded (spec 6, 68).
/// </summary>
public interface ISoftwareInstaller
{
    Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
