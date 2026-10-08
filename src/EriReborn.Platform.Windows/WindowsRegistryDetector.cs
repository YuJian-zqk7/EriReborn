using System.Text.RegularExpressions;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;
using Microsoft.Win32;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Real Windows detection across the detector kinds declared in manifests
/// (spec 21). Anything this build cannot answer truthfully returns
/// Unsupported or Unknown; it never degrades into "not installed" (spec 22).
/// </summary>
public sealed class WindowsRegistryDetector(IProcessService processes, IAppLogger log)
    : ISoftwareDetector, IDetectionCacheControl, IInstalledSoftwareSource
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Enumerating the uninstall registry is the slow part of detection. A whole
    /// scan reuses one snapshot instead of re-reading it once per catalog entry.
    /// </summary>
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    private readonly IProcessService _processes = processes;
    private readonly IAppLogger _log = log;
    private readonly object _snapshotGate = new();
    private IReadOnlyList<UninstallEntry>? _snapshot;
    private DateTimeOffset _snapshotTakenAt;
    private IReadOnlyList<PnpDevice>? _pnpSnapshot;
    private DateTimeOffset _pnpSnapshotTakenAt;
    private IReadOnlyList<AppxPackage>? _appxSnapshot;
    private DateTimeOffset _appxSnapshotTakenAt;

    public async Task<DetectionResult> DetectAsync(SoftwareDefinition software, CancellationToken cancellationToken = default)
    {
        var spec = software.Detector ?? DetectorSpec.None;
        if (spec.IsNone)
        {
            return DetectionResult.Unknown("清单未声明检测方式。", "none");
        }

        try
        {
            return spec.Kind switch
            {
                DetectorKind.Arp => DetectUninstallEntry(spec, "arp"),
                DetectorKind.RegistryRelease => DetectUninstallEntry(spec, "registry_release"),
                DetectorKind.RegistrySubKey => DetectRegistrySubKey(spec),
                DetectorKind.DotNet => DetectDotNet(spec),
                DetectorKind.WebView2 => DetectWebView2(),
                DetectorKind.File => DetectFile(software, spec),
                DetectorKind.Video => DetectDeviceClass("{4d36e968-e325-11ce-bfc1-08002be10318}", spec, "video"),
                DetectorKind.Sound => DetectDeviceClass("{4d36e96c-e325-11ce-bfc1-08002be10318}", spec, "sound"),
                DetectorKind.Service => DetectService(spec),
                DetectorKind.Command => await DetectCommandAsync(spec, cancellationToken).ConfigureAwait(false),
                DetectorKind.Pnp => DetectPnp(spec),
                DetectorKind.Msix => DetectMsix(spec),
                _ => DetectionResult.Unknown($"未知的检测类型 {spec.Kind}。", spec.Kind.ToString()),
            };
        }
        catch (Exception ex)
        {
            _log.Error("detect.error", $"Detection failed for '{software.Id}'.", ex);
            return DetectionResult.Error(ex.Message, spec.Kind.ToString());
        }
    }

    private static Regex? TryBuildRegex(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public string SourceDescription => "Windows 卸载信息（注册表 ARP）";

    /// <summary>
    /// Returns everything the uninstall registry reports, reusing the same
    /// snapshot the detector already builds. This is evidence, not a guess.
    /// </summary>
    public Task<IReadOnlyList<InstalledSoftwareInfo>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<InstalledSoftwareInfo> installed = GetUninstallSnapshot()
            .Select(entry => new InstalledSoftwareInfo(entry.Name, entry.Version, entry.Publisher, "arp"))
            .ToList();

        return Task.FromResult(installed);
    }

    private DetectionResult DetectUninstallEntry(DetectorSpec spec, string source)
    {
        var regex = TryBuildRegex(spec.Param, out var error);
        if (regex is null)
        {
            return error is null
                ? DetectionResult.Error($"检测参数为空：{source}。", source)
                : DetectionResult.Error($"正则表达式非法：{error}", source);
        }

        var entries = GetUninstallSnapshot();
        foreach (var entry in entries)
        {
            if (regex.IsMatch(entry.Name))
            {
                return DetectionResult.Detected(entry.Version, entry.Name, source, entry.KeyPath);
            }
        }

        return DetectionResult.NotDetected(source);
    }

    private readonly record struct UninstallEntry(string Name, string? Version, string? Publisher, string KeyPath);

    /// <summary>Returns the cached snapshot, refreshing it when stale.</summary>
    private IReadOnlyList<UninstallEntry> GetUninstallSnapshot()
    {
        lock (_snapshotGate)
        {
            if (_snapshot is not null && DateTimeOffset.Now - _snapshotTakenAt < SnapshotTtl)
            {
                return _snapshot;
            }
        }

        var entries = EnumerateUninstallEntries().ToList();

        lock (_snapshotGate)
        {
            _snapshot = entries;
            _snapshotTakenAt = DateTimeOffset.Now;
        }

        return entries;
    }

    /// <summary>Forces the next detection to re-read the registry (spec 73).</summary>
    public void Invalidate()
    {
        lock (_snapshotGate)
        {
            _snapshot = null;
            _pnpSnapshot = null;
            _appxSnapshot = null;
        }
    }

    private IEnumerable<UninstallEntry> EnumerateUninstallEntries()
    {
        var locations = new (RegistryHive Hive, string Path)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in locations)
        {
            RegistryKey? baseKey = null;
            RegistryKey? key = null;
            string[] subNames;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                key = baseKey.OpenSubKey(path);
                if (key is null)
                {
                    baseKey.Dispose();
                    continue;
                }

                subNames = key.GetSubKeyNames();
            }
            catch (Exception ex)
            {
                _log.Warn("detect.registry", $"Cannot open {hive}\\{path}: {ex.Message}");
                key?.Dispose();
                baseKey?.Dispose();
                continue;
            }

            try
            {
                foreach (var subName in subNames)
                {
                    RegistryKey? sub = null;
                    try
                    {
                        sub = key.OpenSubKey(subName);
                        if (sub is null)
                        {
                            continue;
                        }

                        if (sub.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display))
                        {
                            continue;
                        }

                        yield return new UninstallEntry(
                            display,
                            sub.GetValue("DisplayVersion") as string,
                            sub.GetValue("Publisher") as string,
                            $"{path}\\{subName}");
                    }
                    finally
                    {
                        sub?.Dispose();
                    }
                }
            }
            finally
            {
                key.Dispose();
                baseKey.Dispose();
            }
        }
    }

    private static DetectionResult DetectRegistrySubKey(DetectorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Param))
        {
            return DetectionResult.Error("registry_subkey 检测缺少键路径。", "registry_subkey");
        }

        var (hive, path) = SplitHive(spec.Param);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(path);
        if (key is null)
        {
            return DetectionResult.NotDetected("registry_subkey");
        }

        var version = key.GetValue("DisplayVersion") as string ?? key.GetValue("Version") as string;
        return DetectionResult.Detected(version, null, "registry_subkey", spec.Param);
    }

    /// <summary>
    /// .NET SDK versions are recorded in the setup key, but on 64-bit Windows
    /// that key can live in either the 64-bit or the 32-bit registry view
    /// (WOW6432Node). Both are probed, and the uninstall snapshot is a fallback.
    /// </summary>
    private DetectionResult DetectDotNet(DetectorSpec spec)
    {
        var pattern = TryBuildRegex(spec.Param, out _);
        var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var prefixes = new[]
        {
            @"SOFTWARE\dotnet\Setup\InstalledVersions",
            @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions",
        };

        foreach (var arch in new[] { "x64", "x86", "arm64" })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var prefix in prefixes)
                {
                    CollectSdkVersions(view, $@"{prefix}\{arch}\sdk", pattern, versions);
                }
            }
        }

        if (versions.Count == 0)
        {
            // Fallback: the uninstall snapshot records the SDK as well.
            var arpPattern = TryBuildRegex(@"^Microsoft \.NET SDK", out _);
            foreach (var entry in GetUninstallSnapshot())
            {
                if (arpPattern is null || !arpPattern.IsMatch(entry.Name))
                {
                    continue;
                }

                var version = entry.Version ?? entry.Name;
                if (pattern is null || pattern.IsMatch(version))
                {
                    versions.Add(version);
                }
            }
        }

        return versions.Count > 0
            ? DetectionResult.Detected(string.Join(", ", versions), ".NET SDK", "dotnet")
            : DetectionResult.NotDetected("dotnet");
    }

    private void CollectSdkVersions(RegistryView view, string subKeyPath, Regex? pattern, SortedSet<string> versions)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(subKeyPath);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames())
            {
                if (!string.IsNullOrWhiteSpace(name) && (pattern is null || pattern.IsMatch(name)))
                {
                    versions.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("detect.dotnet", $"Cannot read {view} / {subKeyPath}: {ex.Message}");
        }
    }

    private static DetectionResult DetectWebView2()
    {
        string[] keys =
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };

        foreach (var path in keys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = baseKey.OpenSubKey(path);
            if (key?.GetValue("pv") is string version && !string.IsNullOrWhiteSpace(version))
            {
                return DetectionResult.Detected(version, "Microsoft Edge WebView2 Runtime", "webview2");
            }
        }

        return DetectionResult.NotDetected("webview2");
    }

    private DetectionResult DetectFile(SoftwareDefinition software, DetectorSpec spec)
    {
        var raw = spec.Param;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DetectionResult.Error("file 检测缺少路径参数。", "file");
        }

        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (File.Exists(expanded))
        {
            return DetectionResult.Detected(null, Path.GetFileName(expanded), "file", expanded);
        }

        if (Directory.Exists(expanded))
        {
            _log.Warn("detect.file", $"'{software.Id}' file detector points at a directory.");
            return DetectionResult.Unknown("file 检测参数指向目录，结果不可判定。", "file");
        }

        return DetectionResult.NotDetected("file");
    }

    /// <summary>
    /// A packaged (MSIX/Appx) application. The package identity encodes the name,
    /// version and architecture; the registry value carries the display name.
    /// </summary>
    private readonly record struct AppxPackage(
        string PackageId,
        string Name,
        string? Version,
        string? Architecture,
        string? DisplayName);

    /// <summary>
    /// Package identities look like "Name_1.2.3.4_x64__publisherhash", so the
    /// name, version and architecture are read from the identity rather than
    /// guessed. Some entries store an indirect resource string
    /// ("@{...?ms-resource://...}") as the display name; those are not usable
    /// text, so the package name is used instead.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AppxIdentityPattern =
        new(@"^(?<name>.+?)_(?<version>\d+(?:\.\d+){1,3})_(?<arch>[^_]*)_",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private IReadOnlyList<AppxPackage> GetAppxSnapshot()
    {
        lock (_snapshotGate)
        {
            if (_appxSnapshot is not null && DateTimeOffset.Now - _appxSnapshotTakenAt < SnapshotTtl)
            {
                return _appxSnapshot;
            }
        }

        var packages = EnumerateAppxPackages().ToList();

        lock (_snapshotGate)
        {
            _appxSnapshot = packages;
            _appxSnapshotTakenAt = DateTimeOffset.Now;
        }

        return packages;
    }

    private static IEnumerable<AppxPackage> EnumerateAppxPackages()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var root = baseKey.OpenSubKey(Path.Combine(
            "Software",
            "Classes",
            "Local Settings",
            "Software",
            "Microsoft",
            "Windows",
            "CurrentVersion",
            "AppModel",
            "Repository",
            "Packages"));

        if (root is null)
        {
            yield break;
        }

        foreach (var keyName in root.GetSubKeyNames())
        {
            using var package = root.OpenSubKey(keyName);
            if (package is null)
            {
                continue;
            }

            var identity = package.GetValue("PackageID") as string ?? keyName;
            var match = AppxIdentityPattern.Match(identity);

            var name = match.Success ? match.Groups["name"].Value : identity;
            var version = match.Success ? match.Groups["version"].Value : null;
            var architecture = match.Success ? match.Groups["arch"].Value : null;

            var display = package.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(display) || display.StartsWith("@{", StringComparison.Ordinal))
            {
                display = null;
            }

            yield return new AppxPackage(identity, name, version, architecture, display);
        }
    }

    /// <summary>
    /// Matches an installed MSIX/Appx package by display name or package name.
    /// </summary>
    private DetectionResult DetectMsix(DetectorSpec spec)
    {
        var regex = TryBuildRegex(spec.Param, out var error);
        if (regex is null && error is not null)
        {
            return DetectionResult.Error($"正则表达式非法：{error}", "msix");
        }

        if (regex is null)
        {
            return DetectionResult.Error("msix 检测缺少包名称模式。", "msix");
        }

        foreach (var package in GetAppxSnapshot())
        {
            var matches = regex.IsMatch(package.Name)
                || (package.DisplayName is not null && regex.IsMatch(package.DisplayName));

            if (!matches)
            {
                continue;
            }

            // Report the display name when there is one, because that is what the
            // user sees in Settings; the package identity is the evidence.
            return DetectionResult.Detected(
                package.Version,
                package.DisplayName ?? package.Name,
                "msix",
                package.PackageId);
        }

        return DetectionResult.NotDetected("msix");
    }

    /// <summary>One PnP device instance, as read from the registry.</summary>
    private readonly record struct PnpDevice(string? ClassGuid, string Name, string? Version, string InstancePath);

    /// <summary>
    /// Reads every device instance under Enum\&lt;enumerator&gt;\&lt;device&gt;\&lt;instance&gt;.
    /// Cached like the uninstall snapshot: one walk serves a whole scan.
    /// </summary>
    private IReadOnlyList<PnpDevice> GetPnpDevices()
    {
        lock (_snapshotGate)
        {
            if (_pnpSnapshot is not null && DateTimeOffset.Now - _pnpSnapshotTakenAt < SnapshotTtl)
            {
                return _pnpSnapshot;
            }
        }

        var devices = EnumeratePnpDevices().ToList();

        lock (_snapshotGate)
        {
            _pnpSnapshot = devices;
            _pnpSnapshotTakenAt = DateTimeOffset.Now;
        }

        return devices;
    }

    private static IEnumerable<PnpDevice> EnumeratePnpDevices()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        // Built with Path.Combine on purpose: a verbatim string here is easy to
        // mangle, and a wrong path silently yields zero devices.
        using var enumKey = baseKey.OpenSubKey(Path.Combine("SYSTEM", "CurrentControlSet", "Enum"));
        if (enumKey is null)
        {
            yield break;
        }

        foreach (var enumeratorName in enumKey.GetSubKeyNames())
        {
            using var enumerator = enumKey.OpenSubKey(enumeratorName);
            if (enumerator is null)
            {
                continue;
            }

            foreach (var deviceName in enumerator.GetSubKeyNames())
            {
                using var device = enumerator.OpenSubKey(deviceName);
                if (device is null)
                {
                    continue;
                }

                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var instance = device.OpenSubKey(instanceName);
                    if (instance is null)
                    {
                        continue;
                    }

                    var name = DescribePnpDevice(instance);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    using var driver = instance.OpenSubKey("Driver");
                    yield return new PnpDevice(
                        instance.GetValue("ClassGUID") as string,
                        name,
                        driver?.GetValue("DriverVersion") as string,
                        $"{enumeratorName}\\{deviceName}\\{instanceName}");
                }
            }
        }
    }

    /// <summary>
    /// A device's display name. <c>FriendlyName</c> is preferred; otherwise
    /// <c>DeviceDesc</c> is usually an indirect string
    /// ("@inf,%key%;Human Name") whose human part is after the last semicolon.
    /// </summary>
    private static string? DescribePnpDevice(RegistryKey instance)
    {
        if (instance.GetValue("FriendlyName") is string friendly && !string.IsNullOrWhiteSpace(friendly))
        {
            return friendly.Trim();
        }

        if (instance.GetValue("DeviceDesc") is not string desc || string.IsNullOrWhiteSpace(desc))
        {
            return null;
        }

        var separator = desc.LastIndexOf(';');
        var tail = separator >= 0 ? desc[(separator + 1)..] : desc;
        return string.IsNullOrWhiteSpace(tail) ? null : tail.Trim();
    }

    /// <summary>
    /// Matches a specific device instance by name, optionally restricted to one
    /// device class. Unlike <c>Video</c>/<c>Sound</c> (which match a driver
    /// class), this can name an individual device.
    /// </summary>
    private DetectionResult DetectPnp(DetectorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Param))
        {
            return DetectionResult.Error("pnp 检测缺少设备名称模式。", "pnp");
        }

        var regex = TryBuildRegex(spec.Param, out var error);
        if (regex is null && error is not null)
        {
            return DetectionResult.Error($"正则表达式非法：{error}", "pnp");
        }

        var classFilter = string.IsNullOrWhiteSpace(spec.SecondaryParam) ? null : spec.SecondaryParam.Trim();

        foreach (var device in GetPnpDevices())
        {
            if (classFilter is not null
                && !string.Equals(device.ClassGuid, classFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (regex is not null && !regex.IsMatch(device.Name))
            {
                continue;
            }

            return DetectionResult.Detected(device.Version, device.Name, "pnp", device.InstancePath);
        }

        return DetectionResult.NotDetected("pnp");
    }

    private static DetectionResult DetectDeviceClass(string classGuid, DetectorSpec spec, string source)
    {
        var regex = TryBuildRegex(spec.Param, out var error);
        if (regex is null && error is not null)
        {
            return DetectionResult.Error($"正则表达式非法：{error}", source);
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var classKey = baseKey.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{classGuid}");
        if (classKey is null)
        {
            return DetectionResult.NotDetected(source);
        }

        foreach (var subName in classKey.GetSubKeyNames())
        {
            if (!int.TryParse(subName, out _))
            {
                continue;
            }

            using var sub = classKey.OpenSubKey(subName);
            if (sub?.GetValue("DriverDesc") is not string description || string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            if (regex is null || regex.IsMatch(description))
            {
                var version = sub.GetValue("DriverVersion") as string;
                return DetectionResult.Detected(version, description, source);
            }
        }

        return DetectionResult.NotDetected(source);
    }

    private static DetectionResult DetectService(DetectorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Param))
        {
            return DetectionResult.Error("service 检测缺少服务名。", "service");
        }

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var key = baseKey.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{spec.Param}");
        return key is null ? DetectionResult.NotDetected("service") : DetectionResult.Detected(null, spec.Param, "service");
    }

    private async Task<DetectionResult> DetectCommandAsync(DetectorSpec spec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.Param))
        {
            return DetectionResult.Error("command 检测缺少命令。", "command");
        }

        var parts = spec.Param.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = await _processes.RunAsync(
            new ProcessRequest(parts[0], parts.Skip(1).ToArray(), Timeout: TimeSpan.FromSeconds(20)),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return DetectionResult.NotDetected("command");
        }

        return result.ExitCode == 0
            ? DetectionResult.Detected(result.StandardOutput.Trim(), null, "command")
            : DetectionResult.NotDetected("command");
    }

    private static (RegistryHive Hive, string Path) SplitHive(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
        {
            var index = trimmed.IndexOf('\\');
            return (RegistryHive.CurrentUser, trimmed[(index + 2)..]);
        }

        if (trimmed.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
        {
            var index = trimmed.IndexOf('\\');
            return (RegistryHive.LocalMachine, trimmed[(index + 2)..]);
        }

        return (RegistryHive.LocalMachine, trimmed);
    }
}
