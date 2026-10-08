using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Detector coverage in the shipped catalog is uneven, and that is a data gap,
/// not something to paper over. What must hold regardless is the contract:
/// a detector that cannot work must never pretend to have decided anything, and
/// "not detected" must never be produced from an entry that simply carries no
/// detector (spec 21 / 109).
/// </summary>
public sealed class DetectorContractTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "catalog")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static async Task<SoftwareCatalog> LoadCatalogAsync()
    {
        var files = Directory
            .GetFiles(Path.Combine(RepositoryRoot(), "assets", "catalog"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return await new CatalogReader(AppLog.For("Test")).LoadFilesAsync(files);
    }

    private static readonly DetectorKind[] KnownKinds = Enum.GetValues<DetectorKind>();

    [Fact]
    public async Task Every_declared_detector_uses_a_known_kind()
    {
        var catalog = await LoadCatalogAsync();
        var unknown = catalog.Software
            .Where(item => !KnownKinds.Contains(item.Detector.Kind))
            .Select(item => $"{item.Id}: {item.Detector.Kind}")
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public async Task A_detector_that_needs_a_parameter_has_one()
    {
        var catalog = await LoadCatalogAsync();

        // A detector with a missing parameter cannot decide anything, yet it looks
        // configured: it would sit in the catalog returning nothing forever.
        var kindsNeedingParameter = new[]
        {
            DetectorKind.Arp, DetectorKind.RegistryRelease, DetectorKind.RegistrySubKey,
            DetectorKind.File, DetectorKind.Command, DetectorKind.Service, DetectorKind.Pnp,
            DetectorKind.Msix, DetectorKind.Video, DetectorKind.Sound, DetectorKind.DotNet,
        };

        var missing = catalog.Software
            .Where(item => kindsNeedingParameter.Contains(item.Detector.Kind))
            .Where(item => string.IsNullOrWhiteSpace(item.Detector.Param))
            .Select(item => $"{item.Id}: {item.Detector.Kind} 缺少参数")
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task WebView2_is_the_only_detector_that_stands_alone()
    {
        var catalog = await LoadCatalogAsync();

        var standalone = catalog.Software
            .Where(item => item.Detector.Kind == DetectorKind.WebView2)
            .ToList();

        // WebView2 asks the platform a fixed question, so it takes no parameter.
        foreach (var item in standalone)
        {
            Assert.True(
                string.IsNullOrWhiteSpace(item.Detector.Param),
                $"{item.Id}: WebView2 探测器不应带参数");
        }
    }

    [Fact]
    public async Task An_entry_without_a_detector_is_declared_as_such_rather_than_left_null()
    {
        var catalog = await LoadCatalogAsync();

        // None must be an explicit, deliberate value: a null detector would mean
        // the loader failed rather than the data saying "we cannot detect this".
        var malformed = catalog.Software
            .Where(item => item.Detector is null)
            .Select(item => item.Id)
            .ToList();

        Assert.Empty(malformed);
    }

    [Fact]
    public async Task Most_of_the_shipped_catalog_still_has_no_detector()
    {
        var catalog = await LoadCatalogAsync();

        var withDetector = catalog.Software.Count(item => !item.Detector.IsNone);
        var without = catalog.Software.Count(item => item.Detector.IsNone);

        // Recorded rather than asserted as a target: this number is a known data
        // gap, and the test exists so it cannot quietly change without anyone
        // noticing. Raising the coverage means lowering "without".
        Assert.Equal(235, without);
        Assert.Equal(28, withDetector);
        Assert.Equal(263, catalog.Software.Count);
    }

    [Fact]
    public async Task The_catalog_reports_its_own_detector_coverage()
    {
        var catalog = await LoadCatalogAsync();

        Assert.NotEmpty(catalog.Software);

        // Every category the catalog uses, and how much of it can actually be
        // decided. This is the inventory behind the gap document.
        var coverage = catalog.Software
            .GroupBy(item => item.CategoryId ?? "(none)", StringComparer.Ordinal)
            .Select(group => (
                Category: group.Key,
                Total: group.Count(),
                Detectable: group.Count(item => !item.Detector.IsNone)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        Assert.Equal(coverage.Sum(entry => entry.Total), catalog.Software.Count);
        Assert.True(coverage.Count > 5, "分类数量异常，目录可能没有加载完整。");
    }
}
