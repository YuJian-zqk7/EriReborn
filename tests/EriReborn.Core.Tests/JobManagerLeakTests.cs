using EriReborn.Core.Jobs;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A job cancelled before it starts must leave nothing behind.
///
/// <para>
/// Only the running path ever removed a job's work from the manager's table, so
/// every job cancelled while still queued kept its delegate — and whatever that
/// closure had captured — for as long as the manager lived. PendingWorkCount is
/// what makes that observable rather than a matter of reading the code carefully.
/// </para>
/// </summary>
public sealed class JobManagerLeakTests
{
    private static async Task<JobManager> Drained(JobManager manager, params Job[] jobs)
    {
        foreach (var job in jobs)
        {
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // The manager's own bookkeeping settles just after a job's completion is
        // signalled, so give it a moment rather than racing it.
        for (var attempt = 0; attempt < 100 && manager.PendingWorkCount > 0; attempt++)
        {
            await Task.Delay(10);
        }

        return manager;
    }

    [Fact]
    public async Task Work_cancelled_before_it_starts_is_released()
    {
        var manager = new JobManager(maxConcurrent: 1);
        var gate = new TaskCompletionSource();
        var ran = false;

        var running = manager.Start("test", "running", async _ =>
        {
            await gate.Task;
        });

        var queued = manager.Start("test", "queued", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        // Cancelled while queued: it must never start.
        Assert.True(manager.Cancel(queued.Id));

        gate.SetResult();
        await Drained(manager, running, queued);

        Assert.False(ran, "排队中被取消的任务不应该开始执行。");
        Assert.Equal(JobState.Cancelled, queued.State);

        // The work table is empty again: the cancelled job's delegate is gone.
        Assert.Equal(0, manager.PendingWorkCount);
    }

    [Fact]
    public async Task Nothing_accumulates_when_many_queued_jobs_are_cancelled()
    {
        var manager = new JobManager(maxConcurrent: 1);
        var gate = new TaskCompletionSource();

        var running = manager.Start("test", "running", async _ => await gate.Task);

        var queued = new List<Job>();
        for (var index = 0; index < 25; index++)
        {
            queued.Add(manager.Start("test", $"queued-{index}", _ => Task.CompletedTask));
        }

        foreach (var job in queued)
        {
            Assert.True(manager.Cancel(job.Id));
        }

        gate.SetResult();
        await Drained(manager, running);
        await Drained(manager, queued.ToArray());

        Assert.All(queued, job => Assert.Equal(JobState.Cancelled, job.State));

        // Before the fix this grew with every cancelled job and never came down.
        Assert.Equal(0, manager.PendingWorkCount);
    }

    [Fact]
    public async Task Work_that_runs_to_completion_is_released_too()
    {
        var manager = new JobManager();

        var job = manager.Start("test", "fine", _ => Task.CompletedTask);
        await Drained(manager, job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(0, manager.PendingWorkCount);
        Assert.Equal(0, manager.RunningCount);
    }

    [Fact]
    public async Task Work_that_fails_is_released()
    {
        var manager = new JobManager();

        var job = manager.Start("test", "boom", _ => throw new InvalidOperationException("failure"));
        await Drained(manager, job);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("failure", job.Error);
        Assert.Equal(0, manager.PendingWorkCount);
    }

    [Fact]
    public async Task Cancelling_a_running_job_releases_it()
    {
        var manager = new JobManager();

        var job = manager.Start("test", "stoppable", async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken);
        });

        // Give it a moment to actually start.
        for (var attempt = 0; attempt < 100 && job.State != JobState.Running; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(manager.Cancel(job.Id));
        await Drained(manager, job);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal(0, manager.PendingWorkCount);
        Assert.Equal(0, manager.RunningCount);
    }

    [Fact]
    public async Task A_job_that_finished_cannot_be_cancelled_afterwards()
    {
        var manager = new JobManager();

        var job = manager.Start("test", "done", _ => Task.CompletedTask);
        await Drained(manager, job);

        // Cancelling something already finished is not success, and saying so is what
        // stops a caller from reporting a cancel that never happened.
        Assert.False(manager.Cancel(job.Id));
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public void Cancelling_an_unknown_job_is_false_not_an_error()
    {
        var manager = new JobManager();

        Assert.False(manager.Cancel("job-does-not-exist"));
    }

    [Fact]
    public async Task A_cancelled_queued_job_does_not_hold_up_the_one_behind_it()
    {
        var manager = new JobManager(maxConcurrent: 1);
        var gate = new TaskCompletionSource();
        var secondRan = false;

        var running = manager.Start("test", "running", async _ => await gate.Task);
        var cancelled = manager.Start("test", "cancelled", _ => Task.CompletedTask);
        var second = manager.Start("test", "second", _ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        Assert.True(manager.Cancel(cancelled.Id));

        gate.SetResult();
        await Drained(manager, running, cancelled, second);

        Assert.True(secondRan, "被取消的任务不应该阻塞排在它后面的任务。");
        Assert.Equal(JobState.Completed, second.State);
        Assert.Equal(0, manager.PendingWorkCount);
    }
}
