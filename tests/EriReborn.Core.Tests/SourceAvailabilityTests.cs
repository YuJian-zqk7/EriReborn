using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Software;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// "One resource, several sources, pick one" (spec 38-41) as behaviour rather than as intent.
///
/// <para>
/// Choosing a source used to be <c>Sources.FirstOrDefault()</c>, so a share link that
/// cannot be fetched without a sign-in was indistinguishable from a direct link that is
/// ready to download, and the page had no way to say so. These tests pin the real
/// availability, and pin that the choice never depends on which platform it is.
/// </para>
/// </summary>
public sealed class SourceAvailabilityTests
{
    private const string ShareUrl = "https://pan.example.test/s/abc";

    /// <summary>A provider whose answers this test decides, standing in for states no shipped platform is in today.</summary>
    private sealed class StubProvider(
        string id,
        string displayName,
        CloudImplementationKind capability,
        CloudAuthState state,
        bool requiresAuthentication = true) : ICloudProvider
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public bool RequiresAuthentication { get; } = requiresAuthentication;

        public CloudImplementationKind ImplementationKind { get; } = capability;

        public string? LimitationNote => "这个平台在本版本里还接不上。";

        public string? DocumentationUrl => null;

        public CloudAuthState GetAuthState(CloudCredential? credential) => state;

        public Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "未使用。"));

        public Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "未使用。"));

        public Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.Unsupported, "未使用。"));
    }

    private static CloudProviderRegistry Registry(params ICloudProvider[] providers)
        => new(providers, AppLog.For("Test"));

    /// <summary>The five real platforms, sharing one credential store so a sign-in can be simulated.</summary>
    private static (CloudProviderRegistry Registry, InMemoryCredentialStore Credentials) RealProviders()
    {
        var network = new TestNetworkService(new HttpClient());
        var credentials = new InMemoryCredentialStore();
        ICloudProvider[] providers =
        {
            new Provider123(network, credentials),
            new ProviderBaidu(network, credentials),
            new ProviderQuark(network, credentials),
            new ProviderLanzou(network, credentials),
            new ProviderXunlei(network, credentials),
        };

        return (new CloudProviderRegistry(providers, AppLog.For("Test")), credentials);
    }

    private static SoftwareSource Share(string? providerId, string? shareUrl = ShareUrl, bool verifiable = false)
        => new()
        {
            Kind = SourceKind.CloudShare,
            ProviderId = providerId,
            ShareUrl = shareUrl,
            Sha256 = verifiable ? "AABBCCDD" : null,
        };

    private static SoftwareSource HttpUrl(bool verifiable = false)
        => new()
        {
            Kind = SourceKind.HttpUrl,
            Url = "https://example.test/setup.exe",
            SizeBytes = verifiable ? 4096 : null,
        };

    private static SoftwareDefinition Software(params SoftwareSource[] sources)
        => new()
        {
            Id = "demo",
            Name = "示例软件",
            CategoryId = "utilities",
            DirectoryName = "demo",
            Sources = sources,
        };

    private static SourceAvailabilityService ServiceFor(CloudProviderRegistry registry)
        => new(registry, AppLog.For("Test"));

    [Fact]
    public async Task A_signed_out_platform_is_not_reported_as_ready()
    {
        var (registry, _) = RealProviders();
        var availability = await ServiceFor(registry).AssessAsync(Share(CloudProviderIds.Quark));

        Assert.Equal(SourceReadiness.NeedsSignIn, availability.Readiness);
        Assert.False(availability.IsReady);
        Assert.Equal("夸克网盘", availability.PlatformName);
    }

    [Fact]
    public async Task A_signed_in_platform_is_ready_and_says_why()
    {
        var (registry, credentials) = RealProviders();
        await credentials.SetAsync(CloudProviderBase.CredentialKey(CloudProviderIds.Quark, "cookie"), "SESSION=stub", CancellationToken.None);

        var availability = await ServiceFor(registry).AssessAsync(Share(CloudProviderIds.Quark));

        Assert.Equal(SourceReadiness.Ready, availability.Readiness);
        Assert.Contains("夸克网盘", availability.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_platform_that_needs_no_account_is_ready_without_one()
    {
        var (registry, _) = RealProviders();
        var availability = await ServiceFor(registry).AssessAsync(Share(CloudProviderIds.Lanzou));

        Assert.Equal(SourceReadiness.Ready, availability.Readiness);
        Assert.Equal(CloudImplementationKind.HtmlParsing, availability.Capability);
    }

    [Fact]
    public async Task A_platform_this_build_cannot_reach_is_named_rather_than_hidden()
    {
        var (registry, _) = RealProviders();
        var availability = await ServiceFor(registry).AssessAsync(Share(CloudProviderIds.Xunlei));

        Assert.Equal(SourceReadiness.PlatformUnavailable, availability.Readiness);
        Assert.Equal("迅雷云盘", availability.PlatformName);
        Assert.False(string.IsNullOrWhiteSpace(availability.Summary));
    }

    [Fact]
    public async Task A_platform_that_is_not_registered_is_reported_by_its_id()
    {
        var (registry, _) = RealProviders();
        var availability = await ServiceFor(registry).AssessAsync(Share("onedrive"));

        Assert.Equal(SourceReadiness.PlatformUnavailable, availability.Readiness);
        Assert.Contains("onedrive", availability.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_share_source_without_a_link_is_incomplete()
    {
        var (registry, _) = RealProviders();
        var availability = await ServiceFor(registry).AssessAsync(Share(CloudProviderIds.Quark, shareUrl: null));

        Assert.Equal(SourceReadiness.Incomplete, availability.Readiness);
    }

    [Fact]
    public async Task A_stale_sign_in_is_distinguished_from_a_missing_one()
    {
        var expired = new StubProvider("expired", "过期平台", CloudImplementationKind.OfficialApi, CloudAuthState.Expired);
        var service = ServiceFor(Registry(expired));

        var availability = await service.AssessAsync(Share("expired"));

        Assert.Equal(SourceReadiness.SignInExpired, availability.Readiness);
        Assert.NotEqual(SourceReadiness.NeedsSignIn, availability.Readiness);
    }

    [Fact]
    public async Task A_source_with_a_hash_or_a_size_counts_as_verifiable()
    {
        var (registry, _) = RealProviders();
        var service = ServiceFor(registry);

        Assert.True((await service.AssessAsync(HttpUrl(verifiable: true))).Verifiable);
        Assert.True((await service.AssessAsync(Share(CloudProviderIds.Quark, verifiable: true))).Verifiable);
        Assert.False((await service.AssessAsync(HttpUrl())).Verifiable);
    }

    [Fact]
    public async Task Every_source_of_a_resource_is_reported()
    {
        var (registry, _) = RealProviders();
        var software = Software(HttpUrl(), Share(CloudProviderIds.Xunlei), Share(CloudProviderIds.Lanzou));

        var all = await ServiceFor(registry).AssessAsync(software);

        Assert.Equal(software.Sources.Count, all.Count);
        Assert.Equal(SourceReadiness.Ready, all[0].Readiness);
        Assert.Equal(SourceReadiness.PlatformUnavailable, all[1].Readiness);
    }

    [Fact]
    public async Task Picking_prefers_a_source_that_is_ready_over_one_that_needs_a_sign_in()
    {
        var (registry, _) = RealProviders();
        var software = Software(Share(CloudProviderIds.Quark), HttpUrl());

        var all = await ServiceFor(registry).AssessAsync(software);
        var picked = SourceAvailabilityService.Pick(all);

        Assert.NotNull(picked);
        Assert.Equal(SourceKind.HttpUrl, picked!.Source.Kind);
    }

    [Fact]
    public async Task Picking_prefers_a_download_that_can_be_checked()
    {
        var (registry, _) = RealProviders();
        var software = Software(HttpUrl(), HttpUrl(verifiable: true));

        var all = await ServiceFor(registry).AssessAsync(software);
        var picked = SourceAvailabilityService.Pick(all);

        Assert.NotNull(picked);
        Assert.True(picked!.Verifiable);
    }

    [Fact]
    public async Task Picking_ranks_how_the_file_is_obtained_and_not_which_platform_it_is()
    {
        // Both are ready and neither is verifiable, so the only thing left to separate them is
        // how each platform says it reads the file. That is a capability, not a favourite.
        var page = new StubProvider("page", "页面平台", CloudImplementationKind.HtmlParsing, CloudAuthState.NotRequired, requiresAuthentication: false);
        var api = new StubProvider("api", "接口平台", CloudImplementationKind.OfficialApi, CloudAuthState.NotRequired, requiresAuthentication: false);
        var service = ServiceFor(Registry(page, api));

        var software = Software(Share("page", "https://page.example.test/s/1"), Share("api", "https://api.example.test/s/1"));
        var picked = SourceAvailabilityService.Pick(await service.AssessAsync(software));

        Assert.NotNull(picked);
        Assert.Equal("api", picked!.Source.ProviderId);
    }

    [Fact]
    public async Task Picking_follows_the_manifest_order_and_not_the_platform()
    {
        // Five platforms in exactly the same state, so nothing separates them but the order
        // the manifest lists them in. Any other answer is a preference for one platform over
        // another, which is the one thing five peers must never have (30/31). Reversing the
        // list must move the answer, which catches an ordering that secretly reads the name.
        var stubs = Enumerable.Range(0, 5)
            .Select(index => new StubProvider(
                "p" + index,
                "平台" + index,
                CloudImplementationKind.OfficialApi,
                CloudAuthState.NotRequired,
                requiresAuthentication: false))
            .ToArray();
        var service = ServiceFor(Registry(stubs));

        var forward = Software(stubs.Select(stub => Share(stub.Id, "https://" + stub.Id + ".example.test/s/1")).ToArray());
        var backward = Software(stubs.Reverse().Select(stub => Share(stub.Id, "https://" + stub.Id + ".example.test/s/1")).ToArray());

        var first = SourceAvailabilityService.Pick(await service.AssessAsync(forward));
        var last = SourceAvailabilityService.Pick(await service.AssessAsync(backward));

        Assert.Equal("p0", first!.Source.ProviderId);
        Assert.Equal("p4", last!.Source.ProviderId);
    }

    [Fact]
    public async Task Picking_keeps_declaration_order_when_nothing_else_separates_them()
    {
        var a = new StubProvider("a", "甲平台", CloudImplementationKind.OfficialApi, CloudAuthState.NotRequired, requiresAuthentication: false);
        var b = new StubProvider("b", "乙平台", CloudImplementationKind.OfficialApi, CloudAuthState.NotRequired, requiresAuthentication: false);
        var service = ServiceFor(Registry(a, b));

        var software = Software(Share("a", "https://a.example.test/s/1"), Share("b", "https://b.example.test/s/1"));

        var first = SourceAvailabilityService.Pick(await service.AssessAsync(software));
        var second = SourceAvailabilityService.Pick(await service.AssessAsync(software));

        Assert.Equal("a", first!.Source.ProviderId);
        Assert.Equal(first.Source.ProviderId, second!.Source.ProviderId);
    }

    [Fact]
    public async Task A_resource_with_no_source_offers_nothing_to_pick()
    {
        var (registry, _) = RealProviders();
        var all = await ServiceFor(registry).AssessAsync(Software());

        Assert.Empty(all);
        Assert.Null(SourceAvailabilityService.Pick(all));
    }

    [Fact]
    public async Task A_source_that_needs_a_person_to_point_at_it_is_not_called_ready()
    {
        var (registry, _) = RealProviders();
        var manual = new SoftwareSource { Kind = SourceKind.Manual };

        var availability = await ServiceFor(registry).AssessAsync(manual);

        Assert.Equal(SourceReadiness.Incomplete, availability.Readiness);
    }

    [Fact]
    public async Task A_source_marked_as_not_yet_located_is_not_called_ready()
    {
        // The official plugin marks an entry it could not find in the share. Resolving such a source can
        // only look for its file name at the share root, which holds folders and no files, so the download
        // fails after the user has already committed to it. It used to be announced as ready all the same.
        var (registry, _) = RealProviders();
        var unlocated = new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = CloudProviderIds.Lanzou,
            ShareUrl = ShareUrl,
            FileName = "Setup.exe",
            NeedsLocatorResolution = true,
        };

        var availability = await ServiceFor(registry).AssessAsync(unlocated);

        Assert.Equal(SourceReadiness.Incomplete, availability.Readiness);
        Assert.False(availability.IsReady);
        Assert.Contains("重新定位", availability.Summary, StringComparison.Ordinal);
        Assert.Contains("Setup.exe", availability.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locator_beside_a_stale_mark_still_counts_as_located()
    {
        // The mark and the locator disagree, and the locator is what actually decides the download. The
        // page must not hide a source that works because of a mark the generator failed to clear.
        var (registry, _) = RealProviders();
        var located = new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = CloudProviderIds.Lanzou,
            ShareUrl = ShareUrl,
            NeedsLocatorResolution = true,
            Locator = new ResourceLocator { Path = "工具/Setup.exe" },
        };

        var availability = await ServiceFor(registry).AssessAsync(located);

        Assert.Equal(SourceReadiness.Ready, availability.Readiness);
    }

    [Fact]
    public async Task Picking_prefers_a_located_source_over_one_that_still_needs_relocating()
    {
        // Without the mark being read, both sources rank the same and declaration order would hand the job
        // to the one that cannot resolve anything, even though the other one is right there and works.
        var (registry, _) = RealProviders();
        var unlocated = new SoftwareSource
        {
            Kind = SourceKind.CloudShare,
            ProviderId = CloudProviderIds.Lanzou,
            ShareUrl = ShareUrl,
            FileName = "Setup.exe",
            NeedsLocatorResolution = true,
        };
        var located = unlocated with
        {
            NeedsLocatorResolution = false,
            Locator = new ResourceLocator { Path = "工具/Setup.exe" },
        };

        var picked = SourceAvailabilityService.Pick(
            await ServiceFor(registry).AssessAsync(Software(unlocated, located)));

        Assert.NotNull(picked);
        Assert.False(picked!.Source.NeedsLocatorResolution);
    }
}
