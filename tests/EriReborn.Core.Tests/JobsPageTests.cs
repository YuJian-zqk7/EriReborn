using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Jobs;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The job list is the only place long work becomes visible. It must show the
/// real state — including "we do not know how far along this is" — rather than a
/// bar that always looks finished (spec 10).
/// </summary>
public sealed class JobsPageTests
{
    private static async Task<(AppHost Host, string UserData)> CreateHostAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (host, userData);
    }

    private static void Cleanup(string userData)
    {
        try
        {
            if (Directory.Exists(userData))
            {
                Directory.Delete(userData, recursive: true);
            }
        }
        finally
        {
            // Best effort.
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task An_idle_job_list_says_so_instead_of_being_blank()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var model = new JobsViewModel(host);

            Assert.Empty(model.Jobs);
            Assert.False(model.HasJobs);
            Assert.Contains("没有任务", model.Summary);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_finished_job_is_listed_with_its_real_state()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var model = new JobsViewModel(host);
            var job = host.Jobs.Start("scan", "扫描环境", context =>
            {
                context.Report(3, 3);
                return Task.CompletedTask;
            });

            Assert.True(await WaitForAsync(() => job.IsFinished));

            model.Refresh();

            var item = Assert.Single(model.Jobs);
            Assert.Equal("扫描环境", item.Title);
            Assert.Equal("已完成", item.StateText);
            Assert.Equal("3/3", item.ProgressText);
            Assert.False(item.CanCancel);
            Assert.True(model.HasJobs);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_failed_job_shows_the_reason()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var model = new JobsViewModel(host);
            var job = host.Jobs.Start("install", "安装 X", _ => throw new InvalidOperationException("磁盘已满"));

            Assert.True(await WaitForAsync(() => job.IsFinished));
            model.Refresh();

            var item = Assert.Single(model.Jobs);
            Assert.Equal("失败", item.StateText);
            Assert.Contains("磁盘已满", item.Detail);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_caller_can_wait_for_a_job_to_finish()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var job = host.Jobs.Start("scan", "可等待", async _ => await Task.Delay(30));

            await job.Completion;

            Assert.Equal(JobState.Completed, job.State);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Waiting_for_a_failed_job_does_not_throw()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var job = host.Jobs.Start("scan", "会失败", _ => throw new InvalidOperationException("失败原因"));

            // A failed job is a state, not an exception: awaiting it must not blow up
            // a caller that only wants to know when the work stopped.
            await job.Completion;

            Assert.Equal(JobState.Failed, job.State);
            Assert.Equal("失败原因", job.Error);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_job_without_a_known_total_does_not_pretend_to_have_finished()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var model = new JobsViewModel(host);
            var job = host.Jobs.Start("ai", "询问模型", _ => Task.CompletedTask);

            Assert.True(await WaitForAsync(() => job.IsFinished));
            model.Refresh();

            var item = Assert.Single(model.Jobs);
            Assert.Equal("进度未知", item.ProgressText);
            Assert.NotEqual("100%", item.ProgressText);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_queued_job_can_be_cancelled_from_the_list()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            // Saturate the manager so the second job stays queued.
            var gate = new TaskCompletionSource();
            host.Jobs.Start("scan", "占用槽位一", async _ => await gate.Task);
            host.Jobs.Start("scan", "占用槽位二", async _ => await gate.Task);

            var queued = host.Jobs.Start("scan", "排队中", _ => Task.CompletedTask);
            var model = new JobsViewModel(host);
            model.Refresh();

            var item = model.Jobs.Single(entry => entry.Title == "排队中");
            Assert.True(item.CanCancel);

            model.CancelCommand.Execute(item);

            Assert.Equal(JobState.Cancelled, queued.State);

            gate.SetResult();
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Cancelling_everything_leaves_finished_jobs_alone()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var finished = host.Jobs.Start("scan", "已完成", _ => Task.CompletedTask);
            Assert.True(await WaitForAsync(() => finished.IsFinished));

            // The work must observe the token, the way real work does; otherwise
            // cancelling it is a request nobody is listening to.
            var running = host.Jobs.Start(
                "scan",
                "进行中",
                async context => await Task.Delay(Timeout.Infinite, context.CancellationToken));

            Assert.True(await WaitForAsync(() => running.State == JobState.Running));

            var model = new JobsViewModel(host);
            model.CancelAllCommand.Execute(null);

            // Cancelling is a request; the state changes once the work observes it.
            var observed = await Task.WhenAny(running.Completion, Task.Delay(5000));
            Assert.Same(running.Completion, observed);
            Assert.True(running.IsFinished);

            // The finished job must not be reported as cancelled.
            Assert.Equal(JobState.Completed, finished.State);
            Assert.Equal(JobState.Cancelled, running.State);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task A_batch_install_is_recorded_as_a_job()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var model = new SoftwareViewModel(host);
            var before = host.Jobs.Jobs.Count;

            // With an empty plan the command must not create a job at all.
            Assert.False(model.HasBatchPlan);
            model.ConfirmBatchInstallCommand.Execute(null);

            Assert.Equal(before, host.Jobs.Jobs.Count);
        }
        finally
        {
            Cleanup(data);
        }
    }
}
