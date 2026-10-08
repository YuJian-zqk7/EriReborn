using System.Net;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The page is about resources and their sources, not about five platforms side by side
/// (spec 38-41), and it reaches the platform through the source instead of guessing from
/// a domain name.
/// </summary>
public sealed class SourcesPageTests
{
    /// <summary>Answers nothing the way an unprovisioned platform does: a failure, not an exception.</summary>
    private sealed class RefuseHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty),
            });
        }
    }

    private static async Task<(AppHost Host, string UserData, HttpMessageHandler Handler)> CreateHostAsync(
        HttpMessageHandler? handler = null)
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        // Most tests want a platform that answers nothing; the share-walking ones hand in a share.
        handler ??= new RefuseHandler();
        var network = new TestNetworkService(new HttpClient(handler));
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
        return (host, userData, handler);
    }

    private static SoftwareSource Share(string providerId, string shareUrl) => new()
    {
        Kind = SourceKind.CloudShare,
        ProviderId = providerId,
        ShareUrl = shareUrl,
    };

    /// <summary>来源页只列插件资源（CloudViewModel 按 IsPluginProvided 过滤）；造一条注入宿主的插件条目。</summary>
    private static SoftwareDefinition PluginResource(string id, string name, SoftwareSource? source = null) => new()
    {
        Id = id,
        Name = name,
        CategoryId = "Utility",
        DirectoryName = id,
        CatalogId = "sources-test",
        IsPluginProvided = true,
        Sources = new[]
        {
            source ?? new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/" + id + ".exe" },
        },
    };

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail a test.
        }
    }

    [Fact]
    public async Task The_page_lists_resources_rather_than_platforms()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[]
            {
                PluginResource("sp-a", "来源甲"),
                PluginResource("sp-b", "来源乙"),
            });
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var withSources = host.AllSoftware()
                .Where(item => item.Sources.Count > 0 && item.IsPluginProvided).ToList();

            Assert.NotEmpty(view.Resources);
            Assert.Equal(withSources.Count, view.Resources.Count);
            Assert.All(view.Resources, row => Assert.True(row.SourceCount > 0));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Every_resource_row_says_where_it_can_come_from()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            Assert.All(view.Resources, row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(row.Text));
                Assert.Contains(row.Name, row.Text, StringComparison.Ordinal);
            });
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Selecting_a_resource_lists_each_of_its_sources_with_a_state_and_a_reason()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[] { PluginResource("sp-sel", "多来源资源") });
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            Assert.NotNull(view.SelectedResource);
            Assert.NotEmpty(view.Sources);
            Assert.Equal(view.SelectedResource!.SourceCount, view.Sources.Count);

            Assert.All(view.Sources, source =>
            {
                Assert.False(string.IsNullOrWhiteSpace(source.StateText));
                Assert.False(string.IsNullOrWhiteSpace(source.Reason));
                Assert.False(string.IsNullOrWhiteSpace(source.Detail));
            });
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Exactly_one_source_is_recommended_and_the_page_explains_why()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[] { PluginResource("sp-rec", "可推荐资源") });
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            Assert.Single(view.Sources, source => source.IsRecommended);
            Assert.False(string.IsNullOrWhiteSpace(view.Recommended));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_cloud_share_is_listed_as_its_own_platform_rather_than_as_a_raw_link()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            host.PluginResources.Add(new[]
            {
                PluginResource("sp-cloud", "云享资源", Share(CloudProviderIds.Pan123, "https://www.123pan.com/s/abc")),
            });
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var cloud = view.Sources.Where(source => source.IsCloud).ToList();
            if (cloud.Count == 0)
            {
                // The first resource had no share; pick one that does.
                var row = view.Resources.FirstOrDefault(resource => resource.UsesCloud);
                Assert.NotNull(row);

                view.SelectedResource = row;
                await view.RefreshCommand.ExecuteAsync(null);
                cloud = view.Sources.Where(source => source.IsCloud).ToList();
            }

            Assert.NotEmpty(cloud);
            Assert.All(cloud, source =>
            {
                Assert.Contains("的分享", source.Title, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(source.Availability.PlatformName));
            });
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Reading_a_share_calls_the_platform_that_the_source_names()
    {
        var refusals = new RefuseHandler();
        var (host, userData, _) = await CreateHostAsync(refusals);
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var source = Share(CloudProviderIds.Xunlei, "https://pan.xunlei.com/s/VTest0000Test0000Test0001?pwd=0000");
            var availability = await host.SourceAvailability.AssessAsync(source);
            var row = new SourceRow(view, availability, isRecommended: false);

            await row.ReadShareCommand.ExecuteAsync(null);

            Assert.StartsWith("迅雷云盘", view.ShareStatus, StringComparison.Ordinal);
            Assert.Empty(refusals.Requests);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Reading_a_share_never_guesses_the_platform_from_the_link()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            // The link is a 百度网盘 address, but the source says the file lives on 夸克.
            // The source decides, so 夸克 answers — including its own complaint.
            var source = Share(
                CloudProviderIds.Quark,
                "https://pan.baidu.com/s/1AbCdEfGhIj?pwd=0000");
            var availability = await host.SourceAvailability.AssessAsync(source);
            var row = new SourceRow(view, availability, isRecommended: false);

            await row.ReadShareCommand.ExecuteAsync(null);

            Assert.StartsWith("夸克网盘", view.ShareStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("百度", view.ShareStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_source_naming_a_platform_this_build_lacks_is_reported_by_name()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var source = Share("onedrive", "https://onedrive.live.test/s/abc");
            var availability = await host.SourceAvailability.AssessAsync(source);
            var row = new SourceRow(view, availability, isRecommended: false);

            await row.ReadShareCommand.ExecuteAsync(null);

            Assert.Contains("onedrive", view.ShareStatus, StringComparison.Ordinal);
            Assert.Empty(view.ShareFiles);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_source_that_is_not_a_share_offers_no_share_to_read()
    {
        var (host, userData, _) = await CreateHostAsync();
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var direct = new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.test/setup.exe" };
            var availability = await host.SourceAvailability.AssessAsync(direct);
            var row = new SourceRow(view, availability, isRecommended: false);

            Assert.False(row.CanReadShare);

            await row.ReadShareCommand.ExecuteAsync(null);
            Assert.Contains("不是网盘分享", view.ShareStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Reading_a_located_share_opens_the_folder_the_locator_names()
    {
        // An official source names one file three folders down. Opening the share root and stopping there
        // left the user to walk the tree by hand every time, even though the location was already known.
        var (host, userData, _) = await CreateHostAsync(
            new Provider123TreeTests.StubHandler(Provider123TreeTests.TreeResponses()));
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var source = new SoftwareSource
            {
                Kind = SourceKind.CloudShare,
                ProviderId = CloudProviderIds.Pan123,
                ShareUrl = Provider123TreeTests.ShareUrl,
                Locator = new ResourceLocator { Path = "A/a1/b1/ResourceA.zip" },
            };
            var availability = await host.SourceAvailability.AssessAsync(source);
            var row = new SourceRow(view, availability, isRecommended: false);

            await row.ReadShareCommand.ExecuteAsync(null);

            Assert.Equal(
                new[] { "分享根目录", "A", "a1", "b1" },
                view.Breadcrumbs.Select(crumb => crumb.Name).ToArray());

            // The folder that holds the item, listed with the item in it — not the two folders above it.
            Assert.Contains(view.ShareFiles, file => file.Text.Contains("ResourceA.zip", StringComparison.Ordinal));
            Assert.DoesNotContain(view.ShareFiles, file => file.Text.Contains("ResourceC.zip", StringComparison.Ordinal));
            Assert.Contains("ResourceA.zip", view.ShareStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_locator_pointing_at_a_folder_that_is_gone_falls_back_to_the_root_and_says_so()
    {
        // A share can be reorganised after the plugin was written. Landing on an empty page would look
        // like the app is broken; the root listing stays usable, and the reason names the step that failed
        // so "re-locate this source" is something the user can act on.
        var (host, userData, _) = await CreateHostAsync(
            new Provider123TreeTests.StubHandler(Provider123TreeTests.TreeResponses()));
        try
        {
            var view = new CloudViewModel(host);
            await view.RefreshCommand.ExecuteAsync(null);

            var source = new SoftwareSource
            {
                Kind = SourceKind.CloudShare,
                ProviderId = CloudProviderIds.Pan123,
                ShareUrl = Provider123TreeTests.ShareUrl,
                Locator = new ResourceLocator { Path = "A/nope/ResourceA.zip" },
            };
            var availability = await host.SourceAvailability.AssessAsync(source);
            var row = new SourceRow(view, availability, isRecommended: false);

            await row.ReadShareCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "分享根目录" }, view.Breadcrumbs.Select(crumb => crumb.Name).ToArray());
            Assert.Contains(view.ShareFiles, file => file.Text.Contains("A", StringComparison.Ordinal));
            Assert.Contains("nope", view.ShareStatus, StringComparison.Ordinal);
            Assert.Contains("重新定位", view.ShareStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(userData);
        }
    }
}
