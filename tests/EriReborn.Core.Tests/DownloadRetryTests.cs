using System.Security.Cryptography;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Rate limiting is a normal answer from these platforms, so the downloader backs
/// off instead of failing or hammering (spec 37).
/// </summary>
public sealed class DownloadRetryTests : IDisposable
{
    private readonly string _temp;
    private readonly TestFileSystemService _files;

    public DownloadRetryTests()
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

    private HttpDownloader CreateDownloader(
        RetryPolicy policy,
        List<TimeSpan>? waits = null)
        => new(
            new HttpClient(),
            _files,
            new DownloadCache(Path.Combine(_temp, "cache")),
            AppLog.For("Test"),
            policy,
            (delay, _) =>
            {
                waits?.Add(delay);
                return Task.CompletedTask;
            });

    // ------------------------------------------------------------------ policy

    [Fact]
    public void The_delay_grows_exponentially()
    {
        var policy = new RetryPolicy(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayFor(0, jitter: () => 1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayFor(1, jitter: () => 1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayFor(2, jitter: () => 1));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.DelayFor(3, jitter: () => 1));
    }

    [Fact]
    public void The_delay_is_capped()
    {
        var policy = new RetryPolicy(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), policy.DelayFor(5, jitter: () => 1));
    }

    [Fact]
    public void Jitter_spreads_the_delay_without_exceeding_it()
    {
        var policy = new RetryPolicy(BaseDelay: TimeSpan.FromSeconds(4), MaxDelay: TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayFor(0, jitter: () => 0));
        Assert.Equal(TimeSpan.FromSeconds(3), policy.DelayFor(0, jitter: () => 0.5));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayFor(0, jitter: () => 1));
    }

    [Fact]
    public void A_server_retry_after_wins_over_the_calculated_delay()
    {
        var policy = new RetryPolicy(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(30));

        // The platform knows when it will accept traffic again.
        Assert.Equal(TimeSpan.FromSeconds(7), policy.DelayFor(0, TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void An_absurd_retry_after_is_capped_by_the_server_wait_ceiling()
    {
        var policy = new RetryPolicy(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(5));

        // A mistaken or hostile header must not stall the download for an hour.
        // It is bounded by how long a server may ask us to wait — not by the
        // backoff ceiling, which answers a different question.
        Assert.Equal(TimeSpan.FromMinutes(5), policy.DelayFor(0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void The_backoff_ceiling_never_shortens_a_retry_after()
    {
        // This used to be wrong: the local backoff cap was applied to the server's
        // instruction, so a server asking for a minute was refused after thirty
        // seconds — the exact behaviour the header exists to prevent.
        var policy = new RetryPolicy(BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayFor(0, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void The_two_ceilings_can_be_configured_separately()
    {
        var policy = new RetryPolicy(
            BaseDelay: TimeSpan.FromSeconds(1),
            MaxDelay: TimeSpan.FromSeconds(5),
            MaxServerWait: TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayFor(0, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(15), policy.DelayFor(0, TimeSpan.FromSeconds(15)));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(416, false)]
    [InlineData(200, false)]
    public void Only_answers_that_can_change_are_retried(int status, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.IsRetryableStatus(status));
    }

    [Fact]
    public void Transport_failures_are_retried_but_programming_errors_are_not()
    {
        Assert.True(RetryPolicy.IsRetryableException(new HttpRequestException("boom")));
        Assert.True(RetryPolicy.IsRetryableException(new IOException("disk")));
        Assert.False(RetryPolicy.IsRetryableException(new InvalidOperationException("bug")));
    }

    [Fact]
    public void The_attempt_count_is_bounded()
    {
        var policy = new RetryPolicy(MaxAttempts: 3);

        Assert.True(policy.CanRetry(0));
        Assert.True(policy.CanRetry(1));
        Assert.False(policy.CanRetry(2));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData(" 12 ", 12)]
    public void Retry_after_seconds_are_parsed(string header, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.ParseRetryAfter(header));
    }

    [Fact]
    public void A_retry_after_date_is_parsed_relative_to_now()
    {
        var when = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R");

        var parsed = RetryPolicy.ParseRetryAfter(when);

        Assert.NotNull(parsed);
        Assert.InRange(parsed!.Value.TotalSeconds, 20, 30);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public void An_unreadable_retry_after_is_ignored_rather_than_guessed(string? header)
    {
        Assert.Null(RetryPolicy.ParseRetryAfter(header));
    }

    // -------------------------------------------------------------- behaviour

    [Fact]
    public async Task A_transient_server_failure_is_retried_and_then_succeeds()
    {
        var full = Payload(30_000);
        var seen = 0;

        using var server = new TestHttpServer(_ =>
        {
            seen++;
            return seen <= 2 ? new TestResponse(503, Array.Empty<byte>()) : TestResponse.Ok(full);
        });

        var waits = new List<TimeSpan>();
        var destination = Path.Combine(_temp, "flaky.bin");

        var result = await CreateDownloader(
            new RetryPolicy(MaxAttempts: 4, BaseDelay: TimeSpan.FromMilliseconds(10), MaxDelay: TimeSpan.FromSeconds(1)),
            waits).DownloadAsync(new DownloadRequest
            {
                Url = server.Url("/flaky.bin"),
                DestinationPath = destination,
                ExpectedSha256 = Sha(full),
            });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.Equal(3, seen);
        Assert.Equal(2, waits.Count);
        Assert.Equal(full, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task An_answer_that_cannot_change_is_not_retried()
    {
        var seen = 0;
        using var server = new TestHttpServer(_ =>
        {
            seen++;
            return TestResponse.NotFound();
        });

        var waits = new List<TimeSpan>();
        var result = await CreateDownloader(new RetryPolicy(MaxAttempts: 4), waits).DownloadAsync(
            new DownloadRequest
            {
                Url = server.Url("/missing.bin"),
                DestinationPath = Path.Combine(_temp, "missing.bin"),
            });

        Assert.Equal(DownloadState.HttpError, result.State);
        Assert.Equal(404, result.HttpStatusCode);

        // Retrying a 404 only wastes the user's time.
        Assert.Equal(1, seen);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Exhausting_the_attempts_reports_the_last_http_answer()
    {
        var seen = 0;
        using var server = new TestHttpServer(_ =>
        {
            seen++;
            return new TestResponse(503, Array.Empty<byte>());
        });

        var result = await CreateDownloader(
            new RetryPolicy(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)),
            new List<TimeSpan>()).DownloadAsync(new DownloadRequest
            {
                Url = server.Url("/down.bin"),
                DestinationPath = Path.Combine(_temp, "down.bin"),
            });

        Assert.Equal(DownloadState.HttpError, result.State);
        Assert.Equal(503, result.HttpStatusCode);

        // Bounded: three attempts, not an endless loop.
        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task A_rate_limit_is_honoured_through_retry_after()
    {
        var full = Payload(20_000);
        var seen = 0;

        using var server = new TestHttpServer(_ =>
        {
            seen++;
            return seen == 1
                ? new TestResponse(429, Array.Empty<byte>())
                {
                    ExtraHeaders = new Dictionary<string, string> { ["Retry-After"] = "0" },
                }
                : TestResponse.Ok(full);
        });

        var waits = new List<TimeSpan>();
        var result = await CreateDownloader(
            new RetryPolicy(MaxAttempts: 3, BaseDelay: TimeSpan.FromSeconds(30)),
            waits).DownloadAsync(new DownloadRequest
            {
                Url = server.Url("/limited.bin"),
                DestinationPath = Path.Combine(_temp, "limited.bin"),
                ExpectedSha256 = Sha(full),
            });

        Assert.Equal(DownloadState.Success, result.State);
        Assert.Equal(2, seen);

        // The server said "0", so the 30s base delay was not used.
        Assert.Single(waits);
        Assert.Equal(TimeSpan.Zero, waits[0]);
    }

    [Fact]
    public async Task Cancelling_during_the_backoff_stops_the_download()
    {
        var seen = 0;
        using var server = new TestHttpServer(_ =>
        {
            seen++;
            return new TestResponse(503, Array.Empty<byte>());
        });

        using var cancellation = new CancellationTokenSource();
        var downloader = new HttpDownloader(
            new HttpClient(),
            _files,
            new DownloadCache(Path.Combine(_temp, "cache")),
            AppLog.For("Test"),
            new RetryPolicy(MaxAttempts: 5, BaseDelay: TimeSpan.FromMilliseconds(1)),
            (_, token) =>
            {
                // The wait itself is cancelled, which is how a real Task.Delay ends.
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var result = await downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = server.Url("/cancel.bin"),
                DestinationPath = Path.Combine(_temp, "cancel.bin"),
            },
            cancellationToken: cancellation.Token);

        Assert.Equal(DownloadState.Cancelled, result.State);
        Assert.Equal(1, seen);
    }
}
