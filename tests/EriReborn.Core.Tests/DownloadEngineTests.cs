using System.Security.Cryptography;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The route/engine layer turns "a share link" into "a download" without letting
/// any provider dictate which engine runs (spec 61/62/63/136).
/// </summary>
public sealed class DownloadEngineTests : IDisposable
{
    private readonly string _temp;
    private readonly TestFileSystemService _files;

    public DownloadEngineTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _files = new TestFileSystemService(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private NativeHttpDownloadEngine CreateNative() => new(
        new HttpDownloader(
            new HttpClient(),
            _files,
            new DownloadCache(Path.Combine(_temp, "cache")),
            AppLog.For("Test")),
        AppLog.For("Test"));

    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ---------------------------------------------------------------- the route

    [Fact]
    public void A_route_carries_its_referer_into_the_request_headers()
    {
        var route = new DownloadRoute(
            DownloadRouteKind.ProviderDirect,
            "https://example.invalid/a.bin",
            Referer: "https://example.invalid/share");

        var headers = route.BuildHeaders();

        Assert.Equal("https://example.invalid/share", headers["Referer"]);
    }

    [Fact]
    public void An_explicit_header_wins_over_the_referer()
    {
        var route = new DownloadRoute(
            DownloadRouteKind.ProviderDirect,
            "https://example.invalid/a.bin",
            Referer: "https://ignored.invalid/",
            Headers: new Dictionary<string, string> { ["Referer"] = "https://explicit.invalid/" });

        Assert.Equal("https://explicit.invalid/", route.BuildHeaders()["Referer"]);
    }

    [Fact]
    public void An_expired_route_knows_it_is_expired()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

        Assert.True(new DownloadRoute(DownloadRouteKind.Resolver, "u", ExpiresAt: now.AddMinutes(-1)).IsExpired(now));
        Assert.False(new DownloadRoute(DownloadRouteKind.Resolver, "u", ExpiresAt: now.AddMinutes(1)).IsExpired(now));

        // No expiry recorded means no claim either way.
        Assert.False(new DownloadRoute(DownloadRouteKind.Resolver, "u").IsExpired(now));
    }

    // --------------------------------------------------------------- the engine

    [Fact]
    public void The_native_engine_declines_a_route_that_needs_a_browser_session()
    {
        var engine = CreateNative();

        var assisted = new DownloadRoute(
            DownloadRouteKind.BrowserAssisted,
            "https://example.invalid/a.bin",
            RequiresSession: true);

        Assert.False(engine.CanHandle(assisted));

        // Without the session requirement it is an ordinary download.
        Assert.True(engine.CanHandle(assisted with { RequiresSession = false }));
    }

    [Fact]
    public void A_route_without_a_url_is_refused()
    {
        Assert.False(CreateNative().CanHandle(new DownloadRoute(DownloadRouteKind.Mirror, string.Empty)));
    }

    [Fact]
    public void The_selector_picks_by_capability_and_says_so_when_nothing_fits()
    {
        var native = CreateNative();
        var selector = new DownloadEngineSelector(new IDownloadEngine[] { native }, AppLog.For("Test"));

        var plain = new DownloadRoute(DownloadRouteKind.ProviderDirect, "https://example.invalid/a.bin");
        Assert.Same(native, selector.Select(plain));

        var needsBrowser = new DownloadRoute(
            DownloadRouteKind.BrowserAssisted,
            "https://example.invalid/a.bin",
            RequiresSession: true);

        // Nothing registered can do it, and silence would be worse than null.
        Assert.Null(selector.Select(needsBrowser));
    }

    // ------------------------------------------------- through the whole layer

    [Fact]
    public async Task A_resumed_download_through_the_engine_keeps_the_206_semantics()
    {
        var full = Payload(50_000);
        const int half = 18_000;

        using var server = new TestHttpServer(request =>
            TestResponse.Partial(full[(int)(request.RangeStart ?? 0)..]));

        var destination = Path.Combine(_temp, "resumed.bin");
        await File.WriteAllBytesAsync(destination + ".part", full[..half]);

        var route = new DownloadRoute(
            DownloadRouteKind.Resolver,
            server.Url("/resumed.bin"),
            ProviderId: "some-provider",
            SupportsRange: true,
            SupportsResume: true);

        var result = await CreateNative().DownloadAsync(route, new DownloadRequest
        {
            Url = route.Url,
            DestinationPath = destination,
            ExpectedSha256 = Sha(full),
        });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.True(result.Resumed);
        Assert.Equal(full, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task A_200_answer_to_a_range_request_restarts_through_the_engine_too()
    {
        var full = Payload(50_000);
        const int half = 18_000;

        // Always 200: appending here would corrupt the file.
        using var server = new TestHttpServer(_ => TestResponse.Ok(full));

        var destination = Path.Combine(_temp, "restarted.bin");
        await File.WriteAllBytesAsync(destination + ".part", full[..half]);

        var route = new DownloadRoute(DownloadRouteKind.Mirror, server.Url("/restarted.bin"));

        var result = await CreateNative().DownloadAsync(route, new DownloadRequest
        {
            Url = route.Url,
            DestinationPath = destination,
            ExpectedSha256 = Sha(full),
        });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.False(result.Resumed);
        Assert.Equal(full, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task The_provider_id_travels_with_the_request_without_changing_behaviour()
    {
        var full = Payload(20_000);
        using var server = new TestHttpServer(_ => TestResponse.Ok(full));

        var destination = Path.Combine(_temp, "provider.bin");
        var route = new DownloadRoute(DownloadRouteKind.ProviderDirect, server.Url("/p.bin"), ProviderId: "baidu");

        var result = await CreateNative().DownloadAsync(route, new DownloadRequest
        {
            Url = route.Url,
            DestinationPath = destination,
            ExpectedSha256 = Sha(full),
        });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.Equal(full, await File.ReadAllBytesAsync(destination));
    }
}
