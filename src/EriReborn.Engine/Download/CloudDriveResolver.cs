using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Download;

/// <summary>
/// Resolves a file that lives in the user's own drive rather than behind a share link.
///
/// <para>
/// Such a file has a platform and the platform's own id for it, but no share URL — which is exactly
/// what distinguishes it from a share source. Because it is an ordinary source, it travels the same
/// resolver → route → engine → job path as everything else instead of the page downloading it by
/// hand. The source names the item through <see cref="ResourceLocator.ProviderItemId"/>, the id a
/// rename or a move does not change.
/// </para>
/// </summary>
public sealed class CloudDriveResolver(CloudProviderRegistry registry, IAppLogger log) : IDownloadResolver
{
    public string Id => "cloud-drive";

    public bool CanResolve(SoftwareSource source)
        => source.Kind == SourceKind.CloudShare
        && string.IsNullOrWhiteSpace(source.ShareUrl)
        && !string.IsNullOrWhiteSpace(source.ProviderId)
        && !string.IsNullOrWhiteSpace(source.Locator?.ProviderItemId);

    public async Task<ResolveOutcome> ResolveAsync(
        SoftwareSource source,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Resolve(source);
        if (provider is null)
        {
            return ResolveOutcome.Fail(
                ResolveStatus.ProviderError,
                $"未注册的云盘平台 '{source.ProviderId ?? "(空)"}'。",
                CloudErrorKind.Unsupported);
        }

        var credential = provider is CloudProviderBase providerBase
            ? await providerBase.LoadCredentialAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var authState = provider.GetAuthState(credential);
        if (authState is CloudAuthState.AuthRequired or CloudAuthState.Expired)
        {
            log.Warn("resolve.auth", $"{provider.DisplayName} needs authentication before it can resolve a drive file.");
            return ResolveOutcome.Fail(
                ResolveStatus.AuthRequired,
                $"{provider.DisplayName} 需要登录后才能下载我的文件（当前状态：{authState}）。",
                CloudErrorKind.AuthRequired);
        }

        var locator = source.Locator!;
        var itemId = locator.ProviderItemId!;
        var resolved = await provider
            .ResolveDriveFileAsync(itemId, locator.ItemName ?? source.FileName ?? string.Empty, credential, cancellationToken)
            .ConfigureAwait(false);

        if (!resolved.Success || resolved.Handle is null)
        {
            return ResolveOutcome.Fail(
                ResolveStatus.ProviderError,
                resolved.Message ?? $"{provider.DisplayName} 未能解析这个网盘文件。",
                resolved.Error);
        }

        var handle = resolved.Handle;
        var route = new DownloadRoute(
            DownloadRouteKind.Resolver,
            handle.DownloadUrl,
            ProviderId: provider.Id,
            Headers: handle.Headers,
            RequiresSession: credential?.HasAnySecret == true,
            ContentLength: handle.File.SizeBytes,
            FileName: handle.File.Name ?? source.FileName,
            Note: handle.Note);

        return ResolveOutcome.Ok(route, $"已通过 {provider.DisplayName} 解析出我的文件下载地址。");
    }
}
