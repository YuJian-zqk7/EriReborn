using EriReborn.Core.Domain;
using EriReborn.Platform.Windows;
using Xunit;

namespace EriReborn.Windows.Tests;

/// <summary>
/// MSIX/Appx detection reads the per-user package repository. These tests use
/// the real machine state, so they assert relationships that hold on any Windows
/// install rather than a frozen package list.
/// </summary>
public sealed class MsixDetectionTests
{
    private static SoftwareDefinition WithSpec(DetectorSpec spec) => new()
    {
        Id = "msix_probe",
        Name = "MSIX probe",
        CategoryId = "Utility",
        DirectoryName = "MsixProbe",
        Trust = SoftwareTrust.Verified,
        Detector = spec,
        Sources = Array.Empty<SoftwareSource>(),
    };

    private static WindowsRegistryDetector Detector() => new(
        new WindowsProcessService(EriReborn.Core.Logging.AppLog.For("MsixTest")),
        EriReborn.Core.Logging.AppLog.For("MsixTest"));

    [Fact]
    public async Task A_pattern_matching_nothing_is_not_detected_not_an_error()
    {
        var result = await Detector().DetectAsync(
            WithSpec(new DetectorSpec(DetectorKind.Msix, "^EriRebornDefinitelyNotInstalled_[0-9]{6}$")));

        Assert.Equal(DetectionOutcome.NotDetected, result.Outcome);
    }

    [Fact]
    public async Task A_missing_pattern_is_a_configuration_error()
    {
        var result = await Detector().DetectAsync(WithSpec(new DetectorSpec(DetectorKind.Msix, null)));

        Assert.Equal(DetectionOutcome.Error, result.Outcome);
    }

    [Fact]
    public async Task An_invalid_regex_is_an_explicit_error()
    {
        var result = await Detector().DetectAsync(WithSpec(new DetectorSpec(DetectorKind.Msix, "([unclosed")));

        Assert.Equal(DetectionOutcome.Error, result.Outcome);
    }

    [Fact]
    public async Task A_windows_built_in_package_is_detected_with_a_version()
    {
        // Every Windows 10/11 installation carries packages under this publisher.
        var result = await Detector().DetectAsync(
            WithSpec(new DetectorSpec(DetectorKind.Msix, "^Microsoft\\.Windows")));

        Assert.Equal(DetectionOutcome.Detected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Version));
    }

    [Fact]
    public async Task Msix_is_no_longer_reported_as_unimplemented()
    {
        // The kind used to return Unsupported. Anything but that means the
        // detector really answers for this platform.
        var result = await Detector().DetectAsync(WithSpec(new DetectorSpec(DetectorKind.Msix, ".")));

        Assert.NotEqual(DetectionOutcome.Unsupported, result.Outcome);
    }
}
