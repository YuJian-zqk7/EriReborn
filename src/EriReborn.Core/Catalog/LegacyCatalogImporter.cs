using System.Text.Json;
using EriReborn.Core.Domain;
using EriReborn.Core.Validation;

namespace EriReborn.Core.Catalog;

public sealed record LegacyImportResult(
    IReadOnlyList<SoftwareDefinition> Software,
    IReadOnlyList<ValidationIssue> Issues,
    int ItemsRead,
    int ItemsSkipped);

/// <summary>
/// Reads a v2 (schema 2) catalog file and produces v3 models (spec 71).
/// Old structure is an input only; nothing is copied verbatim.
/// </summary>
public static class LegacyCatalogImporter
{
    public static LegacyImportResult Import(string json, string catalogId)
    {
        var software = new List<SoftwareDefinition>();
        var issues = new List<ValidationIssue>();
        var skipped = 0;
        var read = 0;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new LegacyImportResult(software, issues, 0, 0);
        }

        foreach (var item in items.EnumerateArray())
        {
            read++;
            var id = GetString(item, "id");
            var name = GetString(item, "name");
            var directory = GetString(item, "directory");

            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add(new ValidationIssue("legacy.no_id", "Catalog item has no id.", catalogId));
                skipped++;
                continue;
            }

            var legacyCategory = GetString(item, "category");
            var categoryId = CategoryTaxonomy.MapLegacyCategory(legacyCategory) ?? "Utility";
            if (!CategoryTaxonomy.IsKnownOfficialId(categoryId))
            {
                issues.Add(new ValidationIssue(
                    "legacy.unknown_category",
                    $"Legacy category '{legacyCategory}' has no official mapping; fell back to Utility.",
                    id));
                categoryId = "Utility";
            }

            var subcategoryId = CategoryTaxonomy.MapLegacySubcategory(GetString(item, "subcategory"), categoryId);

            if (string.IsNullOrWhiteSpace(directory))
            {
                issues.Add(new ValidationIssue("legacy.no_directory", "Catalog item has no official directory name.", id));
                skipped++;
                continue;
            }

            var directoryIssues = DirectoryNameValidator.ValidateName(directory);
            if (!directoryIssues.IsValid)
            {
                issues.AddRange(directoryIssues.Issues.Select(i => i with { Subject = id }));
                skipped++;
                continue;
            }

            software.Add(new SoftwareDefinition
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                CategoryId = categoryId,
                SubcategoryId = subcategoryId,
                DirectoryName = directory,
                Description = GetString(item, "description"),
                Homepage = GetString(item, "homepage"),
                Version = GetString(item, "version"),
                Tier = ParseTier(GetString(item, "tier")),
                Trust = ParseTrust(GetString(item, "trust")),
                Mode = ParseMode(GetString(item, "mode")),
                Detector = ParseDetector(item),
                Sources = ParseSources(item, issues, id),
                Architecture = GetString(item, "architecture"),
                CatalogId = catalogId,
                Tags = Array.Empty<string>(),
            });
        }

        return new LegacyImportResult(software, issues, read, skipped);
    }

    private static IReadOnlyList<SoftwareSource> ParseSources(JsonElement item, List<ValidationIssue> issues, string id)
    {
        var result = new List<SoftwareSource>();

        if (item.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
            {
                var kind = GetString(source, "kind");
                var mapped = MapSource(kind, source);
                if (mapped is null)
                {
                    issues.Add(new ValidationIssue("legacy.unknown_source", $"Unsupported source kind '{kind}'.", id));
                    continue;
                }

                result.Add(mapped);
            }
        }

        if (result.Count == 0)
        {
            // Fall back to the item-level "source" hint.
            var fallback = GetString(item, "source");
            if (!string.IsNullOrWhiteSpace(fallback) && !string.Equals(fallback, "local", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(
                    "legacy.source_hint_only",
                    $"Item declares source '{fallback}' but no concrete source entry; marked manual.",
                    id));
            }
        }

        return result;
    }

    private static SoftwareSource? MapSource(string? kind, JsonElement source)
    {
        var wingetId = GetString(source, "wingetId");
        var url = GetString(source, "url");
        var shareUrl = GetString(source, "shareUrl");
        var fileName = GetString(source, "fileName");
        var sha = GetString(source, "sha256");
        var arch = GetString(source, "architecture");

        switch ((kind ?? string.Empty).ToLowerInvariant())
        {
            case "winget":
                return new SoftwareSource { Kind = SourceKind.Winget, WingetId = wingetId, Architecture = arch };
            case "url":
            case "http":
            case "https":
                return new SoftwareSource { Kind = SourceKind.HttpUrl, Url = url, FileName = fileName, Sha256 = sha, Architecture = arch };
            case "official":
                return new SoftwareSource
                {
                    Kind = SourceKind.Official,
                    Url = url,
                    FileName = fileName,
                    Sha256 = sha,
                    Architecture = arch,
                };
            case "pan123":
            case "123":
                return Cloud("123");
            case "baidu":
            case "baidupan":
                return Cloud("baidu");
            case "quark":
                return Cloud("quark");
            case "lanzou":
            case "lanzl":
                return Cloud("lanzou");
            case "xunlei":
                return Cloud("xunlei");
            default:
                return null;
        }

        SoftwareSource Cloud(string providerId) => new()
        {
            Kind = SourceKind.CloudShare,
            ProviderId = providerId,
            ShareUrl = shareUrl,
            FileName = fileName,
            Sha256 = sha,
            Architecture = arch,

            // A v2 source named its item only by file name. The v3 model carries the location in a
            // locator, so the one-way import expresses the old file name as one: the same item, in
            // the model the rest of the app now speaks (spec 30/34).
            Locator = ResourceLocatorParser.FromLegacyFileName(fileName),
        };
    }

    private static DetectorSpec ParseDetector(JsonElement item)
    {
        if (!item.TryGetProperty("detect", out var detect) || detect.ValueKind != JsonValueKind.Object)
        {
            return DetectorSpec.None;
        }

        var method = GetString(detect, "method");
        var param = GetString(detect, "param");
        return new DetectorSpec(ParseDetectorKind(method), param);
    }

    public static DetectorKind ParseDetectorKind(string? method) => (method ?? "none").ToLowerInvariant() switch
    {
        "arp" => DetectorKind.Arp,
        "registry_release" or "registryrelease" => DetectorKind.RegistryRelease,
        "registry_subkey" or "registrysubkey" => DetectorKind.RegistrySubKey,
        "dotnet" or ".net" => DetectorKind.DotNet,
        "video" => DetectorKind.Video,
        "sound" => DetectorKind.Sound,
        "pnp" => DetectorKind.Pnp,
        "webview2" => DetectorKind.WebView2,
        "file" => DetectorKind.File,
        "msix" => DetectorKind.Msix,
        "command" => DetectorKind.Command,
        "service" => DetectorKind.Service,
        "android_package" or "androidpackage" => DetectorKind.AndroidPackage,
        _ => DetectorKind.None,
    };

    public static SoftwareTier ParseTier(string? tier) => (tier ?? string.Empty).ToLowerInvariant() switch
    {
        "core" => SoftwareTier.Core,
        "recommended" => SoftwareTier.Recommended,
        _ => SoftwareTier.Optional,
    };

    /// <summary>Legacy trust vocabulary maps onto the v3 trust grades (spec 37).</summary>
    public static SoftwareTrust ParseTrust(string? trust) => (trust ?? string.Empty).ToLowerInvariant() switch
    {
        "official" => SoftwareTrust.Official,
        "trusted" or "verified" => SoftwareTrust.Verified,
        "community" => SoftwareTrust.Community,
        "invalid" => SoftwareTrust.Invalid,
        _ => SoftwareTrust.Unknown,
    };

    public static InstallationMode ParseMode(string? mode) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "portable" => InstallationMode.Portable,
        "manual" => InstallationMode.Manual,
        "script" => InstallationMode.Script,
        _ => InstallationMode.Install,
    };

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
