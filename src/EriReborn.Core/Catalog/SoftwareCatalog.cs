using EriReborn.Core.Domain;
using EriReborn.Core.Validation;

namespace EriReborn.Core.Catalog;

/// <summary>What happened while loading the catalog. Rejections are never silent.</summary>
public sealed record CatalogLoadReport(
    int FilesRead,
    int ItemsRead,
    int ItemsAccepted,
    IReadOnlyList<ValidationIssue> Rejected)
{
    public int ItemsRejected => Rejected.Count;
}

/// <summary>Immutable view over the merged software catalog (spec 12).</summary>
public sealed class SoftwareCatalog
{
    public SoftwareCatalog(
        IReadOnlyList<CategoryDefinition> categories,
        IReadOnlyList<SoftwareDefinition> software,
        CatalogLoadReport report,
        CatalogVerdict? verdict = null)
    {
        Categories = categories;
        Software = software;
        Report = report;
        Signature = verdict ?? CatalogVerdict.Invalid("未执行目录签名校验。");
    }

    public static SoftwareCatalog Empty { get; } = new(
        CategoryTaxonomy.Official,
        Array.Empty<SoftwareDefinition>(),
        new CatalogLoadReport(0, 0, 0, Array.Empty<ValidationIssue>()),
        CatalogVerdict.Invalid("目录为空，未执行签名校验。"));

    public IReadOnlyList<CategoryDefinition> Categories { get; }

    public IReadOnlyList<SoftwareDefinition> Software { get; }

    public CatalogLoadReport Report { get; }

    /// <summary>
    /// The result of verifying the official catalog's signature. The UI must show
    /// this: without official authority the data may be displayed but must not
    /// drive high-risk operations.
    /// </summary>
    public CatalogVerdict Signature { get; }

    public bool HasOfficialAuthority => Signature.GrantsOfficialAuthority;

    public int OfficialCount => Software.Count(s => s.Provenance == CatalogProvenance.Official);

    public int ThirdPartyCount => Software.Count(s => s.Provenance == CatalogProvenance.ThirdParty);

    public int UntrustedCount => Software.Count(s => s.Provenance == CatalogProvenance.Untrusted);

    public SoftwareDefinition? Find(string id)
        => Software.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public IEnumerable<SoftwareDefinition> InCategory(string categoryId)
        => Software.Where(s => string.Equals(s.CategoryId, categoryId, StringComparison.Ordinal));

    public IEnumerable<string> UsedCategoryIds
        => Software.Select(s => s.CategoryId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);

    public string DisplayNameFor(string categoryId)
        => Categories.FirstOrDefault(c => string.Equals(c.Id, categoryId, StringComparison.Ordinal))?.DisplayName ?? categoryId;
}
