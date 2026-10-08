using EriReborn.Core.Catalog;

namespace EriReborn.Core.Domain;

/// <summary>
/// The canonical description of one piece of software. Everything the
/// installer/detector engine needs, with no platform dependency (spec 12).
/// </summary>
public sealed record SoftwareDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Official ASCII first-level category id (spec 15).</summary>
    public required string CategoryId { get; init; }

    /// <summary>Official ASCII second-level category id, when present.</summary>
    public string? SubcategoryId { get; init; }

    /// <summary>Official ASCII on-disk directory name (spec 15/16).</summary>
    public required string DirectoryName { get; init; }

    public string? Description { get; init; }

    public string? Homepage { get; init; }

    public string? Version { get; init; }

    public SoftwareTier Tier { get; init; } = SoftwareTier.Optional;

    public SoftwareTrust Trust { get; init; } = SoftwareTrust.Unknown;

    public InstallationMode Mode { get; init; } = InstallationMode.Install;

    public DetectorSpec Detector { get; init; } = DetectorSpec.None;

    public IReadOnlyList<SoftwareSource> Sources { get; init; } = Array.Empty<SoftwareSource>();

    public string? Architecture { get; init; }

    /// <summary>Catalog file the entry was loaded from. For plugin entries this is the origin plugin id.</summary>
    public string? CatalogId { get; init; }

    /// <summary>True when the entry came from a plugin/extension rather than official data.</summary>
    public bool IsPluginProvided { get; init; }

    /// <summary>
    /// 插件资源在插件节点树里的祖先路径：从根层到父节点的显示名序列（group 与 folder
    /// 资源节点都计入），供软件页还原成树状浏览。官方目录条目恒为 null；插件根层资源为空数组。
    /// </summary>
    public IReadOnlyList<string>? PluginGroupPath { get; init; }

    /// <summary>
    /// How much authority this entry earned during catalog loading. Decided by
    /// signature verification, never by the entry's own "trust" field.
    /// </summary>
    public CatalogProvenance Provenance { get; init; } = CatalogProvenance.Untrusted;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public override string ToString() => $"{Id} ({CategoryId}/{DirectoryName})";
}
