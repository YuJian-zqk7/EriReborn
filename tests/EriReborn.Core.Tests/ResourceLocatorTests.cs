using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Where a resource sits inside a share (spec 29/30).
///
/// <para>
/// A share link names a tree, not an item, and several resources legitimately name the same link. What
/// makes them different resources is the locator, so the tests here are about one claim: the same link
/// plus two locators reaches two different items.
/// </para>
/// </summary>
public sealed class ResourceLocatorTests
{
    // ------------------------------------------------------------------ the model

    [Fact]
    public void A_path_splits_into_the_folders_walking_there_and_the_item()
    {
        var locator = new ResourceLocator { Path = "A/a1/b1/ResourceA.zip" };

        Assert.Equal(new[] { "A", "a1", "b1" }, locator.FolderSegments);
        Assert.Equal("ResourceA.zip", locator.ItemName);
        Assert.True(locator.NeedsFolderWalk);
    }

    [Fact]
    public void Backslashes_are_the_same_path()
    {
        // A path typed on Windows arrives with backslashes; treating it as one name would send the
        // walk looking for a folder called "A\a1\b1".
        var locator = new ResourceLocator { Path = @"A\a1\b1\ResourceA.zip" };

        Assert.Equal(new[] { "A", "a1", "b1" }, locator.FolderSegments);
        Assert.Equal("ResourceA.zip", locator.ItemName);
    }

    [Fact]
    public void A_declared_name_wins_over_the_last_path_step()
    {
        // The name is what the share calls the item now; the path is where it used to be.
        var locator = new ResourceLocator { Path = "A/old-name.zip", Name = "ResourceA.zip" };

        Assert.Equal("ResourceA.zip", locator.ItemName);
    }

    [Fact]
    public void A_folder_locator_names_the_folder_itself()
    {
        var locator = new ResourceLocator { Kind = ResourceLocatorKind.Folder, Path = "A/Development" };

        Assert.Equal(new[] { "A" }, locator.FolderSegments);
        Assert.Equal("Development", locator.ItemName);
    }

    [Fact]
    public void A_locator_with_no_folders_is_answered_by_the_share_root_alone()
    {
        var locator = new ResourceLocator { Name = "Java 8" };

        Assert.Empty(locator.FolderSegments);
        Assert.False(locator.NeedsFolderWalk);
        Assert.Equal("Java 8", locator.ItemName);
    }

    [Fact]
    public void A_locator_that_says_nothing_is_empty()
    {
        Assert.True(new ResourceLocator().IsEmpty);
        Assert.True(new ResourceLocator { Path = "  " }.IsEmpty);
        Assert.False(new ResourceLocator { ProviderItemId = "123456" }.IsEmpty);
    }

    // ------------------------------------------------------------------ matching one folder

    [Fact]
    public void The_platform_id_wins_over_the_name()
    {
        var entries = new[]
        {
            new CloudFile("1", "other.zip", false),
            new CloudFile("123456", "renamed.zip", false),
        };

        var found = ResourceLocatorMatcher.Find(
            new ResourceLocator { Name = "ResourceA.zip", ProviderItemId = "123456" },
            entries);

        // The item was renamed in the share; the id still says which one it is.
        Assert.Equal("renamed.zip", found?.Name);
    }

    [Fact]
    public void Two_entries_with_one_name_are_not_guessed_between()
    {
        var entries = new[]
        {
            new CloudFile("1", "ResourceA.zip", false),
            new CloudFile("2", "resourcea.ZIP", false),
        };

        // Picking either would download the wrong file, so the answer is "cannot tell".
        Assert.Null(ResourceLocatorMatcher.FindByName("ResourceA.zip", entries));
    }

    [Fact]
    public void An_id_only_locator_is_not_answered_by_a_name()
    {
        var entries = new[] { new CloudFile("1", "ResourceA.zip", false) };

        Assert.Null(ResourceLocatorMatcher.Find(new ResourceLocator { ProviderItemId = "999" }, entries));
    }

    // ------------------------------------------------------------------ walking a share

    [Fact]
    public async Task Two_locators_over_one_share_url_reach_two_different_items()
    {
        var provider = ProviderWithTree();

        var walkA = await ShareTreeWalker.WalkAsync(
            provider,
            "https://example.invalid/s/abc",
            new ResourceLocator { Path = "A/a1/b1/ResourceA.zip" },
            credential: null);

        var walkB = await ShareTreeWalker.WalkAsync(
            provider,
            "https://example.invalid/s/abc",
            new ResourceLocator { Path = "A/a1/b1/ResourceB.zip" },
            credential: null);

        Assert.True(walkA.Success, walkA.Message);
        Assert.True(walkB.Success, walkB.Message);

        // Same link, different items: the locator is what tells them apart.
        Assert.Equal("ResourceA.zip", walkA.Item?.Name);
        Assert.Equal("ResourceB.zip", walkB.Item?.Name);
        Assert.Equal("A/a1/b1/ResourceA.zip", walkA.Path);
    }

    [Fact]
    public async Task A_locator_that_names_no_folders_is_answered_by_the_share_root()
    {
        var provider = ProviderWithTree();

        // Exactly what a source written before locators existed means.
        var walk = await ShareTreeWalker.WalkAsync(
            provider,
            "https://example.invalid/s/abc",
            new ResourceLocator { Name = "RootTool.zip" },
            credential: null);

        Assert.True(walk.Success, walk.Message);
        Assert.Equal("RootTool.zip", walk.Item?.Name);
    }

    [Fact]
    public async Task A_folder_that_is_not_there_is_reported_by_name()
    {
        var walk = await ShareTreeWalker.WalkAsync(
            ProviderWithTree(),
            "https://example.invalid/s/abc",
            new ResourceLocator { Path = "A/nope/b1/ResourceA.zip" },
            credential: null);

        Assert.False(walk.Success);
        Assert.Equal(CloudErrorKind.NotFound, walk.Error);
        Assert.Contains("nope", walk.Message);
    }

    [Fact]
    public async Task A_step_that_is_a_file_not_a_folder_stops_the_walk()
    {
        var walk = await ShareTreeWalker.WalkAsync(
            ProviderWithTree(),
            "https://example.invalid/s/abc",
            new ResourceLocator { Path = "RootTool.zip/deeper.zip" },
            credential: null);

        Assert.False(walk.Success);
        Assert.Contains("文件而不是文件夹", walk.Message);
    }

    [Fact]
    public async Task A_provider_that_cannot_open_share_folders_says_so()
    {
        // The honest outcome: not "nothing found", but "this platform cannot look there" — the two
        // call for different things from the user, so they must not collapse into one message.
        var walk = await ShareTreeWalker.WalkAsync(
            new RootOnlyProvider(),
            "https://example.invalid/s/abc",
            new ResourceLocator { Path = "A/a1/b1/ResourceA.zip" },
            credential: null);

        Assert.False(walk.Success);
        Assert.Equal(CloudErrorKind.Unsupported, walk.Error);
    }

    [Fact]
    public async Task A_walk_that_never_opens_a_folder_still_works_on_a_root_only_platform()
    {
        // The compat path in one test: a source without folders asks for nothing this platform
        // cannot already do, so it keeps working exactly as before.
        var walk = await ShareTreeWalker.WalkAsync(
            new RootOnlyProvider(),
            "https://example.invalid/s/abc",
            new ResourceLocator { Name = "RootTool.zip" },
            credential: null);

        Assert.True(walk.Success, walk.Message);
        Assert.Equal("RootTool.zip", walk.Item?.Name);
    }

    // ------------------------------------------------------------------ what a plugin may say

    [Fact]
    public void A_locator_survives_the_plugin_importer()
    {
        var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));

        var result = importer.Import(PluginJson(), signatureJson: null, existingIds: Array.Empty<string>());

        Assert.True(result.Succeeded, result.Message);

        var first = result.Accepted.Single(definition => definition.Id == "res_a");
        var source = first.Sources.Single();

        Assert.Equal("https://example.invalid/s/abc", source.ShareUrl);
        Assert.Equal("A/a1/b1/ResourceA.zip", source.Locator?.Path);
        Assert.Equal("123456", source.Locator?.ProviderItemId);
        Assert.Equal(ResourceLocatorKind.File, source.Locator?.Kind);

        // Two resources, one link: still two resources, because their locators differ.
        var second = result.Accepted.Single(definition => definition.Id == "res_b");
        Assert.Equal(source.ShareUrl, second.Sources.Single().ShareUrl);
        Assert.Equal("B/ResourceB.zip", second.Sources.Single().Locator?.Path);
    }

    [Fact]
    public void A_locator_written_blank_is_treated_as_absent()
    {
        var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));

        var result = importer.Import(PluginJson(blankLocator: true), signatureJson: null, existingIds: Array.Empty<string>());

        Assert.True(result.Succeeded, result.Message);

        // "Not declared" and "declared blank" are the same thing downstream, so they are made the same
        // thing here rather than left for every caller to remember.
        var source = result.Accepted.Single(definition => definition.Id == "res_a").Sources.Single();
        Assert.Null(source.Locator);
    }

    [Fact]
    public void A_source_without_a_locator_still_imports()
    {
        var importer = new PluginImportService(new[] { "123" }, AppLog.For("Test"));

        var result = importer.Import(PluginJson(withoutLocator: true), signatureJson: null, existingIds: Array.Empty<string>());

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.Accepted.Single(definition => definition.Id == "res_a").Sources.Single().Locator);
    }

    // ------------------------------------------------------------------ fixtures

    private static string PluginJson(bool blankLocator = false, bool withoutLocator = false)
    {
        var locatorA = withoutLocator
            ? string.Empty
            : blankLocator
                ? """
                  ,
                              "locator": { "type": "", "path": "", "name": "", "providerItemId": "" }
                  """
                : """
                  ,
                              "locator": {
                                  "type": "file",
                                  "path": "A/a1/b1/ResourceA.zip",
                                  "name": "ResourceA.zip",
                                  "providerItemId": "123456"
                              }
                  """;

        var locatorB = withoutLocator
            ? string.Empty
            : """
              ,
                          "locator": { "type": "file", "path": "B/ResourceB.zip" }
              """;

        return $$"""
        {
          "schema": 1,
          "id": "locator_plugin",
          "name": "带定位的插件",
          "version": "1.0.0",
          "resources": [
            {
              "id": "res_a",
              "name": "Resource A",
              "categoryId": "Utility",
              "directory": "ResA",
              "version": "1.0.0",
              "tier": "optional",
              "mode": "portable",
              "sources": [
                {
                  "kind": "CloudShare",
                  "provider": "123",
                  "shareUrl": "https://example.invalid/s/abc",
                  "fileName": "ResourceA.zip"{{locatorA}}
                }
              ]
            },
            {
              "id": "res_b",
              "name": "Resource B",
              "categoryId": "Utility",
              "directory": "ResB",
              "version": "1.0.0",
              "tier": "optional",
              "mode": "portable",
              "sources": [
                {
                  "kind": "CloudShare",
                  "provider": "123",
                  "shareUrl": "https://example.invalid/s/abc",
                  "fileName": "ResourceB.zip"{{locatorB}}
                }
              ]
            }
          ]
        }
        """;
    }

    /// <summary>
    /// A share that really is a tree: root → A → a1 → b1 → two zips, plus a file in the root.
    ///
    /// <para>
    /// A stand-in for 123, whose share endpoint takes the parent folder id. It is here so the walk can
    /// be tested without a live account; whether a given platform accepts a parent id is a separate
    /// question, answered by that provider.
    /// </para>
    /// </summary>
    private static TreeShareProvider ProviderWithTree() => new(new Dictionary<string, IReadOnlyList<CloudFile>>(StringComparer.Ordinal)
    {
        [""] = new[]
        {
            new CloudFile("id-A", "A", true),
            new CloudFile("id-B", "B", true),
            new CloudFile("id-root-tool", "RootTool.zip", false),
        },
        ["id-A"] = new[] { new CloudFile("id-a1", "a1", true) },
        ["id-a1"] = new[] { new CloudFile("id-b1", "b1", true) },
        ["id-b1"] = new[]
        {
            new CloudFile("id-res-a", "ResourceA.zip", false),
            new CloudFile("id-res-b", "ResourceB.zip", false),
        },
        ["id-B"] = new[]
        {
            new CloudFile("id-res-c", "ResourceC.zip", false),
            new CloudFile("id-res-d", "ResourceD.zip", false),
        },
    });

    /// <summary>A platform that reads the share root and nothing deeper.</summary>
    private sealed class RootOnlyProvider : CloudProviderBase
    {
        public RootOnlyProvider()
            : base(new TestNetworkService(new HttpClient()), new InMemoryCredentialStore())
        {
        }

        public override string Id => "123";

        public override string DisplayName => "只读根目录的平台";

        public override bool RequiresAuthentication => false;

        public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public override Task<CloudListResult> ListShareAsync(
            string shareUrl,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(new[]
            {
                // The folder the locator names really is in the root, so this is not "nothing found":
                // the walk gets as far as opening it, which is the step this platform cannot do.
                new CloudFile("1", "A", true),
                new CloudFile("2", "RootTool.zip", false),
            }));
    }

    private sealed class TreeShareProvider : CloudProviderBase
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<CloudFile>> _tree;

        public TreeShareProvider(IReadOnlyDictionary<string, IReadOnlyList<CloudFile>> tree)
            : base(new TestNetworkService(new HttpClient()), new InMemoryCredentialStore())
        {
            _tree = tree;
        }

        public override string Id => "123";

        public override string DisplayName => "树状分享平台";

        public override bool RequiresAuthentication => false;

        public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public override Task<CloudListResult> ListShareAsync(
            string shareUrl,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => ListShareFolderAsync(shareUrl, parentItemId: null, credential, cancellationToken);

        public override Task<CloudListResult> ListShareFolderAsync(
            string shareUrl,
            string? parentItemId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(
                _tree.TryGetValue(parentItemId ?? string.Empty, out var files)
                    ? files
                    : (IReadOnlyList<CloudFile>)Array.Empty<CloudFile>()));

        public override Task<CloudResolveResult> ResolveAsync(
            string shareUrl,
            string? fileName,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Ok(new CloudDownloadHandle(
                new CloudFile(fileName ?? "item.zip", fileName ?? "item.zip", false),
                $"https://example.invalid/download/{fileName}")));
    }
}
