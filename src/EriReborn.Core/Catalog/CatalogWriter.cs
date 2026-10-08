using System.Text.Json;
using EriReborn.Core.Domain;

namespace EriReborn.Core.Catalog;

/// <summary>
/// Serialises v3 definitions back to schema-3 JSON. Used by the migration
/// tool so the shipped catalog is new-format data (spec 71).
/// </summary>
public static class CatalogWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static string Write(string catalogId, IEnumerable<SoftwareDefinition> software)
    {
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = 3,
            ["catalog"] = catalogId,
            ["items"] = software.Select(ToDictionary).ToList(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    private static Dictionary<string, object?> ToDictionary(SoftwareDefinition s)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = s.Id,
            ["name"] = s.Name,
            ["categoryId"] = s.CategoryId,
            ["directory"] = s.DirectoryName,
            ["tier"] = s.Tier.ToString(),
            ["trust"] = s.Trust.ToString(),
            ["mode"] = s.Mode.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(s.SubcategoryId))
        {
            item["subcategoryId"] = s.SubcategoryId;
        }

        if (!string.IsNullOrWhiteSpace(s.Description))
        {
            item["description"] = s.Description;
        }

        if (!string.IsNullOrWhiteSpace(s.Homepage))
        {
            item["homepage"] = s.Homepage;
        }

        if (!string.IsNullOrWhiteSpace(s.Version))
        {
            item["version"] = s.Version;
        }

        if (s.Detector is { Kind: not DetectorKind.None })
        {
            item["detector"] = new Dictionary<string, object?>
            {
                ["kind"] = s.Detector.Kind.ToString(),
                ["param"] = s.Detector.Param,

                // Dropped before, so a round-trip lost the class filter that the
                // Pnp and device-class detectors rely on.
                ["secondaryParam"] = s.Detector.SecondaryParam,
            };
        }

        item["sources"] = s.Sources.Select(SourceToDictionary).ToList();
        return item;
    }

    private static Dictionary<string, object?> SourceToDictionary(SoftwareSource source)
    {
        var map = new Dictionary<string, object?>
        {
            ["kind"] = source.Kind.ToString(),
        };

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[key] = value;
            }
        }

        Add("providerId", source.ProviderId);
        Add("url", source.Url);
        Add("shareUrl", source.ShareUrl);
        Add("wingetId", source.WingetId);
        Add("fileName", source.FileName);
        Add("sha256", source.Sha256);
        Add("architecture", source.Architecture);
        Add("version", source.Version);
        return map;
    }
}
