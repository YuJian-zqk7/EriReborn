using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android detection (spec 21/22). Windows-only detector kinds are answered
/// with Unsupported rather than silently becoming "not installed"; package
/// detection uses the real PackageManager.
/// </summary>
public sealed class AndroidPackageDetector(IAppLogger log) : ISoftwareDetector, IInstalledSoftwareSource
{
    private readonly IAppLogger _log = log;

    public string SourceDescription => "Android 包管理器（PackageManager）";

    /// <summary>
    /// Enumerates the apps the user actually installed. System packages are
    /// excluded on purpose: they are part of the platform rather than software
    /// someone chose to add, so offering them as catalog suggestions would be
    /// misleading (spec 22).
    /// </summary>
    public Task<IReadOnlyList<InstalledSoftwareInfo>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var installed = new List<InstalledSoftwareInfo>();

        try
        {
            var manager = global::Android.App.Application.Context.PackageManager;
            if (manager is null)
            {
                return Task.FromResult<IReadOnlyList<InstalledSoftwareInfo>>(installed);
            }

            // No flags: the binding exposes no "None" member, and no option is
            // needed for a plain listing.
            var packages = manager.GetInstalledPackages((global::Android.Content.PM.PackageInfoFlags)0);
            if (packages is null)
            {
                return Task.FromResult<IReadOnlyList<InstalledSoftwareInfo>>(installed);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var application = package.ApplicationInfo;
                if (application is null)
                {
                    continue;
                }

                if (application.Flags.HasFlag(global::Android.Content.PM.ApplicationInfoFlags.System))
                {
                    continue;
                }

                var label = application.LoadLabel(manager)?.ToString();
                if (string.IsNullOrWhiteSpace(label) || !seen.Add(label))
                {
                    continue;
                }

                installed.Add(new InstalledSoftwareInfo(
                    label,
                    package.VersionName,
                    package.PackageName,
                    "android_package"));
            }
        }
        catch (Exception ex)
        {
            // A failure to enumerate must not look like "nothing is installed".
            _log.Error("installed.android", "枚举已安装应用失败。", ex);
        }

        return Task.FromResult<IReadOnlyList<InstalledSoftwareInfo>>(installed);
    }

    public Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
    {
        var spec = software.Detector ?? DetectorSpec.None;

        var result = spec.Kind switch
        {
            DetectorKind.None => DetectionResult.Unknown("清单未声明检测方式。", "none"),
            DetectorKind.AndroidPackage => DetectPackage(spec),
            DetectorKind.File => DetectFile(spec),
            DetectorKind.Msix => DetectionResult.Unsupported("MSIX 是 Windows 专有格式，Android 不支持。", "msix"),
            DetectorKind.Arp or DetectorKind.RegistryRelease or DetectorKind.RegistrySubKey
                or DetectorKind.DotNet or DetectorKind.WebView2 or DetectorKind.Video
                or DetectorKind.Sound or DetectorKind.Pnp or DetectorKind.Service
                => DetectionResult.Unsupported($"{spec.Kind} 是 Windows 专有检测方式，Android 不支持。", spec.Kind.ToString()),
            DetectorKind.Command => DetectionResult.Unsupported("Android 不提供通用命令执行检测。", "command"),
            _ => DetectionResult.Unknown($"未知的检测类型 {spec.Kind}。", spec.Kind.ToString()),
        };

        return Task.FromResult(result);
    }

    private DetectionResult DetectPackage(DetectorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Param))
        {
            return DetectionResult.Error("android_package 检测缺少包名。", "android_package");
        }

        try
        {
            var context = global::Android.App.Application.Context;
            var manager = context.PackageManager;
            if (manager is null)
            {
                return DetectionResult.Unsupported("当前环境没有 PackageManager。", "android_package");
            }

            var info = manager.GetPackageInfo(spec.Param, 0);
            if (info is null)
            {
                return DetectionResult.NotDetected("android_package");
            }

            return DetectionResult.Detected(info.VersionName, info.ApplicationInfo?.LoadLabel(manager), "android_package");
        }
        catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
        {
            return DetectionResult.NotDetected("android_package");
        }
        catch (Exception ex)
        {
            _log.Error("detect.android", $"Package detection failed for '{spec.Param}'.", ex);
            return DetectionResult.Error(ex.Message, "android_package");
        }
    }

    private static DetectionResult DetectFile(DetectorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Param))
        {
            return DetectionResult.Error("file 检测缺少路径参数。", "file");
        }

        return File.Exists(spec.Param)
            ? DetectionResult.Detected(null, Path.GetFileName(spec.Param), "file", spec.Param)
            : DetectionResult.NotDetected("file");
    }
}
