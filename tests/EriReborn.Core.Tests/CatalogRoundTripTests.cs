using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The reader and the writer must be symmetric. A writer that silently drops a
/// field is a data-loss bug that no amount of reading will reveal — this is
/// exactly how secondaryParam was once lost (spec 149).
///
/// The check runs against the real shipped catalog, so it covers every shape the
/// official data actually uses rather than a hand-made sample.
/// </summary>
public sealed class CatalogRoundTripTests
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

    private static async Task<(SoftwareCatalog Original, SoftwareCatalog Restored)> RoundTripAsync()
    {
        var catalogDirectory = Path.Combine(RepositoryRoot(), "assets", "catalog");
        var files = Directory.GetFiles(catalogDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToList();

        var reader = new CatalogReader(AppLog.For("Test"));
        var original = await reader.LoadFilesAsync(files);

        // Write each catalog back out, then read it in again.
        var written = Directory.CreateTempSubdirectory("erireborn-catalog-");
        try
        {
            foreach (var group in original.Software.GroupBy(item => item.CatalogId ?? "unknown", StringComparer.Ordinal))
            {
                var json = CatalogWriter.Write(group.Key, group);
                await File.WriteAllTextAsync(Path.Combine(written.FullName, group.Key + ".json"), json);
            }

            var restored = await reader.LoadFilesAsync(Directory.GetFiles(written.FullName, "*.json"));

            return (original, restored);
        }
        finally
        {
            written.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Official_entries_never_carry_a_plugin_group_path()
    {
        // PluginGroupPath 是插件节点树的导入产物：官方目录没有插件树，必须恒为 null，
        // 软件页才不会把官方条目错误塞进某个「插件分组」。
        var catalogDirectory = Path.Combine(RepositoryRoot(), "assets", "catalog");
        var files = Directory.GetFiles(catalogDirectory, "*.json");
        var reader = new CatalogReader(AppLog.For("Test"));
        var catalog = await reader.LoadFilesAsync(files);

        Assert.NotEmpty(catalog.Software);
        Assert.All(catalog.Software, item => Assert.Null(item.PluginGroupPath));
    }

    [Fact]
    public async Task The_writer_keeps_every_entry_the_reader_loaded()
    {
        var (original, restored) = await RoundTripAsync();

        Assert.NotEmpty(original.Software);
        Assert.Equal(original.Software.Count, restored.Software.Count);
    }

    [Fact]
    public async Task Every_field_of_every_entry_survives_a_round_trip()
    {
        var (original, restored) = await RoundTripAsync();

        var lost = new List<string>();

        foreach (var before in original.Software)
        {
            var after = restored.Find(before.Id);
            if (after is null)
            {
                lost.Add($"{before.Id}: 整条丢失");
                continue;
            }

            Compare(before, after, lost);
        }

        Assert.Empty(lost);
    }

    private static void Compare(SoftwareDefinition before, SoftwareDefinition after, List<string> lost)
    {
        void Same(string field, object? a, object? b)
        {
            if (!Equals(a, b))
            {
                lost.Add($"{before.Id}.{field}: '{a}' -> '{b}'");
            }
        }

        Same("Name", before.Name, after.Name);
        Same("CategoryId", before.CategoryId, after.CategoryId);
        Same("SubcategoryId", before.SubcategoryId, after.SubcategoryId);
        Same("DirectoryName", before.DirectoryName, after.DirectoryName);
        Same("Description", before.Description, after.Description);
        Same("Homepage", before.Homepage, after.Homepage);
        Same("Version", before.Version, after.Version);
        Same("Architecture", before.Architecture, after.Architecture);
        Same("Tier", before.Tier, after.Tier);
        Same("Trust", before.Trust, after.Trust);
        Same("Mode", before.Mode, after.Mode);
        Same("Provenance", before.Provenance, after.Provenance);
        Same("IsPluginProvided", before.IsPluginProvided, after.IsPluginProvided);
        Same("CatalogId", before.CatalogId, after.CatalogId);

        // The detector is where a dropped parameter hides: the entry still loads,
        // it just detects the wrong thing.
        Same("Detector.Kind", before.Detector.Kind, after.Detector.Kind);
        Same("Detector.Param", before.Detector.Param, after.Detector.Param);
        Same("Detector.SecondaryParam", before.Detector.SecondaryParam, after.Detector.SecondaryParam);

        if (before.Sources.Count != after.Sources.Count)
        {
            lost.Add($"{before.Id}.Sources: {before.Sources.Count} -> {after.Sources.Count}");
            return;
        }

        for (var index = 0; index < before.Sources.Count; index++)
        {
            var sourceBefore = before.Sources[index];
            var sourceAfter = after.Sources[index];

            Same($"Sources[{index}].Kind", sourceBefore.Kind, sourceAfter.Kind);
            Same($"Sources[{index}].ProviderId", sourceBefore.ProviderId, sourceAfter.ProviderId);
            Same($"Sources[{index}].Url", sourceBefore.Url, sourceAfter.Url);
            Same($"Sources[{index}].ShareUrl", sourceBefore.ShareUrl, sourceAfter.ShareUrl);
            Same($"Sources[{index}].FileName", sourceBefore.FileName, sourceAfter.FileName);
            Same($"Sources[{index}].Sha256", sourceBefore.Sha256, sourceAfter.Sha256);
            Same($"Sources[{index}].Architecture", sourceBefore.Architecture, sourceAfter.Architecture);
            Same($"Sources[{index}].Version", sourceBefore.Version, sourceAfter.Version);
        }
    }
}
