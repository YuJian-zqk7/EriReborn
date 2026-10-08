using EriReborn.Core.Catalog;
using EriReborn.Core.Logging;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The shipped catalog must actually verify against its own detached signature.
///
/// This matters more than it looks: the private half of the pinned anchor was
/// deliberately generated and discarded, so nothing in this repository can
/// re-sign the catalog. That means editing a catalog JSON by hand — to add a
/// detector, say — silently demotes the whole catalog from Official to
/// Untrusted, and nothing else in the suite would notice.
/// </summary>
public sealed class ShippedCatalogSignatureTests
{
    private static string CatalogDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "catalog");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    [Fact]
    public void The_shipped_catalog_signature_is_present()
    {
        var signature = Path.Combine(CatalogDirectory(), "catalog.sig");

        Assert.True(File.Exists(signature), $"随包目录缺少签名文件：{signature}");
    }

    [Fact]
    public async Task Every_shipped_entry_is_still_official()
    {
        var catalog = await new CatalogReader(AppLog.For("Test")).LoadDirectoryAsync(CatalogDirectory());

        Assert.NotEmpty(catalog.Software);

        // A hand-edited catalog file fails the digest, so the entries come back
        // readable but no longer Official. That is the signal this test exists for.
        var demoted = catalog.Software
            .Where(item => item.Provenance != CatalogProvenance.Official)
            .Select(item => $"{item.Id}: {item.Provenance}")
            .ToList();

        Assert.Empty(demoted);
    }

    [Fact]
    public async Task Nothing_in_the_shipped_catalog_is_rejected()
    {
        var catalog = await new CatalogReader(AppLog.For("Test")).LoadDirectoryAsync(CatalogDirectory());

        Assert.Equal(0, catalog.Report.ItemsRejected);
    }

    [Fact]
    public async Task The_signature_records_every_catalog_file()
    {
        var directory = CatalogDirectory();
        var signature = Path.Combine(directory, "catalog.sig");

        // The signature lists a digest per file. A catalog file that is present
        // but unsigned is an entry point that bypasses the authority chain.
        var signatureText = await File.ReadAllTextAsync(signature);
        var jsonFiles = Directory
            .GetFiles(directory, "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToList();

        foreach (var file in jsonFiles)
        {
            Assert.Contains(file!, signatureText, StringComparison.Ordinal);
        }
    }
}
