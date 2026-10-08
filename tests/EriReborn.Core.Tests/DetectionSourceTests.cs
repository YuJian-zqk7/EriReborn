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
/// Detection consults several sources, strongest first, so an entry whose
/// manifest declares no detector can still be decided honestly (spec 22).
/// </summary>
public sealed class DetectionSourceTests : IDisposable
{
    private readonly string _root;
    private readonly TestFileSystemService _files;

    public DetectionSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new TestFileSystemService(_root);
    }

    private InstallationRegistry NewRegistry() => new(Path.Combine(_root, "installations.json"), AppLog.For("Test"));

    private DetectionHintStore NewHints() => new(Path.Combine(_root, "hints.json"), AppLog.For("Test"));

    private SoftwareEngine NewEngine(
        ISoftwareDetector? detector = null,
        ISoftwareInstaller? installer = null,
        InstallationRegistry? registry = null,
        DetectionHintStore? hints = null)
    {
        var platform = new TestPlatform(
            _files,
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Detector = detector ?? new StubDetector(DetectionResult.Unknown()),
            Installer = installer ?? new StubInstaller(),
        };

        return new SoftwareEngine(
            platform,
            new PathResolver(),
            registry ?? NewRegistry(),
            hints ?? NewHints(),
            AppLog.For("Test"));
    }

    private static SoftwareDefinition Software(string id = "demo", bool withDetector = false) => new()
    {
        Id = id,
        Name = "Demo",
        CategoryId = "Utility",
        DirectoryName = "Demo",
        Trust = SoftwareTrust.Verified,

        // These tests exercise install semantics, so the entry stands in for
        // verified official data; provenance is covered by its own tests.
        Provenance = CatalogProvenance.Official,
        Detector = withDetector ? new DetectorSpec(DetectorKind.Arp, "^Demo$") : DetectorSpec.None,
        Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/demo.exe" } },
    };

    private EnvironmentContext Context => new() { RootPath = _root };

    [Fact]
    public async Task A_declared_detector_wins_over_recorded_history()
    {
        var registry = NewRegistry();
        registry.Record(new InstallationRecord { SoftwareId = "demo", Directory = _root });

        var engine = NewEngine(new StubDetector(DetectionResult.NotDetected()), registry: registry);
        var result = await engine.DetectAsync(Software(withDetector: true), Context);

        Assert.Equal(DetectionOutcome.NotDetected, result.Outcome);

        // The recorded install must NOT have been used as the answer.
        Assert.NotEqual("install-registry", result.Source);
    }

    [Fact]
    public async Task A_recorded_install_is_detected_while_its_directory_exists()
    {
        var registry = NewRegistry();
        registry.Record(new InstallationRecord
        {
            SoftwareId = "demo",
            Name = "Demo",
            Version = "2.5",
            Directory = _root,
        });

        var result = await NewEngine(registry: registry).DetectAsync(Software(), Context);

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.Equal("install-registry", result.Source);
        Assert.Equal("2.5", result.Version);
    }

    [Fact]
    public async Task A_recorded_install_whose_directory_is_gone_is_not_installed()
    {
        var registry = NewRegistry();
        registry.Record(new InstallationRecord
        {
            SoftwareId = "demo",
            Directory = Path.Combine(_root, "removed-by-user"),
        });

        var result = await NewEngine(registry: registry).DetectAsync(Software(), Context);

        Assert.Equal(DetectionOutcome.NotDetected, result.Outcome);
        Assert.Equal("install-registry", result.Source);
        Assert.Contains("已不存在", result.Detail ?? string.Empty);
    }

    [Fact]
    public async Task A_stale_record_does_not_mask_a_present_install_path()
    {
        // EriReborn recorded an install that has since moved or been removed.
        var registry = NewRegistry();
        registry.Record(new InstallationRecord
        {
            SoftwareId = "demo",
            Directory = Path.Combine(_root, "long-gone"),
        });

        // The software is actually present at the currently configured root.
        var liveRoot = Path.Combine(_root, "current-root");
        Directory.CreateDirectory(Path.Combine(liveRoot, "Utility", "Demo"));

        var result = await NewEngine(registry: registry)
            .DetectAsync(Software(), new EnvironmentContext { RootPath = liveRoot });

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.Equal("install-path", result.Source);
    }

    [Fact]
    public async Task A_user_hint_decides_an_entry_with_no_detector()
    {
        var marker = Path.Combine(_root, "demo.exe");
        await File.WriteAllTextAsync(marker, "x");

        var hints = NewHints();
        hints.Set("demo", new DetectionHint { Path = marker });

        var result = await NewEngine(hints: hints).DetectAsync(Software(), Context);

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.Equal("user-hint", result.Source);
    }

    [Fact]
    public async Task A_user_hint_wins_over_recorded_history()
    {
        var marker = Path.Combine(_root, "elsewhere.exe");
        await File.WriteAllTextAsync(marker, "x");

        var registry = NewRegistry();
        registry.Record(new InstallationRecord { SoftwareId = "demo", Directory = _root });

        var hints = NewHints();
        hints.Set("demo", new DetectionHint { Path = marker });

        var result = await NewEngine(registry: registry, hints: hints).DetectAsync(Software(), Context);

        Assert.Equal("user-hint", result.Source);
        Assert.Equal(marker, result.Detail);
    }

    [Fact]
    public async Task A_hint_pointing_at_nothing_is_not_installed()
    {
        var hints = NewHints();
        hints.Set("demo", new DetectionHint { Path = Path.Combine(_root, "not-here") });

        var result = await NewEngine(hints: hints).DetectAsync(Software(), Context);

        Assert.Equal(DetectionOutcome.NotDetected, result.Outcome);
        Assert.Equal("user-hint", result.Source);
    }

    [Fact]
    public async Task Without_any_signal_an_entry_stays_unknown()
    {
        var emptyRoot = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyRoot);

        var result = await NewEngine().DetectAsync(Software(), new EnvironmentContext { RootPath = emptyRoot });

        Assert.Equal(DetectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task A_successful_install_is_recorded_for_later_scans()
    {
        var registry = NewRegistry();
        var engine = NewEngine(
            new StubDetector(DetectionResult.Detected("1.0")),
            new StubInstaller(),
            registry);

        var context = new EnvironmentContext { RootPath = Path.Combine(_root, "install-root") };
        var result = await engine.InstallAsync(Software(withDetector: true), context);

        Assert.True(result.IsSuccess, result.Message);

        var record = registry.Find("demo");
        Assert.NotNull(record);
        Assert.Equal("1.0", record!.Version);
        Assert.True(Directory.Exists(record.Directory));
    }

    [Fact]
    public void Install_records_survive_a_reload()
    {
        var registry = NewRegistry();
        registry.Record(new InstallationRecord { SoftwareId = "demo", Name = "Demo", Directory = _root });

        var reloaded = NewRegistry();
        reloaded.Load();

        Assert.Equal(1, reloaded.Count);
        Assert.Equal("Demo", reloaded.Find("demo")!.Name);
    }

    [Fact]
    public void Detection_hints_survive_a_reload()
    {
        var hints = NewHints();
        hints.Set("demo", new DetectionHint { Path = @"C:\Tools\demo.exe" });

        var reloaded = NewHints();
        reloaded.Load();

        Assert.Equal(@"C:\Tools\demo.exe", reloaded.GetPath("demo"));
    }

    [Fact]
    public void A_corrupt_store_degrades_to_empty_instead_of_throwing()
    {
        var registryPath = Path.Combine(_root, "installations.json");
        File.WriteAllText(registryPath, "{ this is not json");

        var registry = NewRegistry();
        registry.Load();

        Assert.Equal(0, registry.Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class StubDetector(DetectionResult result) : ISoftwareDetector
    {
        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    /// <summary>Pretends to install by creating the target directory.</summary>
    private sealed class StubInstaller : ISoftwareInstaller
    {
        public Task<InstallResult> InstallAsync(
            InstallRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(request.TargetDirectory))
            {
                Directory.CreateDirectory(request.TargetDirectory);
            }

            return Task.FromResult(new InstallResult(
                InstallState.Succeeded,
                "stub install",
                InstalledPath: request.TargetDirectory));
        }
    }
}
