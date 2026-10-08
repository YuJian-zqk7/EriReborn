using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The Plugins page must do more than display a parse result: an imported
/// resource has to reach the software engine, or the import is decoration
/// (spec 177/178).
/// </summary>
public sealed class PluginsPageTests
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
            new CloudProviderRegistry(
                new ICloudProvider[]
                {
                    new StubProvider("123"), new StubProvider("baidu"), new StubProvider("quark"),
                    new StubProvider("lanzou"), new StubProvider("xunlei"),
                },
                AppLog.For("Test")),
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

    private static string PluginJson(string resourceId, string category = "Utility", string? provider = "123")
        => $$"""
        {
          "schema": 1,
          "id": "market_plugin",
          "name": "Test Plugin",
          "version": "1.0.0",
          "resources": [
            {
              "id": "{{resourceId}}",
              "name": "{{resourceId}}",
              "categoryId": "{{category}}",
              "directory": "{{resourceId}}",
              "version": "1.0.0",
              "sources": [ { "kind": "CloudShare", "provider": "{{provider}}", "shareUrl": "https://example.invalid/s/1" } ]
            }
          ]
        }
        """;

    private static async Task<string> WritePluginAsync(string userData, string json)
    {
        var path = Path.Combine(userData, "plugins", "resources.eriplugin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task The_page_lists_every_provider_without_preferring_one()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new PluginsViewModel(host);

            // Order comes from the registry, and no provider is singled out.
            Assert.Equal(5, page.Providers.Count);
            Assert.Contains(page.Providers, x => x.Id == "quark");
            Assert.Contains(page.Providers, x => x.Id == "xunlei");
            Assert.Contains(page.Providers, x => x.Id == "123");
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Importing_a_plugin_registers_its_resources_on_the_host()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new PluginsViewModel(host);
            page.PluginPath = await WritePluginAsync(data, PluginJson("sample_tool"));

            await page.ImportCommand.ExecuteAsync(null);

            Assert.Single(page.Resources);
            Assert.True(host.PluginResources.Contains("sample_tool"));

            // The point of the whole feature: it reaches the software list.
            Assert.Contains(host.AllSoftware(), item => item.Id == "sample_tool");
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_imported_resource_appears_on_the_software_page()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new PluginsViewModel(host);
            page.PluginPath = await WritePluginAsync(data, PluginJson("visible_tool"));
            await page.ImportCommand.ExecuteAsync(null);

            // Built after the import, the way navigating to the page would.
            var software = new SoftwareViewModel(host);

            Assert.Contains(software.Items, item => item.Id == "visible_tool");
            Assert.Contains("Utility", software.Categories);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_software_page_that_already_exists_picks_up_a_later_import()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // Constructed first, the way the app builds its pages at startup.
            var software = new SoftwareViewModel(host);
            Assert.DoesNotContain(software.Items, item => item.Id == "late_tool");

            var page = new PluginsViewModel(host);
            page.PluginPath = await WritePluginAsync(data, PluginJson("late_tool"));
            await page.ImportCommand.ExecuteAsync(null);

            // The live app showed a stale list here; the page has to reload itself.
            Assert.Contains(software.Items, item => item.Id == "late_tool");
        }
        finally
        {
            Cleanup(data);
        }
    }

    /// <summary>
    /// A server whose index names its own plugin address, which is only known
    /// once the listener is up.
    /// </summary>
    private static TestHttpServer MarketplaceServer(string pluginJson, string? sha)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(pluginJson);
        TestHttpServer? server = null;

        server = new TestHttpServer(request => request.Path switch
        {
            "/index.json" => new TestResponse(
                200,
                System.Text.Encoding.UTF8.GetBytes($$"""
                {
                  "schema": 1,
                  "plugins": [
                    {
                      "id": "market_plugin",
                      "name": "Market Plugin",
                      "version": "2.0.0",
                      "resourceCount": 1,
                      "providers": ["123", "xunlei"],
                      "downloadUrl": "{{server!.Url("/plugin.json")}}",
                      "sha256": "{{sha}}"
                    }
                  ]
                }
                """)),
            "/plugin.json" => new TestResponse(200, bytes),
            _ => TestResponse.NotFound(),
        });

        return server;
    }

    [Fact]
    public async Task The_marketplace_lists_plugins_and_installs_one_through_the_page()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(PluginJson("from_market_page"));
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            using var server = MarketplaceServer(PluginJson("from_market_page"), sha);

            var page = new PluginsViewModel(host);
            page.MarketplaceIndexUrl = server.Url("/index.json");
            await page.RefreshMarketplaceCommand.ExecuteAsync(null);

            var entry = Assert.Single(page.MarketplaceEntries);
            Assert.Equal("Market Plugin", entry.Name);
            Assert.Equal("1 个资源", entry.ResourceText);

            await page.InstallMarketplacePluginCommand.ExecuteAsync(entry);

            // The page installs for real: the resource reaches the software list.
            Assert.True(host.PluginResources.Contains("from_market_page"));
            Assert.Contains(host.AllSoftware(), item => item.Id == "from_market_page");
            Assert.Contains("新增", page.MarketplaceStatus);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_page_recognises_an_installed_plugin_and_updates_it()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // One server whose published version and payload can move forward.
            var version = "1.0.0";
            var resourceId = "page_update_v1";
            TestHttpServer? server = null;

            server = new TestHttpServer(request =>
            {
                var plugin = PluginJson(resourceId);
                var bytes = System.Text.Encoding.UTF8.GetBytes(plugin);
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

                return request.Path switch
                {
                    "/index.json" => new TestResponse(200, System.Text.Encoding.UTF8.GetBytes($$"""
                    {
                      "schema": 1,
                      "plugins": [
                        {
                          "id": "market_plugin",
                          "name": "Market Plugin",
                          "version": "{{version}}",
                          "resourceCount": 1,
                          "downloadUrl": "{{server!.Url("/plugin.json")}}",
                          "sha256": "{{sha}}"
                        }
                      ]
                    }
                    """)),
                    "/plugin.json" => new TestResponse(200, bytes),
                    _ => TestResponse.NotFound(),
                };
            });

            using (server)
            {
                var page = new PluginsViewModel(host);
                page.MarketplaceIndexUrl = server.Url("/index.json");

                await page.RefreshMarketplaceCommand.ExecuteAsync(null);
                var first = Assert.Single(page.MarketplaceEntries);
                Assert.Equal("未安装", first.InstalledText);
                Assert.Equal("安装", first.ActionText);

                await page.InstallMarketplacePluginCommand.ExecuteAsync(first);
                Assert.True(host.PluginResources.Contains("page_update_v1"));

                // The page now says it is installed, at the version it installed.
                await page.RefreshMarketplaceCommand.ExecuteAsync(null);
                var installed = Assert.Single(page.MarketplaceEntries);
                Assert.Equal("已安装 1.0.0", installed.InstalledText);
                Assert.True(installed.IsInstalled);
                Assert.False(installed.CanUpdate);

                // A newer version is published.
                version = "2.0.0";
                resourceId = "page_update_v2";

                await page.RefreshMarketplaceCommand.ExecuteAsync(null);
                var outdated = Assert.Single(page.MarketplaceEntries);
                Assert.True(outdated.CanUpdate);
                Assert.Contains("可更新到 2.0.0", outdated.InstalledText);
                Assert.Equal("更新", outdated.ActionText);

                await page.InstallMarketplacePluginCommand.ExecuteAsync(outdated);

                // The update replaced the old resource rather than adding beside it.
                Assert.True(host.PluginResources.Contains("page_update_v2"));
                Assert.False(host.PluginResources.Contains("page_update_v1"));
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_marketplace_plugin_with_a_wrong_hash_is_refused_and_reports_why()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            using var server = MarketplaceServer(PluginJson("never_lands"), new string('0', 64));

            var page = new PluginsViewModel(host);
            page.MarketplaceIndexUrl = server.Url("/index.json");
            await page.RefreshMarketplaceCommand.ExecuteAsync(null);

            await page.InstallMarketplacePluginCommand.ExecuteAsync(Assert.Single(page.MarketplaceEntries));

            Assert.False(host.PluginResources.Contains("never_lands"));
            Assert.Contains("安装失败", page.MarketplaceStatus);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_id_that_already_exists_is_refused_and_not_registered()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // Take a real catalog id, so the conflict is genuine.
            var existingId = host.Catalog.Software[0].Id;

            var page = new PluginsViewModel(host);
            page.PluginPath = await WritePluginAsync(data, PluginJson(existingId));

            await page.ImportCommand.ExecuteAsync(null);

            Assert.Empty(page.Resources);
            Assert.False(host.PluginResources.Contains(existingId));
            Assert.Contains("被拒绝", page.Status);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Importing_the_same_plugin_twice_does_not_duplicate_it()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var path = await WritePluginAsync(data, PluginJson("once_only"));

            var page = new PluginsViewModel(host);
            page.PluginPath = path;
            await page.ImportCommand.ExecuteAsync(null);
            await page.ImportCommand.ExecuteAsync(null);

            // Scoped to this plugin's own resource. A host also carries the resources of every plugin
            // the user left enabled, so "the registry holds exactly one item" is not what this test
            // is about.
            Assert.Single(host.PluginResources.Imported, item => item.Id == "once_only");
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Switching_a_plugin_off_and_back_on_does_not_grow_the_resource_list()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new PluginsViewModel(host);
            page.PluginPath = await WritePluginAsync(data, PluginJson("toggled_tool"));
            await page.ImportCommand.ExecuteAsync(null);
            Assert.Single(page.Resources);

            // Two whole switches. Enabling imports the file again, and an import appends one row per
            // resource — without the clear this page went 1 → 2 → 3 → 4 while the plugin itself never
            // changed. On the official plugin the same thing turned 263 rows into 526, then 789, and
            // the page became a wall the user had to scroll past to reach anything below it.
            for (var round = 0; round < 2; round++)
            {
                // A toggle rebuilds the list, so each step re-reads its row instead of holding one:
                // the row it held would still say "enabled" and the next press would disable again.
                var on = Assert.Single(page.Installed, item => item.Id == "market_plugin");
                Assert.True(on.IsEnabled);
                await page.TogglePluginCommand.ExecuteAsync(on);

                var off = Assert.Single(page.Installed, item => item.Id == "market_plugin");
                Assert.False(off.IsEnabled);
                await page.TogglePluginCommand.ExecuteAsync(off);

                // Back on: the resource is registered again and listed exactly once.
                Assert.True(Assert.Single(page.Installed, item => item.Id == "market_plugin").IsEnabled);
                Assert.True(host.PluginResources.Contains("toggled_tool"));
                Assert.Single(page.Resources);
            }
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_missing_file_is_reported_rather_than_thrown()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new PluginsViewModel(host);
            page.PluginPath = Path.Combine(data, "plugins", "does-not-exist.eriplugin.json");

            await page.ImportCommand.ExecuteAsync(null);

            Assert.Contains("找不到插件文件", page.Status);
            Assert.Empty(page.Resources);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_signature_that_does_not_verify_stops_the_import_at_that_stage()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var path = await WritePluginAsync(data, PluginJson("signed_tool"));
            await File.WriteAllTextAsync(
                path + ".eriplugin.sig",
                """{ "keyId": "attacker", "signature": "AAAA" }""");

            var page = new PluginsViewModel(host);
            page.PluginPath = path;
            page.SignaturePath = path + ".eriplugin.sig";

            await page.ImportCommand.ExecuteAsync(null);

            Assert.Equal("签名校验", page.StageText);
            Assert.Empty(page.Resources);
            Assert.False(host.PluginResources.Contains("signed_tool"));
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_unsigned_import_says_it_carries_no_authority()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var page = new PluginsViewModel(host);
            page.PluginPath = await WritePluginAsync(data, PluginJson("unsigned_tool"));

            await page.ImportCommand.ExecuteAsync(null);

            Assert.Contains("未签名", page.TrustText);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Importing_the_folder_imports_every_plugin_file_it_holds()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // Two files dropped into the folder. The page imports both without the user
            // ever typing a path — that is what the folder-as-entry-point design buys.
            // The official plugin is seeded here on a fresh install, so the folder starts
            // non-empty; this test is about what the user put in.
            var folder = Path.Combine(data, "plugins");
            Directory.CreateDirectory(folder);
            foreach (var seeded in Directory.GetFiles(folder, "*.eriplugin.json"))
            {
                File.Delete(seeded);
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "one.eriplugin.json"), PluginJson("folder_one"));
            await File.WriteAllTextAsync(Path.Combine(folder, "two.eriplugin.json"), PluginJson("folder_two"));

            var page = new PluginsViewModel(host);
            await page.ImportFromFolderCommand.ExecuteAsync(null);

            Assert.Contains(page.Resources, resource => resource.Id == "folder_one");
            Assert.Contains(page.Resources, resource => resource.Id == "folder_two");
            Assert.True(host.PluginResources.Contains("folder_one"));
            Assert.True(host.PluginResources.Contains("folder_two"));
            Assert.Contains("2 个插件文件", page.Status);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task An_empty_plugin_folder_says_so_instead_of_doing_nothing()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // The official plugin is seeded on a fresh install, so an empty folder has to be
            // arranged rather than assumed.
            var folder = Path.Combine(data, "plugins");
            if (Directory.Exists(folder))
            {
                foreach (var seeded in Directory.GetFiles(folder, "*.eriplugin.json"))
                {
                    File.Delete(seeded);
                }
            }

            var page = new PluginsViewModel(host);

            await page.ImportFromFolderCommand.ExecuteAsync(null);

            Assert.Empty(page.Resources);
            Assert.Contains("还没有", page.Status);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public void The_registry_reports_how_many_resources_were_actually_new()
    {
        var registry = new EriReborn.Engine.Plugins.PluginRegistry();

        var first = new EriReborn.Core.Domain.SoftwareDefinition { Id = "a", Name = "A", CategoryId = "Utility", DirectoryName = "A" };
        var second = new EriReborn.Core.Domain.SoftwareDefinition { Id = "b", Name = "B", CategoryId = "Utility", DirectoryName = "B" };

        Assert.Equal(2, registry.Add(new[] { first, second }));
        Assert.Equal(0, registry.Add(new[] { first, second }));
        Assert.Equal(2, registry.Imported.Count);
    }

    /// <summary>A provider that only reports its identity, for peer-listing tests.</summary>
    private sealed class StubProvider(string id) : ICloudProvider
    {
        public string Id { get; } = id;

        public string DisplayName => Id;

        public bool RequiresAuthentication => false;

        public CloudImplementationKind ImplementationKind => CloudImplementationKind.NotImplemented;

        public string? LimitationNote => "测试替身";

        public string? DocumentationUrl => null;

        public CloudAuthState GetAuthState(CloudCredential? credential) => CloudAuthState.Unsupported;

        public Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "测试替身"));

        public Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "测试替身"));

        public Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.Unsupported, "测试替身"));
    }
}
