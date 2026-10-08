using EriReborn.Core.Logging;

namespace EriReborn.Core.Net;

/// <summary>How a batch is paced.</summary>
public sealed record BatchOptions(
    int MaxConcurrency = 1,
    TimeSpan? Spacing = null,
    bool StopOnFailure = false)
{
    public int Concurrency => Math.Max(1, MaxConcurrency);

    public TimeSpan Interval => Spacing ?? TimeSpan.FromMilliseconds(750);
}

/// <summary>What happened to one item. The item itself travels with the result.</summary>
public sealed record BatchItemResult<T>(T Item, bool Succeeded, string? Error);

/// <summary>
/// Runs a list of operations with a bounded width and a pause between starts.
///
/// This is what stops a batch from becoming a burst: installing thirty items
/// one after another as fast as the loop can go looks, to the platform being
/// contacted, exactly like thirty simultaneous requests (spec 37).
///
/// One item failing does not abandon the rest: each outcome is reported
/// separately, because a batch that stops at the first problem leaves the user
/// guessing how far it got.
/// </summary>
public sealed class BatchRunner(
    BatchOptions options,
    IAppLogger log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly BatchOptions _options = options;
    private readonly IAppLogger _log = log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public BatchOptions Options => _options;

    public async Task<IReadOnlyList<BatchItemResult<T>>> RunAsync<T>(
        IReadOnlyList<T> items,
        Func<T, CancellationToken, Task> operation,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new BatchItemResult<T>[items.Count];
        if (items.Count == 0)
        {
            return results;
        }

        // A fixed pool of workers pulling the next index, rather than one task per
        // item racing for a lock. That makes the order deterministic: at
        // concurrency 1 the items run strictly in list order, so "stop after a
        // failure" means the items after it, not whichever task lost the race.
        var next = -1;
        var started = 0;
        var completed = 0;
        var stop = false;

        // Starts are spaced, not completions: pacing the start is what keeps the
        // outward rate down when items take different amounts of time.
        var startLock = new SemaphoreSlim(1, 1);

        var workers = new Task[_options.Concurrency];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= items.Count)
                    {
                        return;
                    }

                    var item = items[index];

                    try
                    {
                        if (Volatile.Read(ref stop))
                        {
                            results[index] = new BatchItemResult<T>(item, false, "已跳过（前一项失败后停止）。");
                            continue;
                        }

                        if (Interlocked.Increment(ref started) > 1 && _options.Interval > TimeSpan.Zero)
                        {
                            await PaceAsync(startLock, cancellationToken).ConfigureAwait(false);
                        }

                        await operation(item, cancellationToken).ConfigureAwait(false);
                        results[index] = new BatchItemResult<T>(item, true, null);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        results[index] = new BatchItemResult<T>(item, false, "已取消。");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        results[index] = new BatchItemResult<T>(item, false, $"{ex.GetType().Name}: {ex.Message}");
                        _log.Warn("batch.item", $"批处理中的一项失败：{ex.GetType().Name}: {ex.Message}");

                        if (_options.StopOnFailure)
                        {
                            Volatile.Write(ref stop, true);
                        }
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report((done, items.Count));
                    }
                }
            }, cancellationToken);
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The per-item results already record what was cancelled.
        }

        var succeeded = results.Count(result => result is { Succeeded: true });
        _log.Info("batch.done", $"批处理完成：{succeeded}/{items.Count} 项成功。");

        return results;
    }

    /// <summary>Waits out the interval between two starts.</summary>
    private async Task PaceAsync(SemaphoreSlim startLock, CancellationToken cancellationToken)
    {
        await startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _delay(_options.Interval, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            startLock.Release();
        }
    }
}
