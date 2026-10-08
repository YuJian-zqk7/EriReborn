using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// PluginReader 对 schema v2 节点树的递归读取、schema 1 迁移与坏结构 issue（AC-1/2/3）。
/// </summary>
public sealed class PluginTreeReaderTests
{
    // ------------------------------------------------------------- JSON 构造助手

    private static string Schema2Plugin(string nodes) => $$"""
    {
      "schema": 2,
      "id": "mc_pack",
      "name": "Minecraft 合集",
      "version": "3.1.0",
      "author": "tester",
      "nodes": [{{nodes}}]
    }
    """;

    private static string Schema1Plugin(string resources) => $$"""
    {
      "schema": 1,
      "id": "old_pack",
      "name": "旧版插件",
      "version": "1.0.0",
      "resources": [{{resources}}]
    }
    """;

    private static string ResourceObject(string id, string sources, string? extra = null)
    {
        var extraLine = extra is null ? string.Empty : "," + extra;
        return $$"""
        {
          "id": "{{id}}",
          "name": "{{id}} 名称",
          "categoryId": "Game",
          "directory": "{{id}}",
          "tier": "core",
          "mode": "portable"{{extraLine}},
          "sources": [{{sources}}]
        }
        """;
    }

    private static string GroupNode(string id, string name, params string[] children)
    {
        var kids = children.Length == 0
            ? string.Empty
            : ",\"children\":[" + string.Join(",", children) + "]";
        return $"{{\"nodeType\":\"group\",\"id\":\"{id}\",\"name\":\"{name}\"{kids}}}";
    }

    private static string ResourceNode(string nodeType, string resourceJson, params string[] children)
    {
        var kids = children.Length == 0
            ? string.Empty
            : ",\"children\":[" + string.Join(",", children) + "]";
        return $"{{\"nodeType\":\"{nodeType}\",\"resource\":{resourceJson}{kids}}}";
    }

    private static string CloudSource(string provider, string locatorType, string itemId)
        => "{\"kind\":\"CloudShare\",\"provider\":\"" + provider + "\",\"shareUrl\":\"https://x.invalid/s\","
            + "\"locator\":{\"type\":\"" + locatorType + "\",\"path\":\"/a/b/x\",\"providerItemId\":\"" + itemId + "\"}}";

    private static string WingetSource()
        => "{\"kind\":\"Winget\",\"wingetId\":\"Foo.Bar\",\"needsLocatorResolution\":true}";

    // ------------------------------------------------------------- TR-2.1

    /// <summary>
    /// TR-2.1: schema2 嵌套样例（group×2 → folder(含 file 子节点，父子并存) + 根级 file）解析后
    /// 树结构、资源字段（多 sources、locator kind/itemId、tier/mode）无损；扁平派生恰好 3 条。
    /// </summary>
    [Fact]
    public void PluginTreeRoundtrip_ReadsNestedSchema2()
    {
        var client = ResourceNode(
            "folder",
            ResourceObject("client", CloudSource("123", "folder", "item_client") + "," + CloudSource("quark", "folder", "q_client")),
            ResourceNode("file", ResourceObject("mods", CloudSource("123", "file", "item_mods"))));

        var readme = ResourceNode("file", ResourceObject("readme", CloudSource("123", "file", "item_readme")));

        var tree = GroupNode("g_mc", "Minecraft", GroupNode("g_ver", "1.21.1", client), readme);

        var (plugin, issues) = PluginReader.Parse(Schema2Plugin(tree));

        Assert.NotNull(plugin);
        Assert.Empty(issues);
        Assert.Equal(2, plugin!.Schema);

        // 根：单个 group
        var root = Assert.Single(plugin.Nodes);
        var mc = Assert.IsType<PluginGroupNode>(root);
        Assert.Equal("g_mc", mc.NodeId);
        Assert.Equal(4, mc.SubtreeDepth);

        // 文件夹资源节点（含子文件节点，父子资源并存）
        var clientNode = Assert.IsType<PluginResourceNode>(PluginTree.FindNode(plugin.Nodes, "client"));
        Assert.Equal(PluginNodeKind.Folder, clientNode.Kind);
        Assert.Single(clientNode.Children);
        Assert.Equal("mods", Assert.Single(clientNode.Children).NodeId);

        var clientResource = clientNode.Resource;
        Assert.Equal(SoftwareTier.Core, clientResource.Tier);
        Assert.Equal(InstallationMode.Portable, clientResource.Mode);
        Assert.Equal(2, clientResource.Sources.Count);

        var firstSource = clientResource.Sources[0];
        Assert.Equal("123", firstSource.ProviderId);
        Assert.NotNull(firstSource.Locator);
        Assert.Equal(ResourceLocatorKind.Folder, firstSource.Locator!.Kind);
        Assert.Equal("item_client", firstSource.Locator.ProviderItemId);

        // 文件子节点
        var modsNode = Assert.IsType<PluginResourceNode>(PluginTree.FindNode(plugin.Nodes, "mods"));
        Assert.Equal(PluginNodeKind.File, modsNode.Kind);
        Assert.Equal(ResourceLocatorKind.File, modsNode.Resource.Sources[0].Locator!.Kind);

        // 扁平派生：包含且仅包含三个资源节点
        Assert.Equal(
            new[] { "client", "mods", "readme" },
            plugin.Resources.Select(r => r.Id).ToArray());
    }

    // ------------------------------------------------------------- TR-2.2

    /// <summary>
    /// TR-2.2: schema1 样例（folder locator、winget、needsLocatorResolution、多 source）
    /// 读入后成为根级节点，类型按 locator 迁移，字段无损，且无致命 issue。
    /// </summary>
    [Fact]
    public void Schema1Migration_Reads()
    {
        var folderPack = ResourceObject("folder_pack", CloudSource("123", "folder", "item_pack"));
        var multi = ResourceObject("multi", WingetSource() + "," + CloudSource("baidu", "file", "item_multi"));

        var (plugin, issues) = PluginReader.Parse(Schema1Plugin(folderPack + "," + multi));

        Assert.NotNull(plugin);
        Assert.Empty(issues);
        Assert.Equal(1, plugin!.Schema);

        // 两条旧资源成为根级节点，folder locator → Folder
        Assert.Equal(2, plugin.Nodes.Count);
        var first = Assert.IsType<PluginResourceNode>(plugin.Nodes[0]);
        var second = Assert.IsType<PluginResourceNode>(plugin.Nodes[1]);
        Assert.Equal(PluginNodeKind.Folder, first.Kind);
        Assert.Equal(PluginNodeKind.File, second.Kind);

        Assert.Equal(ResourceLocatorKind.Folder, first.Resource.Sources[0].Locator!.Kind);

        var winget = second.Resource.Sources.Single(s => s.Kind == SourceKind.Winget);
        Assert.Equal("Foo.Bar", winget.WingetId);
        Assert.True(winget.NeedsLocatorResolution);

        Assert.Equal(2, plugin.Resources.Count);
    }

    // ------------------------------------------------------------- TR-2.3

    /// <summary>TR-2.3: 重复 id、深度 13、分组带 source、未知类型、locator 类型不符、缺 resource 全部产 issue 且不崩。</summary>
    [Fact]
    public void TreeIntegrity_ReaderIssues()
    {
        // (a) 重复资源 id
        var dupTree = GroupNode(
            "g_dup",
            "重复",
            ResourceNode("file", ResourceObject("dup", CloudSource("123", "file", "i1"))),
            ResourceNode("file", ResourceObject("dup", CloudSource("123", "file", "i2"))));
        var (dupPlugin, dupIssues) = PluginReader.Parse(Schema2Plugin(dupTree));
        Assert.NotNull(dupPlugin);
        Assert.Contains(dupIssues, i => i.Code == "plugin.node_duplicate_id" && i.Subject == "dup");

        // (b) 13 层分组链（上限 12）
        string? chain = null;
        for (var d = 13; d >= 1; d--)
        {
            chain = GroupNode("g_d_" + d, "L" + d, chain is null ? Array.Empty<string>() : new[] { chain });
        }

        var (deepPlugin, deepIssues) = PluginReader.Parse(Schema2Plugin(chain!));
        Assert.NotNull(deepPlugin);
        Assert.Contains(deepIssues, i => i.Code == "plugin.node_too_deep");

        // (c) 分组携带 sources 字段：issue 且分组仍作为无资源节点存在
        var strayGroup = "{\"nodeType\":\"group\",\"id\":\"g_stray\",\"name\":\"坏分组\",\"sources\":[]}";
        var validFile = ResourceNode("file", ResourceObject("ok", CloudSource("123", "file", "iok")));
        var (strayPlugin, strayIssues) = PluginReader.Parse(Schema2Plugin(strayGroup + "," + validFile));
        Assert.NotNull(strayPlugin);
        Assert.Contains(strayIssues, i => i.Code == "plugin.group_has_resource_fields");
        var strayNode = PluginTree.FindNode(strayPlugin!.Nodes, "g_stray");
        Assert.NotNull(strayNode);
        Assert.Empty(strayNode!.EnumerateResourceNodes());
        Assert.Single(strayPlugin.Resources);

        // (d) 未知节点类型：跳过该节点
        var unknown = "{\"nodeType\":\"symlink\",\"id\":\"x\",\"name\":\"X\"}";
        var (unknownPlugin, unknownIssues) = PluginReader.Parse(Schema2Plugin(unknown + "," + validFile));
        Assert.NotNull(unknownPlugin);
        Assert.Contains(unknownIssues, i => i.Code == "plugin.node_unknown_type");
        Assert.Single(unknownPlugin!.Resources);

        // (e) folder 节点的 locator 声明成 file：issue + 以节点类型修正为 folder
        var mismatch = ResourceNode("folder", ResourceObject("misfolder", CloudSource("123", "file", "imis")));
        var (misPlugin, misIssues) = PluginReader.Parse(Schema2Plugin(mismatch));
        Assert.NotNull(misPlugin);
        Assert.Contains(misIssues, i => i.Code == "plugin.node_locator_kind_mismatch");
        var misNode = Assert.IsType<PluginResourceNode>(PluginTree.FindNode(misPlugin!.Nodes, "misfolder"));
        Assert.Equal(ResourceLocatorKind.Folder, misNode.Resource.Sources[0].Locator!.Kind);

        // (f) file 节点缺少 resource 对象：跳过并记 issue
        var noResource = "{\"nodeType\":\"file\",\"name\":\"无载荷\"}";
        var (nrPlugin, nrIssues) = PluginReader.Parse(Schema2Plugin(noResource + "," + validFile));
        Assert.NotNull(nrPlugin);
        Assert.Contains(nrIssues, i => i.Code == "plugin.node_missing_resource");
        Assert.Single(nrPlugin!.Resources);
    }

    /// <summary>schema 1 文档中资源无 source 仍在 ResourceValidate 阶段失败（读取行为不回归）。</summary>
    [Fact]
    public void Schema1_ResourceWithoutSource_KeepsLegacyStage()
    {
        var json = Schema1Plugin(ResourceObject("lonely", string.Empty));
        var result = new PluginImportService(
            new[] { "123", "baidu", "quark", "lanzou", "xunlei" },
            AppLog.For("Test")).Import(json, null, Array.Empty<string>());

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.ResourceValidate, result.Stage);
    }
}
