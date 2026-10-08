using System.Text.Json;
using EriReborn.Cloud;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A share link names a tree, so a resource has to say which item inside it is meant. These cover
/// the two halves of that: a source written before locators existed still resolves, and a locator
/// that carries the platform's own id is really used rather than quietly ignored.
/// </summary>
public sealed class ResourceLocatorCompatibilityTests
{
    private static JsonElement Json(string text)
    {
        var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------ the legacy shape

    [Fact]
    public void A_legacy_file_name_becomes_a_locator_on_import()
    {
        var json = """
        {
          "items": [
            {
              "id": "legacy-x",
              "name": "Legacy X",
              "directory": "LegacyX",
              "sources": [
                { "kind": "pan123", "shareUrl": "https://pan.example.invalid/s/abc", "fileName": "LegacyX.zip" }
              ]
            }
          ]
        }
        """;

        var result = LegacyCatalogImporter.Import(json, "legacy");

        var source = Assert.Single(Assert.Single(result.Software).Sources);
        Assert.Equal(SourceKind.CloudShare, source.Kind);
        Assert.Equal("123", source.ProviderId);

        // The old file name is expressed as the location the new model speaks.
        Assert.NotNull(source.Locator);
        Assert.Equal("LegacyX.zip", source.Locator!.Name);
        Assert.Equal("LegacyX.zip", source.FileName);
    }

    [Fact]
    public void A_catalog_source_may_declare_a_folder_locator()
    {
        var locator = ResourceLocatorParser.Parse(Json(
            """{ "locator": { "type": "folder", "path": "A/DevelopmentPack", "providerItemId": "42" } }"""));

        Assert.NotNull(locator);
        Assert.Equal(ResourceLocatorKind.Folder, locator!.Kind);
        Assert.Equal("A/DevelopmentPack", locator.Path);
        Assert.Equal("42", locator.ProviderItemId);
        Assert.True(locator.NeedsFolderWalk);
        Assert.Equal("DevelopmentPack", locator.ItemName);
    }

    [Fact]
    public void An_empty_locator_object_is_the_same_as_none()
    {
        Assert.Null(ResourceLocatorParser.Parse(Json("""{ "locator": { } }""")));
        Assert.Null(ResourceLocatorParser.Parse(Json("""{ "fileName": "x.zip" }""")));
    }

    [Fact]
    public void The_legacy_helper_refuses_a_blank_name()
    {
        Assert.Null(ResourceLocatorParser.FromLegacyFileName(null));
        Assert.Null(ResourceLocatorParser.FromLegacyFileName("   "));
        Assert.Equal("x.zip", ResourceLocatorParser.FromLegacyFileName("x.zip")!.Name);
    }

    // ------------------------------------------------------------- the ambiguity

    [Fact]
    public void Same_named_entries_are_counted_so_the_reader_can_tell_them_apart()
    {
        var entries = new[]
        {
            new CloudFile("1", "Tool.zip", false, 10),
            new CloudFile("2", "Tool.zip", false, 20),
            new CloudFile("3", "Other.zip", false, 30),
        };

        Assert.Equal(2, ResourceLocatorMatcher.CountByName("Tool.zip", entries));
        Assert.Equal(0, ResourceLocatorMatcher.CountByName("Missing.zip", entries));
        Assert.Null(ResourceLocatorMatcher.FindByName("Tool.zip", entries));
        Assert.Equal("3", ResourceLocatorMatcher.FindByName("Other.zip", entries)!.Id);
    }

    // ------------------------------------------------- how the resolver picks a path

    /// <summary>
    /// Answers the share root, and gives each way of being asked a different URL, so a test can see
    /// which route the resolver actually took instead of only that it resolved something.
    /// </summary>
    private sealed class TreeProvider : ICloudProvider
    {
        public string Id => "stub";

        public string DisplayName => "测试平台";

        public bool RequiresAuthentication => false;

        public CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public string? LimitationNote => null;

        public string? DocumentationUrl => null;

        public CloudAuthState GetAuthState(CloudCredential? credential) => CloudAuthState.NotRequired;

        public Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Ok(
                new CloudDownloadHandle(new CloudFile("by-name", fileName ?? "x", false), "https://cdn/by-name")));

        public Task<CloudListResult> ListShareAsync(string shareUrl, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(new[] { new CloudFile("item-1", "X.zip", false, 10) }));

        public Task<CloudResolveResult> ResolveShareItemAsync(string shareUrl, CloudFile item, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Ok(new CloudDownloadHandle(item, "https://cdn/by-item")));
    }

    private static CloudShareResolver Resolver()
        => new(new CloudProviderRegistry(new ICloudProvider[] { new TreeProvider() }, AppLog.For("Test")), AppLog.For("Test"));

    [Fact]
    public async Task A_locator_that_only_carries_the_platform_id_is_used()
    {
        var source = new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = "stub",
            ShareUrl = "https://share.example.invalid/s/abc",
            Locator = new ResourceLocator { ProviderItemId = "item-1" },
        };

        var outcome = await Resolver().ResolveAsync(source);

        Assert.True(outcome.IsResolved, outcome.Message);

        // The id was honoured: the item route ran, not the by-name one. Before this, an id-only
        // locator fell through to the file-name lookup and the id was ignored.
        Assert.Equal("https://cdn/by-item", outcome.Route!.Url);
    }

    [Fact]
    public async Task A_locator_that_only_names_a_root_item_keeps_the_short_path()
    {
        var source = new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = "stub",
            ShareUrl = "https://share.example.invalid/s/abc",
            FileName = "X.zip",
            Locator = new ResourceLocator { Name = "X.zip" },
        };

        var outcome = await Resolver().ResolveAsync(source);

        Assert.True(outcome.IsResolved, outcome.Message);
        Assert.Equal("https://cdn/by-name", outcome.Route!.Url);
    }

    // ------------------------------------------------- whole-folder package (spec v3.1)

    /// <summary>
    /// A/ 下放一个 Client 文件夹。两个桩只在「能不能整夹打包」上不同：一个实现了打包方法并返回
    /// zip 直链，另一个干脆不实现，吃契约层的默认诚实拒绝。
    /// </summary>
    private class FolderPackageProvider(string id, bool packaging) : ICloudProvider
    {
        public string Id => id;

        public string DisplayName => "整夹" + id;

        public bool RequiresAuthentication => false;

        public CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public string? LimitationNote => null;

        public string? DocumentationUrl => null;

        public bool SupportsFolderPackage => packaging;

        public CloudAuthState GetAuthState(CloudCredential? credential) => CloudAuthState.NotRequired;

        public Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.NotFound, "文件测试不会走按名解析。"));

        public Task<CloudListResult> ListShareAsync(string shareUrl, CloudCredential? credential, CancellationToken cancellationToken = default)
            => ListShareFolderAsync(shareUrl, null, credential, cancellationToken);

        public Task<CloudListResult> ListShareFolderAsync(
            string shareUrl,
            string? parentItemId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(parentItemId switch
            {
                null => new[] { new CloudFile("folder-A", "A", true) },
                "folder-A" => new[]
                {
                    new CloudFile("folder-client", "Client", true, 4096),
                    new CloudFile("file-note", "Note.txt", false, 8),
                },
                _ => Array.Empty<CloudFile>(),
            }));

        public Task<CloudResolveResult> ResolveShareItemAsync(string shareUrl, CloudFile item, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Ok(new CloudDownloadHandle(item, "https://cdn/by-item")));

        // 跟 CloudProviderBase 一样声明为 virtual：子类用 override 表达「这个平台不打包」，
        // 接口分发才会真的走到子类实现（new 隐藏不会改变接口映射）。
        public virtual Task<CloudResolveResult> ResolveShareFolderPackageAsync(
            string shareUrl,
            CloudFile folder,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Ok(
                new CloudDownloadHandle(folder, "https://cdn/client-zip", Note: "文件夹打包直链（测试平台）。")));
    }

    private static CloudShareResolver ResolverFor(ICloudProvider provider)
        => new(new CloudProviderRegistry(new[] { provider }, AppLog.For("Test")), AppLog.For("Test"));

    private static SoftwareSource FolderSource(string providerId)
        => new()
        {
            Kind = SourceKind.CloudShare,
            ProviderId = providerId,
            ShareUrl = "https://share.example.invalid/s/abc-1234",
            FileName = "Client",
            Locator = new ResourceLocator
            {
                Kind = ResourceLocatorKind.Folder,
                Path = "A/Client",
                ProviderItemId = "folder-client",
            },
        };

    [Fact]
    public async Task A_folder_locator_on_a_packaging_provider_becomes_one_named_zip_route()
    {
        var resolver = ResolverFor(new FolderPackageProvider("pkg", packaging: true));

        var outcome = await resolver.ResolveAsync(FolderSource("pkg"));

        Assert.True(outcome.IsResolved, outcome.Message);

        var route = outcome.Route!;
        Assert.Equal("https://cdn/client-zip", route.Url);

        // 落盘产物是「文件夹名.zip」，而不是无名文件夹或被展开成多个文件任务。
        Assert.Equal("Client.zip", route.FileName);

        // 路由说明必须点明这是整文件夹打包 zip。
        Assert.Contains("整文件夹打包 zip", route.Note);
    }

    [Fact]
    public async Task A_folder_locator_on_a_peer_without_packaging_is_an_actionable_failure()
    {
        // 这个桩挂了「不打包」的能力位，但通过单独的子类保留默认拒绝方法，用来验证两者一致。
        var resolver = ResolverFor(new NoPackageStub());

        var outcome = await resolver.ResolveAsync(FolderSource("nopkg"));

        Assert.False(outcome.IsResolved);

        // 不是「资源不存在」，也不是可重试的平台抖动：是这个平台明确不具备能力。
        Assert.Equal(ResolveStatus.Failed, outcome.Status);
        Assert.Equal(CloudErrorKind.Unsupported, outcome.Error);

        // 文案必须可行动：告诉作者去展开选里面的文件。
        Assert.Contains("打包下载", outcome.Message);
        Assert.Contains("展开", outcome.Message);
    }

    /// <summary>跟真实平台一样：能力位关闭，整夹方法给出契约口径的诚实拒绝。</summary>
    private sealed class NoPackageStub : FolderPackageProvider
    {
        public NoPackageStub()
            : base("nopkg", packaging: false)
        {
        }

        public override Task<CloudResolveResult> ResolveShareFolderPackageAsync(
            string shareUrl,
            CloudFile folder,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Fail(
                CloudErrorKind.Unsupported,
                "该平台不支持整个文件夹打包下载，请在插件中展开选择其中的文件。"));
    }
}
