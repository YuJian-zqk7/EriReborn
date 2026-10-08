using System.Net;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// These platforms rate-limit hard, so provider calls are spaced and backed off
/// rather than fired as fast as the code can manage (spec 37).
/// </summary>
public sealed class CloudThrottleTests
{
    /// <summary>A client whose answers are scripted per request.</summary>
    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond = respond;
        private int _count;
        private int _live;
        private int _peak;

        public int Requests => _count;

        public int PeakConcurrency => _peak;

        public int FactoryCalls { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var live = Interlocked.Increment(ref _live);
            _peak = Math.Max(_peak, live);

            try
            {
                // A real send is awaited; yielding here lets overlapping sends be
                // observed if the gate failed to serialise them.
                await Task.Yield();
                var index = Interlocked.Increment(ref _count);
                return _respond(index);
            }
            finally
            {
                Interlocked.Decrement(ref _live);
            }
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code)
    {
        var response = new HttpResponseMessage(code);
        response.Content = new StringContent(string.Empty);
        return response;
    }

    private static HttpResponseMessage Ok(string body = "ok")
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static CloudRequestGate CreateGate(
        TimeSpan? interval = null,
        RetryPolicy? policy = null,
        List<TimeSpan>? waits = null,
        FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        return new CloudRequestGate(
            AppLog.For("Test"),
            interval ?? TimeSpan.FromMilliseconds(1),
            policy ?? new RetryPolicy(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(10), MaxDelay: TimeSpan.FromSeconds(1)),
            () => clock.Now,
            (delay, _) =>
            {
                waits?.Add(delay);
                clock.Advance(delay);
                return Task.CompletedTask;
            });
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public DateTimeOffset Now => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // ------------------------------------------------------------- spacing

    [Fact]
    public async Task The_first_request_goes_out_immediately()
    {
        var waits = new List<TimeSpan>();
        var gate = CreateGate(TimeSpan.FromMilliseconds(500), waits: waits);
        using var client = new HttpClient(new ScriptedHandler(_ => Ok()));

        using var response = await gate.SendAsync("123", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_second_request_waits_for_the_minimum_interval()
    {
        var waits = new List<TimeSpan>();
        var gate = CreateGate(TimeSpan.FromMilliseconds(500), waits: waits);
        using var client = new HttpClient(new ScriptedHandler(_ => Ok()));

        await gate.SendAsync("123", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));
        await gate.SendAsync("123", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/b"));

        // The second call had to wait out the interval, not fire back to back.
        Assert.Single(waits);
        Assert.Equal(TimeSpan.FromMilliseconds(500), waits[0]);
    }

    [Fact]
    public async Task Requests_are_serialised_instead_of_overlapping()
    {
        var gate = CreateGate(TimeSpan.FromMilliseconds(5), new RetryPolicy(MaxAttempts: 1));
        var handler = new ScriptedHandler(_ => Ok());
        using var client = new HttpClient(handler);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            gate.SendAsync("123", client, () => new HttpRequestMessage(HttpMethod.Get, $"https://x.invalid/{i}"))));

        Assert.Equal(4, handler.Requests);
        Assert.Equal(1, handler.PeakConcurrency);
    }

    // ------------------------------------------------------------ retrying

    [Fact]
    public async Task A_rate_limit_is_retried_and_then_succeeds()
    {
        var waits = new List<TimeSpan>();
        var gate = CreateGate(waits: waits);
        var handler = new ScriptedHandler(index => index == 1 ? Status((HttpStatusCode)429) : Ok("finally"));
        using var client = new HttpClient(handler);

        using var response = await gate.SendAsync("baidu", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests);
        Assert.Single(waits);
    }

    [Fact]
    public async Task A_server_side_failure_is_retried_but_a_forbidden_is_not()
    {
        var serverGate = CreateGate();
        var serverHandler = new ScriptedHandler(_ => Status(HttpStatusCode.ServiceUnavailable));
        using var serverClient = new HttpClient(serverHandler);

        using var _ = await serverGate.SendAsync("123", serverClient, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));

        // Bounded, not endless.
        Assert.Equal(3, serverHandler.Requests);

        var forbiddenGate = CreateGate();
        var forbiddenHandler = new ScriptedHandler(_ => Status(HttpStatusCode.Forbidden));
        using var forbiddenClient = new HttpClient(forbiddenHandler);

        using var forbidden = await forbiddenGate.SendAsync("123", forbiddenClient, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // Retrying a 403 only spends the platform's patience.
        Assert.Equal(1, forbiddenHandler.Requests);
    }

    [Fact]
    public async Task A_server_retry_after_is_used_instead_of_the_calculated_backoff()
    {
        var waits = new List<TimeSpan>();
        var gate = CreateGate(
            policy: new RetryPolicy(MaxAttempts: 3, BaseDelay: TimeSpan.FromSeconds(30)),
            waits: waits);

        var handler = new ScriptedHandler(index =>
        {
            if (index > 1)
            {
                return Ok();
            }

            var limited = Status((HttpStatusCode)429);
            limited.Headers.TryAddWithoutValidation("Retry-After", "0");
            return limited;
        });

        using var client = new HttpClient(handler);
        using var response = await gate.SendAsync("quark", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The platform said "now", so the 30s base delay was never used. The
        // minimum-interval wait may also appear, since it applies between every
        // pair of calls — including a retry and the attempt before it.
        Assert.Contains(TimeSpan.Zero, waits);
        Assert.DoesNotContain(waits, wait => wait > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Every_attempt_builds_a_fresh_request()
    {
        // An HttpRequestMessage cannot be sent twice, so a retry that reused one
        // would fail rather than retry.
        var gate = CreateGate();
        var handler = new ScriptedHandler(index => index == 1 ? Status(HttpStatusCode.BadGateway) : Ok());
        using var client = new HttpClient(handler);

        var built = 0;
        using var response = await gate.SendAsync(
            "lanzou",
            client,
            () =>
            {
                built++;
                return new HttpRequestMessage(HttpMethod.Post, "https://x.invalid/a")
                {
                    Content = new StringContent("body"),
                };
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, built);
    }

    [Fact]
    public async Task The_gate_reports_how_many_requests_it_actually_sent()
    {
        var gate = CreateGate(TimeSpan.Zero, new RetryPolicy(MaxAttempts: 1));
        using var client = new HttpClient(new ScriptedHandler(_ => Ok()));

        await gate.SendAsync("xunlei", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/a"));
        await gate.SendAsync("xunlei", client, () => new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/b"));

        Assert.Equal(2, gate.RequestCount);
    }
}
