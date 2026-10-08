using EriReborn.Core.Domain;

namespace EriReborn.Cloud;

/// <summary>
/// Walks a share's folder tree to the item a locator names (spec 30).
///
/// <para>
/// The walk is provider-agnostic on purpose: it opens folders through the provider that owns the
/// share, so no platform's folder semantics live here. A provider that cannot open a folder inside a
/// share says so, and the walk stops with that answer rather than reporting a search it never made.
/// </para>
/// </summary>
public static class ShareTreeWalker
{
    /// <summary>Where a walk ended: the item, the path it took, or the reason it stopped.</summary>
    public sealed record WalkResult(
        bool Success,
        CloudFile? Item,
        string? Path,
        CloudErrorKind Error = CloudErrorKind.None,
        string? Message = null)
    {
        public static WalkResult Ok(CloudFile item, string path) => new(true, item, path);

        public static WalkResult Fail(CloudErrorKind error, string message) =>
            new(false, null, null, error, message);
    }

    /// <summary>
    /// Opens every folder the locator names and then finds the item. A locator with no folders is one
    /// listing of the share root, which is exactly what a source written without a locator has always
    /// needed; a locator that names folders is the case a single listing cannot express.
    /// </summary>
    public static async Task<WalkResult> WalkAsync(
        ICloudProvider provider,
        string shareUrl,
        ResourceLocator locator,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(locator);

        var trail = new List<string>();

        // 根文件夹定位器：kind=folder、没有路径步骤、没有 id 也没有名字——它指的就是
        // 分享本身。返回一个代表分享根的 folder 条目，让调用方走整包下载通道：
        // 支持打包的平台（如 123，根目录 id 固定 "0"）拿到根 folder 即可整包；
        // 不支持的平台如实拒绝"请展开选择其中的文件"，而不是假装在根目录里
        // 按一个空名字找文件。
        if (locator.Kind == ResourceLocatorKind.Folder
            && locator.FolderSegments.Count == 0
            && string.IsNullOrWhiteSpace(locator.ProviderItemId)
            && locator.ItemName is null or { Length: 0 })
        {
            return WalkResult.Ok(
                new CloudFile("0", locator.Path?.TrimEnd('/').Split('/').LastOrDefault() ?? "分享根目录", IsFolder: true),
                "分享根目录");
        }

        // cloud_resource 快捷路径：locator.Path 形如 "by-parent/<父folderId>"，
        // file 的真实 ID 在 locator.ProviderItemId。这种 locator 跳过按名字走 path 的逻辑，
        // 直接用父 folder ID 列父 folder，再按 ProviderItemId 找 file——
        // 因为 PluginNode 的 locator.path 是作者起的中文名（如"系统基础"），
        // 不是云盘里的真实文件夹名，按名字在分享根目录找根本找不到。
        if (!string.IsNullOrWhiteSpace(locator.Path) &&
            locator.Path.StartsWith("by-parent/", StringComparison.Ordinal))
        {
            var parentId = locator.Path.Substring("by-parent/".Length);
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                var parentListing = await provider
                    .ListShareFolderAsync(shareUrl, parentItemId: parentId, credential, cancellationToken)
                    .ConfigureAwait(false);

                if (!parentListing.Success)
                {
                    return WalkResult.Fail(parentListing.Error, parentListing.Message ?? "无法读取这个分享的目录。");
                }

                var directItem = ResourceLocatorMatcher.Find(locator, parentListing.Files);
                if (directItem is not null)
                {
                    return WalkResult.Ok(directItem, locator.Path);
                }

                return WalkResult.Fail(
                    CloudErrorKind.NotFound,
                    $"资源位置不存在：分享里没有「{locator.ItemName ?? locator.ProviderItemId}」（已到：父文件夹）。");
            }
        }

        var listing = await provider
            .ListShareFolderAsync(shareUrl, parentItemId: null, credential, cancellationToken)
            .ConfigureAwait(false);

        if (!listing.Success)
        {
            return WalkResult.Fail(listing.Error, listing.Message ?? "无法读取这个分享的目录。");
        }

        foreach (var step in locator.FolderSegments)
        {
            var folder = ResourceLocatorMatcher.FindByName(step, listing.Files);
            if (folder is null)
            {
                // Two same-named folders and no folder at all are different problems: guessing
                // would walk into the wrong one, so each gets its own words.
                var sameName = ResourceLocatorMatcher.CountByName(step, listing.Files);
                return WalkResult.Fail(
                    CloudErrorKind.NotFound,
                    sameName > 1
                        ? $"分享里有 {sameName} 个叫「{step}」的文件夹，没法判断是哪一个，请重新定位（已到：{Describe(trail)}）。"
                        : $"资源位置不存在：分享里没有叫做「{step}」的文件夹（已到：{Describe(trail)}）。");
            }

            if (!folder.IsFolder)
            {
                return WalkResult.Fail(
                    CloudErrorKind.NotFound,
                    $"资源位置不存在：「{step}」在分享里是文件而不是文件夹，路径到此为止（已到：{Describe(trail)}）。");
            }

            trail.Add(folder.Name);

            listing = await provider
                .ListShareFolderAsync(shareUrl, folder.Id, credential, cancellationToken)
                .ConfigureAwait(false);

            if (!listing.Success)
            {
                return WalkResult.Fail(listing.Error, listing.Message ?? "无法读取这个分享的目录。");
            }
        }

        var item = ResourceLocatorMatcher.Find(locator, listing.Files);
        if (item is not null)
        {
            return WalkResult.Ok(item, string.Join("/", trail.Append(item.Name)));
        }

        // The location named a name that several entries share: the resource may well exist, the
        // locator is just not specific enough. Saying "not found" here would send the user looking
        // for a missing file instead of re-picking the right one.
        if (locator.ItemName is { Length: > 0 } wanted && ResourceLocatorMatcher.CountByName(wanted, listing.Files) > 1)
        {
            return WalkResult.Fail(
                CloudErrorKind.NotFound,
                $"找到多个叫「{wanted}」的资源，没法判断是哪一个，请重新选择资源位置（已到：{Describe(trail)}）。");
        }

        // "the location is gone", not "the resource is gone": the resource may well still be defined,
        // and it is this source's pointer into the share that no longer matches (spec 30).
        return WalkResult.Fail(
            CloudErrorKind.NotFound,
            $"资源位置不存在：分享里没有「{locator.ItemName ?? locator.ProviderItemId}」（已到：{Describe(trail)}）。");
    }

    /// <summary>Where the walk had got to, in the words the share itself uses.</summary>
    private static string Describe(IReadOnlyCollection<string> trail)
        => trail.Count == 0 ? "分享根目录" : "分享根目录/" + string.Join("/", trail);
}
