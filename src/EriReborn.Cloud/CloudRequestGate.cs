using EriReborn.Core.Logging;
using EriReborn.Core.Net;

namespace EriReborn.Cloud;

/// <summary>
/// Spaces and retries the requests a provider makes to its platform.
///
/// These platforms rate-limit hard — 123 云盘 has a history of it — so Eri has
/// to be a polite client (spec 37). Two rules do that:
///
///   * one request at a time, spaced by a minimum interval, so a burst cannot
///     build up in the first place;
///   * a 429 or 5xx is retried with backoff, honouring Retry-After.
///
/// Everything else is returned as-is: retrying a 403 only spends the platform's
/// patience without changing the answer.
/// </summary>
public sealed class CloudRequestGate
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly IAppLogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private DateTimeOffset? _lastRequestAt;

    public CloudRequestGate(
        IAppLogger log,
        TimeSpan? minimumInterval = null,
        RetryPolicy? retryPolicy = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _log = log;
        MinimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(400);
        Policy = retryPolicy ?? new RetryPolicy();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>How closely together two requests to the same platform may fall.</summary>
    public TimeSpan MinimumInterval { get; }

    public RetryPolicy Policy { get; }

    /// <summary>How many requests this gate has actually put on the wire.</summary>
    public int RequestCount { get; private set; }

    /// <summary>
    /// Sends one request.
    ///
    /// It takes a factory rather than a message because an HttpRequestMessage
    /// cannot be sent twice: a retry needs a fresh one, and reusing the old one
    /// would either throw or resend a consumed body.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        string providerName,
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await RespectIntervalAsync(cancellationToken).ConfigureAwait(false);

                using (var request = requestFactory())
                {
                    RequestCount++;
                    var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    var status = (int)response.StatusCode;
                    if (!RetryPolicy.IsRetryableStatus(status) || !Policy.CanRetry(attempt))
                    {
                        return response;
                    }

                    var retryAfter = RetryPolicy.ParseRetryAfter(response);
                    response.Dispose();

                    var wait = Policy.DelayFor(attempt, retryAfter);
                    _log.Warn(
                        "cloud.retry",
                        $"{providerName}: HTTP {status}; retrying as attempt {attempt + 2}/{Policy.MaxAttempts} in {wait.TotalSeconds:0.##}s.");

                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task RespectIntervalAsync(CancellationToken cancellationToken)
    {
        if (_lastRequestAt is { } last)
        {
            var remaining = MinimumInterval - (_now() - last);
            if (remaining > TimeSpan.Zero)
            {
                await _delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }

        _lastRequestAt = _now();
    }
}
