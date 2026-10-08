using EriReborn.Core.Domain;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// schema v2 节点树（PluginNode / PluginTree）的递归枚举与完整性不变量。
/// 树铁律：节点身份只用稳定 NodeId；同一节点只能挂在一个父节点下。
/// </summary>
public sealed class PluginNodeTreeTests
{
    private static PluginResource MakeResource(string id, string name, ResourceLocatorKind locatorKind = ResourceLocatorKind.File)
        => new()
        {
            Id = id,
            Name = name,
            CategoryId = "Game",
            DirectoryName = id,
            Sources = new[]
            {
                new SoftwareSource
                {
                    Kind = SourceKind.CloudShare,
                    ProviderId = "123",
                    ShareUrl = "https://example.invalid/s/" + id,
                    Locator = new ResourceLocator
                    {
                        Kind = locatorKind,
                        Path = "/Minecraft/" + name,
                        ProviderItemId = "item_" + id,
                    },
                },
            },
        };

    /// <summary>
    /// TR-1.1: group / folder(含子) / file 三层树中，EnumerateResourceNodes 恰好返回全部资源节点，
    /// 且每个节点只有一个父节点（前序遍历计数不重复）。
    /// </summary>
    [Fact]
    public void NodeTree_Enumeration_IsRecursiveAndSingleParent()
    {
        var fileInFolder = MakeResource("mods_zip", "mods.zip");
        var clientFolder = MakeResource("client", "客户端", ResourceLocatorKind.Folder);
        var standaloneFile = MakeResource("readme", "说明.txt");

        IReadOnlyList<PluginNode> roots = new PluginNode[]
        {
            PluginGroupNode.Create("g_minecraft", "Minecraft", new PluginNode[]
            {
                PluginResourceNode.Create(PluginNodeKind.Folder, clientFolder, new PluginNode[]
                {
                    PluginResourceNode.Create(PluginNodeKind.File, fileInFolder),
                }),
                PluginResourceNode.Create(PluginNodeKind.File, standaloneFile),
            }),
        };

        // --- 资源枚举恰好包含 3 个资源节点（folder 自己也算一个资源），前序顺序保持父子关系
        var resources = roots.SelectMany(r => r.EnumerateResourceNodes()).ToArray();

        Assert.Equal(3, resources.Length);
        Assert.Equal(
            new[] { "client", "mods_zip", "readme" },
            resources.Select(n => n.Resource.Id).ToArray());
        Assert.Contains(resources, n => n.Kind == PluginNodeKind.Folder);
        Assert.Equal(2, resources.Count(n => n.Kind == PluginNodeKind.File));

        // --- 分组枚举只命中纯分组
        var groups = roots.SelectMany(r => r.EnumerateGroups()).ToArray();
        Assert.Single(groups);
        Assert.Equal("g_minecraft", groups[0].NodeId);

        // --- 单父不变量：为每个节点建立“父→子”映射，任一节点不得出现在两个父节点之下
        var parentMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in roots.SelectMany(r => r.EnumerateSelfAndDescendants()))
        {
            foreach (var child in node.Children)
            {
                Assert.True(
                    parentMap.TryAdd(child.NodeId, node.NodeId),
                    $"节点 {child.NodeId} 同时挂在 {parentMap[child.NodeId]} 与 {node.NodeId} 之下。");
            }
        }

        // 根节点 1 个，整树共 4 个节点（group 1 + folder 1 + file 2）
        Assert.Single(roots);
        Assert.Equal(4, parentMap.Count + roots.Count);
        Assert.Equal(3, roots[0].SubtreeDepth);
    }

    /// <summary>
    /// TR-1.2: 重复资源 id 与深度超过 12 都必须被校验方法检出。
    /// </summary>
    [Fact]
    public void NodeTree_DuplicateIdAndDepth_Detected()
    {
        // --- 重复 id：两个资源节点引用同一 resource.Id
        var duplicated = MakeResource("dup", "dup");
        IReadOnlyList<PluginNode> duplicateRoots = new PluginNode[]
        {
            PluginGroupNode.Create("g_one", "分组一", new[]
            {
                PluginResourceNode.Create(PluginNodeKind.File, duplicated),
            }),
            PluginGroupNode.Create("g_two", "分组二", new[]
            {
                // 不同实例、同一逻辑 id
                PluginResourceNode.Create(PluginNodeKind.File, MakeResource("dup", "dup 副本")),
            }),
        };

        var duplicateIssues = PluginTree.Validate(duplicateRoots);

        Assert.Contains(duplicateIssues, i => i.Code == "plugin.node_duplicate_id" && i.Subject == "dup");

        // --- 深度超限：13 层 group 链（根层 = 1，第 13 层非法）
        var deepest = PluginGroupNode.Create("g_deep_13", "第13层");
        PluginNode chain = deepest;
        for (var level = 12; level >= 1; level--)
        {
            chain = PluginGroupNode.Create("g_deep_" + level, "第" + level + "层", new[] { chain });
        }

        var deepIssues = PluginTree.Validate(new[] { chain });

        Assert.Contains(deepIssues, i => i.Code == "plugin.node_too_deep");

        // 12 层为合法上限：截掉最深层后应无超深 issue
        var twelve = PluginGroupNode.Create("g_deep_12_root", "根", new[]
        {
            BuildChain(depthRemaining: 11),
        });
        Assert.DoesNotContain(PluginTree.Validate(new[] { twelve }), i => i.Code == "plugin.node_too_deep");
    }

    /// <summary>同一节点实例被挂在两个位置属于结构性错误，必须检出且不会递归死循环。</summary>
    [Fact]
    public void NodeTree_SameInstanceMountedTwice_Detected()
    {
        var shared = PluginGroupNode.Create("g_shared", "被共用的分组");
        var roots = new PluginNode[]
        {
            PluginGroupNode.Create("g_a", "A", new[] { shared }),
            PluginGroupNode.Create("g_b", "B", new[] { shared }),
        };

        var issues = PluginTree.Validate(roots);

        Assert.Contains(issues, i => i.Code == "plugin.node_mounted_more_than_once");
    }

    /// <summary>schema 1 迁移映射：folder locator → Folder 节点，其余 → File 节点。</summary>
    [Fact]
    public void FromFlatResources_ClassifiesByLocatorKind()
    {
        IReadOnlyList<PluginNode> roots = PluginTree.FromFlatResources(new[]
        {
            MakeResource("file_one", "a.zip"),
            MakeResource("folder_one", "整包", ResourceLocatorKind.Folder),
        });

        Assert.Equal(
            new[] { PluginNodeKind.File, PluginNodeKind.Folder },
            roots.Cast<PluginResourceNode>().Select(n => n.Kind).ToArray());

        // 派生扁平表包含且仅包含两个资源，id 无损
        Assert.Equal(new[] { "file_one", "folder_one" }, PluginTree.FlattenResources(roots).Select(r => r.Id).ToArray());
    }

    /// <summary>递归构造一条还剩 depthRemaining 层的 group 链（叶子 depthRemaining=1）。</summary>
    private static PluginNode BuildChain(int depthRemaining)
    {
        var node = PluginGroupNode.Create("g_chain_" + depthRemaining, "层" + depthRemaining);
        if (depthRemaining <= 1)
        {
            return node;
        }

        return PluginGroupNode.Create("g_chain_p_" + depthRemaining, "层p" + depthRemaining, new[]
        {
            BuildChain(depthRemaining - 1),
        });
    }
}
