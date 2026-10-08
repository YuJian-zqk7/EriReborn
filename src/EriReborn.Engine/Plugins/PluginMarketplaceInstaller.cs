using EriReborn.Core.Logging;
using EriReborn.Engine.Download;

namespace EriReborn.Engine.Plugins;

public sealed record PluginMarketplaceInstallResult(
    bool Succeeded,
    PluginImportStage? Stage,
    string Message,
    int AddedResources,
    PluginTrust Trust = PluginTrust.Unknown,
    bool WasUpdate = false);

/// <summary>
/// Installs and updates plugins from the marketplace.
///
/// The order matters: download, verify integrity, verify the signature, import,
/// then register. A plugin that fails any of those is not registered at all, so
/// a partial install cannot leave half a plugin in the software list.
///
/// A plugin's own resources are replaced on update and nothing else is touched
/// (spec 198).
/// </summary>
public sealed class PluginMarketplaceInstaller(
    HttpDownloader downloader,
    PluginImportService import,
    PluginRegistry registry,
    PluginInstallationStore installations,
    IAppLogger log)
{
    public Task<PluginMarketplaceInstallResult> InstallAsync(
        PluginMarketplaceEntry entry,
        string pluginsDirectory,
        IReadOnlyCollection<string> existingIds,
        CancellationToken cancellationToken = default)
        => InstallCoreAsync(entry, pluginsDirectory, existingIds, replace: false, cancellationToken);

    /// <summary>
    /// Installs a newer version of a plugin already present.
    ///
    /// The ids this plugin currently owns are excluded from the conflict check:
    /// an update would otherwise be rejected as colliding with its own previous
    /// version, which is the one case where the same ids are expected.
    /// </summary>
    public Task<PluginMarketplaceInstallResult> UpdateAsync(
        PluginMarketplaceEntry entry,
        string pluginsDirectory,
        IReadOnlyCollection<string> catalogIds,
        CancellationToken cancellationToken = default)
    {
        var owned = new HashSet<string>(registry.OwnedIds(entry.Id), StringComparer.Ordinal);

        var existing = catalogIds
            .Concat(registry.Imported.Where(item => !owned.Contains(item.Id)).Select(item => item.Id))
            .ToList();

        return InstallCoreAsync(entry, pluginsDirectory, existing, replace: true, cancellationToken);
    }

    private async Task<PluginMarketplaceInstallResult> InstallCoreAsync(
        PluginMarketplaceEntry entry,
        string pluginsDirectory,
        IReadOnlyCollection<string> existingIds,
        bool replace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
        {
            return Fail("商城索引没有为该插件提供下载地址。");
        }

        Directory.CreateDirectory(pluginsDirectory);

        var destination = Path.Combine(pluginsDirectory, entry.Id + ResourcePlugin.FileExtension);
        var download = await downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = entry.DownloadUrl,
                DestinationPath = destination,
                ExpectedSha256 = entry.Sha256,
                ExpectedSize = entry.SizeBytes,
                FileName = entry.Id + ResourcePlugin.FileExtension,
                Version = entry.Version,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!download.IsSuccess)
        {
            // The integrity failures are already distinguished by the downloader
            // (HashMismatch / SizeMismatch / HttpError), and are reported as-is.
            return Fail($"下载失败（{download.State}）：{download.Message}");
        }

        var signature = await FetchSignatureAsync(entry, destination, cancellationToken).ConfigureAwait(false);
        if (entry.IsSigned && signature is null)
        {
            // The index claiming a signature is not a signature.
            return Fail("索引声明该插件已签名，但签名文件未能取得。");
        }

        var json = await File.ReadAllTextAsync(destination, cancellationToken).ConfigureAwait(false);
        var result = import.Import(json, signature, existingIds);
        if (!result.Succeeded)
        {
            return Fail(result.Message, result.Stage);
        }

        // The index says which plugin this is; the file says which plugin it is.
        // When they disagree, resources would be tagged with one id and later
        // looked up by the other — an update would silently append instead of
        // replacing, leaving the old version's resources in the list.
        if (!string.Equals(result.PluginId, entry.Id, StringComparison.Ordinal))
        {
            return Fail(
                $"插件文件声明 id '{result.PluginId ?? "(空)"}'，商城条目声明 '{entry.Id}'；两者不一致，拒绝安装。");
        }

        var trust = signature is null ? PluginTrust.Unknown : PluginTrust.Official;

        // Replacement removes exactly this plugin's previous resources, and refuses
        // the whole set if any id belongs to another plugin. Used for a fresh install
        // too: the plugin owns nothing then, so it is the same operation, and it
        // refuses the same collisions that a plain add would have silently skipped.
        var outcome = registry.ReplaceChecked(entry.Id, result.Accepted);
        if (!outcome.IsComplete)
        {
            // Nothing was committed, so whatever is installed keeps working. Saying
            // "installed" here would claim resources that were never registered.
            return Fail(
                $"这些资源 id 已属于其它插件：{string.Join("、", outcome.Rejected)}。本次{(replace ? "更新" : "安装")}未生效。");
        }

        var added = outcome.Added;

        // Recorded only after the resources are actually in place.
        var recorded = installations.Record(new InstalledPlugin
        {
            Id = entry.Id,
            Name = entry.Name,
            Version = entry.Version,
            Sha256 = entry.Sha256,
            SourceUrl = entry.DownloadUrl,
            InstalledAt = DateTimeOffset.UtcNow,
            ResourceIds = registry.OwnedIds(entry.Id),
        });

        if (!recorded)
        {
            log.Warn("plugin.marketplace", $"插件 '{entry.Id}' 已安装，但安装记录未能写入磁盘。");
        }

        log.Info("plugin.marketplace", $"插件 '{entry.Id}' {(replace ? "更新" : "安装")}完成，新增 {added} 条资源。");

        var verb = replace ? "已更新" : "已安装";
        return new PluginMarketplaceInstallResult(
            true,
            result.Stage,
            added == 0
                ? $"{verb} {entry.Name}，资源没有变化。"
                : $"{verb} {entry.Name}，新增 {added} 条资源。",
            added,
            trust,
            replace);
    }

    private async Task<string?> FetchSignatureAsync(
        PluginMarketplaceEntry entry,
        string pluginPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.SignatureUrl))
        {
            return null;
        }

        var signaturePath = pluginPath + PluginSignature.FileExtension;
        var download = await downloader.DownloadAsync(
            new DownloadRequest
            {
                Url = entry.SignatureUrl,
                DestinationPath = signaturePath,
                FileName = Path.GetFileName(signaturePath),
                Version = entry.Version,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return download.IsSuccess
            ? await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static PluginMarketplaceInstallResult Fail(string message, PluginImportStage? stage = null)
        => new(false, stage, message, 0);
}
