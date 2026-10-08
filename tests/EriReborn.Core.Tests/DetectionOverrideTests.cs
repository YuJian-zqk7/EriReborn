using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The catalog leaves most entries without a detector, so the user must be able
/// to supply one. An override is an explicit statement about this machine and
/// therefore outranks the manifest's default (spec 22).
/// </summary>
public sealed class DetectionOverrideTests : IDisposable
{
    private readonly string _root;
    private readonly TestFileSystemService _files;

    public DetectionOverrideTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new TestFileSystemService(_root);
    }

    /// <summary>Records the detector spec it was asked to use.</summary>
    private sealed class RecordingDetector : ISoftwareDetector
    {
        public List<DetectorSpec?> Seen { get; } = new();

        public DetectionResult Result { get; set; } = DetectionResult.NotDetected();

        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
        {
            Seen.Add(software.Detector);
            return Task.FromResult(Result);
        }
    }

    private DetectionHintStore NewHints() => new(Path.Combine(_root, "hints.json"), AppLog.For("Test"));

    private SoftwareEngine NewEngine(ISoftwareDetector detector, DetectionHintStore hints)
    {
        var platform = new TestPlatform(
            _files,
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Detector = detector,
        };

        return new SoftwareEngine(platform, new PathResolver(), NewRegistry(), hints, AppLog.For("Test"));
    }

    private InstallationRegistry NewRegistry() => new(Path.Combine(_root, "i.json"), AppLog.For("Test"));

    private static SoftwareDefinition Software(bool withDetector = false) => new()
    {
        Id = "demo",
        Name = "Demo",
        CategoryId = "Utility",
        DirectoryName = "Demo",
        Trust = SoftwareTrust.Verified,
        Detector = withDetector ? new DetectorSpec(DetectorKind.Arp, "^Manifest Name$") : DetectorSpec.None,
        Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/a.exe" } },
    };

    [Fact]
    public void A_legacy_path_only_hint_file_still_loads()
    {
        var path = Path.Combine(_root, "hints.json");
        File.WriteAllText(path, """{"demo":"C:\\Tools\\demo.exe"}""");

        var store = new DetectionHintStore(path, AppLog.For("Test"));
        store.Load();

        Assert.Equal(1, store.Count);
        Assert.Equal(@"C:\Tools\demo.exe", store.Get("demo")!.Path);
        Assert.Null(store.Get("demo")!.ArpPattern);
    }

    [Fact]
    public void A_hand_written_camel_case_hint_is_not_silently_ignored()
    {
        var path = Path.Combine(_root, "hints.json");
        File.WriteAllText(path, """{"Java_8":{"arpPattern":"^Java 8 Update"}}""");

        var store = new DetectionHintStore(path, AppLog.For("Test"));
        store.Load();

        var hint = store.Get("Java_8");
        Assert.NotNull(hint);
        Assert.Equal("^Java 8 Update", hint!.ArpPattern);
        Assert.False(hint.IsEmpty);
    }

    [Fact]
    public void A_pattern_hint_survives_a_reload()
    {
        var store = NewHints();
        store.Set("demo", new DetectionHint { ArpPattern = "^Demo Version" });

        var reloaded = NewHints();
        reloaded.Load();

        Assert.Equal("^Demo Version", reloaded.Get("demo")!.ArpPattern);
    }

    [Fact]
    public void An_empty_hint_is_removed_rather_than_stored()
    {
        var store = NewHints();
        store.Set("demo", new DetectionHint { ArpPattern = "^x$" });
        Assert.Equal(1, store.Count);

        store.Set("demo", new DetectionHint());

        Assert.Equal(0, store.Count);
        Assert.Null(store.Get("demo"));
    }

    [Fact]
    public async Task A_user_arp_pattern_is_run_through_the_real_detector()
    {
        var hints = NewHints();
        hints.Set("demo", new DetectionHint { ArpPattern = "^User Pattern$" });

        var detector = new RecordingDetector { Result = DetectionResult.Detected("9.9") };
        var result = await NewEngine(detector, hints).DetectAsync(Software(), Context());

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.Equal("user-hint", result.Source);

        var spec = Assert.Single(detector.Seen);
        Assert.Equal(DetectorKind.Arp, spec!.Kind);
        Assert.Equal("^User Pattern$", spec.Param);
    }

    [Fact]
    public async Task A_user_override_outranks_the_manifest_detector()
    {
        var hints = NewHints();
        hints.Set("demo", new DetectionHint { ArpPattern = "^User Pattern$" });

        var detector = new RecordingDetector { Result = DetectionResult.Detected() };
        await NewEngine(detector, hints).DetectAsync(Software(withDetector: true), Context());

        // Only the override was consulted; the manifest's own pattern was not.
        var spec = Assert.Single(detector.Seen);
        Assert.Equal("^User Pattern$", spec!.Param);
    }

    [Fact]
    public async Task Without_an_override_the_manifest_pattern_is_used()
    {
        var detector = new RecordingDetector { Result = DetectionResult.Detected() };
        await NewEngine(detector, NewHints()).DetectAsync(Software(withDetector: true), Context());

        var spec = Assert.Single(detector.Seen);
        Assert.Equal("^Manifest Name$", spec!.Param);
    }

    [Fact]
    public async Task An_override_the_detector_cannot_decide_falls_through_instead_of_lying()
    {
        var hints = NewHints();
        hints.Set("demo", new DetectionHint { ArpPattern = "^Nothing$" });

        // The detector cannot decide, and there is no install path either.
        var detector = new RecordingDetector { Result = DetectionResult.Unknown("not found") };
        var result = await NewEngine(detector, hints)
            .DetectAsync(Software(), new EnvironmentContext { RootPath = Path.Combine(_root, "empty") });

        Assert.Equal(DetectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task A_path_override_still_decides_by_existence()
    {
        var marker = Path.Combine(_root, "demo.exe");
        await File.WriteAllTextAsync(marker, "x");

        var hints = NewHints();
        hints.Set("demo", new DetectionHint { Path = marker });

        var result = await NewEngine(new RecordingDetector(), hints).DetectAsync(Software(), Context());

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.Equal("user-hint", result.Source);
    }

    [Fact]
    public async Task A_filename_override_checks_the_install_directory()
    {
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(installRoot, "Utility", "Demo"));
        await File.WriteAllTextAsync(Path.Combine(installRoot, "Utility", "Demo", "demo.exe"), "x");

        var hints = NewHints();
        hints.Set("demo", new DetectionHint { FileName = "demo.exe" });

        var result = await NewEngine(new RecordingDetector(), hints)
            .DetectAsync(Software(), new EnvironmentContext { RootPath = installRoot });

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.Equal("user-hint", result.Source);
    }

    private EnvironmentContext Context() => new() { RootPath = _root };

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
}
