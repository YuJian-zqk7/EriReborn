using EriReborn.Core.Logging;

namespace EriReborn.Core.Jobs;

/// <summary>
/// Runs long work as observable jobs with a real terminal state.
///
/// Before this existed, every long operation reported progress its own way and a
/// failure could leave nothing behind but a log line. A job is always in exactly
/// one state, always ends in Completed / Failed / Cancelled, and can always be
/// cancelled — including while it is still queued (spec 10).
/// </summary>
public sealed class JobManager
{
    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private readonly Queue<Job> _queue = new();
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JobContext, Task>> _work = new(StringComparer.Ordinal);
    private readonly IAppLogger? _log;
    private readonly int _maxConcurrent;

    private int _running;
    private int _sequence;

    public JobManager(int maxConcurrent = 2, IAppLogger? log = null)
    {
        if (maxConcurrent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), maxConcurrent, "并发数必须至少为 1。");
        }

        _maxConcurrent = maxConcurrent;
        _log = log;
    }

    /// <summary>Raised whenever a job is created or changes state.</summary>
    public event EventHandler<Job>? Changed;

    public int MaxConcurrent => _maxConcurrent;

    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToList();
            }
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Work waiting to run. Both the queue and the work table should empty as jobs
    /// reach a terminal state, so this is the number a leak would make grow.
    /// </summary>
    public int PendingWorkCount
    {
        get
        {
            lock (_gate)
            {
                return _work.Count;
            }
        }
    }

    public Job? Find(string jobId)
    {
        lock (_gate)
        {
            return _jobs.FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.Ordinal));
        }
    }

    /// <summary>Queues work and returns immediately; the job starts when a slot is free.</summary>
    public Job Start(string kind, string title, Func<JobContext, Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(work);

        Job job;
        lock (_gate)
        {
            job = new Job($"job-{++_sequence:D4}", kind, title);
            _jobs.Add(job);
            _work[job.Id] = work;
            _queue.Enqueue(job);
        }

        _log?.Info("job.created", $"'{job.Id}' ({kind}) queued: {title}.");
        Changed?.Invoke(this, job);
        Pump();
        return job;
    }

    /// <summary>
    /// Cancels a queued or running job. Returns false for an unknown id or a job
    /// that has already finished — cancelling something that is done is not an error
    /// the caller needs to see, but it is also not success.
    /// </summary>
    public bool Cancel(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        CancellationTokenSource? cancellation = null;
        Job? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.Ordinal));
            if (job is null || job.IsFinished)
            {
                return false;
            }

            if (_cancellations.TryGetValue(jobId, out cancellation))
            {
                // Running: ask it to stop. The state change happens when it does.
            }
            else
            {
                // Queued: it must never start.
                job.MarkCancelled();
            }
        }

        if (cancellation is null)
        {
            _log?.Info("job.cancelled", $"'{jobId}' was cancelled before it started.");
            Changed?.Invoke(this, job!);
            return true;
        }

        _log?.Info("job.cancelling", $"'{jobId}' was asked to stop.");
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished between the check and the call; the state is what matters.
        }

        return true;
    }

    /// <summary>True when every job has reached a terminal state.</summary>
    public bool AllFinished
    {
        get
        {
            lock (_gate)
            {
                return _jobs.All(job => job.IsFinished);
            }
        }
    }

    private void Pump()
    {
        while (true)
        {
            Job? next;
            lock (_gate)
            {
                if (_running >= _maxConcurrent || _queue.Count == 0)
                {
                    return;
                }

                next = _queue.Dequeue();

                // A job cancelled while queued must not be started later.
                if (next.IsFinished)
                {
                    // Its work is dropped here because RunAsync never runs for it, and
                    // that was the only place the entry was ever removed: every job
                    // cancelled before it started kept its delegate — along with
                    // whatever that closure captured — for the life of the manager.
                    _work.Remove(next.Id);
                    continue;
                }

                _running++;
                _cancellations[next.Id] = new CancellationTokenSource();
            }

            _ = RunAsync(next);
        }
    }

    private async Task RunAsync(Job job)
    {
        CancellationToken token;
        Func<JobContext, Task> work;

        lock (_gate)
        {
            token = _cancellations[job.Id].Token;
            work = _work[job.Id];
        }

        job.MarkRunning();
        Changed?.Invoke(this, job);

        var context = new JobContext(
            job.Id,
            progress =>
            {
                job.Report(progress);
                Changed?.Invoke(this, job);
            },
            token);

        try
        {
            await work(context).ConfigureAwait(false);
            job.MarkCompleted(job.Progress);
            _log?.Info("job.completed", $"'{job.Id}' finished at {job.Progress}.");
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
            _log?.Info("job.cancelled", $"'{job.Id}' stopped on request.");
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message);
            _log?.Error("job.failed", $"'{job.Id}' failed.", ex);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
                _cancellations.Remove(job.Id);
                _work.Remove(job.Id);
            }

            Changed?.Invoke(this, job);
            Pump();
        }
    }
}
