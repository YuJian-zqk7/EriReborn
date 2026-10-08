using EriReborn.Core.Logging;
using EriReborn.Core.Net;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A batch must not become a burst: thirty items run back to back look, to the
/// platform being contacted, like thirty simultaneous requests (spec 37).
/// </summary>
public sealed class BatchRunnerTests
{
    /// <summary>Invokes the callback inline, unlike Progress&lt;T&gt; which posts it.</summary>
    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static BatchRunner Create(
        BatchOptions? options = null,
        List<TimeSpan>? waits = null)
        => new(
            options ?? new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.FromMilliseconds(10)),
            AppLog.For("Test"),
            (wait, _) =>
            {
                waits?.Add(wait);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task Every_item_runs_and_is_reported_individually()
    {
        var seen = new List<int>();
        var runner = Create();

        var results = await runner.RunAsync(
            new[] { 1, 2, 3 },
            (item, _) =>
            {
                lock (seen)
                {
                    seen.Add(item);
                }

                return Task.CompletedTask;
            });

        Assert.Equal(3, seen.Count);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(new[] { 1, 2, 3 }, results.Select(result => result.Item).OrderBy(x => x));
    }

    [Fact]
    public async Task One_failure_does_not_abandon_the_rest()
    {
        var runner = Create();

        var results = await runner.RunAsync(
            new[] { "a", "boom", "c" },
            (item, _) => item == "boom"
                ? Task.FromException(new InvalidOperationException("nope"))
                : Task.CompletedTask);

        Assert.Equal(3, results.Count);
        Assert.True(results.Single(r => r.Item == "a").Succeeded);
        Assert.True(results.Single(r => r.Item == "c").Succeeded);

        var failed = results.Single(r => r.Item == "boom");
        Assert.False(failed.Succeeded);
        Assert.Contains("nope", failed.Error);
    }

    [Fact]
    public async Task Stop_on_failure_marks_the_remainder_as_skipped_rather_than_silent()
    {
        var runner = Create(new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.Zero, StopOnFailure: true));

        var results = await runner.RunAsync(
            new[] { "first", "boom", "third" },
            (item, _) => item == "boom"
                ? Task.FromException(new InvalidOperationException("nope"))
                : Task.CompletedTask);

        var skipped = results.Where(r => r is { Succeeded: false, Error: not null } && r.Error.Contains("跳过")).ToList();

        Assert.NotEmpty(skipped);
        Assert.All(skipped, result => Assert.Contains("已跳过", result.Error));
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        var live = 0;
        var peak = 0;
        var gate = new SemaphoreSlim(0);

        var runner = Create(new BatchOptions(MaxConcurrency: 2, Spacing: TimeSpan.Zero));

        var running = runner.RunAsync(
            Enumerable.Range(0, 6).ToArray(),
            async (_, _) =>
            {
                var now = Interlocked.Increment(ref live);
                lock (gate)
                {
                    peak = Math.Max(peak, now);
                }

                await Task.Delay(20);
                Interlocked.Decrement(ref live);
            });

        await running;

        Assert.True(peak <= 2, $"峰值并发 {peak} 超过了上限 2");
        Assert.True(peak >= 2, $"峰值并发只有 {peak}，没有真正并行");
    }

    [Fact]
    public async Task Starts_are_spaced_by_the_configured_interval()
    {
        var waits = new List<TimeSpan>();
        var runner = Create(
            new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.FromMilliseconds(250)),
            waits);

        await runner.RunAsync(new[] { 1, 2, 3, 4 }, (_, _) => Task.CompletedTask);

        // Four items means three gaps; the first item is not made to wait.
        Assert.Equal(3, waits.Count);
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromMilliseconds(250), wait));
    }

    [Fact]
    public async Task No_spacing_means_no_artificial_wait()
    {
        var waits = new List<TimeSpan>();
        var runner = Create(new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.Zero), waits);

        await runner.RunAsync(new[] { 1, 2, 3 }, (_, _) => Task.CompletedTask);

        Assert.Empty(waits);
    }

    [Fact]
    public async Task An_empty_batch_does_nothing_at_all()
    {
        var called = 0;
        var runner = Create();

        var results = await runner.RunAsync(
            Array.Empty<int>(),
            (_, _) =>
            {
                called++;
                return Task.CompletedTask;
            });

        Assert.Empty(results);
        Assert.Equal(0, called);
    }

    [Fact]
    public async Task Progress_is_reported_for_every_completion()
    {
        var reports = new List<(int Completed, int Total)>();
        var progress = new ImmediateProgress<(int Completed, int Total)>(value =>
        {
            lock (reports)
            {
                reports.Add(value);
            }
        });

        await Create(new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.Zero))
            .RunAsync(new[] { 1, 2, 3 }, (_, _) => Task.CompletedTask, progress);

        Assert.Equal(3, reports.Count);
        Assert.Contains(reports, report => report is { Completed: 3, Total: 3 });
    }

    [Fact]
    public async Task Cancelling_records_the_items_rather_than_throwing_at_the_caller()
    {
        using var cancellation = new CancellationTokenSource();

        var runner = Create(new BatchOptions(MaxConcurrency: 1, Spacing: TimeSpan.Zero));

        var results = await runner.RunAsync(
            Enumerable.Range(0, 10).ToArray(),
            async (item, token) =>
            {
                if (item == 0)
                {
                    cancellation.Cancel();
                }

                token.ThrowIfCancellationRequested();
                await Task.Yield();
            },
            cancellationToken: cancellation.Token);

        // The batch returns what it managed, instead of an exception the caller
        // has to unpack to find out how far it got.
        Assert.Equal(10, results.Count);
        Assert.Contains(results, result => !result.Succeeded);
    }

    [Fact]
    public void A_zero_concurrency_is_clamped_rather_than_hanging()
    {
        Assert.Equal(1, new BatchOptions(MaxConcurrency: 0).Concurrency);
        Assert.Equal(1, new BatchOptions(MaxConcurrency: -5).Concurrency);
    }
}
