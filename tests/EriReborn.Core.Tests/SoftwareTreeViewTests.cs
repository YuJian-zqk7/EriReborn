using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// 软件页「插件资源树」视图测试（AC-12）：导入的插件资源按
/// 插件根 → group/文件夹分支 → 文件/文件夹叶子还原；官方目录不进树；
/// 叶子与混合文件夹分支复用既有安装计划入口。
/// </summary>
public sealed class SoftwareTreeViewTests
{
    private static async Task<(AppHost Host, string UserData)> CreateHostAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (host, userData);
    }

    private static void Cleanup(string userData)
    {
        try
        {
            if (Directory.Exists(userData))
            {
                Directory.Delete(userData, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>造一条插件资源定义；folder=true 时来源 locator 指向整个文件夹。</summary>
    private static SoftwareDefinition PluginResource(
        string id,
        string pluginId,
        string name,
        IReadOnlyList<string>? groupPath,
        bool folder = false) => new()
    {
        Id = id,
        Name = name,
        CategoryId = "Utility",
        DirectoryName = id,
        CatalogId = pluginId,
        IsPluginProvided = true,
        PluginGroupPath = groupPath,
        Sources = new[]
        {
            new SoftwareSource
            {
                Kind = SourceKind.CloudShare,
                ProviderId = "123",
                ShareUrl = "https://www.123pan.com/s/abc",
                FileName = name,
                Locator = new ResourceLocator
                {
                    Kind = folder ? ResourceLocatorKind.Folder : ResourceLocatorKind.File,
                    Name = name,
                    ProviderItemId = id,
                },
            },
        },
    };

    [Fact]
    public async Task Builds_plugin_root_group_and_folder_hierarchy()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // 插件安装记录提供根节点显示名（没有记录时退回插件 id，另见用例）。
            Assert.True(host.PluginInstallations.Record(new InstalledPlugin
            {
                Id = "p1",
                Name = "我的插件",
                Version = "1.0.0",
            }));

            host.PluginResources.Add(new[]
            {
                // 注册表保持树前序：根层文件、根层文件夹资源、文件夹内文件、两级分组下文件。
                PluginResource("p1-file-a", "p1", "文件A", Array.Empty<string>()),
                PluginResource("p1-pack", "p1", "素材包", Array.Empty<string>(), folder: true),
                PluginResource("p1-pic", "p1", "图1", new[] { "素材包" }),
                PluginResource("p1-deep", "p1", "深层文件", new[] { "合集", "子组" }),
            });

            var page = new SoftwareViewModel(host);

            Assert.True(page.HasPluginTree);
            var root = Assert.Single(page.PluginTreeRoots);
            Assert.Equal("我的插件", root.Name);
            Assert.True(root.IsPluginRoot);
            Assert.Equal("插件", root.KindText);
            Assert.Null(root.Item);

            Assert.Equal(new[] { "文件A", "素材包", "合集" }, root.Children.Select(c => c.Name).ToArray());

            // 根层文件就是普通叶子。
            var fileA = root.Children[0];
            Assert.Equal("文件", fileA.KindText);
            Assert.True(fileA.IsInstallable);
            Assert.Empty(fileA.Children);

            // 文件夹资源既是可下载叶子、又是子节点的分支（混合节点，同一节点 id）。
            var pack = root.Children[1];
            Assert.Equal("resource:p1-pack", pack.Id);
            Assert.Equal("文件夹", pack.KindText);
            Assert.True(pack.IsInstallable);
            var pic = Assert.Single(pack.Children);
            Assert.Equal("图1", pic.Name);
            Assert.Equal("文件", pic.KindText);

            // group 分支本身不可下载，层级一路还原到底。
            var collection = root.Children[2];
            Assert.False(collection.IsInstallable);
            Assert.Equal("分组", collection.KindText);
            var subgroup = Assert.Single(collection.Children);
            Assert.Equal("子组", subgroup.Name);
            var deep = Assert.Single(subgroup.Children);
            Assert.Equal("深层文件", deep.Name);
            Assert.True(deep.IsInstallable);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Flat_and_schema1_resources_hang_directly_under_the_plugin_root()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[]
            {
                // schema2 根层资源：空数组路径。
                PluginResource("p2-a", "p2", "甲", Array.Empty<string>()),
                // schema1 迁移资源：PluginGroupPath 为 null，同样直接挂根下。
                PluginResource("p2-b", "p2", "乙", null),
            });

            var page = new SoftwareViewModel(host);

            var root = Assert.Single(page.PluginTreeRoots);
            // 没有安装记录时根名诚实退回插件 id。
            Assert.Equal("p2", root.Name);
            Assert.Equal(2, root.Children.Count);
            Assert.All(root.Children, child =>
            {
                Assert.True(child.IsInstallable);
                Assert.Empty(child.Children);
            });
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Official_entries_stay_out_of_the_tree_and_out_of_the_flat_list()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var official = new SoftwareDefinition
            {
                Id = "official-one",
                Name = "官方条目",
                CategoryId = "Utility",
                DirectoryName = "official-one",
                IsPluginProvided = false,
                CatalogId = null,
                Sources = new[]
                {
                    new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/a.exe" },
                },
            };
            host.PluginResources.Add(new[] { official });
            host.PluginResources.Add(new[]
            {
                PluginResource("p3-a", "p3", "插件文件", Array.Empty<string>()),
            });

            var page = new SoftwareViewModel(host);

            // 树里只有插件资源，官方条目不变成树节点。
            var root = Assert.Single(page.PluginTreeRoots);
            Assert.Equal("p3", root.Name);
            Assert.DoesNotContain(page.PluginTreeRoots, r => r.Name == "官方条目");
            Assert.DoesNotContain(root.Children, child => child.Id == "resource:official-one");

            // 列表视图同样只显示插件资源：官方预置目录连扁平列表也不进。
            Assert.DoesNotContain(page.Items, item => item.Id == "official-one");
            Assert.Contains(page.Items, item => item.Id == "p3-a");
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Two_plugins_become_two_roots_ordered_by_display_name()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[]
            {
                PluginResource("pz-a", "pz", "Z 文件", Array.Empty<string>()),
            });
            host.PluginResources.Add(new[]
            {
                PluginResource("pa-a", "pa", "A 文件", Array.Empty<string>()),
            });

            var page = new SoftwareViewModel(host);

            Assert.Equal(new[] { "pa", "pz" }, page.PluginTreeRoots.Select(r => r.Name).ToArray());
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Selecting_a_leaf_or_folder_branch_reuses_the_existing_install_flow()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[]
            {
                PluginResource("p4-pack", "p4", "素材包", Array.Empty<string>(), folder: true),
                PluginResource("p4-pic", "p4", "图1", new[] { "素材包" }),
                PluginResource("p4-g", "p4", "分组里", new[] { "某分组" }),
            });

            var page = new SoftwareViewModel(host);
            Assert.False(page.IsTreeView);
            page.ShowTreeViewCommand.Execute(null);
            Assert.True(page.IsTreeView);

            var root = Assert.Single(page.PluginTreeRoots);
            var pack = root.Children[0];
            var pic = Assert.Single(pack.Children);
            var group = root.Children[1];

            // 树选中文件叶子：扁平列表语义上的「当前软件」同步过去。
            page.SelectedTreeNode = pic;
            Assert.Equal("p4-pic", page.SelectedItem?.Id);

            // 文件夹分支走同一个 PrepareInstall：得到既有安装计划（不另造下载链路）。
            page.PrepareNode(pack);
            Assert.Equal("p4-pack", page.SelectedItem?.Id);
            Assert.True(page.HasInstallPlan);

            // 纯分组节点不触发任何安装动作，选择与计划保持原状。
            page.PrepareNode(group);
            Assert.Equal("p4-pack", page.SelectedItem?.Id);
            Assert.True(page.HasInstallPlan);

            page.CancelInstallCommand.Execute(null);
            Assert.False(page.HasInstallPlan);

            page.ShowListViewCommand.Execute(null);
            Assert.False(page.IsTreeView);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_tree_rebuilds_when_plugin_resources_change_while_the_page_is_open()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new SoftwareViewModel(host);
            Assert.Empty(page.PluginTreeRoots);
            Assert.False(page.HasPluginTree);

            // 页面已打开后再导入插件：与扁平列表一样必须自动刷新。
            host.PluginResources.Add(new[]
            {
                PluginResource("p5-a", "p5", "后来的", Array.Empty<string>()),
            });

            var root = Assert.Single(page.PluginTreeRoots);
            Assert.Equal("p5", root.Name);
            Assert.True(page.HasPluginTree);
        }
        finally
        {
            Cleanup(data);
        }
    }
}
