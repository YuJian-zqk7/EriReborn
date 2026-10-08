using EriReborn.Core.Jobs;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Long work must be observable, cancellable and always reach a terminal state.
/// A job that can end without anyone being able to tell is how "it just stopped"
/// bug reports are born (spec 10).
/// </summary>
public sealed class JobManagerTests
{
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }

    [Fact]
    public async Task A_job_runs_and_completes()
    {
        var manager = new JobManager();
        var job = manager.Start("scan", "扫描环境", context =>
        {
            context.Report(1, 3);
            context.Report(3, 3);
            return Task.CompletedTask;
        });

        Assert.True(await WaitForAsync(() => job.IsFinished));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(3, job.Progress.Completed);
        Assert.Null(job.Error);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task A_job_that_fails_says_why()
    {
        var manager = new JobManager();
        var job = manager.Start("install", "安装 X", _ => throw new InvalidOperationException("磁盘已满"));

        Assert.True(await WaitForAsync(() => job.IsFinished));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("磁盘已满", job.Error);
    }

    [Fact]
    public async Task Progress_never_runs_backwards()
    {
        var manager = new JobManager();
        var job = manager.Start("download", "下载", context =>
        {
            context.Report(5, 10);
            // A resumed download that re-reports from the beginning must not
            // rewind the bar the user is watching.
            context.Report(2, 10);
            return Task.CompletedTask;
        });

        Assert.True(await WaitForAsync(() => job.IsFinished));

        Assert.Equal(5, job.Progress.Completed);
    }

    [Fact]
    public void A_job_that_cannot_know_its_size_is_indeterminate_not_complete()
    {
        var progress = new JobProgress(0, 0);

        // Reporting 100% for unknown work is a lie the UI would render as a
        // finished bar.
        Assert.Null(progress.Fraction);
        Assert.Null(JobProgress.None.Fraction);
        Assert.Equal(0.5, new JobProgress(1, 2).Fraction);
    }

    [Fact]
    public async Task A_running_job_can_be_stopped()
    {
        var manager = new JobManager();
        var started = new TaskCompletionSource();
        var sawCancellation = false;

        var job = manager.Start("download", "下载大文件", async context =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sawCancellation = true;
                throw;
            }
        });

        await started.Task;
        Assert.True(manager.Cancel(job.Id));
        Assert.True(await WaitForAsync(() => job.IsFinished));

        Assert.True(sawCancellation, "任务没有观察到取消令牌。");
        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task A_queued_job_can_be_cancelled_before_it_ever_starts()
    {
        var manager = new JobManager(maxConcurrent: 1);
        var gate = new TaskCompletionSource();
        var secondRan = false;

        var first = manager.Start("scan", "占用唯一的槽位", async _ =>
        {
            await gate.Task.ConfigureAwait(false);
        });

        var second = manager.Start("scan", "排队中", _ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        Assert.True(await WaitForAsync(() => first.State == JobState.Running));
        Assert.True(manager.Cancel(second.Id));

        gate.SetResult();
        Assert.True(await WaitForAsync(() => first.IsFinished && second.IsFinished));

        Assert.False(secondRan, "排队时被取消的任务不应该再启动。");
        Assert.Equal(JobState.Cancelled, second.State);
    }

    [Fact]
    public async Task Only_the_allowed_number_of_jobs_run_at_once()
    {
        var manager = new JobManager(maxConcurrent: 2);
        var gate = new TaskCompletionSource();

        var jobs = new[]
        {
            manager.Start("scan", "一", async _ => await gate.Task.ConfigureAwait(false)),
            manager.Start("scan", "二", async _ => await gate.Task.ConfigureAwait(false)),
            manager.Start("scan", "三", async _ => await gate.Task.ConfigureAwait(false)),
        };

        Assert.True(await WaitForAsync(() => manager.RunningCount == 2));
        Assert.Equal(JobState.Queued, jobs[2].State);

        gate.SetResult();
        Assert.True(await WaitForAsync(() => jobs.All(job => job.IsFinished)));
        Assert.Equal(JobState.Completed, jobs[2].State);
    }

    [Fact]
    public async Task Queued_jobs_start_in_the_order_they_were_added()
    {
        var manager = new JobManager(maxConcurrent: 1);
        var order = new List<string>();
        var gate = new TaskCompletionSource();

        manager.Start("scan", "一", async _ =>
        {
            order.Add("一");
            await gate.Task.ConfigureAwait(false);
        });

        manager.Start("scan", "二", _ => { order.Add("二"); return Task.CompletedTask; });
        manager.Start("scan", "三", _ => { order.Add("三"); return Task.CompletedTask; });

        Assert.True(await WaitForAsync(() => order.Count == 1));
        gate.SetResult();

        Assert.True(await WaitForAsync(() => order.Count == 3));
        Assert.Equal(new[] { "一", "二", "三" }, order);
    }

    [Fact]
    public async Task Cancelling_an_unknown_or_finished_job_is_reported_as_not_done()
    {
        var manager = new JobManager();

        Assert.False(manager.Cancel("job-9999"));

        var job = manager.Start("scan", "快任务", _ => Task.CompletedTask);
        Assert.True(await WaitForAsync(() => job.IsFinished));

        // Already finished: there was nothing to cancel.
        Assert.False(manager.Cancel(job.Id));
        Assert.Equal(JobState.Completed, job.State);
    }

    [Fact]
    public async Task Every_state_change_is_announced()
    {
        var manager = new JobManager();
        var seen = new List<JobState>();
        manager.Changed += (_, job) =>
        {
            lock (seen)
            {
                seen.Add(job.State);
            }
        };

        var job = manager.Start("scan", "通告", _ => Task.CompletedTask);
        Assert.True(await WaitForAsync(() => job.IsFinished));

        lock (seen)
        {
            Assert.Contains(JobState.Queued, seen);
            Assert.Contains(JobState.Running, seen);
            Assert.Contains(JobState.Completed, seen);
        }
    }

    [Fact]
    public async Task A_finished_job_frees_its_slot()
    {
        var manager = new JobManager(maxConcurrent: 1);
        var gate = new TaskCompletionSource();

        var first = manager.Start("scan", "一", async _ => await gate.Task.ConfigureAwait(false));
        var second = manager.Start("scan", "二", _ => Task.CompletedTask);

        Assert.True(await WaitForAsync(() => first.State == JobState.Running));
        gate.SetResult();

        // Without a release, the second job would wait forever.
        Assert.True(await WaitForAsync(() => second.IsFinished));
        Assert.Equal(0, manager.RunningCount);
    }

    [Fact]
    public async Task Jobs_are_listed_and_can_be_found()
    {
        var manager = new JobManager();
        var job = manager.Start("scan", "可查找", _ => Task.CompletedTask);

        Assert.Single(manager.Jobs);
        Assert.Same(job, manager.Find(job.Id));
        Assert.Null(manager.Find("job-9999"));

        Assert.True(await WaitForAsync(() => manager.AllFinished));
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public void An_impossible_concurrency_limit_is_refused()
    {
        // 0 would mean nothing ever runs, which is a silent hang rather than a limit.
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobManager(maxConcurrent: 0));
    }

    [Fact]
    public async Task A_job_that_reports_a_message_without_a_total_keeps_the_message()
    {
        var manager = new JobManager();
        var job = manager.Start("ai", "询问模型", context =>
        {
            context.Report("正在等待响应…");
            return Task.CompletedTask;
        });

        Assert.True(await WaitForAsync(() => job.IsFinished));

        Assert.Equal("正在等待响应…", job.Progress.Message);
        Assert.Null(job.Progress.Fraction);
    }
}
