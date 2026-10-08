using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Download;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Engine.Cloud;

public enum CloudOutcome
{
    Success,
    AuthRequired,
    RateLimited,
    Forbidden,
    NotFound,
    Unsupported,
    ParseFailure,
    HashMismatch,
    DownloadFailed,
    Cancelled,
}

public sealed record CloudDownloadOutcome(
    CloudOutcome Outcome,
    string? Path,
    string Message,
    bool HtmlParsed = false,
    string? ProviderId = null)
{
    public bool IsSuccess => Outcome == CloudOutcome.Success;
}

/// <summary>
/// Turns a SoftwareSource of kind CloudShare into a downloaded local file
/// (spec 29). Which provider is used comes from the source, never from a
/// hard-coded branch (spec 27).
/// </summary>
public sealed class CloudShareLinkResolver(
    CloudProviderRegistry registry,
    HttpDownloader downloader,
    IAppLogger log)
{
    public async Task<CloudDownloadOutcome> DownloadAsync(
        SoftwareDefinition software,
        SoftwareSource source,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind != SourceKind.CloudShare)
        {
            return new CloudDownloadOutcome(CloudOutcome.Unsupported, null, "该来源不是云盘分享。");
        }

        var provider = registry.Resolve(source);
        if (provider is null)
        {
            return new CloudDownloadOutcome(
                CloudOutcome.Unsupported,
                null,
                $"未注册的云盘平台 '{source.ProviderId}'。",
                ProviderId: source.ProviderId);
        }

        if (string.IsNullOrWhiteSpace(source.ShareUrl))
        {
            return new CloudDownloadOutcome(CloudOutcome.NotFound, null, $"{provider.DisplayName} 来源缺少分享链接。", ProviderId: provider.Id);
        }

        var credential = provider is CloudProviderBase providerBase
            ? await providerBase.LoadCredentialAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var authState = provider.GetAuthState(credential);
        if (authState is CloudAuthState.AuthRequired or CloudAuthState.Expired)
        {
            log.Warn("cloud.auth", $"{provider.DisplayName} needs authentication before listing/downloading.");
            return new CloudDownloadOutcome(
                CloudOutcome.AuthRequired,
                null,
                $"{provider.DisplayName} 需要登录后才能下载（当前状态：{authState}）。",
                ProviderId: provider.Id);
        }

        // One refresh, at most. A share link goes stale between listing and
        // downloading, and asking the provider for a fresh one is the difference
        // between "try again tomorrow" and "it just works" (spec 62).
        var refreshed = false;

        while (true)
        {
            EriReborn.Cloud.CloudResolveResult resolveOutcome;
            string? itemFileName = source.FileName;
            // 文件夹打包成 zip 时跳过 ExpectedSize 校验：zip 头/索引会让实际字节数
            // 比文件夹原始 Size 多几百字节，传 source.SizeBytes 必然校验失败（实测差 200 字节）。
            var isFolderPackage = false;

            // A locator walks the share tree folder by folder to the item this resource means.
            // The root-only ResolveAsync can never reach a nested file — which is exactly how
            // every entry living below the share's category folders failed with "分享里找不到
            // 这个文件名" even though the file existed several levels down (spec 29/30).
            if (source.Locator is { IsEmpty: false } locator)
            {
                var walk = await ShareTreeWalker
                    .WalkAsync(provider, source.ShareUrl!, locator, credential, cancellationToken)
                    .ConfigureAwait(false);

                if (!walk.Success || walk.Item is null)
                {
                    return new CloudDownloadOutcome(
                        Map(walk.Error),
                        null,
                        walk.Message ?? "云盘目录定位失败。",
                        ProviderId: provider.Id);
                }

                itemFileName = walk.Item.IsFolder
                    ? walk.Item.Name + ".zip"
                    : walk.Item.Name;
                // 文件夹走 ResolveShareFolderPackageAsync（123 云盘 batch_download_share_info
                // 整夹打包成 zip 直链），文件走 ResolveShareItemAsync（download_info 单文件直链）。
                // 不分分支直接调 ResolveShareItemAsync 是 bug：123 对文件夹的 list 响应 Etag 是空串，
                // download_info 接口校验 Etag 必填非空且格式正确，文件夹 ID 走它必然报
                // "请输入Etag" / "Etag格式异常"。CloudProviderBase.ResolveInShareAsync 自己有
                // 这个分支判断，但这里没用它，直接调 ResolveShareItemAsync 把分支绕过了。
                isFolderPackage = walk.Item.IsFolder;
                resolveOutcome = isFolderPackage
                    ? await provider.ResolveShareFolderPackageAsync(source.ShareUrl!, walk.Item, credential, cancellationToken).ConfigureAwait(false)
                    : await provider.ResolveShareItemAsync(source.ShareUrl!, walk.Item, credential, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                resolveOutcome = await provider
                    .ResolveAsync(source.ShareUrl, source.FileName, credential, cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolved = resolveOutcome;
            if (!resolved.Success || resolved.Handle is null)
            {
                return new CloudDownloadOutcome(
                    Map(resolved.Error),
                    null,
                    resolved.Message ?? "云盘解析失败。",
                    ProviderId: provider.Id);
            }

            var handle = resolved.Handle;
            var download = await downloader.DownloadAsync(
                new DownloadRequest
                {
                    Url = handle.DownloadUrl,
                    DestinationPath = destinationPath,
                    ExpectedSha256 = source.Sha256,
                    // 文件夹打包成 zip 时跳过 ExpectedSize 校验：zip 头/索引会让实际
                    // 字节数比文件夹原始 Size 多几百字节，传 source.SizeBytes 必然校验失败
                    // （实测 123 云盘文件夹 Size=14628661，打包后 zip=14628861，差 200 字节）。
                    // 文件正常传 source.SizeBytes，让 HashMismatch/截断检测照常生效。
                    ExpectedSize = isFolderPackage ? null : source.SizeBytes,
                    SoftwareId = software.Id,
                    Version = source.Version ?? software.Version,
                    Architecture = source.Architecture ?? software.Architecture,
                    ProviderId = provider.Id,
                    FileName = itemFileName,
                    Headers = handle.Headers,
                },
                progress,
                cancellationToken).ConfigureAwait(false);

            if (download.IsSuccess)
            {
                var note = handle.Note ?? $"已通过 {provider.DisplayName} 获取。";
                return new CloudDownloadOutcome(
                    CloudOutcome.Success,
                    download.Path,
                    refreshed ? note + "（直链曾失效，已重新解析一次）" : note,
                    handle.IsHtmlParsed,
                    provider.Id);
            }

            if (!refreshed && LinkExpiryPolicy.LooksLikeStaleLink(download.State, download.HttpStatusCode))
            {
                refreshed = true;
                log.Info(
                    "cloud.link_refreshed",
                    $"{provider.DisplayName} 的直链{LinkExpiryPolicy.Describe(download.HttpStatusCode)}，重新解析一次。");

                // Loop once with a freshly minted handle.
                continue;
            }

            var outcome = download.State switch
            {
                DownloadState.HashMismatch => CloudOutcome.HashMismatch,
                DownloadState.Cancelled => CloudOutcome.Cancelled,
                _ => CloudOutcome.DownloadFailed,
            };

            // Saying a refresh happened matters: without it the second failure looks
            // like the first one and the user cannot tell the difference.
            var message = download.Message ?? "下载失败。";
            if (refreshed)
            {
                message += "（已重新解析直链一次，仍然失败）";
            }

            return new CloudDownloadOutcome(outcome, null, message, handle.IsHtmlParsed, provider.Id);
        }
    }

    private static CloudOutcome Map(CloudErrorKind kind) => kind switch
    {
        CloudErrorKind.AuthRequired => CloudOutcome.AuthRequired,
        CloudErrorKind.RateLimited => CloudOutcome.RateLimited,
        CloudErrorKind.Forbidden => CloudOutcome.Forbidden,
        CloudErrorKind.NotFound => CloudOutcome.NotFound,
        CloudErrorKind.ParseFailure => CloudOutcome.ParseFailure,
        CloudErrorKind.Network => CloudOutcome.DownloadFailed,
        CloudErrorKind.None => CloudOutcome.Success,
        _ => CloudOutcome.Unsupported,
    };
}
