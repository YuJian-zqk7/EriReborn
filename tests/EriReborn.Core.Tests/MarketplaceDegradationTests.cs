using System.Net;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Extension.Marketplace;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The marketplace is supposed to degrade to its cached copy instead of an empty
/// page (spec 35/62). These tests pin the ways that promise used to break: a
/// corrupt cache escaping as an exception, a parse failure blamed on the network,
/// and a bad response destroying a good cache.
/// </summary>
public sealed class MarketplaceDegradationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-marketplace",
        Guid.NewGuid().ToString("N"));

    private string CacheFile => Path.Combine(_root, "marketplace", "index.json");

    public MarketplaceDegradationTests() => Directory.CreateDirectory(Path.Combine(_root, "marketplace"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        finally
        {
            // Best effort.
        }
    }

    private const string GoodIndex = """
    { "schema": 1, "extensions": [
      { "id": "demo.one", "name": "Demo One", "version": "1.0.0" },
      { "id": "demo.two", "name": "Demo Two" }
    ] }
    """;

    private static MarketplaceClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new TestNetworkService(new HttpClient(new Handler(respond))), AppLog.For("Test"));

    private static MarketplaceClient Offline()
        => Client(_ => throw new HttpRequestException("连接被拒绝。"));

    private static MarketplaceClient Serving(string body, HttpStatusCode status = HttpStatusCode.OK)
        => Client(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    // ------------------------------------------------------------ no url set

    [Fact]
    public async Task No_url_and_no_cache_says_how_to_configure_one()
    {
        var result = await Client(_ => throw new InvalidOperationException("not called"))
            .FetchAsync(null, CacheFile);

        Assert.False(result.Success);
        Assert.Contains("未配置商城索引地址", result.Message);
        Assert.Contains("ERIREBORN_MARKETPLACE_INDEX", result.Message);
    }

    [Fact]
    public async Task No_url_with_a_good_cache_still_shows_something()
    {
        await File.WriteAllTextAsync(CacheFile, GoodIndex);

        var result = await Client(_ => throw new InvalidOperationException("not called"))
            .FetchAsync(null, CacheFile);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Equal(2, result.Index.Entries.Count);
    }

    [Fact]
    public async Task A_corrupt_cache_is_reported_and_does_not_escape()
    {
        // This used to be parsed outside any try, so the exception left FetchAsync
        // entirely — from the one path whose whole job was to degrade gracefully.
        await File.WriteAllTextAsync(CacheFile, "{ not json at all");

        var result = await Client(_ => throw new InvalidOperationException("not called"))
            .FetchAsync(null, CacheFile);

        Assert.False(result.Success);
        Assert.Contains("本地缓存无法解析", result.Message);
    }

    // -------------------------------------------------------- network failure

    [Fact]
    public async Task A_network_failure_falls_back_to_the_cache()
    {
        await File.WriteAllTextAsync(CacheFile, GoodIndex);

        var result = await Offline().FetchAsync("https://index.invalid/list.json", CacheFile);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Contains("网络不可用", result.Message);
        Assert.Equal(2, result.Index.Entries.Count);
    }

    [Fact]
    public async Task A_network_failure_with_a_corrupt_cache_still_returns_instead_of_throwing()
    {
        // The parse used to happen inside the catch block, so a corrupt cache threw
        // out of the failure handler itself.
        await File.WriteAllTextAsync(CacheFile, "totally not json");

        var result = await Offline().FetchAsync("https://index.invalid/list.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("本地缓存无法解析", result.Message);
    }

    [Fact]
    public async Task A_network_failure_with_no_cache_names_both_facts()
    {
        var result = await Offline().FetchAsync("https://index.invalid/list.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("不可用", result.Message);
        Assert.Contains("无缓存", result.Message);
    }

    // ---------------------------------------------------------- parse failure

    [Fact]
    public async Task A_malformed_index_is_blamed_on_the_content_not_the_network()
    {
        // Telling someone their network is down when the index is simply malformed
        // sends them to fix the wrong thing.
        var result = await Serving("<html>not an index</html>").FetchAsync("https://index.invalid/list.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("无法解析", result.Message);
        Assert.DoesNotContain("网络不可用", result.Message);
    }

    [Fact]
    public async Task A_malformed_response_does_not_overwrite_a_good_cache()
    {
        await File.WriteAllTextAsync(CacheFile, GoodIndex);

        var result = await Serving("{{{ garbage").FetchAsync("https://index.invalid/list.json", CacheFile);

        // The cache is what the next offline launch has; a bad response destroying it
        // is how "degrade to cache" turns into "empty page".
        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Equal(GoodIndex.Trim(), (await File.ReadAllTextAsync(CacheFile)).Trim());
    }

    // ---------------------------------------------------------------- success

    [Fact]
    public async Task A_good_index_is_used_and_cached()
    {
        var result = await Serving(GoodIndex).FetchAsync("https://index.invalid/list.json", CacheFile);

        Assert.True(result.Success);
        Assert.False(result.FromCache);
        Assert.Equal(2, result.Index.Entries.Count);
        Assert.True(File.Exists(CacheFile));
    }

    [Fact]
    public async Task An_index_with_no_usable_entries_is_reported_as_such_not_as_an_error()
    {
        var result = await Serving("{ \"extensions\": [ { \"name\": \"no id\" } ] }")
            .FetchAsync("https://index.invalid/list.json", CacheFile);

        // Entries without an id are skipped, so the index is legitimately empty — and
        // the count in the message is how the user finds out.
        Assert.True(result.Success);
        Assert.Empty(result.Index.Entries);
        Assert.Contains("0 个扩展", result.Message);
    }

    [Fact]
    public async Task An_http_error_is_treated_as_a_fetch_failure_not_as_content()
    {
        var result = await Serving("Internal Server Error", HttpStatusCode.InternalServerError)
            .FetchAsync("https://index.invalid/list.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("无缓存", result.Message);
    }
}
