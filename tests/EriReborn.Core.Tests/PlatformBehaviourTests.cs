using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Dual-platform behaviour: the default skin follows the running platform, the
/// Android detector kind round-trips through the catalog, and an install that
/// still needs user confirmation is never reported as success (spec 6/8/68).
/// </summary>
public sealed class PlatformBehaviourTests
{
    private static async Task<(AppHost Host, string UserData)> CreateHostAsync(string platformId)
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = EriReborn.App.Shared.AppPaths.Detect(userDataOverride: userData);

        var network = new TestNetworkService(new HttpClient());
        var credentials = new InMemoryCredentialStore();
        var files = new TestFileSystemService(userData);
        var platform = new TestPlatform(files, network, credentials, platformId);

        ICloudProvider[] providers =
        {
            new Provider123(network, credentials),
            new ProviderBaidu(network, credentials),
            new ProviderQuark(network, credentials),
            new ProviderLanzou(network, credentials),
            new ProviderXunlei(network, credentials),
        };

        var registry = new CloudProviderRegistry(providers, AppLog.For("Test"));
        var host = await AppHost.CreateAsync(paths, platform, registry, AppLog.For("Test"));
        return (host, userData);
    }

    [Fact]
    public async Task Windows_platform_defaults_to_the_eri_windows_skin()
    {
        var (host, userData) = await CreateHostAsync("windows");
        try
        {
            Assert.Equal("eri_windows", host.Skins.Active?.Id);
            Assert.False(host.Skins.Active?.IsMobile);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Android_platform_defaults_to_the_eri_android_mobile_skin()
    {
        var (host, userData) = await CreateHostAsync("android");
        try
        {
            Assert.Equal("eri_android", host.Skins.Active?.Id);
            Assert.True(host.Skins.Active?.IsMobile);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Android_package_detector_kind_round_trips_through_the_catalog()
    {
        const string json = """
        {
          "schema": 3,
          "catalog": "android-apps",
          "items": [
            {
              "id": "termux",
              "name": "Termux",
              "categoryId": "Development",
              "directory": "Termux",
              "tier": "Optional",
              "trust": "Community",
              "mode": "Install",
              "detector": { "kind": "android_package", "param": "com.termux" },
              "sources": [ { "kind": "HttpUrl", "url": "https://example.invalid/termux.apk" } ]
            }
          ]
        }
        """;

        Assert.Equal(DetectorKind.AndroidPackage, LegacyCatalogImporter.ParseDetectorKind("android_package"));

        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "android-apps.json");
        File.WriteAllText(file, json);

        var reader = new CatalogReader(AppLog.For("Test"));
        var catalog = await reader.LoadFilesAsync(new[] { file });

        var item = Assert.Single(catalog.Software);
        Assert.Equal(DetectorKind.AndroidPackage, item.Detector.Kind);
        Assert.Equal("com.termux", item.Detector.Param);
        Assert.Equal(0, catalog.Report.ItemsRejected);

        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public async Task Install_awaiting_user_confirmation_is_not_reported_as_success()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var files = new TestFileSystemService(temp);
        var basePlatform = new TestPlatform(files, new TestNetworkService(new HttpClient()), new InMemoryCredentialStore());
        var platform = new ConfirmingPlatform(basePlatform);

        var engine = new SoftwareEngine(platform, new PathResolver(), new InstallationRegistry(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "i.json"), AppLog.For("Test")), new DetectionHintStore(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "h.json"), AppLog.For("Test")), AppLog.For("Test"));
        var software = new SoftwareDefinition
        {
            Id = "termux",
            Name = "Termux",
            CategoryId = "Development",
            DirectoryName = "Termux",
            Trust = SoftwareTrust.Community,
            Provenance = CatalogProvenance.Official,
            Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/termux.apk" } },
        };

        var result = await engine.InstallAsync(software, new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.AwaitingUserConfirmation, result.State);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Verified);
    }

    [Fact]
    public void Awaiting_confirmation_is_distinct_from_every_success_state()
    {
        Assert.False(new InstallResult(InstallState.AwaitingUserConfirmation, "x").IsSuccess);
        Assert.True(new InstallResult(InstallState.Succeeded, "x").IsSuccess);
        Assert.True(new InstallResult(InstallState.AlreadyInstalled, "x").IsSuccess);
    }

    private static void Cleanup(string userData)
    {
        try
        {
            if (Directory.Exists(userData))
            {
                Directory.Delete(userData, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Simulates the Android handoff: the OS installer needs the user's confirmation.</summary>
    private sealed class ConfirmingPlatform(IPlatformService inner) : IPlatformService
    {
        public string PlatformId => "android";

        public PlatformCapabilities Capabilities => inner.Capabilities;

        public ISystemInfoService SystemInfo => inner.SystemInfo;

        public ISoftwareDetector Detector => inner.Detector;

        public ISoftwareInstaller Installer { get; } = new ConfirmingInstaller();

        public ISoftwareUpdater Updater => inner.Updater;

        public IProcessService Processes => inner.Processes;

        public IFileSystemService Files => inner.Files;

        public ICredentialStore Credentials => inner.Credentials;

        public INetworkService Network => inner.Network;

        public IWindowService Windows => inner.Windows;

        public IShellService Shell => inner.Shell;

        public IAudioService Audio => inner.Audio;

        public ISafeAreaService SafeArea => inner.SafeArea;

        public IAppLogger Log => inner.Log;
    }

    private sealed class ConfirmingInstaller : ISoftwareInstaller
    {
        public Task<InstallResult> InstallAsync(
            InstallRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallResult(
                InstallState.AwaitingUserConfirmation,
                "handed to the system installer"));
    }
}