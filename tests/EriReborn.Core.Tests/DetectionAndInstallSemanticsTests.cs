using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>Spec 22/68: "unknown" is not "missing", and no fake success is allowed.</summary>
public sealed class DetectionAndInstallSemanticsTests
{
    private static SoftwareDefinition Installable(string id = "demo") => new()
    {
        Id = id,
        Name = id,
        CategoryId = "Utility",
        DirectoryName = id,
        Trust = SoftwareTrust.Verified,
        Provenance = CatalogProvenance.Official,

        // A declared detector is what makes the platform's detector authoritative. With
        // none the engine answers Unknown on its own, which is a different rule and is
        // tested separately below.
        Detector = new DetectorSpec(DetectorKind.Arp, "^" + id + ".*$"),
        Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/x.exe" } },
    };

    private static SoftwareDefinition WithoutDetector(string id = "demo") => new()
    {
        Id = id,
        Name = id,
        CategoryId = "Utility",
        DirectoryName = id,
        Trust = SoftwareTrust.Verified,
        Provenance = CatalogProvenance.Official,
        Detector = new DetectorSpec(DetectorKind.None, null),
        Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/x.exe" } },
    };

    private static SoftwareEngine Engine(string temp, ISoftwareInstaller installer, ISoftwareDetector detector)
    {
        var platform = new TestPlatform(
            new TestFileSystemService(temp),
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Installer = installer,
            Detector = detector,
        };

        var scratch = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

        return new SoftwareEngine(
            platform,
            new PathResolver(),
            new InstallationRegistry(Path.Combine(scratch, "i.json"), AppLog.For("Test")),
            new DetectionHintStore(Path.Combine(scratch, "h.json"), AppLog.For("Test")),
            AppLog.For("Test"));
    }

    private sealed class SucceedingInstaller : ISoftwareInstaller
    {
        public Task<InstallResult> InstallAsync(InstallRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallResult(InstallState.Succeeded, "装好了。"));
    }

    private sealed class Always(DetectionResult result) : ISoftwareDetector
    {
        public int Calls { get; private set; }

        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task An_install_detection_cannot_confirm_is_a_verification_failure()
    {
        // Spec 73: an install is only done when detection says so. Reporting the
        // installer's own word would be exactly the fake success the spec forbids.
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var engine = Engine(temp, new SucceedingInstaller(), new Always(DetectionResult.NotDetected("fake")));

        var result = await engine.InstallAsync(Installable(), new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.VerificationFailed, result.State);
        Assert.False(result.IsSuccess);

        // The evidence travels with the answer, so nothing has to be guessed.
        Assert.NotNull(result.Verified);
        Assert.Equal(DetectionOutcome.NotDetected, result.Verified!.Outcome);
        Assert.Contains(nameof(DetectionOutcome.NotDetected), result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DetectionOutcome.Unsupported)]
    [InlineData(DetectionOutcome.Unknown)]
    [InlineData(DetectionOutcome.Error)]
    public async Task No_detection_outcome_is_allowed_to_pass_as_a_successful_install(DetectionOutcome outcome)
    {
        // "Unsupported", "unknown" and "error" are all different from "installed", and
        // each of them must fail the verification rather than be folded into it.
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var detection = outcome switch
        {
            DetectionOutcome.Unsupported => DetectionResult.Unsupported("fake"),
            DetectionOutcome.Unknown => DetectionResult.Unknown("fake"),
            _ => DetectionResult.Error("fake"),
        };

        var engine = Engine(temp, new SucceedingInstaller(), new Always(detection));

        var result = await engine.InstallAsync(Installable(), new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.VerificationFailed, result.State);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task An_install_detection_confirms_is_a_success()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var engine = Engine(temp, new SucceedingInstaller(), new Always(DetectionResult.Detected("1.2.3")));

        var result = await engine.InstallAsync(Installable(), new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.Succeeded, result.State);
        Assert.True(result.IsSuccess);
        Assert.Equal("1.2.3", result.Verified?.Version);
    }

    [Fact]
    public async Task Verification_runs_detection_after_the_install_not_before()
    {
        // A single detection call, made after the installer returned. If it ran first
        // the answer would describe the machine before the install, which is the state
        // the whole check exists to move past.
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var detector = new Always(DetectionResult.Detected("1.0"));
        var engine = Engine(temp, new SucceedingInstaller(), detector);

        await engine.InstallAsync(Installable(), new EnvironmentContext { RootPath = temp });

        Assert.Equal(1, detector.Calls);
    }

    [Fact]
    public async Task An_entry_that_declares_no_detector_cannot_verify_a_successful_install()
    {
        // Spec 22: nothing could decide, so the answer is Unknown — and an install whose
        // result is Unknown is not a success. This is the rule that made the first
        // version of these tests fail, and it is worth keeping: without a declared
        // detector the platform's detector is never consulted at all.
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var detector = new Always(DetectionResult.Detected("9.9"));
        var engine = Engine(temp, new SucceedingInstaller(), detector);

        var result = await engine.InstallAsync(WithoutDetector(), new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.VerificationFailed, result.State);
        Assert.Equal(DetectionOutcome.Unknown, result.Verified!.Outcome);

        // And the platform detector was never asked, which is the mechanism behind it.
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public void The_install_request_offers_no_way_to_skip_verification()
    {
        // A VerifyAfterInstall field used to exist, defaulted to true, was set to true in
        // two places, and was read by nothing. A switch that cannot change anything is a
        // promise the API does not keep, so it was removed rather than wired up: turning
        // verification off is the same as turning "no fake success" off.
        var members = typeof(InstallRequest).GetProperties().Select(property => property.Name).ToList();

        Assert.DoesNotContain(members, name => name.Contains("Verify", StringComparison.OrdinalIgnoreCase));

        // The rest of the request is intact, so this is a removal and not a rename that
        // quietly dropped something else.
        Assert.Contains("Software", members);
        Assert.Contains("Source", members);
        Assert.Contains("UiMode", members);
        Assert.Contains("AllowUnsignedInstaller", members);
        Assert.Contains("AllowScriptExecution", members);
    }

    [Fact]
    public void Unknown_detection_is_not_reported_as_missing()
    {
        Assert.Equal(SoftwareStatus.Unknown, DetectionResult.Unknown().ToStatus());
        Assert.NotEqual(SoftwareStatus.Missing, DetectionResult.Unknown().ToStatus());
    }

    [Fact]
    public void Unsupported_detection_stays_unsupported()
        => Assert.Equal(SoftwareStatus.Unsupported, DetectionResult.Unsupported().ToStatus());

    [Fact]
    public async Task Installer_that_cannot_install_reports_platform_unavailable()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var files = new TestFileSystemService(temp);
        var platform = new TestPlatform(files, new TestNetworkService(new HttpClient()), new InMemoryCredentialStore());

        var engine = new SoftwareEngine(platform, new PathResolver(), new InstallationRegistry(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "i.json"), AppLog.For("Test")), new DetectionHintStore(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "h.json"), AppLog.For("Test")), AppLog.For("Test"));

        var software = new SoftwareDefinition
        {
            Id = "demo",
            Name = "Demo",
            CategoryId = "Utility",
            DirectoryName = "Demo",
            Trust = SoftwareTrust.Verified,
            Provenance = CatalogProvenance.Official,
            Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/x.exe" } },
        };

        var result = await engine.InstallAsync(software, new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.PlatformUnavailable, result.State);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Unvetted_software_is_blocked_by_the_trust_gate()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var platform = new TestPlatform(new TestFileSystemService(temp), new TestNetworkService(new HttpClient()), new InMemoryCredentialStore());
        var engine = new SoftwareEngine(platform, new PathResolver(), new InstallationRegistry(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "i.json"), AppLog.For("Test")), new DetectionHintStore(Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "h.json"), AppLog.For("Test")), AppLog.For("Test"));

        var software = new SoftwareDefinition
        {
            Id = "crack",
            Name = "Crack",
            CategoryId = "Utility",
            DirectoryName = "Crack",
            Trust = SoftwareTrust.Unknown,
            Provenance = CatalogProvenance.Official,
            Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/x.exe" } },
        };

        var result = await engine.InstallAsync(software, new EnvironmentContext { RootPath = temp });

        Assert.Equal(InstallState.Unsupported, result.State);
        Assert.Contains("信任等级", result.Message);
    }

    [Fact]
    public async Task Legacy_schema_files_are_refused_by_the_v3_reader()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "legacy.json");
        await File.WriteAllTextAsync(file, """{ "schema": 2, "category": "Utilities", "items": [ { "id": "7zip", "name": "7-Zip", "directory": "7Zip", "category": "Utilities" } ] }""");

        var reader = new CatalogReader(AppLog.For("Test"));
        var catalog = await reader.LoadFilesAsync(new[] { file });

        Assert.Empty(catalog.Software);
        Assert.Contains(catalog.Report.Rejected, i => i.Code == "catalog.legacy_schema");
    }

    [Fact]
    public async Task A_catalog_source_spelled_the_plugin_way_keeps_its_provider()
    {
        // Plugin files say "provider" where catalogs say "providerId". A source moved from a plugin
        // into a catalog must not lose its platform on the way.
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "catalog.json");
        await File.WriteAllTextAsync(file, """
        {
          "schema": 3,
          "catalog": "test",
          "items": [
            {
              "id": "thing", "name": "Thing", "categoryId": "Utility", "directory": "Thing",
              "sources": [ { "kind": "CloudShare", "provider": "123", "shareUrl": "https://example.invalid/s/x" } ]
            }
          ]
        }
        """);

        var reader = new CatalogReader(AppLog.For("Test"));
        var catalog = await reader.LoadFilesAsync(new[] { file });

        var item = Assert.Single(catalog.Software);
        var source = Assert.Single(item.Sources);
        Assert.Equal("123", source.ProviderId);
    }

    [Fact]
    public void Legacy_importer_maps_chinese_categories_to_official_ascii_ids()
    {
        const string json = """
        {
          "schema": 2,
          "category": "OfficialBase",
          "items": [
            { "id": "Java_8", "name": "Java 8", "category": "系统基础", "directory": "Java_8", "tier": "Recommended", "trust": "community", "mode": "Portable",
              "detect": { "method": "none" },
              "sources": [ { "kind": "pan123", "fileName": "Java 8", "shareUrl": "https://example.invalid/share" } ] }
          ]
        }
        """;

        var result = LegacyCatalogImporter.Import(json, "official-base");

        var item = Assert.Single(result.Software);
        Assert.Equal("System", item.CategoryId);
        Assert.Equal("Java_8", item.DirectoryName);
        Assert.Equal(SoftwareTrust.Community, item.Trust);
        var source = Assert.Single(item.Sources);
        Assert.Equal(SourceKind.CloudShare, source.Kind);
        Assert.Equal("123", source.ProviderId);
    }

    [Fact]
    public async Task Catalog_round_trips_through_the_writer_and_reader()
    {
        var temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var definition = new SoftwareDefinition
        {
            Id = "7zip",
            Name = "7-Zip",
            CategoryId = "Utility",
            DirectoryName = "7Zip",
            Trust = SoftwareTrust.Verified,
            Provenance = CatalogProvenance.Official,
            Mode = InstallationMode.Install,
            Detector = new DetectorSpec(DetectorKind.Arp, "^7-Zip.*$"),
            Sources = new[] { new SoftwareSource { Kind = SourceKind.Winget, WingetId = "7zip.7zip" } },
        };

        var file = Path.Combine(temp, "utilities.json");
        await File.WriteAllTextAsync(file, CatalogWriter.Write("utilities", new[] { definition }));

        var reader = new CatalogReader(AppLog.For("Test"));
        var catalog = await reader.LoadFilesAsync(new[] { file });

        var loaded = Assert.Single(catalog.Software);
        Assert.Equal("7zip", loaded.Id);
        Assert.Equal("Utility", loaded.CategoryId);
        Assert.Equal(DetectorKind.Arp, loaded.Detector.Kind);
        Assert.Equal("7zip.7zip", Assert.Single(loaded.Sources).WingetId);
        Assert.Equal(0, catalog.Report.ItemsRejected);
    }
}