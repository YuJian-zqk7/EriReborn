using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Packaging configuration is part of correctness. Both desktop and Android ship
/// the catalog, and both must ship the signature with it: a catalog without its
/// signature loads as "no official authority", which silently blocks every
/// install. That regression has already happened once.
/// </summary>
public sealed class PackagingTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "assets", "catalog")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }

    private static string Project(string relativePath)
        => File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("src/EriReborn.Desktop/EriReborn.Desktop.csproj")]
    [InlineData("src/EriReborn.Mobile/EriReborn.Mobile.csproj")]
    public void The_catalog_is_packaged_whole_so_the_signature_travels_with_it(string project)
    {
        var text = Project(project);

        Assert.Contains(@"assets\catalog\*""", text);

        // The narrower glob is the bug: it copies the data but drops catalog.sig.
        Assert.DoesNotContain(@"assets\catalog\*.json""", text);
    }

    [Fact]
    public void The_shipped_catalog_carries_its_signature_file()
    {
        var signature = Path.Combine(RepositoryRoot(), "assets", "catalog", "catalog.sig");

        Assert.True(File.Exists(signature), "assets/catalog/catalog.sig is missing; the official catalog would have no authority.");

        var body = File.ReadAllText(signature);
        Assert.Contains("keyId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signature", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_catalog_json_file_is_covered_by_the_signature()
    {
        var catalog = Path.Combine(RepositoryRoot(), "assets", "catalog");
        var onDisk = Directory.GetFiles(catalog, "*.json").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var signature = File.ReadAllText(Path.Combine(catalog, "catalog.sig"));

        // A file that ships without being signed would load as untrusted, so it
        // has to be added to the signature rather than dropped in silently.
        Assert.All(onDisk, name => Assert.Contains(name!, signature, StringComparison.OrdinalIgnoreCase));
    }
}
