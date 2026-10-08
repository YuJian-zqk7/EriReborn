using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Windows update pipeline, modelled separately from install (spec 23).
/// A failed update keeps the existing installation and reports why.
/// </summary>
public sealed class WindowsSoftwareUpdater(
    IProcessService processes,
    IFileSystemService files,
    DownloadEngineSelector engines,
    IAppLogger log) : ISoftwareUpdater
{
    private readonly IProcessService _processes = processes;
    private readonly IFileSystemService _files = files;
    private readonly DownloadEngineSelector _engines = engines;

    /// <summary>
    /// Downloads through whichever engine can carry the route. The install path
    /// never names an engine, so adding one does not change this code
    /// (spec 136/137).
    /// </summary>
    private async Task<DownloadResult> DownloadViaEngineAsync(
        DownloadRoute route,
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var engine = _engines.Select(route);
        if (engine is null)
        {
            // Better than silently trying an engine that cannot do it.
            return new DownloadResult(
                DownloadState.EngineUnavailable,
                request.DestinationPath,
                0,
                Message: $"没有能处理 {route.Kind} 路由的下载引擎。");
        }

        return await engine.DownloadAsync(route, request, progress, cancellationToken).ConfigureAwait(false);
    }
    private readonly IAppLogger _log = log;

    public async Task<UpdateResult> UpdateAsync(
        SoftwareDefinition software,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind == SourceKind.Winget)
        {
            if (string.IsNullOrWhiteSpace(source.WingetId))
            {
                return new UpdateResult(UpdateState.ValidationFailed, "winget 更新缺少包标识。");
            }

            if (!_processes.Exists("winget.exe"))
            {
                return new UpdateResult(UpdateState.SourceUnavailable, "本机未安装 winget-cli。");
            }

            var result = await _processes.RunAsync(
                new ProcessRequest(
                    "winget.exe",
                    new[]
                    {
                        "upgrade", "--id", source.WingetId, "--exact",
                        "--silent",
                        "--accept-package-agreements",
                        "--accept-source-agreements",
                        "--disable-interactivity",
                    },
                    Timeout: TimeSpan.FromMinutes(30)),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                return new UpdateResult(UpdateState.Updated, $"winget 已更新 {source.WingetId}。", ToVersion: software.Version);
            }

            // 0x8A15002B: no applicable update found.
            if (result.ExitCode == unchecked((int)0x8A15002B))
            {
                return new UpdateResult(UpdateState.UpToDate, "winget 报告当前已是最新版本。");
            }

            return new UpdateResult(UpdateState.InstallerFailed, $"winget upgrade 退出码 {result.ExitCode}。");
        }

        if (source.Kind is SourceKind.HttpUrl or SourceKind.Official)
        {
            if (string.IsNullOrWhiteSpace(source.Url))
            {
                return new UpdateResult(UpdateState.SourceUnavailable, "该更新来源缺少下载地址。");
            }

            var fileName = source.FileName ?? "update.bin";
            var destination = Path.Combine(_files.GetTempDirectory(), "updates", fileName);
            var route = new DownloadRoute(
                DownloadRouteKind.ProviderDirect,
                source.Url,
                FileName: fileName,
                ContentLength: source.SizeBytes);

            var download = await DownloadViaEngineAsync(
                route,
                new DownloadRequest
                {
                    Url = source.Url,
                    DestinationPath = destination,
                    ExpectedSha256 = source.Sha256,
                    ExpectedSize = source.SizeBytes,
                    SoftwareId = software.Id,
                    Version = source.Version ?? software.Version,
                    Architecture = source.Architecture ?? software.Architecture,
                },
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!download.IsSuccess)
            {
                var state = download.State switch
                {
                    DownloadState.HashMismatch or DownloadState.SizeMismatch => UpdateState.IntegrityFailed,
                    DownloadState.HttpError => UpdateState.SourceUnavailable,
                    _ => UpdateState.DownloadFailed,
                };
                return new UpdateResult(state, download.Message ?? "更新包下载失败。");
            }

            _log.Info("update.downloaded", $"Update package for '{software.Id}' is at '{download.Path}'.");

            // Running the package is deliberately delegated to the installer path
            // so both flows share one implementation.
            return new UpdateResult(
                UpdateState.UpdateAvailable,
                "更新包已下载并校验；请通过安装流程执行更新。",
                ToVersion: source.Version ?? software.Version);
        }

        return new UpdateResult(UpdateState.Unsupported, $"不支持的更新来源类型 {source.Kind}。");
    }
}
