using System.Net;
using System.Text.Json;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Plugins;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The one caching rule every marketplace shares (spec 122/124).
///
/// <para>
/// The parse step is supplied by the caller and runs <b>before</b> anything is
/// written. These tests exist because the earlier version wrote first and returned
/// raw text, which made two failures possible: a bad response replacing a good
/// cache with itself, and a malformed document escaping as an exception from the
/// very path meant to degrade gracefully.
/// </para>
/// </summary>
public sealed class RemoteIndexCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-indexcache",
        Guid.NewGuid().ToString("N"));

    private string CacheFile => Path.Combine(_root, "index.json");

    public RemoteIndexCacheTests() => Directory.CreateDirectory(_root);

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
    { "extensions": [ { "id": "demo.one", "name": "One" }, { "id": "demo.two", "name": "Two" } ] }
    """;

    /// <summary>Stands in for a real document parser: throwing means "not usable".</summary>
    private static string Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("extensions", out var array)
            ? array.GetArrayLength().ToString()
            : "0";
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpClient Serving(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static HttpClient Offline()
        => new(new Handler(_ => throw new HttpRequestException("连接被拒绝。")));

    private static HttpClient NeverCalled()
        => new(new Handler(_ => throw new InvalidOperationException("不应该发出请求。")));

    private static Task<RemoteIndexFetch<string>> FetchAsync(HttpClient client, string? url, string cacheFile)
        => RemoteIndexCache.FetchAsync(client, AppLog.For("Test"), url, cacheFile, "商城", Parse);

    // ------------------------------------------------------------ no url set

    [Fact]
    public async Task No_url_without_a_cache_points_at_the_configuration()
    {
        var result = await FetchAsync(NeverCalled(), null, CacheFile);

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Contains("未配置商城索引地址", result.Message);
    }

    [Fact]
    public async Task A_caller_can_say_how_to_configure_one()
    {
        var result = await RemoteIndexCache.FetchAsync(
            NeverCalled(), AppLog.For("Test"), null, CacheFile, "商城", Parse, "可通过设置页指定");

        Assert.Contains("可通过设置页指定", result.Message);
    }

    [Fact]
    public async Task No_url_with_a_good_cache_uses_it()
    {
        await File.WriteAllTextAsync(CacheFile, GoodIndex);

        var result = await FetchAsync(NeverCalled(), null, CacheFile);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Equal("2", result.Value);
    }

    [Fact]
    public async Task No_cache_at_all_and_a_broken_cache_are_told_apart()
    {
        // "There is nothing cached" and "what is cached cannot be read" send the user
        // to different places, so a single assertion covering one of them was thin
        // cover for the off-by-one that made a missing file look like a broken one.
        var missing = await FetchAsync(NeverCalled(), null, CacheFile);

        Assert.False(missing.Success);
        Assert.Contains("未配置", missing.Message);
        Assert.DoesNotContain("无法解析", missing.Message);

        await File.WriteAllTextAsync(CacheFile, "{ not json");
        var broken = await FetchAsync(NeverCalled(), null, CacheFile);

        Assert.False(broken.Success);
        Assert.Contains("无法解析", broken.Message);
    }

    [Fact]
    public async Task A_cache_that_is_not_there_never_reports_a_parse_problem()
    {
        // The file does not exist at all, so nothing about parsing applies.
        Assert.False(File.Exists(CacheFile));

        var result = await FetchAsync(Offline(), "https://index.invalid/x.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("无缓存", result.Message);
        Assert.DoesNotContain("无法解析", result.Message);
    }

    [Fact]
    public async Task A_corrupt_cache_is_a_message_not_an_exception()
    {
        await File.WriteAllTextAsync(CacheFile, "{ not json");

        var result = await FetchAsync(NeverCalled(), null, CacheFile);

        Assert.False(result.Success);
        Assert.Contains("本地缓存无法解析", result.Message);
    }

    // -------------------------------------------------------- network failure

    [Fact]
    public async Task A_network_failure_falls_back_to_the_cache()
    {
        await File.WriteAllTextAsync(CacheFile, GoodIndex);

        var result = await FetchAsync(Offline(), "https://index.invalid/x.json", CacheFile);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Contains("网络不可用", result.Message);
        Assert.Equal("2", result.Value);
    }

    [Fact]
    public async Task A_network_failure_with_a_corrupt_cache_does_not_throw()
    {
        await File.WriteAllTextAsync(CacheFile, "not json");

        var result = await FetchAsync(Offline(), "https://index.invalid/x.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("本地缓存无法解析", result.Message);
    }

    [Fact]
    public async Task A_network_failure_without_a_cache_names_both_facts()
    {
        var result = await FetchAsync(Offline(), "https://index.invalid/x.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("不可用", result.Message);
        Assert.Contains("无缓存", result.Message);
    }

    // ---------------------------------------------------------- parse failure

    [Fact]
    public async Task A_malformed_document_is_blamed_on_the_content_not_the_network()
    {
        var result = await FetchAsync(Serving("<html>nope</html>"), "https://index.invalid/x.json", CacheFile);

        Assert.False(result.Success);
        Assert.Contains("无法解析", result.Message);
        Assert.DoesNotContain("网络不可用", result.Message);
    }

    [Fact]
    public async Task A_malformed_document_is_never_written_to_the_cache()
    {
        // The heart of it: the parse runs first, so there is no path on which an
        // unusable document reaches the disk. Nothing is written at all here.
        var result = await FetchAsync(Serving("{{{ garbage"), "https://index.invalid/x.json", CacheFile);

        Assert.False(result.Success);
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public async Task A_malformed_document_does_not_replace_a_good_cache()
    {
        await File.WriteAllTextAsync(CacheFile, GoodIndex);

        var result = await FetchAsync(Serving("{{{ garbage"), "https://index.invalid/x.json", CacheFile);

        Assert.True(result.Success);
        Assert.True(result.FromCache);
        Assert.Equal(GoodIndex.Trim(), (await File.ReadAllTextAsync(CacheFile)).Trim());
    }

    // ---------------------------------------------------------------- success

    [Fact]
    public async Task A_good_document_is_returned_and_cached()
    {
        var result = await FetchAsync(Serving(GoodIndex), "https://index.invalid/x.json", CacheFile);

        Assert.True(result.Success);
        Assert.False(result.FromCache);
        Assert.Equal("2", result.Value);
        Assert.True(File.Exists(CacheFile));
    }

    [Fact]
    public async Task An_http_error_is_a_fetch_failure_not_content()
    {
        var result = await FetchAsync(
            Serving("Internal Server Error", HttpStatusCode.InternalServerError),
            "https://index.invalid/x.json",
            CacheFile);

        Assert.False(result.Success);
        Assert.False(File.Exists(CacheFile));
    }

    // ---------------------------------------------------- the second consumer

    [Fact]
    public async Task The_plugin_marketplace_uses_the_same_rule()
    {
        // Two marketplaces, one caching rule: this is the path that would have kept
        // the old bugs alive in a second copy.
        var network = new TestNetworkService(Serving(
            "{ \"plugins\": [ { \"id\": \"p.one\", \"name\": \"One\" } ] }"));

        var client = new PluginMarketplaceClient(network, AppLog.For("Test"));
        var (index, fromCache, message) = await client.FetchAsync("https://index.invalid/p.json", CacheFile);

        Assert.Single(index.Entries);
        Assert.False(fromCache);
        Assert.Contains("1 个插件", message);

        // And a malformed answer afterwards cannot destroy what was just cached.
        var broken = new PluginMarketplaceClient(new TestNetworkService(Serving("{{{")), AppLog.For("Test"));
        var (fallback, cached, fallbackMessage) = await broken.FetchAsync("https://index.invalid/p.json", CacheFile);

        Assert.Single(fallback.Entries);
        Assert.True(cached);
        Assert.Contains("无法解析", fallbackMessage);
    }

    [Fact]
    public async Task The_plugin_marketplace_reports_a_bad_index_without_throwing()
    {
        var network = new TestNetworkService(Serving("<html>not an index</html>"));
        var client = new PluginMarketplaceClient(network, AppLog.For("Test"));

        var (index, _, message) = await client.FetchAsync("https://index.invalid/p.json", CacheFile);

        Assert.Empty(index.Entries);
        Assert.Contains("无法解析", message);
    }
}
