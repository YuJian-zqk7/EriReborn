using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Engine.Cloud;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Composition for the Windows platform. Every member is a real
/// implementation; there is no Noop fallback on this path (spec 67/68).
/// </summary>
public sealed class WindowsPlatformService : IPlatformService
{
    private WindowsPlatformService(
        IAppLogger log,
        ISystemInfoService systemInfo,
        ISoftwareDetector detector,
        ISoftwareInstaller installer,
        ISoftwareUpdater updater,
        IProcessService processes,
        IFileSystemService files,
        ICredentialStore credentials,
        INetworkService network,
        IWindowService windows,
        IShellService shell,
        IAudioService audio,
        CloudProviderRegistry cloudRegistry,
        HttpDownloader downloader,
        DownloadCache cache,
        CloudShareLinkResolver cloudResolver,
        DownloadEngineSelector engines)
    {
        Log = log;
        SystemInfo = systemInfo;
        Detector = detector;
        Installer = installer;
        Updater = updater;
        Processes = processes;
        Files = files;
        Credentials = credentials;
        Network = network;
        Windows = windows;
        Shell = shell;
        Audio = audio;
        CloudRegistry = cloudRegistry;
        Downloader = downloader;
        DownloadCache = cache;
        CloudResolver = cloudResolver;
        Engines = engines;
    }

    public string PlatformId => "windows";

    public PlatformCapabilities Capabilities { get; } = new(
        CanDetectRegistry: true,
        CanDetectPackages: true,
        CanInstallPackages: true,
        CanRunProcesses: true,
        CanStoreCredentials: true,
        CanManageWindows: true,
        CanAccessCloudProviders: true);

    public ISystemInfoService SystemInfo { get; }

    public ISoftwareDetector Detector { get; }

    public ISoftwareInstaller Installer { get; }

    public ISoftwareUpdater Updater { get; }

    public IProcessService Processes { get; }

    public IFileSystemService Files { get; }

    public ICredentialStore Credentials { get; }

    public INetworkService Network { get; }

    public IWindowService Windows { get; }

    public IShellService Shell { get; }

    public IAudioService Audio { get; }

    public IAppLogger Log { get; }

    public CloudProviderRegistry CloudRegistry { get; }

    public HttpDownloader Downloader { get; }

    public DownloadCache DownloadCache { get; }

    /// <summary>
    /// The download engines the platform routes to. It is built with the always-present
    /// built-in engine only; official extensions add more (e.g. an aria2 accelerator)
    /// through the extension host. The installer and updater read this selector at
    /// download time, so an engine registered after they were constructed is still used.
    /// </summary>
    public DownloadEngineSelector Engines { get; }

    /// <summary>
    /// A decorated desktop window reserves nothing: the client area is the whole
    /// window, so zero is the correct answer rather than a placeholder.
    /// </summary>
    public ISafeAreaService SafeArea { get; } = new NoInsetsSafeAreaService();

    public CloudShareLinkResolver CloudResolver { get; }

    /// <summary>
    /// The single place where Windows services are constructed (spec 67).
    /// </summary>
    public static WindowsPlatformService Create(IAppLogger log)
    {
        var client = WindowsNetworkService.CreateDefaultClient();
        var network = new WindowsNetworkService(client);
        var files = new WindowsFileSystemService();
        var processes = new WindowsProcessService(log);
        var credentials = new DpapiCredentialStore(
            Path.Combine(files.GetApplicationDataDirectory(), "credentials"),
            log);
        var cache = new DownloadCache(Path.Combine(files.GetTempDirectory(), "cache"));
        var downloader = new HttpDownloader(client, files, cache, log, segments: SegmentPolicy.Desktop);

        // No cloud platform is constructed here any more. The five providers are
        // contributed by official extensions and registered through the extension host,
        // so the registry starts empty and is filled while extensions load. This is what
        // makes this assembly name no platform of its own (spec 26/76).
        var cloudRegistry = new CloudProviderRegistry(Array.Empty<ICloudProvider>(), log);
        var cloudResolver = new CloudShareLinkResolver(cloudRegistry, downloader, log);

        var detector = new WindowsRegistryDetector(processes, log);

        // Only the built-in engine is constructed here: it has no external dependency.
        // aria2 is contributed by an official extension, so listing it here is no longer
        // necessary (spec 61/136).
        var engines = new DownloadEngineSelector(
            new IDownloadEngine[]
            {
                new NativeHttpDownloadEngine(downloader, log),
            },
            log);

        var installer = new WindowsSoftwareInstaller(processes, files, engines, cloudResolver, log);
        var updater = new WindowsSoftwareUpdater(processes, files, engines, log);
        var windowService = new WindowsWindowService(log);

        return new WindowsPlatformService(
            log,
            new WindowsSystemInfoService(),
            detector,
            installer,
            updater,
            processes,
            files,
            credentials,
            network,
            windowService,
            new WindowsShellService(log),
            new WindowsAudioService(log),
            cloudRegistry,
            downloader,
            cache,
            cloudResolver,
            engines);
    }
}
