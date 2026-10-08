using EriReborn.Core.Logging;

namespace EriReborn.Platform.Abstractions;

/// <summary>
/// The single platform surface injected into Core-facing services (spec 6).
/// Windows and Android each provide a real implementation; there is no Noop
/// fallback on the production path (spec 68).
/// </summary>
public interface IPlatformService
{
    string PlatformId { get; }

    PlatformCapabilities Capabilities { get; }

    ISystemInfoService SystemInfo { get; }

    ISoftwareDetector Detector { get; }

    ISoftwareInstaller Installer { get; }

    ISoftwareUpdater Updater { get; }

    IProcessService Processes { get; }

    IFileSystemService Files { get; }

    ICredentialStore Credentials { get; }

    INetworkService Network { get; }

    IWindowService Windows { get; }

    /// <summary>
    /// Opens folders in the platform file manager. Windows uses explorer.exe;
    /// Android reports the sandbox path instead of crashing (spec 5/67).
    /// </summary>
    IShellService Shell { get; }

    /// <summary>Plays short sound effects (download complete, etc.).</summary>
    IAudioService Audio { get; }

    /// <summary>
    /// The space the system reserves. On a phone that is the status bar and the
    /// gesture bar; on a decorated desktop window it is genuinely nothing.
    /// </summary>
    ISafeAreaService SafeArea { get; }

    IAppLogger Log { get; }
}
