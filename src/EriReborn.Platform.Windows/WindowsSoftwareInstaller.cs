using System.IO.Compression;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Cloud;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// The real Windows install pipeline (spec 19): acquire the package, run the
/// declared installer type, and report a concrete state. Nothing here claims
/// success without the engine re-detecting the software afterwards.
/// </summary>
public sealed class WindowsSoftwareInstaller(
    IProcessService processes,
    IFileSystemService files,
    DownloadEngineSelector engines,
    CloudShareLinkResolver cloudResolver,
    IAppLogger log) : ISoftwareInstaller
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
    private readonly CloudShareLinkResolver _cloudResolver = cloudResolver;
    private readonly IAppLogger _log = log;

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = request.Source;

        return source.Kind switch
        {
            SourceKind.Winget => await InstallViaWingetAsync(request, source, cancellationToken).ConfigureAwait(false),
            SourceKind.Local => await InstallLocalAsync(request, source.FileName, progress, cancellationToken).ConfigureAwait(false),
            SourceKind.HttpUrl or SourceKind.Official => await InstallFromUrlAsync(request, source, progress, cancellationToken).ConfigureAwait(false),
            SourceKind.CloudShare => await InstallFromCloudAsync(request, source, progress, cancellationToken).ConfigureAwait(false),
            SourceKind.Manual => new InstallResult(InstallState.Unsupported, "该条目声明为手动安装，不进入自动安装流程。"),
            _ => new InstallResult(InstallState.Unsupported, $"不支持的来源类型 {source.Kind}。"),
        };
    }

    private async Task<InstallResult> InstallViaWingetAsync(InstallRequest request, SoftwareSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.WingetId))
        {
            return new InstallResult(InstallState.ValidationFailed, "winget 来源缺少包标识。");
        }

        if (!_processes.Exists("winget.exe"))
        {
            return new InstallResult(InstallState.SourceUnavailable, "本机未安装 winget-cli，无法使用 winget 来源。");
        }

        var result = await _processes.RunAsync(
            new ProcessRequest(
                "winget.exe",
                new[]
                {
                    "install", "--id", source.WingetId, "--exact",
                    "--silent",
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                    "--disable-interactivity",
                },
                Timeout: TimeSpan.FromMinutes(30)),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return new InstallResult(InstallState.InstallerFailed, $"无法启动 winget：{result.StandardError}");
        }

        // 0x8A150061: package is already installed.
        if (result.ExitCode == unchecked((int)0x8A150061))
        {
            return new InstallResult(InstallState.AlreadyInstalled, "winget 报告该软件已安装。");
        }

        if (result.ExitCode != 0)
        {
            return new InstallResult(InstallState.InstallerFailed, $"winget 退出码 {result.ExitCode}。", Error: null);
        }

        return new InstallResult(InstallState.Succeeded, $"winget 已安装 {source.WingetId}。");
    }

    private async Task<InstallResult> InstallFromUrlAsync(
        InstallRequest request,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            return new InstallResult(InstallState.SourceUnavailable, "该来源缺少下载地址。");
        }

        var fileName = source.FileName ?? BuildFileNameFromUrl(source.Url);
        var localPath = await DownloadAsync(request, source.Url, fileName, source.Sha256, null, source, progress, cancellationToken).ConfigureAwait(false);
        if (localPath.Result is not null)
        {
            return localPath.Result;
        }

        return await RunPackageAsync(request, localPath.Path!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallResult> InstallFromCloudAsync(
        InstallRequest request,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileName = source.FileName ?? "package.bin";
        var destination = Path.Combine(ResolvedDownloadDirectory(request), SanitizeFileName(fileName));

        var outcome = await _cloudResolver.DownloadAsync(request.Software, source, destination, progress, cancellationToken).ConfigureAwait(false);
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

        return await RunPackageAsync(request, outcome.Path!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstallResult> InstallLocalAsync(
        InstallRequest request,
        string? localPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !_files.FileExists(localPath))
        {
            return new InstallResult(InstallState.SourceUnavailable, "本地来源文件不存在。");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return await RunPackageAsync(request, localPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string? Path, InstallResult? Result)> DownloadAsync(
        InstallRequest request,
        string url,
        string fileName,
        string? sha256,
        string? providerId,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(ResolvedDownloadDirectory(request), SanitizeFileName(fileName));
        var route = new DownloadRoute(
            DownloadRouteKind.ProviderDirect,
            url,
            ProviderId: providerId,
            FileName: fileName,
            ContentLength: source.SizeBytes,
            Note: source.Sha256 is null ? null : "来源自带 SHA-256");

        var download = await DownloadViaEngineAsync(
            route,
            new DownloadRequest
            {
                Url = url,
                DestinationPath = destination,
                ExpectedSha256 = sha256,
                ExpectedSize = source.SizeBytes,
                SoftwareId = request.Software.Id,
                Version = source.Version ?? request.Software.Version,
                Architecture = source.Architecture ?? request.Software.Architecture,
                ProviderId = providerId,
                FileName = fileName,
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        if (download.IsSuccess)
        {
            return (download.Path, null);
        }

        var state = download.State switch
        {
            DownloadState.HashMismatch => InstallState.IntegrityFailed,
            DownloadState.SizeMismatch => InstallState.IntegrityFailed,
            DownloadState.Cancelled => InstallState.Cancelled,
            DownloadState.HttpError => InstallState.SourceUnavailable,
            _ => InstallState.DownloadFailed,
        };
        return (null, new InstallResult(state, download.Message ?? "下载失败。"));
    }

    /// <summary>
    /// True when this package cannot be installed without elevation.
    ///
    /// <para>
    /// An MSI defaults to a machine-wide install, which writes to HKLM. Without
    /// elevation msiexec fails with 1603 and its log names a registry access denial —
    /// a message that says nothing useful to the person reading it. Deciding here means
    /// they are told before anything starts, and that the decision is testable by
    /// someone who is not an administrator.
    /// </para>
    /// </summary>
    internal static bool NeedsElevation(string extension, bool isElevated)
        => !isElevated && string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the current process can perform a machine-wide install.</summary>
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Turns an msiexec exit code into the reason it actually stands for. The
    /// bare number is not a diagnosis.
    /// </summary>
    /// <summary>
    /// True when msiexec's exit code means the install itself worked.
    ///
    /// <para>
    /// 1641 belongs here and did not used to: it means "the installer completed and
    /// started a restart", which <see cref="DescribeMsiexec"/> already said in words
    /// while the caller filed it under <c>InstallerFailed</c>. A successful install
    /// was reported as a failure whose own message read "install complete".
    /// </para>
    /// </summary>
    /// <summary>
    /// The MSI flags each UI mode asks for.
    ///
    /// <para>
    /// Silent and graphical are different claims: a silent install proves the package
    /// works unattended, a graphical one proves the vendor's wizard completes here.
    /// Interactive deliberately adds no flag — the wizard is the point — and a mode
    /// nobody defined must not quietly become silent.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> MsiexecUiArguments(InstallerUiMode mode) => mode switch
    {
        InstallerUiMode.Silent => new[] { "/qn" },
        InstallerUiMode.Basic => new[] { "/qb" },
        _ => Array.Empty<string>(),
    };

    internal static bool IsMsiexecSuccess(int code) => code is 0 or 1641 or 3010;

    /// <summary>True when the code says the machine must restart before the work is finished.</summary>
    internal static bool MsiexecWantsRestart(int code) => code is 1641 or 3010;

    internal static string DescribeMsiexec(int code) => code switch
    {
        1602 => "用户取消了安装",
        1603 => "安装过程中发生致命错误（详细原因见日志）",
        1618 => "另一个安装正在进行，请稍后重试",
        1619 => "无法打开安装包，文件可能已损坏",
        1620 => "安装包格式无效",
        1625 => "安装被系统策略禁止",
        1633 => "该安装包不支持当前平台",
        1638 => "已安装了另一个版本，无法继续",
        1641 => "安装完成并已请求重启",
        3010 => "安装完成，需要重启",
        _ => $"msiexec 退出码 {code}",
    };

    /// <summary>Folds installer stderr into the message so a failure carries its reason.</summary>
    internal static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 300 ? $" {trimmed}" : $" {trimmed[..300]}…";
    }

    private async Task<InstallResult> RunPackageAsync(InstallRequest request, string packagePath, CancellationToken cancellationToken)
    {
        var mode = request.Software.Mode;
        var extension = Path.GetExtension(packagePath).ToLowerInvariant();
        var targetDirectory = request.TargetDirectory;

        // Scripts must be explicitly allowed and only for vetted entries (spec 19).
        if (extension is ".ps1" or ".bat" or ".cmd" or ".sh")
        {
            if (!request.AllowScriptExecution)
            {
                return new InstallResult(
                    InstallState.Unsupported,
                    "该来源是脚本，默认不执行。需要显式允许脚本执行且信任等级为 Official/Verified。");
            }

            if (request.Software.Trust is not (SoftwareTrust.Official or SoftwareTrust.Verified))
            {
                return new InstallResult(
                    InstallState.Unsupported,
                    $"信任等级 {request.Software.Trust} 不允许执行安装脚本。");
            }

            return await RunScriptAsync(packagePath, extension, request, cancellationToken).ConfigureAwait(false);
        }

        // Publisher gate. A hash proves the bytes are the expected ones; only a
        // signature says who produced them, and an installer is code we are
        // about to execute (spec 19/37).
        if (extension is ".msi" or ".exe")
        {
            var trust = AuthenticodeVerifier.Verify(packagePath);
            _log.Info(
                "install.publisher",
                $"'{request.Software.Id}' installer signature: {trust.Trust}, signer={trust.Signer ?? "(none)"}.");

            if (!trust.IsValid && !request.AllowUnsignedInstaller)
            {
                return new InstallResult(
                    InstallState.PublisherUntrusted,
                    $"安装包发布者验证未通过（{trust.Trust}）：{trust.Message} 未执行安装程序。");
            }
        }

        if (extension == ".msi")
        {
            // Machine-wide MSI installs write to HKLM. Without elevation msiexec
            // fails with 1603 and the log names a registry access denial, which
            // says nothing useful to the user. Check before starting instead.
            if (NeedsElevation(extension, IsElevated()))
            {
                return new InstallResult(
                    InstallState.PermissionDenied,
                    "安装 MSI 需要管理员权限（Windows Installer 需要写入 HKLM）。"
                    + "当前进程未提权，已在执行安装程序之前阻止。请以管理员身份重新运行。");
            }

            var logPath = Path.Combine(_files.GetTempDirectory(), $"install-{request.Software.Id}.log");

            // Silent and graphical are different claims about the package, so the
            // caller chooses rather than this method assuming.
            var uiArguments = MsiexecUiArguments(request.UiMode);

            var arguments = new List<string> { "/i", packagePath };
            arguments.AddRange(uiArguments);
            arguments.Add("/norestart");
            arguments.Add("/l*v");
            arguments.Add(logPath);

            var result = await _processes.RunAsync(
                new ProcessRequest("msiexec.exe", arguments, Timeout: TimeSpan.FromMinutes(30)),
                cancellationToken).ConfigureAwait(false);

            if (IsMsiexecSuccess(result.ExitCode))
            {
                // Deliberately no InstalledPath: an MSI installs wherever the
                // vendor decides, so the only honest evidence is re-detection,
                // which the engine performs before calling this a success.
                return new InstallResult(
                    InstallState.Succeeded,
                    MsiexecWantsRestart(result.ExitCode)
                        ? $"MSI 安装完成（{request.UiMode}），需要重启。"
                        : $"MSI 安装完成（{request.UiMode}）。",
                    LogPath: logPath);
            }

            return new InstallResult(
                InstallState.InstallerFailed,
                $"{DescribeMsiexec(result.ExitCode)}（{request.UiMode}）{Flatten(result.StandardError)}",
                LogPath: logPath);
        }

        if (extension == ".exe")
        {
            var declared = request.Source.Arguments;

            // Never invent silent switches. With no declared arguments a "silent"
            // run would either show a wizard nobody can answer or hang until the
            // timeout, and both would be reported as our failure rather than the
            // source's omission.
            if (request.UiMode == InstallerUiMode.Silent && declared.Count == 0)
            {
                return new InstallResult(
                    InstallState.Unsupported,
                    "该来源没有声明静默安装参数，而本工具不会猜测安装参数。"
                    + "请在来源中补充明确的安装参数，或改用 Basic / Interactive 模式运行厂商安装程序。");
            }

            var result = await _processes.RunAsync(
                new ProcessRequest(
                    packagePath,
                    declared.ToArray(),
                    WorkingDirectory: Path.GetDirectoryName(packagePath),
                    Timeout: TimeSpan.FromMinutes(30)),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                return new InstallResult(InstallState.Succeeded, $"安装程序正常返回（{request.UiMode}）。");
            }

            return new InstallResult(
                InstallState.InstallerFailed,
                $"安装程序退出码 {result.ExitCode}（{request.UiMode}）。{Flatten(result.StandardError)}");
        }

        if (extension == ".zip")
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return new InstallResult(InstallState.ValidationFailed, "解压安装需要目标目录。");
            }

            await _files.EnsureDirectoryAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(packagePath, targetDirectory, overwriteFiles: true);

            // Archives usually wrap their payload in one versioned folder; the
            // program belongs directly in the directory the user chose.
            if (ArchiveLayout.FlattenSingleRoot(targetDirectory))
            {
                _log.Info("install.extract", $"Flattened the archive's single top-level folder in '{targetDirectory}'.");
            }

            _log.Info("install.extract", $"Extracted '{Path.GetFileName(packagePath)}' to '{targetDirectory}'.");
            return new InstallResult(InstallState.Succeeded, "已解压到目标目录。", InstalledPath: targetDirectory);
        }

        // 7z / rar / tar / tgz / gz … go through SharpCompress; zip above stays on
        // the framework reader it has always used.
        if (ArchiveExtractor.IsArchive(packagePath))
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return new InstallResult(InstallState.ValidationFailed, "解压安装需要目标目录。");
            }

            await _files.EnsureDirectoryAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            ArchiveExtractor.Extract(packagePath, targetDirectory);

            if (ArchiveLayout.FlattenSingleRoot(targetDirectory))
            {
                _log.Info("install.extract", $"Flattened the archive's single top-level folder in '{targetDirectory}'.");
            }

            _log.Info("install.extract", $"Extracted '{Path.GetFileName(packagePath)}' to '{targetDirectory}'.");
            return new InstallResult(InstallState.Succeeded, "已解压到目标目录。", InstalledPath: targetDirectory);
        }

        if (mode == InstallationMode.Portable)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return new InstallResult(InstallState.ValidationFailed, "便携安装需要目标目录。");
            }

            await _files.EnsureDirectoryAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            var destination = Path.Combine(targetDirectory, Path.GetFileName(packagePath));
            File.Copy(packagePath, destination, overwrite: true);
            return new InstallResult(InstallState.Succeeded, "已复制便携文件到目标目录。", InstalledPath: destination);
        }

        return new InstallResult(InstallState.Unsupported, $"不支持的安装包类型 '{extension}'。");
    }

    private async Task<InstallResult> RunScriptAsync(string packagePath, string extension, InstallRequest request, CancellationToken cancellationToken)
    {
        ProcessRequest processRequest = extension switch
        {
            ".ps1" => new ProcessRequest("pwsh.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", packagePath }, Timeout: TimeSpan.FromMinutes(30)),
            ".bat" or ".cmd" => new ProcessRequest("cmd.exe", new[] { "/c", packagePath }, Timeout: TimeSpan.FromMinutes(30)),
            _ => new ProcessRequest("pwsh.exe", new[] { "-NoProfile", "-File", packagePath }, Timeout: TimeSpan.FromMinutes(30)),
        };

        var result = await _processes.RunAsync(processRequest, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? new InstallResult(InstallState.Succeeded, "脚本执行成功。", InstalledPath: request.TargetDirectory)
            : new InstallResult(InstallState.InstallerFailed, $"脚本退出码 {result.ExitCode}。");
    }

    private string GetDownloadDirectory()
    {
        var directory = Path.Combine(_files.GetTempDirectory(), "downloads");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The directory a package is staged in: the user's configured download directory when the
    /// request carries one, otherwise the platform's own staging folder. Either way the directory
    /// is created before it is returned, so callers can write into it directly.
    /// </summary>
    private string ResolvedDownloadDirectory(InstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DownloadDirectory))
        {
            Directory.CreateDirectory(request.DownloadDirectory);
            return request.DownloadDirectory;
        }

        return GetDownloadDirectory();
    }

    private static string BuildFileNameFromUrl(string url)
    {
        var candidate = Path.GetFileName(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(candidate) ? "package.bin" : candidate;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString();
    }
}
