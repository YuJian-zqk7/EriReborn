using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Core.Jobs;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Every download the interface starts has to become a job that walks the one path — resolver →
/// route → engine → job — and reports the downloader's own numbers. These drive that service
/// directly, and check that the page itself owns none of it.
/// </summary>
public sealed class DownloadJobTests
{
    private static readonly byte[] Payload =
    {
        (byte)'M', (byte)'Z', 1, 2, 3, 4, 5, 6, 7, 8,
    };

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private sealed class StubResolver(ResolveOutcome outcome, bool canResolve = true) : IDownloadResolver
    {
        public string Id => "stub-resolver";

        public bool CanResolve(SoftwareSource source) => canResolve;

        public Task<ResolveOutcome> ResolveAsync(SoftwareSource source, CancellationToken cancellationToken = default)
            => Task.FromResult(outcome);
    }

    private sealed class StubEngine : IDownloadEngine
    {
        public string Id => "stub-engine";

        public string DisplayName => "测试下载引擎";

        public bool CanHandle(DownloadRoute route) => true;

        public async Task<DownloadResult> DownloadAsync(
            DownloadRoute route,
            DownloadRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, Payload, cancellationToken);

            // A real transfer reports as it goes; the last report is the finished one.
            progress?.Report(new DownloadProgress(Payload.Length / 2, Payload.Length, 1024, TimeSpan.FromSeconds(4)));
            progress?.Report(new DownloadProgress(Payload.Length, Payload.Length, 2048, TimeSpan.Zero));

            return new DownloadResult(
                DownloadState.Success,
                request.DestinationPath,
                Payload.Length,
                Sha256: request.ExpectedSha256,
                HttpStatusCode: 200);
        }
    }

    private static DownloadJobService Service(
        IDownloadResolver? resolver,
        IDownloadEngine? engine,
        out JobManager jobs)
    {
        jobs = new JobManager(maxConcurrent: 2, AppLog.For("Test"));

        var resolvers = new DownloadResolverRegistry(
            resolver is null ? Array.Empty<IDownloadResolver>() : new[] { resolver },
            AppLog.For("Test"));

        var engines = new DownloadEngineSelector(
            engine is null ? Array.Empty<IDownloadEngine>() : new[] { engine },
            AppLog.For("Test"));

        return new DownloadJobService(resolvers, engines, jobs, AppLog.For("Test"));
    }

    private static SoftwareSource Source(string? sha256 = null) => new()
    {
        Kind = SourceKind.CloudShare,
        ProviderId = "stub",
        ShareUrl = "https://share.example.invalid/s/abc",
        FileName = "Tool.zip",
        Sha256 = sha256,
    };

    private static DownloadRoute Route(string url = "https://cdn.example.invalid/Tool.zip")
        => new(DownloadRouteKind.Resolver, url, ProviderId: "stub", FileName: "Tool.zip");

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task A_download_job_runs_the_engine_and_reports_the_real_transfer()
    {
        var destination = TempDirectory();
        try
        {
            var service = Service(
                new StubResolver(ResolveOutcome.Ok(Route(), "已解析")),
                new StubEngine(),
                out var jobs);

            var job = service.Start(new DownloadJobRequest(Source(sha256: "ABC"), destination));
            await job.Completion;

            Assert.Equal(JobState.Completed, job.State);

            var landed = Path.Combine(destination, "Tool.zip");
            Assert.True(File.Exists(landed));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(landed));

            // The speed and the check result are the downloader's own, not a second estimate.
            Assert.NotNull(job.Progress.Telemetry);
            Assert.Equal(2048, job.Progress.Telemetry!.BytesPerSecond);
            Assert.True(job.Progress.Fraction is > 0.99);
            Assert.Contains("SHA-256", job.Progress.Verdict, StringComparison.Ordinal);

            Assert.Single(jobs.Jobs);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [Fact]
    public async Task A_job_started_from_an_already_obtained_route_still_travels_the_engine()
    {
        var destination = TempDirectory();
        try
        {
            // A file in the user's own drive has no share link; its route is obtained directly, but
            // the bytes must still go through the engine and the job.
            var service = Service(null, new StubEngine(), out _);

            var job = service.StartFromRoute(Route(), new DownloadJobRequest(Source(), destination));
            await job.Completion;

            Assert.Equal(JobState.Completed, job.State);
            Assert.True(File.Exists(Path.Combine(destination, "Tool.zip")));
        }
        finally
        {
            Cleanup(destination);
        }
    }

    // ------------------------------------------------------------------ the failures

    [Fact]
    public async Task A_source_that_cannot_be_resolved_fails_with_the_resolver_s_words()
    {
        var destination = TempDirectory();
        try
        {
            var service = Service(
                new StubResolver(ResolveOutcome.Fail(ResolveStatus.AuthRequired, "需要先登录这个网盘", CloudErrorKind.AuthRequired)),
                new StubEngine(),
                out _);

            var job = service.Start(new DownloadJobRequest(Source(), destination));
            await job.Completion;

            Assert.Equal(JobState.Failed, job.State);
            Assert.Contains("需要先登录", job.Error);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [Fact]
    public async Task A_source_with_no_resolver_at_all_is_reported_as_a_gap()
    {
        var destination = TempDirectory();
        try
        {
            var service = Service(null, new StubEngine(), out _);

            var job = service.Start(new DownloadJobRequest(Source(), destination));
            await job.Completion;

            Assert.Equal(JobState.Failed, job.State);
            Assert.Contains("没有能处理", job.Error);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [Fact]
    public async Task A_route_with_no_engine_says_so_rather_than_claiming_success()
    {
        var destination = TempDirectory();
        try
        {
            var service = Service(new StubResolver(ResolveOutcome.Ok(Route(), "已解析")), null, out _);

            var job = service.Start(new DownloadJobRequest(Source(), destination));
            await job.Completion;

            Assert.Equal(JobState.Failed, job.State);
            Assert.Contains("没有可用的下载引擎", job.Error);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    // -------------------------------------------------------------- the wording tests

    [Fact]
    public void Progress_wording_comes_from_the_transfer_numbers()
    {
        var text = DownloadJobService.DescribeProgress(
            new DownloadProgress(512, 1024, 1024, TimeSpan.FromSeconds(30)));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("/s", text);
        Assert.Contains("剩余", text);

        // Nothing to say when nothing has moved yet.
        Assert.Null(DownloadJobService.DescribeProgress(new DownloadProgress(0, null, 0, null)));
    }

    [Fact]
    public void A_hash_mismatch_is_explained_as_a_check_failure()
    {
        var explained = DownloadJobService.Explain(new DownloadResult(DownloadState.HashMismatch, "x", 0));

        Assert.Contains("校验失败", explained);
    }

    [Fact]
    public void A_source_without_an_expected_hash_says_the_check_was_not_made()
    {
        var verdict = DownloadJobService.Verdict(
            Source(),
            new DownloadResult(DownloadState.Success, "x", 1));

        Assert.Contains("未做哈希校验", verdict);
    }

    // ------------------------------------------------------------------ the invariant

    [Fact]
    public void The_sources_page_carries_no_download_of_its_own()
    {
        var file = Path.Combine(
            RepoRoot(), "src", "EriReborn.App.Shared", "ViewModels", "CloudViewModel.cs");

        var text = File.ReadAllText(file);

        // The page asks for a job; it must not open a connection or write a file itself. These are
        // exactly the calls the old private download used.
        Assert.DoesNotContain("GetAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Create", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyToAsync", text, StringComparison.Ordinal);
        Assert.Contains("DownloadJobs", text, StringComparison.Ordinal);
    }
}
