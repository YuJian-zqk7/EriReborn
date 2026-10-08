using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Download;

/// <summary>
/// Resolves a cloud share link by asking the provider that owns it.
///
/// Which provider is asked comes from the source, never from a branch on a
/// provider id — so a new provider needs no change here (spec 135/161).
/// </summary>
public sealed class CloudShareResolver(CloudProviderRegistry registry, IAppLogger log) : IDownloadResolver
{
    public string Id => "cloud-share";

    public bool CanResolve(SoftwareSource source) => source.Kind == SourceKind.CloudShare;

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

        if (string.IsNullOrWhiteSpace(source.ShareUrl))
        {
            return ResolveOutcome.Fail(
                ResolveStatus.Failed,
                $"{provider.DisplayName} 来源缺少分享链接。",
                CloudErrorKind.NotFound);
        }

        var credential = provider is CloudProviderBase providerBase
            ? await providerBase.LoadCredentialAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var authState = provider.GetAuthState(credential);
        if (authState is CloudAuthState.AuthRequired or CloudAuthState.Expired)
        {
            // Missing credentials are their own state, not a generic failure: the
            // user has to do something different about it.
            log.Warn("resolve.auth", $"{provider.DisplayName} needs authentication before it can resolve links.");
            return ResolveOutcome.Fail(
                ResolveStatus.AuthRequired,
                $"{provider.DisplayName} 需要登录后才能解析分享链接（当前状态：{authState}）。",
                CloudErrorKind.AuthRequired);
        }

        // A locator is used whenever it says something the single-level call cannot: folders to walk
        // through, or the platform's own id for the item — which survives a rename a name match
        // would miss. A locator that names only a root-level item adds nothing to fileName, so that
        // case keeps the shorter path and its exact error messages. A platform that cannot open
        // folders inside a share says so through the walker instead of resolving the wrong item.
        var resolved = source.Locator is { IsEmpty: false } locator
            && (locator.NeedsFolderWalk || !string.IsNullOrWhiteSpace(locator.ProviderItemId))
            ? await provider
                .ResolveInShareAsync(source.ShareUrl, locator, credential, cancellationToken)
                .ConfigureAwait(false)
            : await provider
                .ResolveAsync(source.ShareUrl, source.FileName, credential, cancellationToken)
                .ConfigureAwait(false);

        if (!resolved.Success || resolved.Handle is null)
        {
            // The provider's own reason travels through, so a platform that answers "sign in first"
            // keeps saying that instead of becoming a generic failure the user cannot act on.
            // Unsupported is a real, final "this build cannot do it" (e.g. a platform with no folder
            // packaging endpoint), not a provider hiccup to retry or a missing entry (spec v3.1).
            var status = resolved.Error switch
            {
                CloudErrorKind.AuthRequired => ResolveStatus.AuthRequired,
                CloudErrorKind.Unsupported => ResolveStatus.Failed,
                _ => ResolveStatus.ProviderError,
            };

            var message = resolved.Message ?? $"{provider.DisplayName} 未能解析该分享链接。";

            // A source with no locator can only be looked for by name at the share root, and the
            // official share's root holds folders and no files at all. Saying which of the two
            // problems this is — the entry has no location recorded, rather than the file being gone
            // — is what makes it fixable (spec 30).
            if (source.Locator is null or { IsEmpty: true })
            {
                message += "（这条来源只登记了分享链接、没有登记它在这份分享里的位置，所以只能按文件名在分享根目录找；"
                    + "请在插件编辑器里为它重新定位。）";
            }

            return ResolveOutcome.Fail(status, message, resolved.Error);
        }

        var handle = resolved.Handle;

        // A folder resource downloads as one server-side archive: its saved name is
        // 「文件夹名.zip」, and the engine never auto-extracts it (spec v3.1). The suffix is added
        // here, at the single resolver boundary, instead of trusting every provider to remember it.
        var fileName = handle.File.Name ?? source.FileName;
        if (handle.File.IsFolder && fileName is { Length: > 0 }
            && !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".zip";
        }

        var route = new DownloadRoute(
            DownloadRouteKind.Resolver,
            handle.DownloadUrl,
            ProviderId: provider.Id,
            Headers: handle.Headers,

            // A session was needed to obtain this URL, so it may be needed again
            // when the URL expires and has to be re-resolved.
            RequiresSession: credential?.HasAnySecret == true,

            ContentLength: handle.File.SizeBytes,
            FileName: fileName,
            Note: Describe(handle, provider));

        return ResolveOutcome.Ok(route, $"已通过 {provider.DisplayName} 解析出下载地址。");
    }

    private static string Describe(CloudDownloadHandle handle, ICloudProvider provider)
    {
        // A parsed page is not an official API, and the difference matters when it
        // breaks (spec 34/200).
        var origin = handle.IsHtmlParsed
            ? "地址由页面结构解析得到，网站改版后可能失效"
            : "地址由官方接口返回";

        // A folder handle is the platform's server-side whole-folder archive (spec v3.1):
        // say so plainly, since it changes what lands on disk (one zip, not the folder's files).
        var scope = handle.File.IsFolder ? "整文件夹打包 zip；" : string.Empty;

        return handle.Note is null
            ? $"{provider.DisplayName}：{scope}{origin}"
            : $"{provider.DisplayName}：{scope}{origin}；{handle.Note}";
    }
}
