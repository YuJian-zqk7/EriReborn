using EriReborn.Core.Domain;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// schema v2 的写出器与读取器互为镜像：节点树（group/file/folder、嵌套、多 source）
/// 经 PluginWriter 写出再由 PluginReader 读回必须无损，且第二次写出保持稳定（TR-3.1）。
/// </summary>
public sealed class PluginWriterRoundtripTests
{
    private static ResourcePlugin BuildTreePlugin()
    {
        // mc（分组）
        // ├─ client（文件夹资源，带主 source + 镜像 source）
        // │   └─ mods（文件资源，云盘 file locator）
        // └─ tools（分组）
        //     └─ code（文件资源，winget，验证包 id 不丢）
        var mods = PluginResourceNode.Create(
            PluginNodeKind.File,
            new PluginResource
            {
                Id = "mc_mods",
                Name = "mods",
                CategoryId = "Game",
                DirectoryName = "mods",
                Tier = SoftwareTier.Optional,
                Mode = InstallationMode.Install,
                Sources = new[]
                {
                    new SoftwareSource
                    {
                        Kind = SourceKind.CloudShare,
                        ProviderId = "123",
                        ShareUrl = "https://example.invalid/s/mc",
                        FileName = "mods",
                        Locator = new ResourceLocator
                        {
                            Kind = ResourceLocatorKind.File,
                            Path = "1.21.1/Client/mods",
                            Name = "mods",
                            ProviderItemId = "item_mods",
                        },
                    },
                },
            });

        var client = PluginResourceNode.Create(
            PluginNodeKind.Folder,
            new PluginResource
            {
                Id = "mc_client",
                Name = "Client",
                CategoryId = "Game",
                DirectoryName = "client",
                Tier = SoftwareTier.Recommended,
                Mode = InstallationMode.Install,
                Sources = new SoftwareSource[]
                {
                    new()
                    {
                        Kind = SourceKind.CloudShare,
                        ProviderId = "123",
                        ShareUrl = "https://example.invalid/s/mc",
                        FileName = "Client",
                        Locator = new ResourceLocator
                        {
                            Kind = ResourceLocatorKind.Folder,
                            Path = "1.21.1/Client",
                            Name = "Client",
                            ProviderItemId = "item_client",
                        },
                    },
                    new()
                    {
                        Kind = SourceKind.CloudShare,
                        ProviderId = "quark",
                        ShareUrl = "https://example.invalid/s/mc-mirror",
                        FileName = "Client",
                        Locator = new ResourceLocator
                        {
                            Kind = ResourceLocatorKind.Folder,
                            Path = "1.21.1/Client",
                            Name = "Client",
                            ProviderItemId = "mirror_client",
                        },
                    },
                },
            },
            new[] { (PluginNode)mods });

        var code = PluginResourceNode.Create(
            PluginNodeKind.File,
            new PluginResource
            {
                Id = "vscode",
                Name = "VS Code",
                CategoryId = "Utility",
                DirectoryName = "vscode",
                Tier = SoftwareTier.Optional,
                Mode = InstallationMode.Install,
                Sources = new[]
                {
                    new SoftwareSource
                    {
                        Kind = SourceKind.Winget,
                        WingetId = "Microsoft.VisualStudioCode",
                    },
                },
            });

        var tools = PluginGroupNode.Create("g_tools", "工具", new[] { (PluginNode)code });
        var mc = PluginGroupNode.Create("g_mc", "Minecraft", new PluginNode[] { client, tools });

        return ResourcePlugin.FromNodes(
            "tree_plugin",
            "树插件",
            "3.1.0",
            new[] { (PluginNode)mc },
            author: "tester",
            description: "节点树往返");
    }

    [Fact]
    public void Nested_tree_round_trips_without_loss()
    {
        var plugin = BuildTreePlugin();

        var json = PluginWriter.Write(plugin);
        var (read, issues) = PluginReader.Parse(json);

        Assert.NotNull(read);
        Assert.Empty(issues);
        Assert.Equal(2, read!.Schema);
        Assert.Equal("tree_plugin", read.Id);
        Assert.Equal("树插件", read.Name);
        Assert.Equal("tester", read.Author);

        // 根层只有一个分组。
        var root = Assert.Single(read.Nodes);
        var rootGroup = Assert.IsType<PluginGroupNode>(root);
        Assert.Equal("g_mc", rootGroup.NodeId);
        Assert.Equal("Minecraft", rootGroup.Name);

        // mc → client(folder, 含子 file) + tools(group → code file)。
        Assert.Equal(2, rootGroup.Children.Count);

        var clientNode = Assert.IsType<PluginResourceNode>(rootGroup.Children[0]);
        Assert.Equal(PluginNodeKind.Folder, clientNode.Kind);
        Assert.Equal("mc_client", clientNode.NodeId);

        var clientSource = Assert.Single(clientNode.Resource.Sources, s => s.ProviderId == "123");
        Assert.Equal(ResourceLocatorKind.Folder, clientSource.Locator!.Kind);
        Assert.Equal("1.21.1/Client", clientSource.Locator.Path);
        Assert.Equal("item_client", clientSource.Locator.ProviderItemId);

        // 镜像 source 必须原样保留，不能只剩第一个。
        Assert.Contains(clientNode.Resource.Sources, source => source.ProviderId == "quark");
        Assert.Equal(2, clientNode.Resource.Sources.Count);

        var modsNode = Assert.IsType<PluginResourceNode>(Assert.Single(clientNode.Children));
        Assert.Equal(PluginNodeKind.File, modsNode.Kind);
        Assert.Equal("mc_mods", modsNode.NodeId);
        Assert.Equal("item_mods", modsNode.Resource.Sources.Single().Locator!.ProviderItemId);

        var toolsNode = Assert.IsType<PluginGroupNode>(rootGroup.Children[1]);
        var codeNode = Assert.IsType<PluginResourceNode>(Assert.Single(toolsNode.Children));
        Assert.Equal("Microsoft.VisualStudioCode", codeNode.Resource.Sources.Single().WingetId);

        // 前序扁平化：client → mods → code。
        Assert.Equal(new[] { "mc_client", "mc_mods", "vscode" }, read.Resources.Select(r => r.Id));
    }

    [Fact]
    public void Writing_twice_is_stable()
    {
        var plugin = BuildTreePlugin();

        var first = PluginWriter.Write(plugin);
        var (read, _) = PluginReader.Parse(first);

        var second = PluginWriter.Write(read!);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_leaf_group_without_children_round_trips()
    {
        // 空分组（没有任何子节点）也必须能写出读回，children 缺省即可。
        var plugin = ResourcePlugin.FromNodes(
            "empty_group_plugin",
            "空分组",
            "1.0.0",
            new PluginNode[]
            {
                PluginResourceNode.Create(
                    PluginNodeKind.File,
                    new PluginResource
                    {
                        Id = "only",
                        Name = "唯一资源",
                        CategoryId = "Utility",
                        DirectoryName = "only",
                        Sources = new[]
                        {
                            new SoftwareSource { Kind = SourceKind.Manual, FileName = "readme.txt" },
                        },
                    }),
                PluginGroupNode.Create("g_empty", "空分组"),
            });

        var (read, issues) = PluginReader.Parse(PluginWriter.Write(plugin));

        Assert.NotNull(read);
        Assert.Empty(issues);
        Assert.Contains(read!.Nodes, node => node is PluginGroupNode { NodeId: "g_empty" }
            && node.Children.Count == 0);
    }
}
