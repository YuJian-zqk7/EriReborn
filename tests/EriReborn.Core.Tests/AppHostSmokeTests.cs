using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// End-to-end composition smoke test: it builds the real object graph against
/// the shipped catalog and assets, then drives navigation the way the shell
/// does. This is the automated half of the startup verification (spec 72/73).
/// </summary>
public sealed class AppHostSmokeTests
{
    private static async Task<(AppHost Host, string UserData)> CreateHostAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var network = new TestNetworkService(new HttpClient());
        var credentials = new InMemoryCredentialStore();
        var files = new TestFileSystemService(userData);
        var platform = new TestPlatform(files, network, credentials);

        ICloudProvider[] providers =
        {
            new Provider123(network, credentials),
            new ProviderBaidu(network, credentials),
            new ProviderQuark(network, credentials),
            new ProviderLanzou(network, credentials),
            new ProviderXunlei(network, credentials),
        };

        var registry = new CloudProviderRegistry(providers, AppLog.For("Test"));
        var host = await AppHost.CreateAsync(paths, platform, registry, AppLog.For("Test"));
        return (host, userData);
    }

    [Fact]
    public async Task Shipped_catalog_loads_completely_with_zero_rejections()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            // Not a frozen number: the catalog grows, and the point of this test
            // is that whatever ships loads completely and rejects nothing.
            Assert.True(host.Catalog.Software.Count >= 251, $"loaded {host.Catalog.Software.Count} items");
            Assert.True(
                host.Catalog.Report.ItemsRejected == 0,
                "rejections: " + string.Join(" | ", host.Catalog.Report.Rejected));
            Assert.True(host.Catalog.UsedCategoryIds.Count() >= 10);

            // Every shipped entry must satisfy the official naming rule.
            foreach (var software in host.Catalog.Software)
            {
                Assert.True(
                    EriReborn.Core.Validation.DirectoryNameValidator.ValidateName(software.DirectoryName).IsValid,
                    $"{software.Id} has an invalid directory name '{software.DirectoryName}'");
            }
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task All_six_official_skins_are_discovered()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            var ids = host.Skins.Available.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(6, ids.Count);
            foreach (var expected in new[] { "eri_windows", "eri_android", "tech_windows", "tech_android", "win11", "android_style" })
            {
                Assert.Contains(expected, ids);
            }

            // The product default skin is applied during startup.
            Assert.Equal("eri_windows", host.Skins.Active?.Id);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task All_five_cloud_platforms_are_registered_as_equals()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            var ordered = host.CloudProviders.InDisplayOrder();
            Assert.Equal(5, ordered.Count);
            Assert.Equal(new[] { "123", "baidu", "quark", "lanzou", "xunlei" }, ordered.Select(p => p.Id).ToArray());

            // Every provider is a peer implementation of the same contract.
            foreach (var provider in ordered)
            {
                Assert.IsAssignableFrom<ICloudProvider>(provider);
            }

            // None of them claims to be authenticated without a credential.
            foreach (var provider in ordered.Where(p => p.RequiresAuthentication))
            {
                Assert.Equal(CloudAuthState.AuthRequired, provider.GetAuthState(null));
            }
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Asset_manifest_registers_every_sheet()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            // The count the manifest declares, not a literal: adding art must not
            // require editing this assertion.
            var declared = System.Text.Json.JsonDocument
                .Parse(File.ReadAllText(Path.Combine(host.Paths.AssetsRoot, "asset_manifest.json")))
                .RootElement.GetProperty("sheets")
                .GetArrayLength();

            Assert.Equal(declared, host.Assets.Sheets.Count);
            Assert.Contains(host.Assets.Sheets, s => s.Skin == "eri_windows");
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Navigation_resolves_every_first_class_page()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            var navigation = new NavigationService();
            var main = new MainViewModel(host, navigation);

            navigation.Navigate("software");
            Assert.IsType<SoftwareViewModel>(main.CurrentPage);

            navigation.Navigate("environment");
            Assert.IsType<EnvironmentViewModel>(main.CurrentPage);

            navigation.Navigate("cloud");
            Assert.IsType<CloudViewModel>(main.CurrentPage);

            navigation.Navigate("ai");
            Assert.IsType<AiViewModel>(main.CurrentPage);

            navigation.Navigate("extensions");
            Assert.IsType<ExtensionViewModel>(main.CurrentPage);

            navigation.Navigate("workshop");
            Assert.IsType<WorkshopViewModel>(main.CurrentPage);

            navigation.Navigate("settings");
            Assert.IsType<SettingsViewModel>(main.CurrentPage);

            navigation.Navigate("update");
            Assert.IsType<UpdateViewModel>(main.CurrentPage);

            navigation.Navigate("blog");
            Assert.IsType<BlogViewModel>(main.CurrentPage);

            navigation.Navigate("home");
            Assert.IsType<HomeViewModel>(main.CurrentPage);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Software_page_lists_plugin_resources_and_filters_by_category()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            var navigation = new NavigationService();
            var main = new MainViewModel(host, navigation);

            // 列表只显示插件资源（官方预置目录不进列表）。注入放在页面建好之后，
            // 走 PluginResources.Changed → ReloadSoftware 的真实导入路径。
            host.PluginResources.Add(new[]
            {
                PluginResource("sm-a", "甲工具", "Utility"),
                PluginResource("sm-b", "乙工具", "Utility"),
                PluginResource("sm-c", "系统组件", "System"),
            });

            Assert.Equal(3, main.Software.Items.Count);

            // Derived from the host rather than pinned to a literal: the page filters the
            // injected plugin entries by the category they declare.
            var expectedSystem = host.AllSoftware().Count(item => item.CategoryId == "System" && item.IsPluginProvided);

            navigation.Navigate("software:System");
            Assert.Equal("System", main.Software.SelectedCategory);
            Assert.Equal(expectedSystem, main.Software.Items.Count);
            Assert.True(main.Software.Items.All(i => i.CategoryId == "System"));

            // Searching is scoped by the selected category, so widen it first.
            main.Software.SelectedCategory = "All";
            main.Software.SearchText = "乙工具";
            Assert.Contains(main.Software.Items, i => i.Name.Contains("乙工具", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Cloud_page_reports_real_authentication_state_for_all_platforms()
    {
        var (host, userData) = await CreateHostAsync();
        try
        {
            var main = new MainViewModel(host, new NavigationService());
            await main.Cloud.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(5, main.Cloud.Providers.Count);
            Assert.Contains(main.Cloud.Providers, p => p.AuthStateText == "需要登录");
        }
        finally
        {
            Cleanup(userData);
        }
    }

    /// <summary>软件页列表只显示插件资源；造一条注入宿主的插件条目。</summary>
    private static SoftwareDefinition PluginResource(string id, string name, string categoryId) => new()
    {
        Id = id,
        Name = name,
        CategoryId = categoryId,
        DirectoryName = id,
        CatalogId = "smoke-test",
        IsPluginProvided = true,
        Sources = new[]
        {
            new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/" + id + ".exe" },
        },
    };

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
            // Best effort cleanup.
        }
    }
}
