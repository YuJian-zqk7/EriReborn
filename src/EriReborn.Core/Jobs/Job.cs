using System.ComponentModel;
using EriReborn.Core.Domain;

namespace EriReborn.Core.Jobs;

/// <summary>Where a job is in its life. These are the only states there are.</summary>
public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// How far a job has got. Total is optional: a job that cannot know its size
/// reports 0 and the UI shows indeterminate progress rather than a fake bar.
/// </summary>
public sealed record JobProgress(int Completed, int Total, string? Message = null)
{
    public static readonly JobProgress None = new(0, 0);

    /// <summary>
    /// The real transfer numbers behind a download job: bytes, speed and remaining time, exactly as
    /// the downloader measured them. Null for every other kind of job, so the fields are never
    /// filled with a guess.
    /// </summary>
    public DownloadProgress? Telemetry { get; init; }

    /// <summary>
    /// The outcome of the checks a download ran once the bytes were all in — the hash comparison, a
    /// size check — said in words. Null while there is nothing to report yet.
    /// </summary>
    public string? Verdict { get; init; }

    /// <summary>0..1, or null when the job cannot know how much is left.</summary>
    public double? Fraction => Total <= 0 ? null : Math.Clamp((double)Completed / Total, 0, 1);

    public override string ToString() => Total <= 0 ? $"{Completed}" : $"{Completed}/{Total}";
}

/// <summary>What a job is given so it can report progress and be cancelled.</summary>
public sealed class JobContext
{
    private readonly Action<JobProgress> _report;

    internal JobContext(string jobId, Action<JobProgress> report, CancellationToken cancellationToken)
    {
        JobId = jobId;
        _report = report;
        CancellationToken = cancellationToken;
    }

    public string JobId { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>Reports absolute progress, not an increment, so a retry cannot
    /// make the counter run backwards.</summary>
    public void Report(int completed, int total, string? message = null)
        => _report(new JobProgress(completed, total, message));

    public void Report(string message) => _report(new JobProgress(0, 0, message));

    /// <summary>
    /// Reports progress from a real transfer, carrying the transfer's own bytes, speed and
    /// remaining time through to whoever is watching. The counters are byte counts narrowed to the
    /// <see cref="int"/> the job model uses; the exact numbers stay in <see cref="JobProgress.Telemetry"/>.
    /// </summary>
    public void ReportDownload(DownloadProgress progress, string? message = null, string? verdict = null)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var completed = (int)Math.Clamp(progress.BytesReceived, 0, int.MaxValue);
        var total = (int)Math.Clamp(progress.TotalBytes ?? 0, 0, int.MaxValue);
        _report(new JobProgress(completed, total, message) { Telemetry = progress, Verdict = verdict });
    }

    public void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();
}

/// <summary>
/// One unit of long-running work. A job is observable, cancellable and has a
/// terminal state; it never quietly disappears (spec 10).
/// </summary>
public sealed class Job : INotifyPropertyChanged
{
    private JobState _state = JobState.Queued;
    private JobProgress _progress = JobProgress.None;
    private string? _error;

    internal Job(string id, string kind, string title)
    {
        Id = id;
        Kind = kind;
        Title = title;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    /// <summary>"download", "install", "scan"… free-form so new work needs no new enum.</summary>
    public string Kind { get; }

    public string Title { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public JobState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            Raise(nameof(State));
            Raise(nameof(IsFinished));
        }
    }

    public JobProgress Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            Raise(nameof(Progress));
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            _error = value;
            Raise(nameof(Error));
        }
    }

    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    /// <summary>
    /// Completes when the job reaches a terminal state. A caller that needs to know
    /// when the work is done can await this instead of polling; it never throws,
    /// because a failed or cancelled job is a state, not an exception.
    /// </summary>
    public Task Completion => _completion.Task;

    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void MarkRunning()
    {
        StartedAt = DateTimeOffset.UtcNow;
        State = JobState.Running;
    }

    internal void Report(JobProgress progress)
    {
        // Never let the counter go backwards: a resumed download that re-reports
        // from the start would otherwise make the bar jump back.
        if (progress.Total > 0 && Progress.Total == progress.Total && progress.Completed < Progress.Completed)
        {
            return;
        }

        Progress = progress;
    }

    /// <summary>Raised once when the job reaches a terminal state (completed, failed or cancelled).</summary>
    public event EventHandler<JobState>? Finished;

    internal void MarkCompleted(JobProgress progress)
    {
        if (IsFinished)
        {
            return;
        }

        Progress = progress;
        FinishedAt = DateTimeOffset.UtcNow;
        State = JobState.Completed;
        _completion.TrySetResult();
        Finished?.Invoke(this, JobState.Completed);
    }

    internal void MarkFailed(string error)
    {
        if (IsFinished)
        {
            return;
        }

        Error = error;
        FinishedAt = DateTimeOffset.UtcNow;
        State = JobState.Failed;
        _completion.TrySetResult();
        Finished?.Invoke(this, JobState.Failed);
    }

    internal void MarkCancelled()
    {
        // Guarded like the others: "a job is always in exactly one state and always
        // ends in a terminal one" is a property of this class, not something every
        // caller has to keep true. Nothing reaches this today, because the manager
        // checks IsFinished under its lock first — which is exactly why the guard
        // belongs here rather than in the manager's future call sites.
        if (IsFinished)
        {
            return;
        }

        FinishedAt = DateTimeOffset.UtcNow;
        State = JobState.Cancelled;
        _completion.TrySetResult();
        Finished?.Invoke(this, JobState.Cancelled);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => $"{Kind}/{Id}: {State} {Progress}";
}
