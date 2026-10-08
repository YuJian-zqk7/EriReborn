using System.Net;
using System.Security.Cryptography;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The download matrix (milestone 1): every way a real server can answer, driven
/// over a real socket.
///
/// <para>
/// The point is not that the downloader calls the right APIs — a message handler
/// would show that. It is that the downloader copes with a server that answers
/// badly: ignores ranges, rate-limits, drops the connection, or lies about how
/// many bytes it sent.
/// </para>
/// </summary>
public sealed class DownloadMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));

    public DownloadMatrixTests() => Directory.CreateDirectory(_root);

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

    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var index = 0; index < size; index++)
        {
            bytes[index] = (byte)((index * 37 + index / 11) % 251);
        }

        return bytes;
    }

    /// <summary>Serves ranges properly, and lets a case override specific attempts.</summary>
    private static TestHttpServer Server(byte[] payload, Func<int, TestResponse?>? overrideFor = null)
    {
        var attempt = 0;

        return new TestHttpServer(request =>
        {
            var number = Interlocked.Increment(ref attempt);

            if (overrideFor?.Invoke(number) is { } custom)
            {
                return custom;
            }

            if (request.RangeStart is { } start)
            {
                var end = request.RangeEnd is { } requested
                    ? (int)Math.Min(requested, payload.Length - 1)
                    : payload.Length - 1;

                var slice = payload[(int)start..(end + 1)];
                return new TestResponse(206, slice) { RangeStart = start, RangeEnd = end, TotalLength = payload.Length };
            }

            return TestResponse.Ok(payload);
        });
    }

    private sealed record Run(
        DownloadResult Result,
        TestHttpServer Server,
        string Destination,
        List<TimeSpan> Waits);

    private async Task<Run> DownloadAsync(
        byte[] payload,
        Func<int, TestResponse?>? overrideFor = null,
        long? partBytes = null,
        long? expectedSize = null,
        string? expectedSha = null,
        SegmentOptions? segments = null,
        CancellationToken cancellationToken = default,
        Func<Task>? afterStart = null)
    {
        using var server = Server(payload, overrideFor);
        var files = new TestFileSystemService(_root);
        var destination = Path.Combine(_root, "out", "tool.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (partBytes is { } existing && existing > 0)
        {
            await File.WriteAllBytesAsync(destination + ".part", payload[..(int)existing]);
        }

        var waits = new List<TimeSpan>();

        var downloader = new HttpDownloader(
            new HttpClient(),
            files,
            new DownloadCache(Path.Combine(_root, "cache")),
            AppLog.For("Test"),
            new RetryPolicy(MaxAttempts: 4, BaseDelay: TimeSpan.FromMilliseconds(1), MaxDelay: TimeSpan.FromMilliseconds(5)),
            (span, _) =>
            {
                lock (waits)
                {
                    waits.Add(span);
                }

                return Task.CompletedTask;
            },
            segments);

        if (afterStart is not null)
        {
            _ = Task.Run(afterStart);
        }

        var result = await downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = server.Url("/tool.bin"),
                DestinationPath = destination,
                ExpectedSize = expectedSize,
                ExpectedSha256 = expectedSha,
                SoftwareId = "matrix",
            },
            cancellationToken: cancellationToken);

        return new Run(result, server, destination, waits);
    }

    // ------------------------------------------------------------ the shape

    [Fact]
    public async Task A_plain_download_is_written_byte_for_byte()
    {
        var payload = Payload(3072);

        // The server must outlive the assertions that read its counters.
        using var server = Server(payload);
        var files = new TestFileSystemService(_root);
        var destination = Path.Combine(_root, "plain", "tool.bin");

        var downloader = new HttpDownloader(new HttpClient(), files, new DownloadCache(Path.Combine(_root, "cache")), AppLog.For("Test"));

        var result = await downloader.DownloadAsync(new DownloadRequest
        {
            Url = server.Url("/tool.bin"),
            DestinationPath = destination,
            ExpectedSize = payload.Length,
            SoftwareId = "matrix",
        });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, server.RequestCount);
        Assert.False(result.Resumed);
    }

    [Fact]
    public async Task A_partial_file_is_resumed_and_reported_as_resumed()
    {
        var payload = Payload(4096);
        var run = await DownloadAsync(payload, partBytes: 1024, expectedSize: payload.Length);

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.True(run.Result.Resumed);

        // The first request must have asked to continue from the partial length.
        Assert.Equal(1024, run.Server.Requests[0].RangeStart);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_restarts_from_zero_rather_than_appending()
    {
        var payload = Payload(4096);

        // 200 to a range request: the server is sending the whole file, so
        // appending it to the partial would corrupt the result.
        var run = await DownloadAsync(
            payload,
            overrideFor: number => number == 1 ? TestResponse.Ok(payload) : null,
            partBytes: 1024,
            expectedSize: payload.Length);

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.False(run.Result.Resumed);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));
    }

    // ------------------------------------------------------- retry behaviour

    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task A_retryable_status_is_retried_until_it_works(int status)
    {
        var payload = Payload(2048);
        var run = await DownloadAsync(
            payload,
            overrideFor: number => number <= 2 ? new TestResponse(status, Array.Empty<byte>()) : null,
            expectedSize: payload.Length);

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(3, run.Server.RequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));
    }

    [Fact]
    public async Task A_retry_after_header_is_honoured_instead_of_the_default_backoff()
    {
        var payload = Payload(1024);
        var run = await DownloadAsync(
            payload,
            overrideFor: number => number == 1
                ? new TestResponse(429, Array.Empty<byte>())
                {
                    ExtraHeaders = new Dictionary<string, string> { ["Retry-After"] = "7" },
                }
                : null,
            expectedSize: payload.Length);

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(run.Waits));
    }

    [Fact]
    public async Task A_permanent_status_is_not_retried_at_all()
    {
        var payload = Payload(1024);
        var run = await DownloadAsync(
            payload,
            overrideFor: _ => TestResponse.NotFound(),
            expectedSize: payload.Length);

        Assert.Equal(DownloadState.HttpError, run.Result.State);
        Assert.Equal(404, run.Result.HttpStatusCode);

        // Retrying a 404 is a wasted request and a slower failure for the user.
        Assert.Equal(1, run.Server.RequestCount);
    }

    [Fact]
    public async Task A_dropped_connection_is_a_transport_failure_that_gets_retried()
    {
        var payload = Payload(2048);
        var run = await DownloadAsync(
            payload,
            overrideFor: number => number == 1 ? new TestResponse(200, Array.Empty<byte>()) { DropConnection = true } : null,
            expectedSize: payload.Length);

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));
    }

    // ---------------------------------------------------------- verification

    [Fact]
    public async Task A_wrong_hash_is_refused_and_the_partial_file_is_removed()
    {
        var payload = Payload(2048);
        var run = await DownloadAsync(payload, expectedSize: payload.Length, expectedSha: new string('a', 64));

        Assert.Equal(DownloadState.HashMismatch, run.Result.State);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(run.Destination)!, "tool.bin.part")));
    }

    [Fact]
    public async Task A_correct_hash_is_accepted()
    {
        var payload = Payload(2048);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var run = await DownloadAsync(payload, expectedSize: payload.Length, expectedSha: sha);

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));
    }

    [Fact]
    public async Task A_short_body_is_a_size_mismatch_not_a_success()
    {
        var payload = Payload(2048);
        var run = await DownloadAsync(payload, expectedSize: 9999);

        Assert.Equal(DownloadState.SizeMismatch, run.Result.State);
    }

    [Fact]
    public async Task Cancelling_mid_download_reports_cancelled_rather_than_failed()
    {
        var payload = Payload(4096);
        using var cancellation = new CancellationTokenSource();

        var run = await DownloadAsync(
            payload,
            overrideFor: _ => new TestResponse(200, payload) { Delay = TimeSpan.FromSeconds(5) },
            expectedSize: payload.Length,
            cancellationToken: cancellation.Token,
            afterStart: async () =>
            {
                await Task.Delay(150);
                cancellation.Cancel();
            });

        Assert.Equal(DownloadState.Cancelled, run.Result.State);
    }

    // ------------------------------------------------------------- segmented

    [Fact]
    public async Task A_segmented_download_over_a_real_socket_is_byte_exact()
    {
        var payload = Payload(8192);

        var run = await DownloadAsync(
            payload,
            expectedSize: payload.Length,
            segments: new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 4, MinimumSegmentSize: 1000));

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));

        // Probe plus four segments.
        Assert.Equal(5, run.Server.RequestCount);

        var segments = run.Server.Requests.Skip(1).OrderBy(request => request.RangeStart).ToList();
        Assert.Equal(4, segments.Count);

        var cursor = 0L;
        foreach (var segment in segments)
        {
            Assert.Equal(cursor, segment.RangeStart);
            cursor = (segment.RangeEnd ?? 0) + 1;
        }

        Assert.Equal(payload.Length, cursor);
    }

    [Fact]
    public async Task A_middle_segment_carries_the_right_content_range()
    {
        var payload = Payload(4000);

        var run = await DownloadAsync(
            payload,
            expectedSize: payload.Length,
            segments: new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 4, MinimumSegmentSize: 1000));

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));

        // Every segment after the first must be a closed range, never an open tail.
        foreach (var segment in run.Server.Requests.Skip(1).Skip(1))
        {
            Assert.NotNull(segment.RangeEnd);
        }
    }

    [Fact]
    public async Task A_segmented_download_falls_back_when_the_server_ignores_ranges()
    {
        var payload = Payload(8192);

        var run = await DownloadAsync(
            payload,
            overrideFor: _ => TestResponse.Ok(payload),
            expectedSize: payload.Length,
            segments: new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 4, MinimumSegmentSize: 1000));

        Assert.True(run.Result.IsSuccess, run.Result.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(run.Destination));
    }
}
