using System.Text.Json;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Validation;

namespace EriReborn.Core.Catalog;

/// <summary>
/// Loads v3 (schema 3) catalog files and validates every entry before it
/// reaches the application (spec 12/16).
/// </summary>
public sealed class CatalogReader(IAppLogger log)
{
    private readonly IAppLogger _log = log;

    public async Task<SoftwareCatalog> LoadDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            _log.Warn("catalog.load", $"Catalog directory not found: {directory}");
            return SoftwareCatalog.Empty;
        }

        // Authority comes from the signature, never from the order files happen
        // to be read in.
        var verdict = CatalogVerifier.Verify(directory, _log);

        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        return await LoadFilesAsync(files, verdict, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads files with no signature check, so nothing gains authority.</summary>
    public Task<SoftwareCatalog> LoadFilesAsync(IEnumerable<string> files, CancellationToken cancellationToken = default)
        => LoadFilesAsync(
            files,
            CatalogVerdict.Invalid("该次加载未做目录签名校验，因此没有任何条目获得官方权威。"),
            cancellationToken);

    public async Task<SoftwareCatalog> LoadFilesAsync(
        IEnumerable<string> files,
        CatalogVerdict verdict,
        CancellationToken cancellationToken = default)
    {
        var official = new List<SoftwareDefinition>();
        var thirdParty = new List<SoftwareDefinition>();
        var untrusted = new List<SoftwareDefinition>();
        var rejected = new List<ValidationIssue>();
        var read = 0;
        var filesRead = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string json;
            try
            {
                json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("catalog.read", $"Failed to read '{file}'.", ex);
                rejected.Add(new ValidationIssue("catalog.unreadable", ex.Message, file));
                continue;
            }

            filesRead++;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var schema = root.TryGetProperty("schema", out var schemaElement) && schemaElement.TryGetInt32(out var value)
                    ? value
                    : 3;

                if (schema >= 3)
                {
                    var provenance = ResolveProvenance(file, verdict);
                    var parsed = ReadV3(root, file, provenance, rejected, ref read);

                    switch (provenance)
                    {
                        case CatalogProvenance.Official:
                            official.AddRange(parsed);
                            break;
                        case CatalogProvenance.ThirdParty:
                            thirdParty.AddRange(parsed);
                            break;
                        default:
                            untrusted.AddRange(parsed);
                            break;
                    }
                }
                else
                {
                    // A v2 file must go through migration, not be loaded raw (spec 71).
                    rejected.Add(new ValidationIssue(
                        "catalog.legacy_schema",
                        $"File uses legacy schema {schema}; run the catalog migration tool first.",
                        file));
                    _log.Warn("catalog.schema", $"Legacy schema {schema} in '{file}' was not loaded.");
                }
            }
            catch (JsonException ex)
            {
                _log.Error("catalog.parse", $"Invalid JSON in '{file}'.", ex);
                rejected.Add(new ValidationIssue("catalog.invalid_json", ex.Message, file));
            }
        }

        // Cross-entry checks: duplicate ids and sibling directory collisions.
        foreach (var duplicate in official
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key))
        {
            rejected.Add(new ValidationIssue("catalog.duplicate_id", "官方目录中出现重复 id。", duplicate));
        }

        var accepted = official
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        // The rule the whole scheme exists for: third-party data appends, and can
        // never override an official id. The outcome is decided by id membership,
        // so no filename or load order can change who wins.
        var officialIds = accepted.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var thirdPartyIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in thirdParty)
        {
            if (officialIds.Contains(definition.Id))
            {
                rejected.Add(new ValidationIssue(
                    "catalog.third_party_conflict",
                    "第三方条目试图定义官方已有的 id，已拒绝；官方定义保持不变。",
                    definition.Id));
                continue;
            }

            if (!thirdPartyIds.Add(definition.Id))
            {
                rejected.Add(new ValidationIssue(
                    "catalog.third_party_duplicate",
                    "两个第三方文件定义了同一个 id，已拒绝重复项。",
                    definition.Id));
                continue;
            }

            accepted.Add(definition);
        }

        // Unverified data carries no authority, so it may only fill ids that
        // nothing authoritative defines.
        var claimedIds = accepted.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var definition in untrusted)
        {
            if (!claimedIds.Add(definition.Id))
            {
                rejected.Add(new ValidationIssue(
                    "catalog.untrusted_duplicate",
                    "未受信任条目与其他条目 id 重复，已拒绝。",
                    definition.Id));
                continue;
            }

            accepted.Add(definition);
        }

        var collisions = DirectoryNameValidator.ValidateSiblingSet(accepted.Select(d => d.DirectoryName));
        foreach (var issue in collisions.Issues)
        {
            rejected.Add(issue);
        }

        var report = new CatalogLoadReport(filesRead, read, accepted.Count, rejected);
        _log.Info(
            "catalog.load",
            $"Loaded {accepted.Count}/{read} items from {filesRead} file(s); {rejected.Count} rejection(s); "
            + $"signature={verdict.State}; official={accepted.Count(d => d.Provenance == CatalogProvenance.Official)}, "
            + $"third-party={accepted.Count(d => d.Provenance == CatalogProvenance.ThirdParty)}, "
            + $"untrusted={accepted.Count(d => d.Provenance == CatalogProvenance.Untrusted)}.");

        return new SoftwareCatalog(CategoryTaxonomy.Official, accepted, report, verdict);
    }

    private List<SoftwareDefinition> ReadV3(
        JsonElement root,
        string file,
        CatalogProvenance provenance,
        List<ValidationIssue> rejected,
        ref int read)
    {
        var definitions = new List<SoftwareDefinition>();

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            rejected.Add(new ValidationIssue("catalog.no_items", "Catalog file has no items array.", file));
            return definitions;
        }

        var catalogId = root.TryGetProperty("catalog", out var catalogElement) && catalogElement.ValueKind == JsonValueKind.String
            ? catalogElement.GetString() ?? Path.GetFileNameWithoutExtension(file)
            : Path.GetFileNameWithoutExtension(file);

        foreach (var item in items.EnumerateArray())
        {
            read++;

            var id = Str(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                rejected.Add(new ValidationIssue("catalog.no_id", "Item has no id.", file));
                continue;
            }

            var categoryId = Str(item, "categoryId") ?? string.Empty;
            var directory = Str(item, "directory") ?? string.Empty;
            var issues = new List<ValidationIssue>();

            var categoryResult = CategoryTaxonomy.ValidateOfficialCategoryId(categoryId);
            if (!categoryResult.IsValid)
            {
                issues.AddRange(categoryResult.Issues);
            }

            var subcategoryId = Str(item, "subcategoryId");
            if (!string.IsNullOrWhiteSpace(subcategoryId) && !DirectoryNameValidator.ValidateName(subcategoryId).IsValid)
            {
                issues.AddRange(DirectoryNameValidator.ValidateName(subcategoryId).Issues);
            }

            var nameResult = DirectoryNameValidator.ValidateName(directory);
            if (!nameResult.IsValid)
            {
                issues.AddRange(nameResult.Issues);
            }

            if (issues.Count > 0)
            {
                rejected.AddRange(issues.Select(i => i with { Subject = id }));
                continue;
            }

            var detector = DetectorSpec.None;
            if (item.TryGetProperty("detector", out var detectorElement) && detectorElement.ValueKind == JsonValueKind.Object)
            {
                detector = new DetectorSpec(
                    LegacyCatalogImporter.ParseDetectorKind(Str(detectorElement, "kind")),
                    Str(detectorElement, "param"),
                    Str(detectorElement, "secondaryParam"));
            }

            definitions.Add(new SoftwareDefinition
            {
                Id = id,
                Name = Str(item, "name") ?? id,
                CategoryId = categoryId,
                SubcategoryId = subcategoryId,
                DirectoryName = directory,
                Description = Str(item, "description"),
                Homepage = Str(item, "homepage"),
                Version = Str(item, "version"),
                Tier = LegacyCatalogImporter.ParseTier(Str(item, "tier")),
                // The file's own "trust" string is a claim; provenance is what
                // verification decided, and it caps that claim.
                Trust = CapTrust(LegacyCatalogImporter.ParseTrust(Str(item, "trust")), provenance),
                Provenance = provenance,
                Mode = LegacyCatalogImporter.ParseMode(Str(item, "mode")),
                Detector = detector,
                Sources = ReadSources(item),
                Architecture = Str(item, "architecture"),
                CatalogId = catalogId,
                Tags = ReadTags(item),
            });
        }

        return definitions;
    }

    /// <summary>
    /// A file is official only when the signature verified and listed it. Any
    /// other file is an add-on. When the signature did not verify, nothing is
    /// official and nothing carries authority.
    /// </summary>
    private static CatalogProvenance ResolveProvenance(string file, CatalogVerdict verdict)
    {
        if (!verdict.GrantsOfficialAuthority)
        {
            return CatalogProvenance.Untrusted;
        }

        return verdict.OfficialFiles.Contains(Path.GetFileName(file))
            ? CatalogProvenance.Official
            : CatalogProvenance.ThirdParty;
    }

    /// <summary>
    /// Only a verified official file may carry official trust. Everything else is
    /// capped at community level, whatever the file claims. The enum orders trust
    /// from strongest to weakest, so the cap is the numerically larger value.
    /// </summary>
    private static SoftwareTrust CapTrust(SoftwareTrust declared, CatalogProvenance provenance)
        => provenance == CatalogProvenance.Official
            ? declared
            : (SoftwareTrust)Math.Max((int)declared, (int)SoftwareTrust.Community);

    private static IReadOnlyList<SoftwareSource> ReadSources(JsonElement item)
    {
        if (!item.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SoftwareSource>();
        }

        var list = new List<SoftwareSource>();
        foreach (var source in sources.EnumerateArray())
        {
            var kindText = Str(source, "kind") ?? "Official";
            if (!Enum.TryParse<SourceKind>(kindText, ignoreCase: true, out var kind))
            {
                continue;
            }

            list.Add(new SoftwareSource
            {
                Kind = kind,
                // The catalog format says "providerId"; the plugin format says "provider". Both are
                // accepted so a source moved between the two keeps its platform.
                ProviderId = Str(source, "providerId") ?? Str(source, "provider"),
                Url = Str(source, "url"),
                ShareUrl = Str(source, "shareUrl"),
                WingetId = Str(source, "wingetId"),
                FileName = Str(source, "fileName"),
                Sha256 = Str(source, "sha256"),
                Architecture = Str(source, "architecture"),
                Version = Str(source, "version"),
                Arguments = ReadArguments(source),
                // A catalog file may name where the item sits inside its share, the same way a
                // plugin can. Without this the official directory could not carry locators, so the
                // bundled resources were stuck with the file-name-only lookup.
                Locator = ResourceLocatorParser.Parse(source),
            });
        }

        return list;
    }

    private static IReadOnlyList<string> ReadArguments(JsonElement source)
    {
        if (!source.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return arguments.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return tags.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.String)
            .Select(t => t.GetString()!)
            .ToArray();
    }

    private static string? Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
