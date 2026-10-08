using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A share link is not a download URL: something bridges the two. Which resolver
/// runs depends on the source's kind, never on which provider it came from
/// (spec 62/135/201).
/// </summary>
public sealed class DownloadResolverTests
{
    private static DownloadResolverRegistry Registry()
        => new(
            new IDownloadResolver[]
            {
                new DirectUrlResolver(),
                new NonDownloadableResolver(),
            },
            AppLog.For("Test"));

    private static SoftwareSource Source(SourceKind kind, string? url = null, string? share = null, string? provider = null)
        => new() { Kind = kind, Url = url, ShareUrl = share, ProviderId = provider, FileName = "tool.exe" };

    // ------------------------------------------------------------- direct URL

    [Fact]
    public async Task A_direct_url_becomes_a_route_without_any_resolving()
    {
        var outcome = await Registry().ResolveAsync(Source(SourceKind.HttpUrl, url: "https://example.invalid/tool.exe"));

        Assert.True(outcome.IsResolved);
        Assert.Equal(DownloadRouteKind.ProviderDirect, outcome.Route!.Kind);
        Assert.Equal("https://example.invalid/tool.exe", outcome.Route.Url);
        Assert.Equal("tool.exe", outcome.Route.FileName);
    }

    [Fact]
    public async Task An_official_source_is_also_a_direct_url()
    {
        var outcome = await Registry().ResolveAsync(Source(SourceKind.Official, url: "https://example.invalid/official.exe"));

        Assert.True(outcome.IsResolved);
        Assert.Equal(DownloadRouteKind.ProviderDirect, outcome.Route!.Kind);
    }

    // ----------------------------------------- sources that need no download

    [Theory]
    [InlineData(SourceKind.Winget)]
    [InlineData(SourceKind.Local)]
    [InlineData(SourceKind.Manual)]
    public async Task A_source_that_needs_no_download_is_not_reported_as_a_failure(SourceKind kind)
    {
        var outcome = await Registry().ResolveAsync(Source(kind));

        Assert.Equal(ResolveStatus.NotADownload, outcome.Status);
        Assert.False(outcome.IsResolved);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Message));

        // The distinction the status enum exists for.
        Assert.NotEqual(ResolveStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task A_kind_with_no_resolver_at_all_is_reported_as_a_gap_not_a_failure()
    {
        // CloudShare has no resolver registered in this registry.
        var outcome = await Registry().ResolveAsync(
            Source(SourceKind.CloudShare, share: "https://pan.example.invalid/s/abc", provider: "123"));

        Assert.Equal(ResolveStatus.NotADownload, outcome.Status);
        Assert.Contains("CloudShare", outcome.Message);
    }

    // ------------------------------------------------------------ the selector

    [Fact]
    public void The_resolver_is_chosen_by_source_kind()
    {
        var registry = Registry();

        Assert.Equal("direct-url", registry.ResolverFor(Source(SourceKind.HttpUrl, url: "u"))!.Id);
        Assert.Equal("not-a-download", registry.ResolverFor(Source(SourceKind.Winget))!.Id);
    }

    [Fact]
    public async Task A_direct_source_with_no_url_is_a_real_failure_not_an_unsupported_source()
    {
        var outcome = await Registry().ResolveAsync(Source(SourceKind.HttpUrl));

        // The source is understood; it is simply unusable. That is a failure.
        Assert.Equal(ResolveStatus.Failed, outcome.Status);
    }

    // ------------------------------------------------------- the cloud branch

    [Fact]
    public async Task An_unregistered_provider_is_reported_by_id_rather_than_guessed_at()
    {
        var registry = new DownloadResolverRegistry(
            new IDownloadResolver[]
            {
                new CloudShareResolver(
                    new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
                    AppLog.For("Test")),
            },
            AppLog.For("Test"));

        var outcome = await registry.ResolveAsync(Source(
            SourceKind.CloudShare,
            share: "https://pan.example.invalid/s/abc",
            provider: "quark"));

        Assert.Equal(ResolveStatus.ProviderError, outcome.Status);
        Assert.Contains("quark", outcome.Message);
        Assert.Equal(CloudErrorKind.Unsupported, outcome.Error);
    }

    [Fact]
    public async Task A_cloud_source_without_a_share_link_says_so()
    {
        var provider = new UnauthenticatedProvider();
        var registry = new DownloadResolverRegistry(
            new IDownloadResolver[]
            {
                new CloudShareResolver(
                    new CloudProviderRegistry(new ICloudProvider[] { provider }, AppLog.For("Test")),
                    AppLog.For("Test")),
            },
            AppLog.For("Test"));

        var outcome = await registry.ResolveAsync(Source(SourceKind.CloudShare, provider: provider.Id));

        Assert.Equal(ResolveStatus.Failed, outcome.Status);
        Assert.Contains("分享链接", outcome.Message);
    }

    /// <summary>A provider that always needs a login, to exercise the auth branch.</summary>
    private sealed class UnauthenticatedProvider : ICloudProvider
    {
        public string Id => "needs-login";

        public string DisplayName => "需要登录的测试平台";

        public bool RequiresAuthentication => true;

        public CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public string? LimitationNote => "测试替身";

        public string? DocumentationUrl => null;

        public CloudAuthState GetAuthState(CloudCredential? credential) => CloudAuthState.AuthRequired;

        public Task<CloudListResult> ListAsync(
            string folderId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudListResult> ListChildrenAsync(
            string folderId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudResolveResult> ResolveAsync(
            string shareUrl,
            string? fileName,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.AuthRequired, "needs login"));
    }
}
