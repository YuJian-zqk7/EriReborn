using System.IO.Compression;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Validation;
using EriReborn.Engine.Download;
using EriReborn.Extension.Signing;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Extension.Marketplace;

public sealed record ExtensionInstallOutcome(
    bool Success,
    string Message,
    string? Directory = null,
    bool DeferredRemoval = false);

/// <summary>
/// Installs a marketplace extension for real: download → verify SHA-256 →
/// extract safely → validate the manifest → move into place (spec 35/69).
/// Nothing here reports success unless the package is actually on disk and
/// its manifest matches the index entry.
/// </summary>
public sealed class ExtensionInstaller(
    HttpDownloader downloader,
    IFileSystemService files,
    IExtensionLifecycle? lifecycle,
    IAppLogger log,
    TrustedKeyStore? trustedKeys = null)
{
    private readonly HttpDownloader _downloader = downloader;
    private readonly IFileSystemService _files = files;
    private readonly IExtensionLifecycle? _lifecycle = lifecycle;
    private readonly IAppLogger _log = log;
    private readonly TrustedKeyStore? _trustedKeys = trustedKeys;

    /// <summary>
    /// Trust grades, best first. A manifest may not claim more than its
    /// signature proves (spec 35).
    /// </summary>
    private static int Rank(SoftwareTrust trust) => trust switch
    {
        SoftwareTrust.Official => 4,
        SoftwareTrust.Verified => 3,
        SoftwareTrust.Community => 2,
        SoftwareTrust.Unknown => 1,
        _ => 0,
    };

    public async Task<ExtensionInstallOutcome> InstallAsync(
        MarketplaceEntry entry,
        string extensionsRoot,
        string workRoot,
        CancellationToken cancellationToken = default)
    {
        var idValidation = DirectoryNameValidator.ValidateName(entry.Id);
        if (!idValidation.IsValid)
        {
            return new ExtensionInstallOutcome(false, $"扩展 ID 非法：{idValidation.Describe()}");
        }

        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
        {
            return new ExtensionInstallOutcome(false, $"'{entry.Id}' 没有提供下载地址。");
        }

        var downloads = Path.Combine(workRoot, "downloads");
        var packagePath = Path.Combine(downloads, entry.Id + ".zip");

        var download = await _downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = entry.DownloadUrl!,
                DestinationPath = packagePath,
                ExpectedSha256 = entry.Sha256,
                ExpectedSize = entry.SizeBytes,
                SoftwareId = "extension/" + entry.Id,
                Version = entry.Version,
                FileName = entry.Id + ".zip",
            },
            progress: null,
            cancellationToken).ConfigureAwait(false);

        if (!download.IsSuccess)
        {
            return new ExtensionInstallOutcome(false, $"下载失败：{download.Message}");
        }

        var extractRoot = Path.Combine(workRoot, "extract", entry.Id);
        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }

        Directory.CreateDirectory(extractRoot);
        try
        {
            ExtractSafely(packagePath, extractRoot);
        }
        catch (Exception ex)
        {
            _log.Error("extension.extract", $"Failed to extract '{entry.Id}'.", ex);
            return new ExtensionInstallOutcome(false, $"解压失败：{ex.Message}");
        }

        var manifestPath = Directory.EnumerateFiles(extractRoot, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
        if (manifestPath is null)
        {
            return new ExtensionInstallOutcome(false, "包内缺少 manifest.json。");
        }

        var sourceDirectory = Path.GetDirectoryName(manifestPath)!;

        ExtensionManifest? manifest;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = ExtensionManifestParser.Parse(json, sourceDirectory, manifestPath);
        }
        catch (Exception ex)
        {
            return new ExtensionInstallOutcome(false, $"manifest.json 解析失败：{ex.Message}");
        }

        if (manifest is null)
        {
            return new ExtensionInstallOutcome(false, "manifest.json 缺少 id。");
        }

        // Guard against a package that pretends to be a different extension.
        if (!string.Equals(manifest.Id, entry.Id, StringComparison.Ordinal))
        {
            return new ExtensionInstallOutcome(
                false,
                $"包内 id '{manifest.Id}' 与商城条目 '{entry.Id}' 不一致，已拒绝安装。");
        }

        var validation = new ExtensionValidator().Validate(manifest);
        if (!validation.IsValid)
        {
            return new ExtensionInstallOutcome(false, $"扩展未通过校验：{validation.Describe()}");
        }

        // Trust is decided by the signature, never by the package's own claim.
        var trustOutcome = ResolveTrust(sourceDirectory, manifest);
        if (!trustOutcome.Accepted)
        {
            return new ExtensionInstallOutcome(false, trustOutcome.Message);
        }

        _log.Info("extension.trust", $"'{entry.Id}' accepted at trust level {trustOutcome.Trust}.");

        var target = Path.Combine(extensionsRoot, entry.Id);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(extensionsRoot);
        MoveDirectory(sourceDirectory, target);

        _log.Info("extension.install", $"Installed extension '{entry.Id}' v{manifest.Version} into '{target}'.");
        return new ExtensionInstallOutcome(true, $"已安装 {manifest.Name} v{manifest.Version}。", target);
    }

    /// <summary>
    /// Decides the trust a package actually receives. A valid signature from a
    /// stored key confers that key's level; anything else is capped at
    /// <see cref="SignatureVerifier.UnsignedTrust"/>, so a manifest claiming
    /// "official" without a signature is rejected rather than believed.
    /// </summary>
    private (bool Accepted, SoftwareTrust Trust, string Message) ResolveTrust(
        string sourceDirectory,
        ExtensionManifest manifest)
    {
        var signature = PackageSignature.TryRead(sourceDirectory);

        SoftwareTrust effective;
        if (signature is null)
        {
            effective = SignatureVerifier.UnsignedTrust;
        }
        else if (_trustedKeys is null)
        {
            return (false, SoftwareTrust.Invalid, "包带有签名，但没有可用的信任密钥列表，已拒绝安装。");
        }
        else
        {
            var check = SignatureVerifier.Verify(sourceDirectory, signature, _trustedKeys);
            if (!check.IsValid)
            {
                return (false, SoftwareTrust.Invalid, $"签名校验未通过：{check.Message}");
            }

            effective = check.Trust;
        }

        if (Rank(manifest.Trust) > Rank(effective))
        {
            return (
                false,
                SoftwareTrust.Invalid,
                $"清单声明信任等级 {manifest.Trust}，但签名只能证明 {effective}，已拒绝安装。");
        }

        return (true, effective, "ok");
    }

    public Task<ExtensionInstallOutcome> UninstallAsync(
        string extensionId,
        string extensionsRoot,
        CancellationToken cancellationToken = default)
    {
        var validation = DirectoryNameValidator.ValidateName(extensionId);
        if (!validation.IsValid)
        {
            return Task.FromResult(new ExtensionInstallOutcome(false, $"扩展 ID 非法：{validation.Describe()}"));
        }

        var target = Path.Combine(extensionsRoot, extensionId);
        if (!Directory.Exists(target))
        {
            return Task.FromResult(new ExtensionInstallOutcome(false, "该扩展未安装。"));
        }

        // A loaded extension keeps its DLL mapped, so release the registry
        // reference and the load context first.
        _lifecycle?.Unload(extensionId);

        if (DirectoryRemoval.TryDelete(target))
        {
            File.Delete(DirectoryRemoval.PendingMarker(extensionsRoot, extensionId));
            _log.Info("extension.uninstall", $"Removed extension '{extensionId}'.");
            return Task.FromResult(new ExtensionInstallOutcome(true, $"已卸载 {extensionId}。"));
        }

        // The runtime refused to release the files. Mark the extension so it is
        // never loaded again, and finish the removal on the next scan.
        Directory.CreateDirectory(extensionsRoot);
        File.WriteAllText(DirectoryRemoval.PendingMarker(extensionsRoot, extensionId), DateTimeOffset.Now.ToString("O"));
        _log.Warn("extension.uninstall", $"'{extensionId}' is still mapped; removal deferred to the next start.");

        return Task.FromResult(new ExtensionInstallOutcome(
            true,
            $"已卸载 {extensionId}；文件仍被占用，将在下次启动 EriReborn 时清理。",
            DeferredRemoval: true));
    }

    /// <summary>
    /// Extracts an archive while refusing entries that would escape the target
    /// directory (zip-slip).
    /// </summary>
    private void ExtractSafely(string archivePath, string targetDirectory)
    {
        var target = Path.GetFullPath(targetDirectory);
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry.
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"归档条目越界，已拒绝：{entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // Different volume: fall back to a recursive copy.
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }
}
