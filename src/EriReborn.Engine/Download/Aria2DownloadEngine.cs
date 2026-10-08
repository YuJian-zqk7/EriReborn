using System.Security.Cryptography;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Engine.Download;

/// <summary>
/// Downloads through the aria2 command-line client.
///
/// It exists to prove the engine abstraction is real: this engine reaches the
/// same file by a completely different route, and the software engine neither
/// knows nor cares which one ran (spec 61/136/137).
///
/// aria2 must actually be installed. When it is not, this engine declines the
/// route rather than pretending — the selector then falls back to the built-in
/// downloader.
/// </summary>
public sealed class Aria2DownloadEngine(
    IProcessService processes,
    Aria2Locator locator,
    IAppLogger log) : IDownloadEngine
{
    /// <summary>Explicit requests only, because silently exporting state is worse than re-reading a path.</summary>
    private readonly Aria2Locator _locator = locator;

    private readonly IProcessService _processes = processes;

    public string Id => "aria2";

    public string DisplayName => "aria2";

    public bool CanHandle(DownloadRoute route)
    {
        if (string.IsNullOrWhiteSpace(route.Url) || !_processes.IsSupported)
        {
            return false;
        }

        // A route that needs a browser session belongs to an engine that has one.
        if (route.Kind == DownloadRouteKind.BrowserAssisted && route.RequiresSession)
        {
            return false;
        }

        return _locator.Find() is not null;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRoute route,
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var executable = _locator.Find();
        if (executable is null)
        {
            // Said plainly: the caller can then use another engine instead of
            // being told the download failed.
            log.Warn("download.aria2", "aria2 未安装，无法使用该引擎。");
            return new DownloadResult(
                DownloadState.EngineUnavailable,
                request.DestinationPath,
                0,
                Message: "aria2 未安装。");
        }

        var directory = Path.GetDirectoryName(request.DestinationPath);
        var fileName = ResolveFileName(route, request);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return new DownloadResult(
                DownloadState.NetworkError,
                request.DestinationPath,
                0,
                Message: "无法确定下载目录或文件名。");
        }

        // Absolute, because aria2 is a child process with this directory as its working directory:
        // handing it a relative --dir makes it resolve the path a second time against that working
        // directory, and the file lands one level deeper than the path this engine then checks.
        directory = Path.GetFullPath(directory);

        Directory.CreateDirectory(directory);

        var arguments = BuildArguments(route, request, directory, fileName);
        log.Info("download.aria2", $"aria2c {string.Join(' ', arguments)}");

        var result = await _processes
            .RunAsync(new ProcessRequest(executable, arguments, directory, CaptureOutput: true), cancellationToken)
            .ConfigureAwait(false);

        var produced = Path.Combine(directory, fileName);

        if (!File.Exists(produced))
        {
            return new DownloadResult(
                DownloadState.NetworkError,
                produced,
                0,
                HttpStatusCode: null,
                Message: $"aria2 退出码 {result.ExitCode}，未生成文件。{Excerpt(result.StandardError)}");
        }

        var size = new FileInfo(produced).Length;

        if (request.ExpectedSize is { } expected && expected > 0 && size != expected)
        {
            return new DownloadResult(
                DownloadState.SizeMismatch,
                produced,
                size,
                Message: $"期望 {expected} 字节，实际 {size} 字节。");
        }

        string? sha = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            // Verified on the finished file, exactly as the built-in engine does.
            sha = await ComputeSha256Async(produced, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sha, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Error("download.aria2", $"aria2 产物 SHA-256 不符：{sha} != {request.ExpectedSha256}。");
                return new DownloadResult(
                    DownloadState.HashMismatch,
                    produced,
                    size,
                    sha,
                    Message: "SHA-256 与期望不符。");
            }
        }

        progress?.Report(new DownloadProgress(size, size, 0, TimeSpan.Zero));
        return new DownloadResult(DownloadState.Success, produced, size, sha, null, false, "aria2");
    }

    /// <summary>
    /// The aria2 command line.
    ///
    /// Every flag here is deliberate: overwrite and auto-rename are disabled so
    /// the file lands where the caller expects, and file allocation is skipped
    /// because pre-allocating a large file shows progress that has not happened.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        DownloadRoute route,
        DownloadRequest request,
        string directory,
        string fileName)
    {
        var arguments = new List<string>
        {
            "--dir", directory,
            "--out", fileName,
            "--allow-overwrite=true",
            "--auto-file-renaming=false",
            "--file-allocation=none",
            "--console-log-level=warn",
            "--summary-interval=0",
            "--continue=true",
        };

        // The route's headers travel as aria2 headers, so a platform that needs a
        // referer gets one whichever engine runs.
        foreach (var (name, value) in route.BuildHeaders())
        {
            arguments.Add("--header");
            arguments.Add($"{name}: {value}");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            arguments.Add("--checksum");
            arguments.Add($"sha-256={request.ExpectedSha256!.ToLowerInvariant()}");
        }

        arguments.Add(route.Url);
        return arguments;
    }

    /// <summary>The name the file should end up with, from the request or the URL.</summary>
    public static string? ResolveFileName(DownloadRoute route, DownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FileName))
        {
            return request.FileName;
        }

        if (!string.IsNullOrWhiteSpace(route.FileName))
        {
            return route.FileName;
        }

        if (Uri.TryCreate(route.Url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private static string Excerpt(string text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim().Split('\n')[0];

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
