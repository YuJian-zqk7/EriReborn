using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Engine.Cloud;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Composition for the Android platform (spec 67). Every member is a real
/// implementation; capabilities Android genuinely lacks report Unsupported.
/// </summary>
public sealed class AndroidPlatformService : IPlatformService
{
    private AndroidPlatformService(
        IAppLogger log,
        ISystemInfoService systemInfo,
        ISoftwareDetector detector,
        ISoftwareInstaller installer,
        ISoftwareUpdater updater,
        IProcessService processes,
        IFileSystemService files,
        IShellService shell,
        ICredentialStore credentials,
        INetworkService network,
        IWindowService windows,
        CloudProviderRegistry cloudRegistry,
        HttpDownloader downloader,
        DownloadCache cache,
        CloudShareLinkResolver cloudResolver,
        AndroidAssetProvisioner assetProvisioner)
    {
        Log = log;
        SystemInfo = systemInfo;
        Detector = detector;
        Installer = installer;
        Updater = updater;
        Processes = processes;
        Files = files;
        Shell = shell;
        Credentials = credentials;
        Network = network;
        Windows = windows;
        SafeArea = new AndroidSafeAreaService(log);
        CloudRegistry = cloudRegistry;
        Downloader = downloader;
        DownloadCache = cache;
        CloudResolver = cloudResolver;
        AssetProvisioner = assetProvisioner;
    }

    public string PlatformId => "android";

    public PlatformCapabilities Capabilities { get; } = new(
        CanDetectRegistry: false,
        CanDetectPackages: true,
        CanInstallPackages: true,
        CanRunProcesses: false,
        CanStoreCredentials: true,
        CanManageWindows: false,
        CanAccessCloudProviders: true);

    public ISystemInfoService SystemInfo { get; }

    public ISoftwareDetector Detector { get; }

    public ISoftwareInstaller Installer { get; }

    public ISoftwareUpdater Updater { get; }

    public IProcessService Processes { get; }

    public IFileSystemService Files { get; }

    public IShellService Shell { get; }

    public IAudioService Audio { get; } = new NoopAudioService();

    public ICredentialStore Credentials { get; }

    public INetworkService Network { get; }

    public IWindowService Windows { get; }

    /// <summary>Android reserves a status bar and a gesture bar; this reports them.</summary>
    public ISafeAreaService SafeArea { get; }

    public IAppLogger Log { get; }

    public CloudProviderRegistry CloudRegistry { get; }

    public HttpDownloader Downloader { get; }

    public DownloadCache DownloadCache { get; }

    public CloudShareLinkResolver CloudResolver { get; }

    public AndroidAssetProvisioner AssetProvisioner { get; }

    public static AndroidPlatformService Create(IAppLogger log)
    {
        var client = AndroidNetworkService.CreateDefaultClient();
        var network = new AndroidNetworkService(client);
        var files = new AndroidFileSystemService();
        var credentials = new AndroidCredentialStore(
            Path.Combine(files.GetApplicationDataDirectory(), "credentials"),
            log);
        var cache = new DownloadCache(Path.Combine(files.GetTempDirectory(), "cache"));
        var downloader = new HttpDownloader(client, files, cache, log);

        // No cloud platform is constructed here any more. The five providers are
        // contributed by official extensions and registered through the extension host,
        // exactly as on Windows: the registry starts empty and is filled while
        // extensions load, so this assembly names no platform of its own (spec 26/76).
        var cloudRegistry = new CloudProviderRegistry(Array.Empty<ICloudProvider>(), log);
        var cloudResolver = new CloudShareLinkResolver(cloudRegistry, downloader, log);
        var detector = new AndroidPackageDetector(log);
        var installer = new AndroidSoftwareInstaller(files, downloader, cloudResolver, log);

        // Updating on Android follows the same install handoff; there is no
        // silent update path, so the engine reports AwaitingUserConfirmation.
        var updater = new AndroidSoftwareUpdater(installer, log);

        return new AndroidPlatformService(
            log,
            new AndroidSystemInfoService(),
            detector,
            installer,
            updater,
            new AndroidProcessService(),
            files,
            new AndroidShellService(log),
            credentials,
            network,
            new AndroidWindowService(),
            cloudRegistry,
            downloader,
            cache,
            cloudResolver,
            new AndroidAssetProvisioner(log));
    }
}
