using EriReborn.Core.Domain;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// Where a resource plugin came from, decided by verification rather than by
/// what the file claims about itself (spec 45/46).
/// </summary>
public enum PluginTrust
{
    /// <summary>Signed by a key pinned in this build.</summary>
    Official,

    /// <summary>Unsigned. Usable, but carries no authority.</summary>
    Unknown,

    /// <summary>Carried a signature that did not verify, or a malformed one.</summary>
    Invalid,
}

/// <summary>
/// One resource a plugin provides.
///
/// A plugin is data: it says where a resource can be downloaded from. It is not
/// code, it is not an extension, and importing one must never execute anything
/// (spec 42/192/193).
/// </summary>
public sealed record PluginResource
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string CategoryId { get; init; }

    public string? SubcategoryId { get; init; }

    public required string DirectoryName { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }

    public string? Homepage { get; init; }

    /// <summary>SHA-256 of the resource itself, when the plugin knows it.</summary>
    public string? Sha256 { get; init; }

    public string? Architecture { get; init; }

    public SoftwareTier Tier { get; init; } = SoftwareTier.Optional;

    public InstallationMode Mode { get; init; } = InstallationMode.Install;

    /// <summary>
    /// The cloud or direct sources for this one resource. Several sources are
    /// still one resource — never several pieces of software (spec 40/44).
    /// </summary>
    public IReadOnlyList<SoftwareSource> Sources { get; init; } = Array.Empty<SoftwareSource>();
}

/// <summary>A resource plugin: an organisable, selectable, downloadable tree of resources.</summary>
public sealed record ResourcePlugin
{
    /// <summary>Schema v2: resources live in a node tree (<see cref="Nodes"/>); schema 1 is migrated on read.</summary>
    public const int CurrentSchema = 2;

    public const string FileExtension = ".eriplugin.json";

    public int Schema { get; init; } = CurrentSchema;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>Decided by verification, never read from the file.</summary>
    public PluginTrust Trust { get; init; } = PluginTrust.Unknown;

    /// <summary>
    /// Schema v2 主结构：group / file / folder 节点树。
    /// </summary>
    public IReadOnlyList<PluginNode> Nodes { get; init; } = Array.Empty<PluginNode>();

    /// <summary>
    /// 由 <see cref="Nodes"/> 前序派生的扁平资源表，包含且仅包含所有 file/folder 资源节点。
    /// 仅供导入/计数等既有消费方使用，不再是手写主结构。
    /// </summary>
    public IReadOnlyList<PluginResource> Resources => PluginTree.FlattenResources(Nodes);

    /// <summary>以节点树建立插件（schema v2 的标准构造方式）。</summary>
    public static ResourcePlugin FromNodes(
        string id,
        string name,
        string version,
        IReadOnlyList<PluginNode> nodes,
        string? author = null,
        string? description = null,
        int schema = CurrentSchema,
        PluginTrust trust = PluginTrust.Unknown)
        => new()
        {
            Schema = schema,
            Id = id,
            Name = name,
            Version = version,
            Author = author,
            Description = description,
            Trust = trust,
            Nodes = nodes,
        };
}

/// <summary>A detached signature over the plugin file's bytes.</summary>
public sealed record PluginSignature
{
    public const string FileExtension = ".eriplugin.sig";

    public required string KeyId { get; init; }

    public string Algorithm { get; init; } = "ecdsa-p256-sha256";

    public required string Signature { get; init; }
}
