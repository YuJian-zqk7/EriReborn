using System.Text.Json;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The workshop builder writes a .eriplugin.json. What makes it a plugin builder
/// rather than a text generator is that the file it writes can be read back by the
/// importer — so the round trip is what is tested here, not the string.
/// </summary>
public sealed class WorkshopPluginBuilderTests
{
    private static async Task<(PluginBuilderViewModel Builder, string UserData)> CreateAsync()
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

        return (new PluginBuilderViewModel(host), userData);
    }

    /// <summary>
    /// A host with one cloud platform registered. The plain host has none, which is what makes "a
    /// share source must name a registered platform" testable — but it also means a share source
    /// cannot be completed at all there, so authoring one needs its own host.
    /// </summary>
    private static async Task<(PluginBuilderViewModel Builder, string UserData, FakeCloudProvider Cloud)> CreateWithCloudAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var cloud = new FakeCloudProvider();
        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(new ICloudProvider[] { cloud }, AppLog.For("Test")),
            AppLog.For("Test"));

        return (new PluginBuilderViewModel(host), userData, cloud);
    }

    private sealed class FakeCloudProvider : CloudProviderBase
    {
        public FakeCloudProvider()
            : base(new TestNetworkService(new HttpClient()), new InMemoryCredentialStore())
        {
        }

        public override string Id => "123";

        public override string DisplayName => "123 云盘（测试）";

        public override bool RequiresAuthentication => false;

        public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        /// <summary>
        /// 真实平台「列目录」请求的次数：TR-6.2 用它证明子级只在第一次展开时读取，
        /// 读取分享、收起再展开、选择条目都不会多发请求。
        /// </summary>
        public int ListCallCount { get; private set; }

        /// <summary>
        /// A share with two levels of folders, so "read the tree and pick something" has a tree to
        /// read and something nested to pick.
        ///
        /// <para>A/ → a1/ → ResourceA.zip, ResourceB.zip</para>
        /// </summary>
        public override Task<CloudListResult> ListShareFolderAsync(
            string shareUrl,
            string? parentItemId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            return Task.FromResult(CloudListResult.Ok(parentItemId switch
            {
                null => new[] { new CloudFile("folder-A", "A", true) },
                "folder-A" => new[] { new CloudFile("folder-a1", "a1", true) },
                "folder-a1" => new[]
                {
                    new CloudFile("file-A", "ResourceA.zip", false, 512),
                    new CloudFile("file-B", "ResourceB.zip", false, 1024),
                },
                _ => Array.Empty<CloudFile>(),
            }));
        }
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

    [Fact]
    public async Task A_generated_plugin_is_read_back_by_the_importer()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            builder.PluginId = "workshop_plugin";
            builder.PluginName = "工坊生成的插件";
            builder.PluginVersion = "1.2.0";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");

            builder.DraftId = "workshop_tool";
            builder.DraftName = "工坊工具";
            builder.DraftDirectoryName = "WorkshopTool";
            builder.DraftKind = SourceKind.HttpUrl;
            builder.DraftUrl = "https://example.invalid/workshop-tool.exe";
            builder.DraftFileName = "workshop-tool.exe";
            builder.AddResourceCommand.Execute(null);

            Assert.Single(builder.Resources);

            builder.GeneratePluginCommand.Execute(null);

            // It must land where the plugin page looks, not somewhere private.
            var path = Path.Combine(builder.PluginsDirectory, "workshop_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));
            var result = importer.Import(File.ReadAllText(path), signatureJson: null, existingIds: Array.Empty<string>());

            Assert.True(result.Succeeded, result.Message);

            var accepted = result.Accepted.Single(definition => definition.Id == "workshop_tool");
            Assert.Equal("工坊工具", accepted.Name);
            Assert.Equal("Utility", accepted.CategoryId);

            // The source has to survive the round trip with its kind and address intact:
            // the reader reads "url", not "Url", so a serialized record would lose it.
            var source = accepted.Sources.Single();
            Assert.Equal(SourceKind.HttpUrl, source.Kind);
            Assert.Equal("https://example.invalid/workshop-tool.exe", source.Url);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_form_without_resources_refuses_to_generate_and_says_why()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            builder.PluginId = "workshop_plugin";
            builder.GeneratePluginCommand.Execute(null);

            Assert.False(File.Exists(Path.Combine(builder.PluginsDirectory, "workshop_plugin.eriplugin.json")));
            Assert.Contains("资源", builder.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_share_source_must_name_a_registered_platform()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            // No cloud platform is registered in this host, so a share source cannot be
            // completed. It must be refused rather than written with an empty provider —
            // that file would fail the importer's provider check.
            Assert.Empty(builder.Providers);

            builder.PluginId = "workshop_plugin";
            builder.DraftId = "workshop_tool";
            builder.DraftName = "工坊工具";
            builder.DraftDirectoryName = "WorkshopTool";
            builder.DraftKind = SourceKind.CloudShare;
            builder.DraftShareUrl = "https://example.invalid/share";
            builder.AddResourceCommand.Execute(null);

            Assert.Empty(builder.Resources);
            Assert.Contains("网盘平台", builder.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_plugin_may_invent_a_category_when_it_is_a_legal_folder_name()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            // The official taxonomy is the registered vocabulary, not the only permitted
            // one: the id only has to be usable as a folder name, which is what it becomes
            // under the install root.
            builder.NewCategory = "MyTools";
            builder.AddCategoryCommand.Execute(null);
            Assert.Equal("MyTools", builder.DraftCategory?.Id);

            builder.PluginId = "workshop_plugin";
            builder.DraftId = "workshop_tool";
            builder.DraftName = "工坊工具";
            builder.DraftDirectoryName = "WorkshopTool";
            builder.DraftKind = SourceKind.HttpUrl;
            builder.DraftUrl = "https://example.invalid/workshop-tool.exe";
            builder.AddResourceCommand.Execute(null);
            builder.GeneratePluginCommand.Execute(null);

            var path = Path.Combine(builder.PluginsDirectory, "workshop_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));
            var result = importer.Import(File.ReadAllText(path), signatureJson: null, existingIds: Array.Empty<string>());

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal("MyTools", result.Accepted.Single().CategoryId);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_locator_typed_in_the_form_round_trips_through_the_file()
    {
        var (builder, userData, _) = await CreateWithCloudAsync();
        try
        {
            builder.PluginId = "located_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");

            builder.DraftId = "res_a";
            builder.DraftName = "Resource A";
            builder.DraftDirectoryName = "ResA";
            builder.DraftKind = SourceKind.CloudShare;
            builder.DraftProviderId = "123";
            builder.DraftShareUrl = "https://example.invalid/s/abc";
            builder.DraftFileName = "ResourceA.zip";

            // The share is a tree; this is the part that says which item in it.
            builder.DraftLocatorPath = "A/a1/b1/ResourceA.zip";
            builder.DraftProviderItemId = "123456";

            builder.AddResourceCommand.Execute(null);
            Assert.Single(builder.Resources);

            builder.GeneratePluginCommand.Execute(null);

            var path = Path.Combine(builder.PluginsDirectory, "located_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));
            var result = importer.Import(File.ReadAllText(path), signatureJson: null, existingIds: Array.Empty<string>());

            Assert.True(result.Succeeded, result.Message);

            var locator = result.Accepted.Single().Sources.Single().Locator;
            Assert.Equal("A/a1/b1/ResourceA.zip", locator?.Path);
            Assert.Equal("123456", locator?.ProviderItemId);
            Assert.Equal(ResourceLocatorKind.File, locator?.Kind);

            // And opening it again puts what was typed back in the form, or an edit would quietly
            // drop the locator and leave the resource pointing at the share root.
            builder.ResetCommand.Execute(null);
            Assert.Empty(builder.DraftLocatorPath);

            builder.RefreshInstalledPlugins();
            builder.EditInstalledPluginCommand.Execute(builder.InstalledPlugins.Single(entry => entry.Id == "located_plugin"));
            builder.EditResourceCommand.Execute(builder.Resources.Single());

            Assert.Equal("A/a1/b1/ResourceA.zip", builder.DraftLocatorPath);
            Assert.Equal("123456", builder.DraftProviderItemId);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Selecting_a_folder_then_a_file_makes_two_independent_locator_nodes()
    {
        var (builder, userData, _) = await CreateWithCloudAsync();
        try
        {
            builder.PluginId = "tree_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");
            builder.DraftKind = SourceKind.CloudShare;
            builder.DraftProviderId = "123";
            builder.DraftShareUrl = "https://example.invalid/s/tree";

            // 读取分享，懒展开两层：A → a1 → ResourceA.zip / ResourceB.zip。
            await builder.ReadShareCommand.ExecuteAsync(null);
            var rootA = Assert.Single(builder.ShareTreeRoots);
            Assert.Equal("A", rootA.File.Name);

            rootA.ToggleExpandCommand.Execute(null);
            var a1 = Assert.Single(rootA.Children);
            Assert.Equal("a1", a1.File.Name);

            a1.ToggleExpandCommand.Execute(null);
            Assert.Equal(2, a1.Children.Count);

            var fileA = a1.Children.Single(row => row.File.Name == "ResourceA.zip");
            var fileB = a1.Children.Single(row => row.File.Name == "ResourceB.zip");

            // 先选文件夹 a1：它作为一个完整的 Folder 资源直接进树，路径与平台条目 id 全部来自真实遍历。
            a1.SelectCommand.Execute(null);

            var folderNode = Assert.Single(builder.Nodes);
            Assert.Equal(PluginNodeKind.Folder, folderNode.Kind);
            var folderLocator = folderNode.Resource!.Source.Locator!;
            Assert.Equal(ResourceLocatorKind.Folder, folderLocator.Kind);
            Assert.Equal("A/a1", folderLocator.Path);
            Assert.Equal("folder-a1", folderLocator.ProviderItemId);

            // 选择文件夹绝不枚举内容：两个兄弟文件没有被偷偷加进插件树。
            Assert.DoesNotContain(
                builder.Resources,
                resource => resource.AllSources.Any(source => source.Locator?.ProviderItemId == "file-A"));
            Assert.DoesNotContain(
                builder.Resources,
                resource => resource.AllSources.Any(source => source.Locator?.ProviderItemId == "file-B"));

            // 再选里面的文件：与父文件夹资源并存，但它是另一个独立 locator 的 File 资源。
            fileA.SelectCommand.Execute(null);

            Assert.Equal(2, builder.Nodes.Count);
            var fileNode = builder.Nodes[1];
            Assert.Equal(PluginNodeKind.File, fileNode.Kind);
            var fileLocator = fileNode.Resource!.Source.Locator!;
            Assert.Equal(ResourceLocatorKind.File, fileLocator.Kind);
            Assert.Equal("A/a1/ResourceA.zip", fileLocator.Path);
            Assert.Equal("file-A", fileLocator.ProviderItemId);

            // 同一个真实条目选第二次：稳定 itemId 去重，不新增节点。
            a1.SelectCommand.Execute(null);
            Assert.Equal(2, builder.Nodes.Count);

            fileB.SelectCommand.Execute(null);
            Assert.Equal(3, builder.Nodes.Count);

            // 一个分享链接，三个各自独立的 locator：生成的文件被导入器读回来时全部成立。
            builder.GeneratePluginCommand.Execute(null);

            var path = Path.Combine(builder.PluginsDirectory, "tree_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));
            var result = importer.Import(File.ReadAllText(path), signatureJson: null, existingIds: Array.Empty<string>());

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(3, result.Accepted.Count);

            var sources = result.Accepted.SelectMany(resource => resource.Sources).ToList();
            Assert.Equal(3, sources.Count);

            // 三条资源共用一条分享链接，区分它们的是各自的 locator。
            Assert.All(sources, source => Assert.Equal(builder.DraftShareUrl, source.ShareUrl));

            var paths = sources.Select(source => source.Locator?.Path).OrderBy(text => text).ToArray();
            Assert.Equal(new[] { "A/a1", "A/a1/ResourceA.zip", "A/a1/ResourceB.zip" }, paths);

            var kinds = sources.Select(source => source.Locator!.Kind).OrderBy(kind => kind).ToArray();
            Assert.Equal(
                new[] { ResourceLocatorKind.File, ResourceLocatorKind.File, ResourceLocatorKind.Folder },
                kinds);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task The_share_tree_lists_each_folder_only_when_it_is_first_expanded()
    {
        var (builder, userData, cloud) = await CreateWithCloudAsync();
        try
        {
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");
            builder.DraftKind = SourceKind.CloudShare;
            builder.DraftProviderId = "123";
            builder.DraftShareUrl = "https://example.invalid/s/tree";

            // 读分享只请求一次根目录；没有任何子级被预加载。
            await builder.ReadShareCommand.ExecuteAsync(null);
            Assert.Equal(1, cloud.ListCallCount);

            var rootA = Assert.Single(builder.ShareTreeRoots);
            Assert.Equal(ShareLoadState.NotLoaded, rootA.LoadState);
            Assert.Empty(rootA.Children);

            // 第一次展开 A：请求一次。
            rootA.ToggleExpandCommand.Execute(null);
            Assert.Equal(2, cloud.ListCallCount);
            Assert.Equal(ShareLoadState.Loaded, rootA.LoadState);
            var a1 = Assert.Single(rootA.Children);
            Assert.Equal(ShareLoadState.NotLoaded, a1.LoadState);

            // 收起再展开：已加载过，不再请求。
            rootA.ToggleExpandCommand.Execute(null);
            rootA.ToggleExpandCommand.Execute(null);
            Assert.Equal(2, cloud.ListCallCount);

            // a1 从未展开过，因此它的内容一次都没被列过。
            a1.ToggleExpandCommand.Execute(null);
            Assert.Equal(3, cloud.ListCallCount);
            Assert.Equal(ShareLoadState.Loaded, a1.LoadState);
            Assert.Equal(2, a1.Children.Count);

            // 选择文件夹、选择文件都只是记录 locator，永远不触发列目录请求。
            a1.SelectCommand.Execute(null);
            a1.Children[0].SelectCommand.Execute(null);
            Assert.Equal(3, cloud.ListCallCount);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Editing_a_resource_writes_the_change_back_into_the_same_line()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            builder.PluginId = "workshop_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");

            builder.DraftId = "workshop_tool";
            builder.DraftName = "工坊工具";
            builder.DraftDirectoryName = "WorkshopTool";
            builder.DraftKind = SourceKind.HttpUrl;
            builder.DraftUrl = "https://example.invalid/workshop-tool.exe";
            builder.AddResourceCommand.Execute(null);

            // The resource list is not a receipt: a line has to be openable again, or a typo in it
            // can only be fixed by deleting the line and typing the whole thing over.
            var line = builder.Resources.Single();
            builder.EditResourceCommand.Execute(line);

            Assert.True(builder.IsEditingResource);
            Assert.Equal("编辑这条资源", builder.ResourceFormTitle);
            Assert.Equal("workshop_tool", builder.DraftId);
            Assert.Equal("工坊工具", builder.DraftName);
            Assert.Equal("https://example.invalid/workshop-tool.exe", builder.DraftUrl);

            builder.DraftName = "工坊工具（改过）";
            builder.DraftUrl = "https://example.invalid/workshop-tool-v2.exe";
            builder.SaveResourceEditCommand.Execute(null);

            // Changed in place: one line still, same id, new values.
            var edited = Assert.Single(builder.Resources);
            Assert.Equal("workshop_tool", edited.Id);
            Assert.Equal("工坊工具（改过）", edited.Name);
            Assert.Equal("https://example.invalid/workshop-tool-v2.exe", edited.Source.Url);
            Assert.False(builder.IsEditingResource);

            // And the edit reaches the file the importer reads.
            builder.GeneratePluginCommand.Execute(null);

            var path = Path.Combine(builder.PluginsDirectory, "workshop_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));
            var result = importer.Import(File.ReadAllText(path), signatureJson: null, existingIds: Array.Empty<string>());

            Assert.True(result.Succeeded, result.Message);

            var accepted = result.Accepted.Single(definition => definition.Id == "workshop_tool");
            Assert.Equal("工坊工具（改过）", accepted.Name);
            Assert.Equal("https://example.invalid/workshop-tool-v2.exe", accepted.Sources.Single().Url);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task An_edited_resource_may_not_take_another_line_s_id()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            builder.PluginId = "workshop_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");
            builder.DraftKind = SourceKind.HttpUrl;

            builder.DraftId = "workshop_tool";
            builder.DraftName = "工坊工具";
            builder.DraftDirectoryName = "WorkshopTool";
            builder.DraftUrl = "https://example.invalid/a.exe";
            builder.AddResourceCommand.Execute(null);

            builder.DraftId = "workshop_tool2";
            builder.DraftName = "工坊工具二";
            builder.DraftDirectoryName = "WorkshopTool2";
            builder.DraftUrl = "https://example.invalid/b.exe";
            builder.AddResourceCommand.Execute(null);

            builder.EditResourceCommand.Execute(builder.Resources.First());
            builder.DraftId = "workshop_tool2";
            builder.SaveResourceEditCommand.Execute(null);

            // The id keys the install folder and the catalog entry, so a clash is refused and the
            // edit stays pending rather than quietly overwriting the other line.
            Assert.Contains("已经在资源树里", builder.Status);
            Assert.True(builder.IsEditingResource);
            Assert.Equal(2, builder.Resources.Count);
            Assert.Equal("workshop_tool", builder.Resources.First().Id);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Cancelling_an_edit_leaves_the_line_alone()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            builder.PluginId = "workshop_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");
            builder.DraftKind = SourceKind.HttpUrl;

            builder.DraftId = "workshop_tool";
            builder.DraftName = "工坊工具";
            builder.DraftDirectoryName = "WorkshopTool";
            builder.DraftUrl = "https://example.invalid/a.exe";
            builder.AddResourceCommand.Execute(null);

            builder.EditResourceCommand.Execute(builder.Resources.Single());
            builder.DraftName = "改了一半不想要了";
            builder.CancelResourceEditCommand.Execute(null);

            Assert.False(builder.IsEditingResource);
            Assert.Equal("工坊工具", builder.Resources.Single().Name);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task An_illegal_category_name_is_refused()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            // The name becomes a directory, so a name that cannot be one is refused
            // rather than slugified behind the author's back.
            builder.NewCategory = "不是合法名字";
            builder.AddCategoryCommand.Execute(null);

            Assert.Contains("不合法", builder.Status);
            Assert.DoesNotContain(builder.Categories, option => option.Id == "不是合法名字");
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Resources_added_under_a_group_form_a_nested_tree_and_round_trip()
    {
        var (builder, userData, _) = await CreateWithCloudAsync();
        try
        {
            builder.PluginId = "tree_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");

            // 先建分组，新资源默认进选中节点下面。
            builder.AddGroupCommand.Execute("Minecraft");
            var group = Assert.Single(builder.Nodes);
            Assert.Equal(PluginNodeKind.Group, group.Kind);
            Assert.Same(group, builder.SelectedNode);

            // 分组下放一个文件夹资源（云盘 folder locator）。
            builder.DraftId = "res_client";
            builder.DraftName = "Client";
            builder.DraftDirectoryName = "client";
            builder.DraftKind = SourceKind.CloudShare;
            builder.DraftProviderId = "123";
            builder.DraftShareUrl = "https://example.invalid/s/mc";
            builder.DraftFileName = "Client";
            builder.DraftLocatorKind = ResourceLocatorKind.Folder;
            builder.DraftLocatorPath = "1.21.1/Client";
            builder.DraftProviderItemId = "item_client";
            builder.AddResourceCommand.Execute(null);

            var client = Assert.Single(group.Children);
            Assert.Equal(PluginNodeKind.Folder, client.Kind);

            // 再选中文件夹节点，往它下面放一个文件资源（父文件夹资源与子资源并存）。
            builder.SelectedNode = client;
            builder.DraftId = "res_mods";
            builder.DraftName = "mods";
            builder.DraftDirectoryName = "mods";
            builder.DraftKind = SourceKind.HttpUrl;
            builder.DraftUrl = "https://example.invalid/mods.zip";
            builder.DraftFileName = "mods.zip";
            builder.AddResourceCommand.Execute(null);

            var mods = Assert.Single(client.Children);
            Assert.Equal(PluginNodeKind.File, mods.Kind);

            // 扁平投影保持前序：client → mods。
            Assert.Equal(new[] { "res_client", "res_mods" }, builder.Resources.Select(r => r.Id));

            builder.GeneratePluginCommand.Execute(null);

            var path = Path.Combine(builder.PluginsDirectory, "tree_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var json = File.ReadAllText(path);

            // 写出来的必须是 schema 2 的嵌套 nodes，而不是旧的扁平 resources。
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("schema").GetInt32());
            Assert.False(root.TryGetProperty("resources", out _));

            var rootNode = Assert.Single(root.GetProperty("nodes").EnumerateArray());
            Assert.Equal("group", rootNode.GetProperty("nodeType").GetString());

            var clientElement = Assert.Single(rootNode.GetProperty("children").EnumerateArray());
            Assert.Equal("folder", clientElement.GetProperty("nodeType").GetString());
            Assert.Equal("res_client", clientElement.GetProperty("resource").GetProperty("id").GetString());
            Assert.Equal(
                "folder",
                clientElement.GetProperty("resource").GetProperty("sources")[0]
                    .GetProperty("locator").GetProperty("type").GetString());

            var modsElement = Assert.Single(clientElement.GetProperty("children").EnumerateArray());
            Assert.Equal("file", modsElement.GetProperty("nodeType").GetString());
            Assert.Equal("res_mods", modsElement.GetProperty("resource").GetProperty("id").GetString());

            // 导入侧按前序拿到两条资源。
            var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));
            var result = importer.Import(json, signatureJson: null, existingIds: Array.Empty<string>());
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(new[] { "res_client", "res_mods" }, result.Accepted.Select(d => d.Id));

            // 再从文件夹读回制作器，树结构（含 folder locator）必须还在。
            builder.ResetCommand.Execute(null);
            builder.RefreshInstalledPlugins();
            builder.EditInstalledPluginCommand.Execute(
                builder.InstalledPlugins.Single(entry => entry.Id == "tree_plugin"));

            var reloadedGroup = Assert.Single(builder.Nodes);
            var reloadedClient = Assert.Single(reloadedGroup.Children);
            Assert.Equal(PluginNodeKind.Folder, reloadedClient.Kind);
            Assert.Equal(
                ResourceLocatorKind.Folder,
                reloadedClient.Resource!.Source.Locator!.Kind);
            Assert.Equal("res_mods", Assert.Single(reloadedClient.Children).NodeId);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Removing_a_group_takes_its_whole_subtree_down()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            builder.PluginId = "tree_plugin";
            builder.DraftCategory = builder.Categories.Single(category => category.Id == "Utility");

            builder.AddGroupCommand.Execute("Minecraft");
            var group = Assert.Single(builder.Nodes);

            builder.DraftId = "res_a";
            builder.DraftName = "Resource A";
            builder.DraftDirectoryName = "res_a";
            builder.DraftKind = SourceKind.HttpUrl;
            builder.DraftUrl = "https://example.invalid/a.exe";
            builder.AddResourceCommand.Execute(null);

            Assert.Single(group.Children);
            Assert.Single(builder.Resources);

            // 测试宿主没有注册确认弹窗，ConfirmAsync 默认放行：验证的是命令链本身——
            // 删分组连带整支子树，扁平投影与选中状态一起清掉。
            builder.RemoveTreeNodeCommand.Execute(group);

            Assert.Empty(builder.Nodes);
            Assert.Empty(builder.Resources);
            Assert.Null(builder.SelectedNode);
            Assert.Contains("整支", builder.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_schema1_plugin_loaded_and_saved_is_upgraded_to_schema2_tree()
    {
        var (builder, userData) = await CreateAsync();
        try
        {
            // 旧版扁平 resources 文档（FR-4：载入不破坏，保存即升级）。
            builder.GeneratedJson = """
            {
              "schema": 1,
              "id": "legacy_plugin",
              "name": "旧插件",
              "version": "1.0.0",
              "resources": [
                {
                  "id": "old_one",
                  "name": "旧资源",
                  "categoryId": "Utility",
                  "directory": "oldone",
                  "tier": "optional",
                  "mode": "install",
                  "sources": [
                    { "kind": "HttpUrl", "url": "https://example.invalid/old.exe" }
                  ]
                }
              ]
            }
            """;

            builder.ImportJsonCommand.Execute(null);

            // 载入后已经是草稿树（旧资源成为根级 file 节点）。
            var node = Assert.Single(builder.Nodes);
            Assert.Equal(PluginNodeKind.File, node.Kind);
            Assert.Equal("old_one", node.NodeId);
            Assert.Single(builder.Resources);

            builder.GeneratePluginCommand.Execute(null);

            var path = Path.Combine(builder.PluginsDirectory, "legacy_plugin.eriplugin.json");
            Assert.True(File.Exists(path), builder.Status);

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(2, doc.RootElement.GetProperty("schema").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("resources", out _));

            var nodeElement = Assert.Single(doc.RootElement.GetProperty("nodes").EnumerateArray());
            Assert.Equal("file", nodeElement.GetProperty("nodeType").GetString());
            Assert.Equal(
                "https://example.invalid/old.exe",
                nodeElement.GetProperty("resource").GetProperty("sources")[0]
                    .GetProperty("url").GetString());

            var (plugin, issues) = PluginReader.Parse(json);
            Assert.NotNull(plugin);
            Assert.Equal(2, plugin!.Schema);
            Assert.Equal("old_one", Assert.Single(plugin.Resources).Id);
            Assert.DoesNotContain(issues, issue => issue.Code.StartsWith("plugin.node", StringComparison.Ordinal));

            var importer = new PluginImportService(Array.Empty<string>(), AppLog.For("Test"));
            var result = importer.Import(json, signatureJson: null, existingIds: Array.Empty<string>());
            Assert.True(result.Succeeded, result.Message);
        }
        finally
        {
            Cleanup(userData);
        }
    }
}

