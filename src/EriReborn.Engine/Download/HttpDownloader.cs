using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Engine.Download;

/// <summary>
/// Resumable HTTP downloader (spec 24).
/// 206 Partial Content appends to the existing .part file; a 200 answer to a
/// range request means the server ignored the range, so the partial file is
/// discarded and the download restarts from zero. The SHA-256 is always
/// recomputed over the final assembled file.
/// </summary>
public sealed class HttpDownloader(
    HttpClient client,
    IFileSystemService files,
    DownloadCache cache,
    IAppLogger log,
    RetryPolicy? retryPolicy = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    SegmentOptions? segments = null)
{
    private const int BufferSize = 81920;

    private readonly RetryPolicy _policy = retryPolicy ?? new RetryPolicy();

    // Injectable so tests can exercise backoff without actually waiting.
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay = delay;

    // Disabled unless the caller asks: a second connection is not free.
    private readonly SegmentOptions _segments = segments ?? new SegmentOptions();

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destination = request.DestinationPath;
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            await files.EnsureDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        var cacheKey = new DownloadCacheKey(
            request.SoftwareId,
            request.Version,
            request.Architecture,
            request.ProviderId,
            request.Url,
            request.ExpectedSha256);

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256) && cache.TryGetVerified(cacheKey, out var cachedPath))
        {
            log.Info("download.cache", $"Cache hit for '{request.SoftwareId ?? request.Url}'.");
            File.Copy(cachedPath, destination, overwrite: true);
            progress?.Report(new DownloadProgress(new FileInfo(destination).Length, new FileInfo(destination).Length, 0, TimeSpan.Zero));
            return new DownloadResult(DownloadState.ServedFromCache, destination, new FileInfo(destination).Length, request.ExpectedSha256, null, false, "Served from verified cache.");
        }

        var partial = destination + ".part";
        var resumed = false;

        // Segmented first, when it applies. It never mixes with a resume: a
        // half-written single-stream .part has offsets that mean nothing to a
        // ranged write, so a segmented run always starts from scratch.
        if (_segments.Enabled && !File.Exists(partial)
            && await TrySegmentedAsync(request, partial, progress, cancellationToken).ConfigureAwait(false))
        {
            return await FinaliseAsync(request, destination, partial, false, cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }

        for (var attempt = 0; ; attempt++)
        {
            // Re-read every attempt: a previous try may have written bytes, and
            // resuming from a stale length either re-downloads them or skips data.
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;

            try
            {
                var outcome = await AttemptAsync(request, partial, existing, progress, cancellationToken).ConfigureAwait(false);

                if (outcome.Result is { } result)
                {
                    // A retryable status is not a final answer. Rate limiting is the
                    // platform asking us to slow down, not an error to hand the user
                    // (spec 37).
                    if (result.State == DownloadState.HttpError
                        && result.HttpStatusCode is { } status
                        && RetryPolicy.IsRetryableStatus(status)
                        && _policy.CanRetry(attempt))
                    {
                        await WaitBeforeRetryAsync(attempt, outcome.RetryAfter, status, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return result;
                }

                resumed = outcome.Resumed;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DownloadResult(DownloadState.Cancelled, partial, existing, Message: "Download cancelled.");
            }
            catch (Exception ex) when (RetryPolicy.IsRetryableException(ex) && _policy.CanRetry(attempt))
            {
                await WaitBeforeRetryAsync(attempt, null, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error("download.failed", "Download failed.", ex);
                return new DownloadResult(DownloadState.NetworkError, partial, File.Exists(partial) ? new FileInfo(partial).Length : 0, Message: ex.Message);
            }
        }

        return await FinaliseAsync(request, destination, partial, resumed, cacheKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Everything that happens once the bytes are on disk, whoever fetched them:
    /// size check, hash check, cache and move. Shared, so the single-stream and
    /// segmented paths cannot drift apart in what they verify.
    /// </summary>
    private async Task<DownloadResult> FinaliseAsync(
        DownloadRequest request,
        string destination,
        string partial,
        bool resumed,
        DownloadCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(partial))
        {
            return new DownloadResult(DownloadState.NetworkError, partial, 0, Message: "No data was written.");
        }

        var finalSize = new FileInfo(partial).Length;
        if (request.ExpectedSize is { } expectedSize && expectedSize > 0 && finalSize != expectedSize)
        {
            log.Warn("download.size", $"Size mismatch: expected {expectedSize}, got {finalSize}.");
            return new DownloadResult(DownloadState.SizeMismatch, partial, finalSize, Message: $"Expected {expectedSize} bytes, received {finalSize}.");
        }

        string? actualSha = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            // The hash is always computed over the complete assembled file.
            actualSha = await files.ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Error("download.hash", $"SHA-256 mismatch for '{request.SoftwareId ?? request.Url}': {actualSha} != {request.ExpectedSha256}.");
                TryDelete(partial);
                return new DownloadResult(DownloadState.HashMismatch, partial, finalSize, actualSha, Message: "SHA-256 mismatch; the partial file was removed.");
            }

            cache.Store(cacheKey, partial);
        }

        File.Move(partial, destination, overwrite: true);
        log.Info("download.done", $"Downloaded '{destination}' ({finalSize} bytes, resumed={resumed}).");
        return new DownloadResult(DownloadState.Success, destination, finalSize, actualSha, null, resumed);
    }

    /// <summary>Backs off before the next attempt, honouring a server Retry-After.</summary>
    private async Task WaitBeforeRetryAsync(
        int attempt,
        TimeSpan? retryAfter,
        int? statusCode,
        CancellationToken cancellationToken)
    {
        var wait = _policy.DelayFor(attempt, retryAfter);
        var reason = statusCode is { } code ? $"HTTP {code}" : "transport failure";

        log.Warn(
            "download.retry",
            $"{reason}; retrying as attempt {attempt + 2}/{_policy.MaxAttempts} in {wait.TotalSeconds:0.##}s.");

        if (_delay is not null)
        {
            await _delay(wait, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the file as parallel ranges when the size is known and the server
    /// agrees to ranges. Returns false when the caller should use one stream: the
    /// two paths agree on everything after the bytes are on disk, because both end
    /// in <see cref="FinaliseAsync"/>.
    /// </summary>
    private async Task<bool> TrySegmentedAsync(
        DownloadRequest request,
        string partial,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedSize is not { } total || total <= 0)
        {
            return false;
        }

        var plan = SegmentedDownloadPlanner.Plan(total, _segments);
        if (plan.Count <= 1)
        {
            return false;
        }

        if (!await SupportsRangesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            log.Info(
                "download.segments_unsupported",
                $"服务器未提供字节范围，'{request.SoftwareId ?? request.Url}' 使用单连接下载。");
            return false;
        }

        log.Info(
            "download.segmented",
            $"'{request.SoftwareId ?? request.Url}'：{plan.Count} 段，共 {total} 字节。");

        try
        {
            await DownloadSegmentsAsync(request, partial, plan, total, progress, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Falling back is safe and honest: the single stream starts from zero,
            // and whatever fails there is the error the user sees.
            log.Warn("download.segments_failed", $"{ex.GetType().Name}: {ex.Message} 回退到单连接下载。");
            TryDelete(partial);
            return false;
        }
    }

    /// <summary>Asks for a single byte: a 206 with a Content-Range is the only
    /// answer that proves ranges are supported, and it costs one small request.</summary>
    private async Task<bool> SupportsRangesAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (request.Headers is not null)
        {
            foreach (var header in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        message.Headers.Range = new RangeHeaderValue(0, 0);

        try
        {
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange?.Length is not null;
        }
        catch (Exception ex) when (RetryPolicy.IsRetryableException(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Writes every range at its own offset. Positions are absolute, so the
    /// segments never need to coordinate with each other — only the shared handle.
    /// </summary>
    private async Task DownloadSegmentsAsync(
        DownloadRequest request,
        string partial,
        IReadOnlyList<ByteRange> plan,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(partial);
        if (!string.IsNullOrEmpty(directory))
        {
            await files.EnsureDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        TryDelete(partial);

        // Preallocate so every offset exists before any writer reaches it.
        using (var preallocate = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            preallocate.SetLength(total);
        }

        using var handle = File.OpenHandle(partial, FileMode.Open, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);

        long written = 0;
        var started = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            plan,
            new ParallelOptions { MaxDegreeOfParallelism = plan.Count, CancellationToken = cancellationToken },
            async (range, token) =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
                if (request.Headers is not null)
                {
                    foreach (var header in request.Headers)
                    {
                        message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                message.Headers.Range = new RangeHeaderValue(range.Start, range.EndInclusive);

                using var response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new HttpRequestException($"分段 {range} 期望 206，实际 {(int)response.StatusCode}。");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                var buffer = new byte[BufferSize];
                var offset = range.Start;
                var remaining = range.Length;

                while (remaining > 0)
                {
                    var read = await stream
                        .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                        .ConfigureAwait(false);

                    if (read <= 0)
                    {
                        // A short range is a hole in the file, never something to accept.
                        throw new IOException($"分段 {range} 提前结束，还差 {remaining} 字节。");
                    }

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, token).ConfigureAwait(false);
                    offset += read;
                    remaining -= read;

                    var done = Interlocked.Add(ref written, read);
                    var elapsed = started.Elapsed;
                    progress?.Report(new DownloadProgress(
                        done,
                        total,
                        elapsed.TotalSeconds <= 0 ? 0 : done / elapsed.TotalSeconds,
                        elapsed));
                }
            }).ConfigureAwait(false);
    }

    private async Task<(DownloadResult? Result, bool Resumed, TimeSpan? RetryAfter)> AttemptAsync(
        DownloadRequest request,
        string partial,
        long existing,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (request.Headers is not null)
        {
            foreach (var header in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (existing > 0)
        {
            message.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var decision = DecideRange(existing, response.StatusCode);
        if (decision == RangeDecision.RestartRequired)
        {
            log.Info("download.range", "Server answered 200 to a range request; restarting the download from zero.");
            TryDelete(partial);
            existing = 0;
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            log.Warn("download.http", $"HTTP {status} for {request.Url}.");
            return (
                new DownloadResult(DownloadState.HttpError, partial, existing, HttpStatusCode: status, Message: $"HTTP {status}"),
                false,
                RetryPolicy.ParseRetryAfter(response));
        }

        var append = decision == RangeDecision.ResumeAccepted;
        var remaining = response.Content.Headers.ContentLength;
        long? total = remaining is null ? null : append ? existing + remaining.Value : remaining.Value;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            partial,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        var buffer = new byte[BufferSize];
        var received = existing;
        var stopwatch = Stopwatch.StartNew();
        var sinceLastReport = 0L;
        var windowStart = stopwatch.Elapsed;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            sinceLastReport += read;

            if (sinceLastReport < 256 * 1024)
            {
                continue;
            }

            var elapsed = stopwatch.Elapsed - windowStart;
            var speed = elapsed.TotalSeconds > 0.0001 ? sinceLastReport / elapsed.TotalSeconds : 0;
            TimeSpan? eta = speed > 0 && total is { } t && t > received
                ? TimeSpan.FromSeconds((t - received) / speed)
                : null;
            progress?.Report(new DownloadProgress(received, total, speed, eta));
            sinceLastReport = 0;
            windowStart = stopwatch.Elapsed;
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new DownloadProgress(received, total, 0, null));
        // No HTTP answer to report: the attempt succeeded and resumed, or not.
        return (null, append, null);
    }

    /// <summary>
    /// What to do with a partial file given the server's answer to a range request.
    ///
    /// <para>
    /// A server that ignores the range header answers 200 with the whole body; appending
    /// that to a partial file produces a file whose beginning is duplicated. That is the
    /// resume bug this decision exists to prevent, and it is a decision rather than an
    /// inline check so a test can hold it still.
    /// </para>
    /// </summary>
    internal static RangeDecision DecideRange(long existing, HttpStatusCode status)
    {
        if (existing <= 0)
        {
            return RangeDecision.NoResumePossible;
        }

        return status switch
        {
            HttpStatusCode.PartialContent => RangeDecision.ResumeAccepted,
            HttpStatusCode.OK => RangeDecision.RestartRequired,
            _ => RangeDecision.NoResumePossible,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Nothing useful to do; the caller already reports the failure.
        }
    }
}
