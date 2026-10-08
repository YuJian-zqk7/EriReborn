using System.Globalization;
using System.Net;

namespace EriReborn.Core.Net;

/// <summary>
/// How a failed network attempt is retried.
///
/// Rate limiting is a normal answer from these platforms rather than a terminal
/// error, so the policy backs off instead of hammering (spec 37). Three things
/// keep that from becoming pointless polling:
///
///   * the attempt count is bounded;
///   * the delay grows exponentially and is capped;
///   * answers that cannot change are never retried, so a wrong URL fails once
///     instead of four times.
///
/// It lives in Core because downloads and platform API calls both need it, and
/// neither should own the other's copy.
/// </summary>
public sealed record RetryPolicy(
    int MaxAttempts = 4,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    TimeSpan? MaxServerWait = null)
{
    public TimeSpan Base => BaseDelay ?? TimeSpan.FromMilliseconds(500);

    /// <summary>How far our own exponential backoff may grow.</summary>
    public TimeSpan Ceiling => MaxDelay ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a server is allowed to ask us to wait.
    ///
    /// <para>
    /// Deliberately not the same bound as <see cref="Ceiling"/>. They answer
    /// different questions: one caps a delay we invent, the other caps an
    /// instruction we were given. Reusing the backoff ceiling silently shortened
    /// every Retry-After — a server asking for a minute was told no after thirty
    /// seconds, which is precisely the behaviour the header exists to prevent.
    /// </para>
    ///
    /// <para>
    /// It still needs a bound: a wrong or hostile value must not park the
    /// application for a day.
    /// </para>
    /// </summary>
    public TimeSpan ServerWaitCeiling => MaxServerWait ?? TimeSpan.FromMinutes(5);

    /// <summary>True while another attempt is still allowed.</summary>
    public bool CanRetry(int attempt) => attempt + 1 < MaxAttempts;

    /// <summary>
    /// Whether an HTTP status is worth trying again. A 403 will still be a 403,
    /// and retrying it only annoys the platform.
    /// </summary>
    public static bool IsRetryableStatus(int statusCode) => statusCode switch
    {
        408 => true,                  // request timeout
        425 => true,                  // too early
        429 => true,                  // rate limited: the whole reason this exists
        >= 500 and <= 599 => true,    // server side, may pass on its own
        _ => false,
    };

    /// <summary>Whether a transport-level failure is worth trying again.</summary>
    public static bool IsRetryableException(Exception exception) => exception switch
    {
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };

    /// <summary>
    /// The delay before an attempt.
    ///
    /// A server-supplied Retry-After wins, because the platform knows when it
    /// will accept traffic again; it is still capped, so a hostile or mistaken
    /// header cannot stall a request indefinitely.
    /// </summary>
    public TimeSpan DelayFor(int attempt, TimeSpan? retryAfter = null, Func<double>? jitter = null)
    {
        if (retryAfter is { } serverDelay)
        {
            // A server that says zero — or names a moment already past — means
            // "come back now". Falling through to the exponential calculation
            // would wait far longer than the platform asked for.
            if (serverDelay <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return serverDelay > ServerWaitCeiling ? ServerWaitCeiling : serverDelay;
        }

        var growth = Math.Pow(2, Math.Max(0, attempt));
        var raw = TimeSpan.FromTicks((long)(Base.Ticks * growth));
        var capped = raw > Ceiling ? Ceiling : raw;

        // Jitter, so clients that failed together do not retry together and
        // recreate the burst that got them limited.
        var sample = jitter?.Invoke() ?? Random.Shared.NextDouble();
        var factor = 0.5 + (Math.Clamp(sample, 0, 1) * 0.5);
        return TimeSpan.FromTicks((long)(capped.Ticks * factor));
    }

    /// <summary>
    /// Reads a Retry-After header. It is either a number of seconds or an HTTP
    /// date, and a value that is neither is ignored rather than guessed at.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(seconds, 3600));
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when - DateTimeOffset.UtcNow
            : null;
    }

    /// <summary>Reads Retry-After from response headers, tolerating its absence.</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta)
            {
                return delta;
            }

            if (header.Date is { } date)
            {
                return date - DateTimeOffset.UtcNow;
            }
        }

        return null;
    }
}
