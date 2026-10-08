namespace EriReborn.Platform.Abstractions;

/// <summary>
/// What the current platform implementation can actually do. The UI uses this
/// to explain missing capability instead of showing a fake success (spec 6).
/// </summary>
public sealed record PlatformCapabilities(
    bool CanDetectRegistry,
    bool CanDetectPackages,
    bool CanInstallPackages,
    bool CanRunProcesses,
    bool CanStoreCredentials,
    bool CanManageWindows,
    bool CanAccessCloudProviders)
{
    public static PlatformCapabilities None { get; } = new(false, false, false, false, false, false, false);
}
