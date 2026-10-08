using EriReborn.Core.Domain;
using EriReborn.Core.Validation;

namespace EriReborn.Engine.Plugins;

/// <summary>
/// 插件资源树（schema v2）的节点种类。
/// </summary>
public enum PluginNodeKind
{
    /// <summary>纯组织节点：不对应任何云盘资源、没有 locator、不可下载。</summary>
    Group,

    /// <summary>文件资源：对应一个真实文件（或 winget/http/local/manual 等单资源来源），可直接下载。</summary>
    File,

    /// <summary>文件夹资源：对应一个真实云盘文件夹，整夹作为一个资源打包下载（zip）。</summary>
    Folder,
}

/// <summary>
/// 插件资源树上的一个节点。
///
/// <para>
/// 树是唯一的父子结构：folder 节点也允许拥有子节点（父资源与子资源并存）。
/// 所有树变换必须递归返回新的 children，禁止把树扁平化收集后再重挂；
/// 节点身份只允许使用稳定的 <see cref="NodeId"/>，禁止用显示名做去重主键。
/// </para>
/// </summary>
public abstract record PluginNode
{
    /// <summary>允许的最大树深（根层记为 1）。</summary>
    public const int MaxNodeDepth = 12;

    /// <summary>
    /// 全树稳定唯一 id。资源节点取 <c>resource.Id</c>；分组节点由制作方生成（约定 "g_" 前缀）。
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>界面显示名。允许分组与资源同名；名称永远不是身份键。</summary>
    public required string Name { get; init; }

    /// <summary>节点种类。</summary>
    public abstract PluginNodeKind Kind { get; }

    /// <summary>有序子节点；叶子为空数组，永不为 null。</summary>
    public IReadOnlyList<PluginNode> Children { get; init; } = Array.Empty<PluginNode>();

    /// <summary>是否为纯组织分组。</summary>
    public bool IsGroup => Kind == PluginNodeKind.Group;

    /// <summary>本子树的深度：叶子为 1。</summary>
    public int SubtreeDepth => 1 + (Children.Count == 0 ? 0 : Children.Max(c => c.SubtreeDepth));

    /// <summary>前序遍历本节点及其全部后代（保持父子顺序）。</summary>
    public IEnumerable<PluginNode> EnumerateSelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.EnumerateSelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>本子树内全部可下载资源节点（file/folder），前序。</summary>
    public IEnumerable<PluginResourceNode> EnumerateResourceNodes()
        => EnumerateSelfAndDescendants().OfType<PluginResourceNode>();

    /// <summary>本子树内全部分组节点，前序。</summary>
    public IEnumerable<PluginGroupNode> EnumerateGroups()
        => EnumerateSelfAndDescendants().OfType<PluginGroupNode>();

    /// <summary>按稳定 NodeId 在本子树内查找（包含自身）；找不到返回 null。</summary>
    public PluginNode? FindNode(string nodeId)
        => EnumerateSelfAndDescendants().FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal));
}

/// <summary>
/// 分组节点：仅负责插件内部组织结构，不携带资源载荷与 locator，不可下载。
/// </summary>
public sealed record PluginGroupNode : PluginNode
{
    public override PluginNodeKind Kind => PluginNodeKind.Group;

    /// <summary>建立一个分组节点。</summary>
    public static PluginGroupNode Create(string nodeId, string name, IReadOnlyList<PluginNode>? children = null)
        => new()
        {
            NodeId = nodeId,
            Name = name,
            Children = children ?? Array.Empty<PluginNode>(),
        };
}

/// <summary>
/// 资源节点：承载一个既有的 <see cref="PluginResource"/> 载荷；节点种类只能是 File 或 Folder。
/// </summary>
public sealed record PluginResourceNode : PluginNode
{
    /// <summary>声明资源节点的种类，只接受 File/Folder。</summary>
    public PluginResourceNode(PluginNodeKind kind)
    {
        if (kind is not (PluginNodeKind.File or PluginNodeKind.Folder))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "资源节点只能是 File 或 Folder。");
        }

        Kind = kind;
    }

    public override PluginNodeKind Kind { get; }

    /// <summary>资源载荷（含全部既有字段与多 sources）。</summary>
    public required PluginResource Resource { get; init; }

    /// <summary>
    /// 标准构造：节点 id/名直接取自资源，保证 NodeId == Resource.Id 的全树唯一约束。
    /// </summary>
    public static PluginResourceNode Create(
        PluginNodeKind kind,
        PluginResource resource,
        IReadOnlyList<PluginNode>? children = null)
        => new(kind)
        {
            NodeId = resource.Id,
            Name = resource.Name,
            Resource = resource,
            Children = children ?? Array.Empty<PluginNode>(),
        };
}

/// <summary>
/// 资源树的只读递归操作：扁平化派生、查找、schema 1 迁移映射与完整性校验。
/// 这里不做任何会重挂节点的可变操作。
/// </summary>
public static class PluginTree
{
    /// <summary>
    /// 把若干棵树前序扁平化为资源列表（供导入等既有消费方），结果包含且仅包含 file/folder 节点。
    /// </summary>
    public static IReadOnlyList<PluginResource> FlattenResources(IEnumerable<PluginNode> roots)
        => roots
            .SelectMany(root => root.EnumerateResourceNodes().Select(node => node.Resource))
            .ToArray();

    /// <summary>按稳定 NodeId 在多棵树中查找；找不到返回 null。</summary>
    public static PluginNode? FindNode(IEnumerable<PluginNode> roots, string nodeId)
        => roots
            .Select(root => root.FindNode(nodeId))
            .FirstOrDefault(node => node is not null);

    /// <summary>
    /// schema 1 迁移映射：每条旧的扁平资源成为一个根级资源节点。
    /// 文件夹判定：任一 CloudShare source 的 locator.Kind == Folder 即为 Folder，否则为 File。
    /// </summary>
    public static IReadOnlyList<PluginNode> FromFlatResources(IEnumerable<PluginResource> resources)
        => resources
            .Select(resource => PluginResourceNode.Create(ClassifyResourceKind(resource), resource))
            .ToArray();

    /// <summary>按资源的 locator 判定它在树中应为文件夹节点还是文件节点。</summary>
    public static PluginNodeKind ClassifyResourceKind(PluginResource resource)
        => resource.Sources.Any(source =>
               source.Kind == SourceKind.CloudShare
               && source.Locator is { Kind: ResourceLocatorKind.Folder })
            ? PluginNodeKind.Folder
            : PluginNodeKind.File;

    /// <summary>
    /// 前序枚举全部资源节点及其祖先路径（根层到父节点的显示名序列）。
    /// group 与 folder 资源节点作为分支时都计入路径；资源节点自身的名字不计入。
    /// 供导入扁平化与软件页树还原共用，保证两边口径一致。
    /// </summary>
    public static IEnumerable<(PluginResourceNode Node, IReadOnlyList<string> GroupPath)> EnumerateResourcesWithGroupPath(
        IEnumerable<PluginNode> roots)
    {
        foreach (var root in roots)
        {
            foreach (var item in WalkWithPath(root, new List<string>()))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<(PluginResourceNode, IReadOnlyList<string>)> WalkWithPath(
        PluginNode node,
        List<string> ancestorNames)
    {
        if (node is PluginResourceNode resourceNode)
        {
            yield return (resourceNode, ancestorNames.ToArray());
        }

        // 无论分组还是资源节点，下钻时它的名字都是子节点路径的一部分
        //（父文件夹资源与子资源并存时，软件页要能还原出这层分支）。
        ancestorNames.Add(node.Name);
        foreach (var child in node.Children)
        {
            foreach (var item in WalkWithPath(child, ancestorNames))
            {
                yield return item;
            }
        }

        ancestorNames.RemoveAt(ancestorNames.Count - 1);
    }

    /// <summary>
    /// 递归校验整棵树的完整性：空 id/名、重复 id、深度超限、同一节点被重复挂载（或成环）、
    /// 资源节点 id 与载荷不一致、资源节点没有任何 source。
    ///
    /// 身份集合（seenIds / seenRefs）由调用入口创建一次并贯穿全树递归，
    /// 禁止每层重新创建，否则只能做同层去重。
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(
        IEnumerable<PluginNode> roots,
        int maxDepth = PluginNode.MaxNodeDepth)
    {
        var issues = new List<ValidationIssue>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRefs = new HashSet<PluginNode>();

        foreach (var root in roots)
        {
            Walk(root, depth: 1, issues, seenIds, seenRefs, maxDepth);
        }

        return issues;
    }

    private static void Walk(
        PluginNode node,
        int depth,
        List<ValidationIssue> issues,
        HashSet<string> seenIds,
        HashSet<PluginNode> seenRefs,
        int maxDepth)
    {
        // 引用级去重：同一节点实例被挂在两处（或树成环）是结构性错误；
        // 命中后停止向下递归，避免环导致的死循环。
        if (!seenRefs.Add(node))
        {
            issues.Add(new ValidationIssue(
                "plugin.node_mounted_more_than_once",
                $"节点 '{node.NodeId}' 在树中出现了多次，每个节点只能有一个父节点。",
                node.NodeId));
            return;
        }

        if (string.IsNullOrWhiteSpace(node.NodeId))
        {
            issues.Add(new ValidationIssue(
                "plugin.node_no_id",
                $"存在缺少 id 的节点（显示名：{node.Name}）。",
                null));
        }
        else if (!seenIds.Add(node.NodeId))
        {
            issues.Add(new ValidationIssue(
                "plugin.node_duplicate_id",
                $"节点 id '{node.NodeId}' 在树中重复，节点 id 必须全树唯一。",
                node.NodeId));
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            issues.Add(new ValidationIssue(
                "plugin.node_empty_name",
                $"节点 '{(string.IsNullOrWhiteSpace(node.NodeId) ? "?" : node.NodeId)}' 的名称为空。",
                node.NodeId));
        }

        if (depth > maxDepth)
        {
            issues.Add(new ValidationIssue(
                "plugin.node_too_deep",
                $"节点 '{node.NodeId}' 的深度为 {depth}，超过上限 {maxDepth}。",
                node.NodeId));
        }

        if (node is PluginResourceNode resourceNode)
        {
            if (!string.Equals(resourceNode.NodeId, resourceNode.Resource.Id, StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(
                    "plugin.resource_node_id_mismatch",
                    $"资源节点 id '{resourceNode.NodeId}' 与其资源 id '{resourceNode.Resource.Id}' 不一致。",
                    resourceNode.NodeId));
            }

            if (resourceNode.Resource.Sources.Count == 0)
            {
                issues.Add(new ValidationIssue(
                    "plugin.resource_without_sources",
                    $"资源节点 '{resourceNode.NodeId}' 没有声明任何下载来源。",
                    resourceNode.NodeId));
            }
        }

        foreach (var child in node.Children)
        {
            Walk(child, depth + 1, issues, seenIds, seenRefs, maxDepth);
        }
    }
}
