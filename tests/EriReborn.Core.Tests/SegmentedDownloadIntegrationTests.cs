using System.Net;
using System.Net.Http.Headers;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The planner can be perfect and the download still corrupt: offsets, offsets
/// into the wrong file, a short range accepted as complete. These run the real
/// downloader against a range-capable server and compare every byte (spec 24).
/// </summary>
public sealed class SegmentedDownloadIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    public SegmentedDownloadIntegrationTests() => Directory.CreateDirectory(_root);

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

    /// <summary>A payload with no repeating pattern, so a misplaced byte shows up.</summary>
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var index = 0; index < size; index++)
        {
            bytes[index] = (byte)((index * 31 + index / 7) % 251);
        }

        return bytes;
    }

    private sealed class RangeServer : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public RangeServer(byte[] payload, bool supportsRanges = true)
        {
            _payload = payload;
            SupportsRanges = supportsRanges;
        }

        public bool SupportsRanges { get; }

        // Segments arrive concurrently, so every recorded fact needs a lock:
        // List<T>.Add and the ++ operators are not thread-safe and silently drop
        // entries, which looks exactly like a missing range.
        private readonly object _gate = new();

        public int Requests { get; private set; }

        public int Probes { get; private set; }

        public List<(long Start, long End)> ServedRanges { get; } = new();

        /// <summary>Every request's Range header, in order, for diagnosis.</summary>
        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var rangeHeader = request.Headers.Range?.ToString() ?? "(no range)";
            lock (_gate)
            {
                Requests++;
                Seen.Add(rangeHeader);
            }

            var requested = request.Headers.Range?.Ranges.FirstOrDefault();

            if (requested is null || !SupportsRanges)
            {
                return Task.FromResult(Full());
            }

            var start = requested.From ?? 0;
            var end = Math.Min(requested.To ?? _payload.Length - 1, _payload.Length - 1);

            lock (_gate)
            {
                // A one-byte request from zero is the capability probe, not a segment.
                if (start == 0 && end == 0 && _payload.Length > 1)
                {
                    Probes++;
                }
                else
                {
                    ServedRanges.Add((start, end));
                }
            }

            var slice = _payload[(int)start..(int)(end + 1)];

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice),
            };
            response.Content.Headers.ContentLength = slice.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _payload.Length);
            return Task.FromResult(response);
        }

        private HttpResponseMessage Full()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
            };
            response.Content.Headers.ContentLength = _payload.Length;
            return response;
        }
    }

    private async Task<(DownloadResult Result, RangeServer Server, string Destination)> DownloadAsync(
        byte[] payload,
        bool supportsRanges,
        SegmentOptions options,
        long? expectedSize = null,
        string? expectedSha = null)
    {
        var server = new RangeServer(payload, supportsRanges);
        var files = new TestFileSystemService(_root);
        var destination = Path.Combine(_root, "out", "tool.bin");

        var downloader = new HttpDownloader(
            new HttpClient(server),
            files,
            new DownloadCache(Path.Combine(_root, "cache")),
            AppLog.For("Test"),
            segments: options);

        var result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = "https://cdn.invalid/tool.bin",
            DestinationPath = destination,
            ExpectedSize = expectedSize,
            ExpectedSha256 = expectedSha,
            SoftwareId = "demo",
        });

        return (result, server, destination);
    }

    private static SegmentOptions On => new(
        Enabled: true,
        MinimumSize: 100,
        MaxSegments: 4,
        MinimumSegmentSize: 100);

    [Fact]
    public async Task A_segmented_download_produces_the_exact_same_bytes()
    {
        var payload = Payload(4096);

        var (result, server, destination) = await DownloadAsync(payload, supportsRanges: true, On, expectedSize: payload.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));

        // One probe plus one request per segment.
        Assert.True(
            server.ServedRanges.Count == 4,
            $"期望 4 段，实际 {server.ServedRanges.Count} 段；探针 {server.Probes} 次，共 {server.Requests} 次请求。看到的 Range 头：{string.Join(" | ", server.Seen)}");
        Assert.Equal(1, server.Probes);
        Assert.Equal(5, server.Requests);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1234)]
    [InlineData(4097)]
    [InlineData(100_000)]
    public async Task Awkward_sizes_still_assemble_exactly(int size)
    {
        var payload = Payload(size);

        var (result, _, destination) = await DownloadAsync(payload, supportsRanges: true, On, expectedSize: size);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task The_segments_cover_the_whole_file_with_no_overlap()
    {
        var payload = Payload(5000);

        var (result, server, _) = await DownloadAsync(payload, supportsRanges: true, On, expectedSize: payload.Length);

        Assert.True(result.IsSuccess, result.Message);

        var served = server.ServedRanges.OrderBy(range => range.Start).ToList();
        var cursor = 0L;
        foreach (var (start, end) in served)
        {
            Assert.Equal(cursor, start);
            cursor = end + 1;
        }

        Assert.Equal(payload.Length, cursor);
    }

    [Fact]
    public async Task Since_segmentation_is_off_by_default_no_range_is_requested()
    {
        var payload = Payload(4096);

        var (result, server, destination) = await DownloadAsync(payload, supportsRanges: true, new SegmentOptions(), expectedSize: payload.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Empty(server.ServedRanges);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task A_server_without_range_support_falls_back_to_one_stream()
    {
        var payload = Payload(4096);

        var (result, server, destination) = await DownloadAsync(payload, supportsRanges: false, On, expectedSize: payload.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));

        // The probe was answered with 200, so no segment was ever served.
        Assert.Empty(server.ServedRanges);
    }

    [Fact]
    public async Task A_small_file_is_not_segmented_even_when_enabled()
    {
        var payload = Payload(50);

        var (result, server, destination) = await DownloadAsync(payload, supportsRanges: true, On, expectedSize: payload.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Empty(server.ServedRanges);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task A_segmented_download_still_verifies_the_hash()
    {
        var payload = Payload(2048);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

        var (result, _, destination) = await DownloadAsync(
            payload,
            supportsRanges: true,
            On,
            expectedSize: payload.Length,
            expectedSha: sha);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task A_segmented_download_with_a_wrong_hash_is_still_refused()
    {
        var payload = Payload(2048);

        var (result, _, _) = await DownloadAsync(
            payload,
            supportsRanges: true,
            On,
            expectedSize: payload.Length,
            expectedSha: new string('a', 64));

        Assert.Equal(DownloadState.HashMismatch, result.State);
    }

    [Fact]
    public async Task An_unknown_size_is_never_segmented()
    {
        var payload = Payload(4096);

        // No ExpectedSize: there is no middle to split at.
        var (result, server, destination) = await DownloadAsync(payload, supportsRanges: true, On);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Empty(server.ServedRanges);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }
}
