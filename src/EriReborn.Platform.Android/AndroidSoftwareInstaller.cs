using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Cloud;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android install pipeline (spec 19). Android forbids silent APK installs by
/// design, so the honest flow is: download and verify the package, hand it to
/// the system installer, and let the user confirm. The result is reported as
/// <see cref="InstallState.AwaitingUserConfirmation"/> — never as success.
/// </summary>
public sealed class AndroidSoftwareInstaller(
    IFileSystemService files,
    HttpDownloader downloader,
    CloudShareLinkResolver cloudResolver,
    IAppLogger log) : ISoftwareInstaller
{
    private readonly IFileSystemService _files = files;
    private readonly HttpDownloader _downloader = downloader;
    private readonly CloudShareLinkResolver _cloudResolver = cloudResolver;
    private readonly IAppLogger _log = log;

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = request.Source;

        switch (source.Kind)
        {
            case SourceKind.Winget:
                return new InstallResult(InstallState.Unsupported, "winget 是 Windows 包管理器，Android 不支持。");

            case SourceKind.Manual:
                return new InstallResult(InstallState.Unsupported, "该条目声明为手动安装。");

            case SourceKind.Local:
                return HandToSystemInstaller(request.Software, source.FileName, request.TargetDirectory);

            case SourceKind.HttpUrl:
            case SourceKind.Official:
            {
                if (string.IsNullOrWhiteSpace(source.Url))
                {
                    return new InstallResult(InstallState.SourceUnavailable, "该来源缺少下载地址。");
                }

                var fileName = source.FileName ?? "package.apk";
                var destination = Path.Combine(_files.GetTempDirectory(), "downloads", fileName);
                var download = await _downloader.DownloadAsync(
                    new DownloadRequest
                    {
                        Url = source.Url,
                        DestinationPath = destination,
                        ExpectedSha256 = source.Sha256,
                        ExpectedSize = source.SizeBytes,
                        SoftwareId = request.Software.Id,
                        Version = source.Version ?? request.Software.Version,
                        Architecture = source.Architecture ?? request.Software.Architecture,
                        FileName = fileName,
                    },
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (!download.IsSuccess)
                {
                    var state = download.State switch
                    {
                        DownloadState.HashMismatch or DownloadState.SizeMismatch => InstallState.IntegrityFailed,
                        DownloadState.HttpError => InstallState.SourceUnavailable,
                        DownloadState.Cancelled => InstallState.Cancelled,
                        _ => InstallState.DownloadFailed,
                    };
                    return new InstallResult(state, download.Message ?? "下载失败。");
                }

                return HandToSystemInstaller(request.Software, download.Path, request.TargetDirectory);
            }

            case SourceKind.CloudShare:
            {
                var fileName = source.FileName ?? "package.apk";
                var destination = Path.Combine(_files.GetTempDirectory(), "downloads", fileName);
                var outcome = await _cloudResolver
                    .DownloadAsync(request.Software, source, destination, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!outcome.IsSuccess)
                {
                    var state = outcome.Outcome switch
                    {
                        CloudOutcome.AuthRequired => InstallState.Unsupported,
                        CloudOutcome.HashMismatch => InstallState.IntegrityFailed,
                        CloudOutcome.NotFound => InstallState.SourceUnavailable,
                        CloudOutcome.Cancelled => InstallState.Cancelled,
                        _ => InstallState.DownloadFailed,
                    };
                    return new InstallResult(state, outcome.Message);
                }

                return HandToSystemInstaller(request.Software, outcome.Path, request.TargetDirectory);
            }

            default:
                return new InstallResult(InstallState.Unsupported, $"不支持的来源类型 {source.Kind}。");
        }
    }

    private InstallResult HandToSystemInstaller(SoftwareDefinition software, string? packagePath, string? targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !_files.FileExists(packagePath))
        {
            return new InstallResult(InstallState.SourceUnavailable, "安装包文件不存在。");
        }

        try
        {
            var context = global::Android.App.Application.Context;
            var file = new Java.IO.File(packagePath!);
            var authority = $"{context.PackageName}.fileprovider";
            var uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, file);

            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);

            _log.Info("install.handoff", $"Handed '{packagePath}' to the system package installer.");

            return new InstallResult(
                InstallState.AwaitingUserConfirmation,
                "安装包已交给系统安装程序，请在弹出的界面中确认。确认后请重新检测。",
                InstalledPath: targetDirectory);
        }
        catch (Exception ex)
        {
            _log.Error("install.handoff", "Failed to launch the system package installer.", ex);
            return new InstallResult(InstallState.InstallerFailed, $"无法调起系统安装程序：{ex.Message}");
        }
    }
}
