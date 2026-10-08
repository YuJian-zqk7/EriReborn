using System.Text.Json;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The environment page groups drivers and security separately, so the catalog
/// must actually cover them. A Pnp entry without its class filter silently
/// matches the wrong device, which is how a dropped "secondaryParam" manifested.
/// </summary>
public sealed class HardwareCatalogTests
{
    private static string CatalogDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "catalog");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("assets/catalog was not found above the test directory.");
    }

    private static List<JsonElement> Items()
    {
        var items = new List<JsonElement>();
        foreach (var file in Directory.EnumerateFiles(CatalogDirectory(), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                // Cloned so it survives the document being disposed.
                items.Add(item.Clone());
            }
        }

        return items;
    }

    private static string? Category(JsonElement item)
        => item.TryGetProperty("categoryId", out var c) ? c.GetString() : null;

    private static string? DetectorKind(JsonElement item)
        => item.TryGetProperty("detector", out var d) && d.TryGetProperty("kind", out var k) ? k.GetString() : null;

    [Fact]
    public void The_catalog_covers_drivers_and_security()
    {
        var items = Items();

        var drivers = items.Count(i => Category(i) == "System_Drivers");
        var security = items.Count(i => Category(i) == "System_Security");

        Assert.True(drivers > 0, "the catalog has no driver entries, so the 驱动 group is always empty");
        Assert.True(security > 0, "the catalog has no security entries, so the 安全 group is always empty");

        // Every one of them must be decidable rather than an unknown row.
        Assert.All(
            items.Where(i => Category(i) is "System_Drivers" or "System_Security"),
            item => Assert.NotNull(DetectorKind(item)));
    }

    [Fact]
    public void Every_pnp_entry_carries_its_device_class_filter()
    {
        var pnp = Items().Where(i => DetectorKind(i) == "Pnp").ToList();

        Assert.NotEmpty(pnp);
        Assert.All(pnp, item =>
        {
            var detector = item.GetProperty("detector");
            Assert.True(
                detector.TryGetProperty("secondaryParam", out var filter) && !string.IsNullOrWhiteSpace(filter.GetString()),
                $"'{item.GetProperty("id").GetString()}' matches any device class, so it can match the wrong hardware");
        });
    }

    [Fact]
    public void Driver_entries_name_real_products_and_have_an_id_and_directory()
    {
        var drivers = Items().Where(i => Category(i) == "System_Drivers").ToList();

        Assert.All(drivers, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("directory").GetString()));
        });

        // The catalog is a peer set: no entry is marked as the only correct one.
        var ids = drivers.Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Security_entries_use_service_detection_rather_than_guessing()
    {
        var security = Items().Where(i => Category(i) == "System_Security").ToList();

        Assert.All(security, item =>
        {
            Assert.Equal("Service", DetectorKind(item));
            Assert.NotNull(item.GetProperty("detector").GetProperty("param").GetString());
        });
    }
}
