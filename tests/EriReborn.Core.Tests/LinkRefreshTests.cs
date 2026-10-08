using System.Net;
using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Cloud;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A cloud share's direct link is minted for a session and dies on its own. When
/// that happens the server answers the same 403/404 it would for a file that is
/// genuinely gone, so the only sane response is to ask for a new link — once
/// (spec 62).
/// </summary>
public sealed class LinkRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    public LinkRefreshTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static readonly byte[] Payload = System.Text.Encoding.UTF8.GetBytes("eri-reborn-payload");

    /// <summary>Answers with a scripted sequence of status codes.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;

        public ScriptedHandler(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

        public int Requests { get; private set; }

        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);

            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status);

            if (status == HttpStatusCode.OK)
            {
                response.Content = new ByteArrayContent(Payload);
                response.Content.Headers.ContentLength = Payload.Length;
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>Hands out a different direct link on every resolve, the way a real provider does.</summary>
    private sealed class RotatingProvider : ICloudProvider
    {
        public int ResolveCalls { get; private set; }

        public string Id => "test-cloud";

        public string DisplayName => "测试云盘";

        public bool RequiresAuthentication => false;

        public CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public string? LimitationNote => "测试替身";

        public string? DocumentationUrl => null;

        public CloudAuthState GetAuthState(CloudCredential? credential) => CloudAuthState.NotRequired;

        public Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(Array.Empty<CloudFile>()));

        public Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            var url = "https://cloud.invalid/direct/" + ResolveCalls;

            return Task.FromResult(CloudResolveResult.Ok(new CloudDownloadHandle(
                new CloudFile("file-1", fileName ?? "tool.exe", false),
                url,
                Note: "第 " + ResolveCalls + " 次解析得到的直链")));
        }
    }

    private static SoftwareDefinition Software(string? sha256 = null) => new()
    {
        Id = "demo",
        Name = "Demo",
        CategoryId = "Utility",
        DirectoryName = "Demo",
        Trust = SoftwareTrust.Verified,
        Sources = new[] { new SoftwareSource { Kind = SourceKind.CloudShare, ProviderId = "test-cloud", ShareUrl = "https://cloud.invalid/s/abc", Sha256 = sha256 } },
    };

    private async Task<(CloudDownloadOutcome Outcome, RotatingProvider Provider, ScriptedHandler Handler)> RunAsync(
        string? sha256,
        params HttpStatusCode[] statuses)
    {
        var definition = Software(sha256);
        var source = definition.Sources[0];

        var provider = new RotatingProvider();
        var registry = new CloudProviderRegistry(new ICloudProvider[] { provider }, AppLog.For("Test"));
        var handler = new ScriptedHandler(statuses);
        var files = new TestFileSystemService(_root);

        var downloader = new HttpDownloader(
            new HttpClient(handler),
            files,
            new DownloadCache(Path.Combine(_root, "cache")),
            AppLog.For("Test"));

        var resolver = new CloudShareLinkResolver(registry, downloader, AppLog.For("Test"));

        var outcome = await resolver.DownloadAsync(
            definition,
            source,
            Path.Combine(_root, "out", "tool.exe"));

        return (outcome, provider, handler);
    }

    // ------------------------------------------------------------- the policy

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public void A_link_that_stopped_working_is_worth_one_refresh(int status)
    {
        Assert.True(LinkExpiryPolicy.LooksLikeStaleLink(DownloadState.HttpError, status));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(200)]
    [InlineData(null)]
    public void Anything_else_is_not_a_stale_link(int? status)
    {
        // A server error is retried by the downloader; a refresh would not help.
        Assert.False(LinkExpiryPolicy.LooksLikeStaleLink(DownloadState.HttpError, status));
    }

    [Fact]
    public void A_hash_mismatch_never_triggers_a_refresh()
    {
        // The bytes were wrong, not the link. Re-resolving would fetch the same
        // wrong bytes again and hide a tamper or a corrupt transfer.
        Assert.False(LinkExpiryPolicy.LooksLikeStaleLink(DownloadState.HashMismatch, 403));
        Assert.False(LinkExpiryPolicy.LooksLikeStaleLink(DownloadState.SizeMismatch, 404));
        Assert.False(LinkExpiryPolicy.LooksLikeStaleLink(DownloadState.Cancelled, 403));
    }

    [Fact]
    public void The_reason_is_described_in_words()
    {
        Assert.Contains("403", LinkExpiryPolicy.Describe(403));
        Assert.Contains("404", LinkExpiryPolicy.Describe(404));
        Assert.Contains("没有给出状态码", LinkExpiryPolicy.Describe(null));
    }

    // ----------------------------------------------------------- the refresh

    [Fact]
    public async Task An_expired_link_is_re_resolved_and_the_download_then_succeeds()
    {
        var (outcome, provider, handler) = await RunAsync(null, HttpStatusCode.Forbidden, HttpStatusCode.OK);

        Assert.True(outcome.IsSuccess, outcome.Message);

        // Two resolves, two attempts, and the second link is the one that worked.
        Assert.Equal(2, provider.ResolveCalls);
        Assert.Equal(2, handler.Requests);
        Assert.EndsWith("/direct/2", handler.Urls[1]);

        // The user must be able to tell that a refresh happened.
        Assert.Contains("重新解析", outcome.Message);
    }

    [Fact]
    public async Task A_link_that_keeps_failing_is_refreshed_exactly_once()
    {
        var (outcome, provider, handler) = await RunAsync(
            null,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Forbidden);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CloudOutcome.DownloadFailed, outcome.Outcome);

        // Refreshing forever would turn a deleted file into a loop against someone
        // else's server.
        Assert.Equal(2, provider.ResolveCalls);
        Assert.Equal(2, handler.Requests);
        Assert.Contains("已重新解析直链一次", outcome.Message);
    }

    [Fact]
    public async Task A_good_link_is_never_re_resolved()
    {
        var (outcome, provider, handler) = await RunAsync(null, HttpStatusCode.OK);

        Assert.True(outcome.IsSuccess, outcome.Message);
        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain("重新解析", outcome.Message);
    }

    [Fact]
    public async Task A_wrong_hash_is_reported_and_not_retried()
    {
        var (outcome, provider, handler) = await RunAsync(
            new string('a', 64),
            HttpStatusCode.OK,
            HttpStatusCode.OK);

        Assert.Equal(CloudOutcome.HashMismatch, outcome.Outcome);

        // One attempt: the link was fine, the bytes were not.
        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain("重新解析", outcome.Message);
    }
}
