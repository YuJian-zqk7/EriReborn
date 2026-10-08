using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The environment scan must keep every detection outcome distinct, and the
/// user's install root must survive a restart (spec 10/17/22).
/// </summary>
public sealed class ScanAndConfigTests
{
    private static SoftwareDefinition Definition(string id) => new()
    {
        Id = id,
        Name = id,
        CategoryId = "Utility",
        DirectoryName = "Dir_" + id,
        // A declared detector keeps the engine from falling back to path probing.
        Detector = new DetectorSpec(DetectorKind.Arp, "^" + id + "$"),
    };

    private static SystemScanService CreateScanService(ISoftwareDetector detector)
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var platform = new TestPlatform(
            new TestFileSystemService(temp),
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Detector = detector,
        };

        return new SystemScanService(
            new SoftwareEngine(platform, new EriReborn.Core.Paths.PathResolver(), new InstallationRegistry(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "i.json"), AppLog.For("Test")), new DetectionHintStore(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "h.json"), AppLog.For("Test")), AppLog.For("Test")),
            AppLog.For("Test"));
    }

    [Fact]
    public async Task Scan_keeps_every_outcome_separate()
    {
        var detector = new ScriptedDetector(id => id switch
        {
            "a" => DetectionResult.Detected("1.0"),
            "b" => DetectionResult.NotDetected(),
            "c" => DetectionResult.Unsupported("windows-only"),
            "d" => DetectionResult.Unknown(),
            _ => DetectionResult.Error("boom"),
        });

        var result = await CreateScanService(detector).ScanAsync(new[]
        {
            Definition("a"), Definition("b"), Definition("c"), Definition("d"), Definition("e"),
        });

        Assert.Equal(5, result.Total);
        Assert.Equal(1, result.Installed);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Unsupported);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(1, result.Failed);
        Assert.Equal(5, result.Results.Count);
    }

    [Fact]
    public async Task Unsupported_is_never_counted_as_missing()
    {
        var detector = new ScriptedDetector(_ => DetectionResult.Unsupported("not supported here"));
        var result = await CreateScanService(detector).ScanAsync(new[] { Definition("x"), Definition("y") });

        Assert.Equal(0, result.Missing);
        Assert.Equal(2, result.Unsupported);
    }

    [Fact]
    public async Task Scan_invalidates_the_detection_cache_before_reading()
    {
        var detector = new ScriptedDetector(_ => DetectionResult.NotDetected());
        await CreateScanService(detector).ScanAsync(new[] { Definition("a") });

        Assert.Equal(1, detector.InvalidateCount);
    }

    [Fact]
    public async Task Scan_of_an_empty_set_is_a_no_op()
    {
        var detector = new ScriptedDetector(_ => DetectionResult.Detected());
        var result = await CreateScanService(detector).ScanAsync(Array.Empty<SoftwareDefinition>());

        Assert.Equal(0, result.Total);
        Assert.Equal(0, detector.InvalidateCount);
    }

    [Fact]
    public void User_preferences_survive_a_restart()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        new UserConfigService(paths, AppLog.For("Test")).Save(new UserPreferences
        {
            InstallRoot = @"D:\EriReborn",
            SkinId = "tech_windows",
            IncludeSubcategoryFolder = false,
        });

        var reloaded = new UserConfigService(paths, AppLog.For("Test")).Load();

        Assert.Equal(@"D:\EriReborn", reloaded.InstallRoot);
        Assert.Equal("tech_windows", reloaded.SkinId);
        Assert.False(reloaded.IncludeSubcategoryFolder);

        Directory.Delete(userData, recursive: true);
    }

    [Fact]
    public async Task App_host_takes_the_install_root_from_user_preferences()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var chosenRoot = Path.Combine(Path.GetTempPath(), "erireborn-chosen-root");
        var paths = AppPaths.Detect(userDataOverride: userData);

        new UserConfigService(paths, AppLog.For("Test")).Save(new UserPreferences { InstallRoot = chosenRoot });

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(new TestFileSystemService(userData), new TestNetworkService(new HttpClient()), new InMemoryCredentialStore()),
            new EriReborn.Cloud.CloudProviderRegistry(Array.Empty<EriReborn.Cloud.ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        Assert.Equal(chosenRoot, host.Environment.RootPath);

        Directory.Delete(userData, recursive: true);
    }

    private sealed class ScriptedDetector(Func<string, DetectionResult> script) : ISoftwareDetector, IDetectionCacheControl
    {
        public int InvalidateCount { get; private set; }

        public void Invalidate() => InvalidateCount++;

        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
            => Task.FromResult(script(software.Id));
    }
}