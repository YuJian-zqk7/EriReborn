using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud.Providers;

/// <summary>
/// Shared behaviour for the first-stage providers. Authentication gates and
/// failure reporting are identical for every platform (spec 27); only the
/// platform conversation differs.
/// </summary>
public abstract class CloudProviderBase : ICloudProvider
{
    protected CloudProviderBase(
        INetworkService network,
        ICredentialStore credentials,
        CloudRequestGate? gate = null,
        ICloudBrowserChannel? browser = null)
    {
        Network = network;
        Credentials = credentials;
        Browser = browser;

        // One gate per provider instance: each platform has its own limits, so
        // throttling is per-platform by construction rather than by remembering
        // to key a shared one correctly.
        Gate = gate ?? new CloudRequestGate(AppLog.For("Cloud"));
    }

    protected INetworkService Network { get; }

    /// <summary>Spaces and retries this provider's calls to its platform.</summary>
    protected CloudRequestGate Gate { get; }

    /// <summary>
    /// The embedded browser, when one is attached. Platforms whose pages are JavaScript challenges
    /// borrow it; a provider with no browser must say so instead of guessing at a page it never saw.
    /// </summary>
    protected ICloudBrowserChannel? Browser { get; }

    /// <summary>This provider's browser, or the one the running app installed, whichever exists.</summary>
    protected ICloudBrowserChannel? ActiveBrowser => CloudBrowser.Resolve(Browser);

    /// <summary>
    /// Sends a platform request through this provider's throttle. Every outbound
    /// call goes through here, so no code path can accidentally bypass it.
    /// </summary>
    protected Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
        => Gate.SendAsync(DisplayName, Network.Client, requestFactory, cancellationToken);

    protected ICredentialStore Credentials { get; }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract bool RequiresAuthentication { get; }

    public abstract CloudImplementationKind ImplementationKind { get; }

    public virtual string? LimitationNote => null;

    public virtual string? DocumentationUrl => null;

    public virtual CloudAuthState GetAuthState(CloudCredential? credential)
    {
        if (!RequiresAuthentication)
        {
            return CloudAuthState.NotRequired;
        }

        if (credential is null || !credential.HasAnySecret)
        {
            return CloudAuthState.AuthRequired;
        }

        if (credential.ExpiresAt is { } expires && expires <= DateTimeOffset.Now)
        {
            return CloudAuthState.Expired;
        }

        return CloudAuthState.Authenticated;
    }

    public virtual Task<CloudListResult> ListAsync(
        string folderId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => ListChildrenAsync(folderId, credential, cancellationToken);

    public virtual Task<CloudListResult> ListChildrenAsync(
        string folderId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudListResult.Fail(
            CloudErrorKind.Unsupported,
            $"{DisplayName} folder listing is not available in this build."));

    public virtual Task<CloudResolveResult> ResolveAsync(
        string shareUrl,
        string? fileName,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudResolveResult.Fail(
            CloudErrorKind.Unsupported,
            $"{DisplayName} share resolution is not available in this build."));

    /// <summary>Loads the credential for this provider from the secure store.</summary>
    public async Task<CloudCredential?> LoadCredentialAsync(CancellationToken cancellationToken = default)
    {
        if (!Credentials.IsAvailable)
        {
            return null;
        }

        var token = await Credentials.GetAsync(CredentialKey(Id, "token"), cancellationToken).ConfigureAwait(false);
        var cookie = await Credentials.GetAsync(CredentialKey(Id, "cookie"), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        return new CloudCredential(Id, token, cookie);
    }

    /// <summary>
    /// Writes a credential into the secure store. The login entry needs this: reading existed
    /// from the start, writing did not, so no token or cookie could ever be entered.
    /// </summary>
    public async Task SaveCredentialAsync(CloudCredential credential, CancellationToken cancellationToken = default)
    {
        if (!Credentials.IsAvailable)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(credential.Token))
        {
            await Credentials.SetAsync(CredentialKey(Id, "token"), credential.Token!, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(credential.Cookie))
        {
            await Credentials.SetAsync(CredentialKey(Id, "cookie"), credential.Cookie!, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes every stored field for this provider ("解除绑定").</summary>
    public async Task ClearCredentialAsync(CancellationToken cancellationToken = default)
    {
        if (!Credentials.IsAvailable)
        {
            return;
        }

        await Credentials.RemoveAsync(CredentialKey(Id, "token"), cancellationToken).ConfigureAwait(false);
        await Credentials.RemoveAsync(CredentialKey(Id, "cookie"), cancellationToken).ConfigureAwait(false);
    }

    public static string CredentialKey(string providerId, string field) => $"cloud/{providerId}/{field}";

    protected CloudResolveResult AuthGate(CloudCredential? credential)
    {
        var state = GetAuthState(credential);
        return state switch
        {
            CloudAuthState.NotRequired or CloudAuthState.Authenticated => CloudResolveResult.Ok(
                new CloudDownloadHandle(
                    new CloudFile(string.Empty, string.Empty, false),
                    string.Empty)),
            CloudAuthState.Expired => CloudResolveResult.Fail(
                CloudErrorKind.AuthRequired,
                $"{DisplayName} 凭据已过期，请重新登录。"),
            CloudAuthState.Unsupported => CloudResolveResult.Fail(
                CloudErrorKind.Unsupported,
                $"{DisplayName} 在当前环境不受支持。"),
            _ => CloudResolveResult.Fail(
                CloudErrorKind.AuthRequired,
                $"{DisplayName} 需要登录后才能读取目录。"),
        };
    }

    /// <summary>
    /// Reads the files inside a share link. Declared here (not only on the interface) because a provider
    /// implements the interface through this base type: a method added only in a derived class is not part
    /// of that mapping and would silently fall back to the interface default.
    /// </summary>
    public virtual Task<CloudListResult> ListShareAsync(
        string shareUrl,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "该平台没有提供分享目录读取。"));


    /// <summary>Resolves a file in the user's own drive. Default: stated plainly, never faked.</summary>
    public virtual Task<CloudResolveResult> ResolveDriveFileAsync(
        string fileId,
        string fileName,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.Unsupported, "该平台没有提供网盘文件直链。"));

    /// <summary>
    /// Opens one folder inside a share. Declared on the base as well as the interface for the same
    /// reason <see cref="ListShareAsync"/> is: a method that exists only on a derived class is not
    /// what the interface maps to, and the fallback would be reached without anyone noticing.
    /// </summary>
    public virtual Task<CloudListResult> ListShareFolderAsync(
        string shareUrl,
        string? parentItemId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(parentItemId)
            ? ListShareAsync(shareUrl, credential, cancellationToken)
            : Task.FromResult(CloudListResult.Fail(
                CloudErrorKind.Unsupported,
                $"{DisplayName} 没有提供分享内子目录读取，无法进入更深一层。"));

    /// <summary>
    /// Walks the share to the item the locator names, then asks the platform for its download link.
    /// The walk itself is provider-agnostic, so no platform's folder semantics are written here.
    /// A reached folder is a whole-folder package download (spec v3.1), routed to its own method so
    /// a platform that cannot package one answers honestly instead of handing back a wrong file.
    /// </summary>
    public virtual async Task<CloudResolveResult> ResolveInShareAsync(
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

    /// <summary>Turns a located item into a download handle. Default: resolve it by name, as before.</summary>
    public virtual Task<CloudResolveResult> ResolveShareItemAsync(
        string shareUrl,
        CloudFile item,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => ResolveAsync(shareUrl, item.Name, credential, cancellationToken);

    /// <summary>
    /// Whether this platform can package a whole shared folder into one zip (spec v3.1). Off unless
    /// the provider overrides it together with <see cref="ResolveShareFolderPackageAsync"/>.
    /// </summary>
    public virtual bool SupportsFolderPackage => false;

    /// <summary>
    /// Whole-folder package download. Default is an honest, actionable refusal: a platform that has
    /// no packaging endpoint must not hand back a file link that silently fetches the wrong thing.
    /// </summary>
    public virtual Task<CloudResolveResult> ResolveShareFolderPackageAsync(
        string shareUrl,
        CloudFile folder,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CloudResolveResult.Fail(
            CloudErrorKind.Unsupported,
            "该平台不支持整个文件夹打包下载，请在插件中展开选择其中的文件。"));

}
