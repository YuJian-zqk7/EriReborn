using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Engine.Software;

/// <summary>
/// Orchestrates detect / install / update on top of the injected platform
/// (spec 19/23). All policy that must be identical on Windows and Android
/// lives here; only the mechanics differ per platform.
/// </summary>
public sealed class SoftwareEngine(
    IPlatformService platform,
    PathResolver pathResolver,
    InstallationRegistry installations,
    DetectionHintStore hints,
    IAppLogger log)
{
    private readonly IPlatformService _platform = platform;
    private readonly PathResolver _pathResolver = pathResolver;
    private readonly InstallationRegistry _installations = installations;
    private readonly DetectionHintStore _hints = hints;
    private readonly IAppLogger _log = log;

    /// <summary>
    /// The directory install packages are downloaded into. Set by the host from the user's
    /// configured download directory; null lets each platform use its own staging folder.
    /// </summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>
    /// Detection policy lives here so every caller agrees (spec 22). Signals are
    /// consulted strongest-first:
    ///   1. the override the user supplied (ARP pattern, path or file name),
    ///   2. the detector the manifest declares,
    ///   3. the path currently configured for the software,
    ///   4. what EriReborn itself installed,
    ///   5. the directory EriReborn would install into.
    /// A signal that cannot decide returns Unknown, never "not installed".
    /// </summary>
    public async Task<DetectionResult> DetectAsync(
        SoftwareDefinition software,
        EnvironmentContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var detector = _platform.Detector;
        if (detector is null)
        {
            return DetectionResult.Unsupported("当前平台没有提供检测器。");
        }

        // 1. The user's own override is the strongest statement about this
        //    machine: they are asserting ground truth, so it outranks the
        //    manifest's default for this installation. An override that cannot
        //    decide returns null and the remaining signals still apply.
        if (_hints.Get(software.Id) is { IsEmpty: false } hint)
        {
            var overridden = await ApplyHintAsync(software, hint, detector, context, cancellationToken).ConfigureAwait(false);
            if (overridden is not null)
            {
                return overridden;
            }
        }

        // 2. A declared detector is authoritative when it can decide.
        DetectionResult? declared = null;
        if (software.Detector is not null && !software.Detector.IsNone)
        {
            declared = await detector.DetectAsync(software, cancellationToken).ConfigureAwait(false);
            if (declared.Outcome is DetectionOutcome.Detected or DetectionOutcome.NotDetected)
            {
                return declared;
            }
        }

        // 3. The path currently configured for this software exists. This is
        //    checked BEFORE the historical record: a record describes where the
        //    software was installed once, and a move or a fresh install to a new
        //    root must not be masked by it.
        var targetDirectory = context is null
            ? null
            : _pathResolver.ResolveSoftwareDirectory(context, software);

        if (targetDirectory is not null && _platform.Files.DirectoryExists(targetDirectory))
        {
            return DetectionResult.Detected(null, software.Name, "install-path", targetDirectory);
        }

        // 4. EriReborn installed it before and recorded where. A record is
        //    evidence about the past, so it is the weaker signal.
        if (_installations.Find(software.Id) is { } record)
        {
            return !string.IsNullOrEmpty(record.Directory) && Exists(record.Directory!)
                ? DetectionResult.Detected(record.Version, record.Name, "install-registry", record.Directory)
                : DetectionResult.NotDetected(
                    "install-registry",
                    $"EriReborn 曾安装到 {record.Directory}，该目录已不存在。");
        }

        // 5. Nothing could decide.
        if (declared is not null)
        {
            return declared;
        }

        return targetDirectory is not null
            ? DetectionResult.Unknown($"清单未声明检测方式；默认安装目录不存在：{targetDirectory}", "install-path")
            : DetectionResult.Unknown("清单未声明检测方式。", "none");
    }

    /// <summary>
    /// Applies a user override. An ARP pattern is run through the real platform
    /// detector; a path or file name is checked against the disk. Returns null
    /// when the override cannot decide, so the remaining signals still apply.
    /// </summary>
    private async Task<DetectionResult?> ApplyHintAsync(
        SoftwareDefinition software,
        DetectionHint hint,
        ISoftwareDetector detector,
        EnvironmentContext? context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(hint.ArpPattern))
        {
            var withPattern = software with
            {
                Detector = new DetectorSpec(DetectorKind.Arp, hint.ArpPattern),
            };

            var result = await detector.DetectAsync(withPattern, cancellationToken).ConfigureAwait(false);

            // Unsupported is conclusive: the platform said it cannot judge this,
            // which is different from "no signal yet" and must not decay into
            // Unknown. Only Unknown falls through to the remaining signals.
            if (result.Outcome is DetectionOutcome.Detected
                or DetectionOutcome.NotDetected
                or DetectionOutcome.Unsupported)
            {
                _log.Info("detect.hint", $"'{software.Id}' decided by the user's ARP pattern /{hint.ArpPattern}/: {result.Outcome}.");
                return result with { Source = "user-hint" };
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(hint.Path))
        {
            return Exists(hint.Path)
                ? DetectionResult.Detected(null, software.Name, "user-hint", hint.Path)
                : DetectionResult.NotDetected("user-hint", $"用户指定的路径不存在：{hint.Path}");
        }

        if (!string.IsNullOrWhiteSpace(hint.FileName) && context is not null)
        {
            var directory = _pathResolver.ResolveSoftwareDirectory(context, software);
            var candidate = Path.Combine(directory, hint.FileName!);
            return Exists(candidate)
                ? DetectionResult.Detected(null, software.Name, "user-hint", candidate)
                : DetectionResult.NotDetected("user-hint", $"默认安装目录下没有 {hint.FileName}：{directory}");
        }

        return null;
    }

    private bool Exists(string path)
        => _platform.Files.FileExists(path) || _platform.Files.DirectoryExists(path);

    /// <summary>Drops any cached detector snapshot so the next read is fresh.</summary>
    public void InvalidateDetectionCache() => (_platform.Detector as IDetectionCacheControl)?.Invalidate();

    public async Task<InstallResult> InstallAsync(
        SoftwareDefinition software,
        EnvironmentContext context,
        SoftwareSource? preferredSource = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _log.Info("install.start", $"Installing '{software.Id}' ({software.Name}).");

        // 0. Authority gate: an entry whose signature did not verify is data of
        //    unknown origin. It may be looked at, but it must never drive an
        //    unattended install (spec 37).
        if (software.Provenance == CatalogProvenance.Untrusted)
        {
            _log.Error(
                "install.untrusted",
                $"Refused to install '{software.Id}': its catalog entry is untrusted.",
                null);
            return new InstallResult(
                InstallState.UntrustedDefinition,
                $"'{software.Name}' 的目录来源未通过签名校验，已阻止安装；请先确认官方目录未被篡改。");
        }

        // 1. The official directory name and category must be valid (spec 16).
        var pathValidation = _pathResolver.ValidateSoftwareDirectory(context, software);
        if (!pathValidation.IsValid)
        {
            return new InstallResult(InstallState.ValidationFailed, pathValidation.Describe());
        }

        // 2. Trust gate: unvetted entries never enter the automated path (spec 37).
        if (software.Trust is SoftwareTrust.Unknown or SoftwareTrust.Invalid)
        {
            return new InstallResult(
                InstallState.Unsupported,
                $"'{software.Name}' 的信任等级为 {software.Trust}，已阻止自动安装。");
        }

        var source = preferredSource ?? software.Sources.FirstOrDefault();
        if (source is null)
        {
            return new InstallResult(InstallState.SourceUnavailable, $"'{software.Name}' 没有可用的来源。");
        }

        var targetDirectory = _pathResolver.ResolveSoftwareDirectory(context, software);

        var request = new InstallRequest
        {
            Software = software,
            Source = source,
            TargetDirectory = targetDirectory,
            DownloadDirectory = DownloadDirectory,

            AllowScriptExecution = false,
        };

        var result = await _platform.Installer
            .InstallAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _log.Warn("install.failed", $"'{software.Id}' -> {result.State}: {result.Message}");
            return result;
        }

        // 3. Real verification: detection must confirm the new state (spec 73).
        // The platform may have cached a "before" snapshot, so drop it first.
        InvalidateDetectionCache();
        var verified = await DetectAsync(software, context, cancellationToken).ConfigureAwait(false);
        if (verified.Outcome != DetectionOutcome.Detected)
        {
            _log.Error("install.verify", $"'{software.Id}' installed but detection returned {verified.Outcome}.");
            return result with
            {
                State = InstallState.VerificationFailed,
                Verified = verified,
                Message = $"{result.Message} 但安装后检测结果为 {verified.Outcome}。",
            };
        }

        // Remember what we installed: it is first-hand evidence for later scans.
        var recorded = _installations.Record(new InstallationRecord
        {
            SoftwareId = software.Id,
            Name = software.Name,
            Version = verified.Version ?? software.Version,
            Directory = result.InstalledPath ?? targetDirectory,
            Source = source.ToString(),
            InstalledAt = DateTimeOffset.Now,
        });

        if (!recorded)
        {
            // The install itself succeeded; only the note about it did not. Saying so
            // is what stops the next scan reporting "unknown" with no explanation.
            _log.Warn(
                "install.record",
                $"'{software.Id}' 安装成功，但安装记录未能写入磁盘；下次扫描可能无法据记录判断。");
        }

        _log.Info("install.done", $"'{software.Id}' verified as {verified.Outcome}.");
        return result with { Verified = verified };
    }

    public async Task<UpdateResult> UpdateAsync(
        SoftwareDefinition software,
        SoftwareSource source,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var before = await DetectAsync(software, null, cancellationToken).ConfigureAwait(false);
        if (before.Outcome != DetectionOutcome.Detected)
        {
            return new UpdateResult(
                UpdateState.Unsupported,
                $"更新前检测结果为 {before.Outcome}，无法确认当前版本。",
                FromVersion: before.Version);
        }

        var result = await _platform.Updater
            .UpdateAsync(software, source, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.State is UpdateState.Updated)
        {
            InvalidateDetectionCache();
            var after = await DetectAsync(software, null, cancellationToken).ConfigureAwait(false);
            if (after.Outcome != DetectionOutcome.Detected)
            {
                return result with
                {
                    State = UpdateState.VerificationFailed,
                    Verified = after,
                    Message = $"{result.Message} 但更新后检测结果为 {after.Outcome}。",
                };
            }

            return result with { Verified = after, FromVersion = before.Version };
        }

        return result with { FromVersion = before.Version };
    }

    public PlanPreview Plan(SoftwareDefinition software, EnvironmentContext context)
    {
        var path = _pathResolver.ResolveSoftwareDirectory(context, software);
        var validation = _pathResolver.ValidateSoftwareDirectory(context, software);

        // The facts a person needs before saying yes: where it goes, whether something is already
        // there, how big the download is, and whether the volume has room. A size nobody declared
        // stays null rather than being guessed, and free space stays null when it cannot be read
        // (spec 10/151).
        return new PlanPreview(
            software.Id,
            path,
            software.Mode,
            software.Sources.Count,
            validation.IsValid,
            validation.Describe(),
            software.Trust,
            FreeSpaceBytes: string.IsNullOrWhiteSpace(path) ? null : _platform.Files.GetAvailableFreeSpace(path),
            TargetExists: !string.IsNullOrWhiteSpace(path) && _platform.Files.DirectoryExists(path),
            ExpectedSizeBytes: software.Sources.Select(source => source.SizeBytes).FirstOrDefault(size => size is > 0));
    }
}

/// <summary>The confirmation payload shown before anything touches the system (spec 60).</summary>
public sealed record PlanPreview(
    string SoftwareId,
    string TargetDirectory,
    InstallationMode Mode,
    int SourceCount,
    bool PathValid,
    string? PathProblem,
    SoftwareTrust Trust,
    long? FreeSpaceBytes = null,
    bool TargetExists = false,
    long? ExpectedSizeBytes = null)
{
    /// <summary>
    /// True only when both numbers are known and the download plainly does not fit. Unknown on either
    /// side is not evidence of a problem, and saying "not enough space" from a missing reading would
    /// stop installs that are perfectly fine (spec 10).
    /// </summary>
    public bool FreeSpaceInsufficient
        => FreeSpaceBytes is { } free && ExpectedSizeBytes is { } size && size > free;
}
