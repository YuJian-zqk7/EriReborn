using EriReborn.Core.Domain;
using EriReborn.Core.Jobs;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Download;

/// <summary>What to download, where to put it, and what to call the job that does it.</summary>
public sealed record DownloadJobRequest(
    SoftwareSource Source,
    string DestinationDirectory,
    string? FileNameHint = null,
    string? SoftwareId = null,
    string? Version = null,
    string? Title = null);

/// <summary>
/// Turns "download this resource" into a job that really runs: resolve the source into a route, pick
/// the engine that can carry it, download, and report the transfer's own numbers as they arrive.
///
/// <para>
/// This is the single path every download started from the interface goes through. It exists so a
/// page can offer a download without carrying its own HTTP client, its own resume handling or its
/// own progress arithmetic: the page asks for a job, and the job is what the task list watches. The
/// numbers on screen are the downloader's, not a second estimate computed beside it.
/// </para>
/// </summary>
public sealed class DownloadJobService(
    DownloadResolverRegistry resolvers,
    DownloadEngineSelector engines,
    JobManager jobs,
    IAppLogger log)
{
    /// <summary>Starts a download job for a resource that has a source the resolver registry knows.</summary>
    public Job Start(DownloadJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var title = request.Title ?? "下载 " + DescribeItem(request.Source, request.FileNameHint);
        return jobs.Start("download", title, ctx => RunResolvedAsync(request, ctx));
    }

    /// <summary>
    /// Starts a job from a route that was already obtained, for a case the resolver registry cannot
    /// name — a file in the user's own drive, which has no share link. The bytes still travel the
    /// same route → engine → job path; only where the route came from is different.
    /// </summary>
    public Job StartFromRoute(DownloadRoute route, DownloadJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);

        var title = request.Title ?? "下载 " + DescribeItem(request.Source, request.FileNameHint);
        return jobs.Start("download", title, ctx => RunRouteAsync(route, request, ctx));
    }

    private async Task RunResolvedAsync(DownloadJobRequest request, JobContext context)
    {
        context.Report("正在解析来源…");

        var outcome = await resolvers.ResolveAsync(request.Source, context.CancellationToken).ConfigureAwait(false);
        if (!outcome.IsResolved || outcome.Route is null)
        {
            // A friendly message, because this becomes the job's failure text in the task list.
            throw new InvalidOperationException(DescribeFailure(outcome));
        }

        await RunRouteAsync(outcome.Route, request, context).ConfigureAwait(false);
    }

    private async Task RunRouteAsync(DownloadRoute route, DownloadJobRequest request, JobContext context)
    {
        var source = request.Source;
        var fileName = FirstMeaningful(
                route.FileName,
                request.FileNameHint,
                source.Locator?.ItemName,
                source.FileName)
            ?? "download";

        Directory.CreateDirectory(request.DestinationDirectory);
        var target = Path.Combine(request.DestinationDirectory, Sanitize(fileName));

        var engine = engines.Select(route);
        if (engine is null)
        {
            throw new InvalidOperationException("没有可用的下载引擎，无法下载。");
        }

        var downloadRequest = new DownloadRequest
        {
            Url = route.Url,
            DestinationPath = target,
            ExpectedSha256 = source.Sha256,
            ExpectedSize = source.SizeBytes,
            SoftwareId = request.SoftwareId,
            Version = FirstMeaningful(request.Version, source.Version),
            Architecture = source.Architecture,
            ProviderId = FirstMeaningful(source.ProviderId, route.ProviderId),
            FileName = fileName,
            Headers = route.BuildHeaders(),
        };

        context.Report($"开始下载 {fileName}…");
        var relay = new Relay(context);
        var result = await engine
            .DownloadAsync(route, downloadRequest, relay, context.CancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(Explain(result));
        }

        var verdict = Verdict(source, result);

        // The final line keeps the speed the transfer last measured, so a finished download still
        // shows how fast it went rather than a zero the screen has to explain away.
        var finished = new DownloadProgress(
            result.BytesWritten,
            result.BytesWritten,
            relay.Last?.BytesPerSecond ?? 0,
            TimeSpan.Zero);

        context.ReportDownload(finished, $"{FormatSize(result.BytesWritten)} 已下载完成", verdict);

        log.Info("download.job", $"'{fileName}' -> '{target}' ({verdict}).");
    }

    /// <summary>Keeps the transfer's own numbers and adds a human line beside them.</summary>
    private sealed class Relay(JobContext context) : IProgress<DownloadProgress>
    {
        /// <summary>The last report the downloader made, so the finishing line can reuse its speed.</summary>
        public DownloadProgress? Last { get; private set; }

        public void Report(DownloadProgress value)
        {
            Last = value;
            context.ReportDownload(value, DescribeProgress(value));
        }
    }

    /// <summary>The size, the speed and what is left, from the numbers the downloader reported.</summary>
    internal static string? DescribeProgress(DownloadProgress progress)
    {
        var parts = new List<string>(3);

        if (progress.TotalBytes is > 0)
        {
            parts.Add(FormatSize(progress.BytesReceived) + " / " + FormatSize(progress.TotalBytes.Value));
        }
        else if (progress.BytesReceived > 0)
        {
            parts.Add("已接收 " + FormatSize(progress.BytesReceived));
        }

        if (progress.BytesPerSecond > 0)
        {
            parts.Add(FormatSpeed(progress.BytesPerSecond));
        }

        if (progress.Eta is { } eta && eta > TimeSpan.Zero)
        {
            parts.Add("剩余 " + FormatEta(eta));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>What the checks said once the bytes were all in.</summary>
    internal static string Verdict(SoftwareSource source, DownloadResult result)
    {
        if (string.IsNullOrWhiteSpace(source.Sha256))
        {
            return "已下载（来源未声明 SHA-256，未做哈希校验）";
        }

        return string.IsNullOrWhiteSpace(result.Sha256)
            ? "已下载（校验未报告哈希）"
            : "SHA-256 校验通过";
    }

    /// <summary>Turns a resolution outcome into words a user can act on.</summary>
    internal static string DescribeFailure(ResolveOutcome outcome) => outcome.Status switch
    {
        ResolveStatus.AuthRequired => "需要先登录这个网盘：" + outcome.Message,
        ResolveStatus.NotADownload => outcome.Message,
        _ => outcome.Message,
    };

    /// <summary>Turns a download state into words a user can act on, with the technical part after.</summary>
    internal static string Explain(DownloadResult result)
    {
        var headline = result.State switch
        {
            DownloadState.HashMismatch => "文件校验失败（SHA-256 不匹配）。",
            DownloadState.SizeMismatch => "文件大小与声明的对不上。",
            DownloadState.EngineUnavailable => "下载引擎不可用。",
            DownloadState.NetworkError => "网络中断，下载失败。",
            DownloadState.Cancelled => "下载已取消。",
            DownloadState.HttpError when result.HttpStatusCode is { } code => $"服务器返回 HTTP {code}。",
            DownloadState.HttpError => "服务器拒绝了下载请求。",
            DownloadState.RangeRestartRequired => "服务器不支持续传，需要重新下载。",
            _ => "下载失败。",
        };

        return string.IsNullOrWhiteSpace(result.Message) ? headline : headline + " " + result.Message;
    }

    private static string DescribeItem(SoftwareSource source, string? hint)
    {
        var name = FirstMeaningful(hint, source.Locator?.ItemName, source.FileName, source.ShareUrl);
        return string.IsNullOrWhiteSpace(name) ? "资源" : name!;
    }

    private static string? FirstMeaningful(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim();

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30)
        {
            return (bytes / (double)(1L << 30)).ToString("0.#") + " GB";
        }

        if (bytes >= 1L << 20)
        {
            return (bytes / (double)(1L << 20)).ToString("0.#") + " MB";
        }

        return bytes >= 1024 ? (bytes / 1024.0).ToString("0.#") + " KB" : bytes + " B";
    }

    private static string FormatSpeed(double bytesPerSecond) => FormatSize((long)bytesPerSecond) + "/s";

    private static string FormatEta(TimeSpan eta)
        => eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours} 小时 {eta.Minutes} 分"
            : eta.TotalMinutes >= 1
                ? $"{(int)eta.TotalMinutes} 分 {eta.Seconds} 秒"
                : eta.Seconds + " 秒";

    /// <summary>
    /// Keeps a name usable as a file name. The name may come from a share or a server header, so it
    /// is not trusted to be legal on this machine.
    /// </summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "download" : cleaned;
    }
}
