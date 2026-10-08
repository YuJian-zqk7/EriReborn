using EriReborn.Core.Domain;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud;

/// <summary>
/// Capability contract every cloud platform implements (spec 27/28). Adding
/// Google Drive or OneDrive must not require touching the software engine.
/// </summary>
public interface ICloudProvider
{
    /// <summary>Stable provider id, one of <see cref="CloudProviderIds"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>True when listing/downloading needs a user credential.</summary>
    bool RequiresAuthentication { get; }

    /// <summary>How this provider currently obtains its data, shown verbatim in the UI.</summary>
    CloudImplementationKind ImplementationKind { get; }

    /// <summary>Human-readable note about known limits (spec 33).</summary>
    string? LimitationNote { get; }

    /// <summary>
    /// True when this platform can package a whole shared folder into one downloadable archive
    /// (zip). Off unless a provider explicitly implements folder packaging: a platform that cannot
    /// do it says so, and the UI guides the author to pick files inside the folder instead of
    /// pretending a direct link exists (spec v3.1).
    /// </summary>
    bool SupportsFolderPackage => false;

    string? DocumentationUrl { get; }

    /// <summary>Evaluates whether the supplied credential can be used right now.</summary>
    CloudAuthState GetAuthState(CloudCredential? credential);

    Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default);

    Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default);

    Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the files inside a share link. Some platforms allow this without an account. A platform
    /// that cannot do it says so plainly (the default below) instead of failing silently.
    /// </summary>
    Task<CloudListResult> ListShareAsync(string shareUrl, CloudCredential? credential, CancellationToken cancellationToken = default)
        => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "该平台没有提供分享目录读取。"));

    /// <summary>
    /// Resolves a file that already lives in the user's own drive into a direct URL. Platforms that
    /// cannot do it say so plainly instead of pretending.
    /// </summary>
    Task<CloudResolveResult> ResolveDriveFileAsync(string fileId, string fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
        => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.Unsupported, "该平台没有提供网盘文件直链。"));

    /// <summary>
    /// Reads the direct children of one folder inside a share. A null <paramref name="parentItemId"/>
    /// is the share's root, which is what <see cref="ListShareAsync"/> already reads, so a provider
    /// that keeps only that method still answers for the root. Going deeper needs the platform to
    /// accept a parent item id; a platform that cannot says so rather than returning the root again —
    /// returning the root would look like a successful search that found nothing.
    /// </summary>
    Task<CloudListResult> ListShareFolderAsync(
        string shareUrl,
        string? parentItemId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(parentItemId)
            ? ListShareAsync(shareUrl, credential, cancellationToken)
            : Task.FromResult(CloudListResult.Fail(
                CloudErrorKind.Unsupported,
                "该平台没有提供分享内子目录读取，无法进入更深一层。"));

    /// <summary>
    /// Resolves the item a locator names, walking the share's tree to reach it (spec 30). Platforms
    /// that can only look at the share root inherit this, and they stop with "cannot open that
    /// folder" instead of resolving the wrong item.
    ///
    /// <para>
    /// A folder item and a file item are two different downloads (spec v3.1): a reached folder goes
    /// to <see cref="ResolveShareFolderPackageAsync"/>, which only packaging-capable platforms
    /// implement, so a platform that cannot package a folder never quietly resolves the wrong thing.
    /// </para>
    /// </summary>
    async Task<CloudResolveResult> ResolveInShareAsync(
        string shareUrl,
        ResourceLocator locator,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var walk = await ShareTreeWalker
            .WalkAsync(this, shareUrl, locator, credential, cancellationToken)
            .ConfigureAwait(false);

        if (!walk.Success || walk.Item is null)
        {
            return CloudResolveResult.Fail(walk.Error, walk.Message ?? "没有在这个分享里找到该资源。");
        }

        return walk.Item.IsFolder
            ? await ResolveShareFolderPackageAsync(shareUrl, walk.Item, credential, cancellationToken).ConfigureAwait(false)
            : await ResolveShareItemAsync(shareUrl, walk.Item, credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns an item already located inside a share into a ready-to-download handle. The default asks
    /// the platform to resolve it by name, which is the call a root-level source has always used; a
    /// platform that can take a located item by its own id overrides this and skips the extra listing.
    /// </summary>
    Task<CloudResolveResult> ResolveShareItemAsync(
        string shareUrl,
        CloudFile item,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => ResolveAsync(shareUrl, item.Name, credential, cancellationToken);

    /// <summary>
    /// Resolves a whole shared folder into one downloadable archive (zip), without expanding the
    /// folder into file jobs (spec v3.1). The answer is an honest refusal unless the platform really
    /// has a packaging endpoint: <see cref="SupportsFolderPackage"/> must be overridden together with
    /// this method, so the UI can say "pick files inside it" instead of pretending a direct link
    /// exists.
    /// </summary>
    Task<CloudResolveResult> ResolveShareFolderPackageAsync(
        string shareUrl,
        CloudFile folder,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudResolveResult.Fail(
            CloudErrorKind.Unsupported,
            "该平台不支持整个文件夹打包下载，请在插件中展开选择其中的文件。"));
}

/// <summary>How a provider really talks to its platform. Never overstated (spec 33).</summary>
public enum CloudImplementationKind
{
    /// <summary>Uses an official, documented HTTP API.</summary>
    OfficialApi,

    /// <summary>Derives information from page structure; may break when the site changes.</summary>
    HtmlParsing,

    /// <summary>Declared but not yet able to talk to the platform in this build.</summary>
    NotImplemented,
}
