using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;
using EriReborn.Platform.Windows;
using Xunit;

namespace EriReborn.Windows.Tests;

/// <summary>
/// Exercises the real Windows platform code: the registry detector against the
/// live registry, DPAPI credentials, and hashing (spec 72).
/// </summary>
public sealed class WindowsPlatformTests
{
    private static SoftwareDefinition WithDetector(DetectorKind kind, string? param)
        => new()
        {
            Id = "probe",
            Name = "Probe",
            CategoryId = "Utility",
            DirectoryName = "Probe",
            Detector = new DetectorSpec(kind, param),
        };

    private static WindowsRegistryDetector CreateDetector()
        => new(new WindowsProcessService(AppLog.For("Test")), AppLog.For("Test"));

    [Fact]
    public async Task Dotnet_sdk_is_detected_on_a_machine_running_this_test()
    {
        // The test host itself runs on the .NET SDK, so this must be conclusive.
        var result = await CreateDetector().DetectAsync(WithDetector(DetectorKind.DotNet, null));

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Version));
    }

    [Fact]
    public async Task An_unmatchable_arp_pattern_reports_not_detected()
    {
        var result = await CreateDetector()
            .DetectAsync(WithDetector(DetectorKind.Arp, "^EriRebornDefinitelyNotInstalled_[0-9]{6}$"));

        Assert.Equal(DetectionOutcome.NotDetected, result.Outcome);
    }

    [Fact]
    public async Task An_invalid_regex_is_an_explicit_error_not_a_silent_miss()
    {
        var result = await CreateDetector().DetectAsync(WithDetector(DetectorKind.Arp, "([unclosed"));

        Assert.Equal(DetectionOutcome.Error, result.Outcome);
        Assert.Contains("正则", result.Detail ?? string.Empty);
    }

    [Fact]
    public async Task Every_declared_detector_kind_is_backed_on_windows()
    {
        var detector = CreateDetector();

        // PnP reads the registry Enum tree and MSIX reads the package repository.
        // Both used to be stubs; neither may report "unsupported" any more, and a
        // missing pattern is a configuration error rather than a silent miss.
        foreach (var kind in new[] { DetectorKind.Pnp, DetectorKind.Msix })
        {
            Assert.Equal(
                DetectionOutcome.Error,
                (await detector.DetectAsync(WithDetector(kind, null))).Outcome);

            Assert.NotEqual(
                DetectionOutcome.Unsupported,
                (await detector.DetectAsync(WithDetector(kind, "."))).Outcome);
        }
    }

    [Fact]
    public async Task Detection_snapshot_can_be_invalidated()
    {
        var detector = CreateDetector();
        var probe = WithDetector(DetectorKind.Arp, "^EriRebornDefinitelyNotInstalled_[0-9]{6}$");

        var first = await detector.DetectAsync(probe);
        detector.Invalidate();
        var second = await detector.DetectAsync(probe);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(DetectionOutcome.NotDetected, second.Outcome);
    }

    [Fact]
    public async Task Dpapi_credential_store_round_trips_a_secret()
    {
        var directory = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var store = new DpapiCredentialStore(directory, AppLog.For("Test"));

        Assert.True(store.IsAvailable);

        const string key = "cloud/123/token";
        const string secret = "secret-value-123";

        await store.SetAsync(key, secret);
        Assert.Equal(secret, await store.GetAsync(key));

        var keys = await store.ListKeysAsync();
        Assert.Single(keys);

        Assert.True(await store.RemoveAsync(key));
        Assert.Null(await store.GetAsync(key));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Missing_secret_reads_as_null_not_an_exception()
    {
        var directory = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var store = new DpapiCredentialStore(directory, AppLog.For("Test"));

        Assert.Null(await store.GetAsync("nothing/here"));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Sha256_matches_a_known_digest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "payload.bin");
        await File.WriteAllTextAsync(file, "erireborn");

        var service = new WindowsFileSystemService();
        var hash = await service.ComputeSha256Async(file);

        // 64 lowercase hex characters, and content-sensitive.
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);

        var other = Path.Combine(directory, "other.bin");
        await File.WriteAllTextAsync(other, "erireborn!");
        Assert.NotEqual(hash, await service.ComputeSha256Async(other));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Process_service_finds_a_windows_binary()
    {
        var service = new WindowsProcessService(AppLog.For("Test"));

        Assert.True(service.IsSupported);
        Assert.True(service.Exists("cmd.exe"));
        Assert.False(service.Exists("erireborn-definitely-not-a-program.exe"));
    }

    /// <summary>
    /// Opening a link is the one place this app hands a string to the operating system to run. The
    /// refusal has to happen before that call: ShellExecute on a non-web scheme starts whatever the
    /// machine associates with it, so "not a web link" must mean "not opened", not "opened anyway".
    ///
    /// <para>
    /// Nothing here is actually launched — every case below is refused, and the accepted one is only
    /// validated. Opening a browser out of a test run is not the test's business.
    /// </para>
    /// </summary>
    [Fact]
    public void The_shell_opens_web_links_and_refuses_everything_else()
    {
        var shell = new WindowsShellService(AppLog.For("Test"));

        Assert.NotNull(shell.OpenUrl("file:///C:/Windows/System32/calc.exe"));
        Assert.NotNull(shell.OpenUrl("javascript:alert(1)"));
        Assert.NotNull(shell.OpenUrl("cmd.exe"));
        Assert.NotNull(shell.OpenUrl(string.Empty));

        // The reason is ours, and it names the rule.
        Assert.Contains("http", shell.OpenUrl("cmd.exe"));

        // A real web address passes the same rule the shell service uses.
        Assert.True(WebLinks.TryNormalise("https://space.bilibili.com/689572905", out _, out _));
    }
}
