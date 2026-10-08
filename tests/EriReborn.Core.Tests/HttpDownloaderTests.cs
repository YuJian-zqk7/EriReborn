using System.Security.Cryptography;
using System.Text;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Spec 24/25: 206 appends, a 200 answer to a range request must restart, and
/// the hash is always computed over the final complete file.
/// </summary>
public sealed class HttpDownloaderTests : IDisposable
{
    private readonly string _temp;
    private readonly TestFileSystemService _files;

    public HttpDownloaderTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _files = new TestFileSystemService(_temp);
    }

    private HttpDownloader CreateDownloader()
    {
        var client = new HttpClient();
        var cache = new DownloadCache(Path.Combine(_temp, "cache"));
        return new HttpDownloader(client, _files, cache, AppLog.For("Test"));
    }

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

    [Fact]
    public async Task Fresh_download_writes_the_complete_file()
    {
        var full = Payload(50_000);
        using var server = new TestHttpServer(_ => TestResponse.Ok(full));

        var destination = Path.Combine(_temp, "fresh.bin");
        var result = await CreateDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/fresh.bin"),
            DestinationPath = destination,
            ExpectedSha256 = Sha(full),
        });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.Equal(full, await File.ReadAllBytesAsync(destination));
        Assert.Equal(Sha(full), result.Sha256);
    }

    [Fact]
    public async Task Partial_file_is_resumed_when_the_server_answers_206()
    {
        var full = Payload(50_000);
        const int half = 20_000;

        using var server = new TestHttpServer(request =>
        {
            var start = request.RangeStart ?? 0;
            return TestResponse.Partial(full[(int)start..]);
        });

        var destination = Path.Combine(_temp, "resume.bin");
        await File.WriteAllBytesAsync(destination + ".part", full[..half]);

        var result = await CreateDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/resume.bin"),
            DestinationPath = destination,
            ExpectedSha256 = Sha(full),
        });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.True(result.Resumed);
        Assert.Equal(full, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Server_ignoring_the_range_forces_a_full_restart_not_an_append()
    {
        var full = Payload(50_000);
        const int half = 20_000;

        // This server always returns 200 with the whole payload, even when a
        // Range header was sent: appending here would corrupt the file.
        using var server = new TestHttpServer(_ => TestResponse.Ok(full));

        var destination = Path.Combine(_temp, "restart.bin");
        await File.WriteAllBytesAsync(destination + ".part", full[..half]);

        var result = await CreateDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/restart.bin"),
            DestinationPath = destination,
            ExpectedSha256 = Sha(full),
        });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.False(result.Resumed);
        var written = await File.ReadAllBytesAsync(destination);
        Assert.Equal(full.Length, written.Length);
        Assert.Equal(full, written);
    }

    [Fact]
    public async Task Hash_mismatch_fails_and_removes_the_partial_file()
    {
        var full = Payload(10_000);
        using var server = new TestHttpServer(_ => TestResponse.Ok(full));

        var destination = Path.Combine(_temp, "bad.bin");
        var result = await CreateDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/bad.bin"),
            DestinationPath = destination,
            ExpectedSha256 = Sha(Payload(999)),
        });

        Assert.Equal(DownloadState.HashMismatch, result.State);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Verified_download_is_served_from_cache_on_the_second_request()
    {
        var full = Payload(30_000);
        using var server = new TestHttpServer(_ => TestResponse.Ok(full));
        var downloader = CreateDownloader();

        var first = Path.Combine(_temp, "first.bin");
        var resultOne = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/cached.bin"),
            DestinationPath = first,
            ExpectedSha256 = Sha(full),
            SoftwareId = "cached-app",
            Version = "1.0.0",
        });
        Assert.Equal(DownloadState.Success, resultOne.State);

        var second = Path.Combine(_temp, "second.bin");
        var resultTwo = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/cached.bin"),
            DestinationPath = second,
            ExpectedSha256 = Sha(full),
            SoftwareId = "cached-app",
            Version = "1.0.0",
        });

        Assert.Equal(DownloadState.ServedFromCache, resultTwo.State);
        Assert.Equal(full, await File.ReadAllBytesAsync(second));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public void Cache_identity_separates_different_versions_and_providers()
    {
        var a = new DownloadCacheKey("app", "1.0", "x64", "123", "https://host/file.bin", null).ToStableName();
        var b = new DownloadCacheKey("app", "2.0", "x64", "123", "https://host/file.bin", null).ToStableName();
        var c = new DownloadCacheKey("app", "1.0", "x64", "baidu", "https://host/file.bin", null).ToStableName();
        var d = new DownloadCacheKey("other", "1.0", "x64", "123", "https://host/file.bin", null).ToStableName();

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public async Task Http_error_is_reported_with_the_status_code()
    {
        using var server = new TestHttpServer(_ => TestResponse.NotFound());
        var destination = Path.Combine(_temp, "missing.bin");

        var result = await CreateDownloader().DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/missing.bin"),
            DestinationPath = destination,
        });

        Assert.Equal(DownloadState.HttpError, result.State);
        Assert.Equal(404, result.HttpStatusCode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
