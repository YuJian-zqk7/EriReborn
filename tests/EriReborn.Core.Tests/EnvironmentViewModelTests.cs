using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The environment page must describe the real machine. It used to list catalog
/// metadata only, so these tests assert that scanning actually runs and that
/// each group reports its own slice of the result (spec 9/11).
///
/// Most shipped catalog entries declare no detector, so the tests install a
/// per-entry override first; otherwise the platform detector is legitimately
/// never consulted and everything is "unknown".
/// </summary>
public sealed class EnvironmentViewModelTests
{
    private sealed class UniformDetector(DetectionResult result) : ISoftwareDetector
    {
        public int Calls { get; private set; }

        public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static async Task<(EnvironmentViewModel ViewModel, UniformDetector Detector, string UserData)> BuildAsync(
        DetectionResult result)
    {
        var data = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: data);
        var detector = new UniformDetector(result);

        var platform = new TestPlatform(
            new TestFileSystemService(data),
            new TestNetworkService(new HttpClient()),
            new InMemoryCredentialStore())
        {
            Detector = detector,
        };

        var host = await AppHost.CreateAsync(
            paths,
            platform,
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        // Route every catalog entry through the detector so the scan is
        // observable regardless of what the manifest declares.
        host.DetectionHints.SetMany(host.Catalog.Software.ToDictionary(
            software => software.Id,
            _ => new DetectionHint { ArpPattern = "^" },
            StringComparer.Ordinal));

        return (new EnvironmentViewModel(host), detector, data);
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

    [Fact]
    public async Task The_page_covers_the_four_environment_groups()
    {
        var (viewModel, _, data) = await BuildAsync(DetectionResult.Detected());
        try
        {
            Assert.Equal(
                new[] { "Runtime", "System_Drivers", "System_Security", "System" },
                viewModel.Sections.Select(s => s.CategoryId).ToArray());

            Assert.Equal("尚未扫描环境。", viewModel.Summary);
            Assert.Same(viewModel.Sections[0], viewModel.SelectedSection);

            // All four groups are now covered by the shipped catalog.
            foreach (var section in viewModel.Sections)
            {
                Assert.True(section.Total > 0, $"{section.CategoryId} has no catalog entries");
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Scanning_reports_what_is_actually_installed()
    {
        var (viewModel, detector, data) = await BuildAsync(DetectionResult.Detected("1.2.3"));
        try
        {
            await viewModel.ScanEnvironmentCommand.ExecuteAsync(null);

            Assert.True(detector.Calls > 0, "the detector was never asked");
            Assert.Contains("已安装", viewModel.Summary);

            foreach (var section in viewModel.Sections.Where(s => s.Definitions.Count > 0))
            {
                Assert.True(
                    section.Definitions.Count == section.Results.Count,
                    $"{section.CategoryId}: defs={section.Definitions.Count} results={section.Results.Count}");
                Assert.True(
                    section.Definitions.Count == section.Installed,
                    $"{section.CategoryId}: installed={section.Installed} unknown={section.Unknown} missing={section.Missing} unsupported={section.Unsupported} first={section.Results.FirstOrDefault()?.StatusText} detail={section.Results.FirstOrDefault()?.Detail} calls={detector.Calls}");
                Assert.Equal(0, section.Missing);
                Assert.All(section.Results, item => Assert.Equal("已安装", item.StatusText));
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_missing_environment_reports_missing_not_unknown()
    {
        var (viewModel, _, data) = await BuildAsync(DetectionResult.NotDetected());
        try
        {
            await viewModel.ScanEnvironmentCommand.ExecuteAsync(null);

            foreach (var section in viewModel.Sections.Where(s => s.Definitions.Count > 0))
            {
                Assert.Equal(0, section.Installed);
                Assert.Equal(section.Results.Count, section.Missing);
                Assert.Contains("未安装", section.Summary);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Unsupported_is_kept_separate_from_missing()
    {
        // This is what an Android device sees for Windows-only detectors.
        var (viewModel, _, data) = await BuildAsync(DetectionResult.Unsupported("仅 Windows"));
        try
        {
            await viewModel.ScanEnvironmentCommand.ExecuteAsync(null);

            foreach (var section in viewModel.Sections.Where(s => s.Definitions.Count > 0))
            {
                Assert.Equal(0, section.Missing);
                Assert.Equal(section.Results.Count, section.Unsupported);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Each_section_reports_only_its_own_entries()
    {
        var (viewModel, _, data) = await BuildAsync(DetectionResult.Detected());
        try
        {
            await viewModel.ScanEnvironmentCommand.ExecuteAsync(null);

            foreach (var section in viewModel.Sections)
            {
                var expected = section.Definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                Assert.All(section.Results, item => Assert.Contains(item.Name, expected));
            }

            var total = viewModel.Sections.Sum(s => s.Results.Count);
            Assert.Equal(viewModel.Sections.Sum(s => s.Definitions.Count), total);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public void A_group_the_catalog_does_not_cover_says_so_instead_of_reporting_zeroes()
    {
        var empty = new EnvironmentSection("驱动", "System_Drivers", Array.Empty<SoftwareDefinition>());

        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Results);
        Assert.Equal("尚未扫描。", empty.Summary);

        empty.Apply(ScanResult.Empty);

        Assert.Contains("目录覆盖不足", empty.Summary);
        Assert.Contains("并非扫描失败", empty.Summary);
    }

    [Fact]
    public async Task Scanning_twice_does_not_duplicate_rows()
    {
        var (viewModel, _, data) = await BuildAsync(DetectionResult.Detected());
        try
        {
            await viewModel.ScanEnvironmentCommand.ExecuteAsync(null);
            var first = viewModel.Sections.Sum(s => s.Results.Count);

            await viewModel.ScanEnvironmentCommand.ExecuteAsync(null);

            Assert.Equal(first, viewModel.Sections.Sum(s => s.Results.Count));
        }
        finally
        {
            Cleanup(data);
        }
    }
}
