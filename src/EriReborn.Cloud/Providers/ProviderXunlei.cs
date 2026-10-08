using EriReborn.Platform.Abstractions;

namespace EriReborn.Cloud.Providers;

/// <summary>
/// 迅雷云盘. Listed in the product spec as a first-stage platform and modelled
/// as an equal peer, not as a special case (spec 26/76).
/// </summary>
public sealed class ProviderXunlei(
    INetworkService network,
    ICredentialStore credentials,
    ICloudBrowserChannel? browser = null)
    : CloudProviderBase(network, credentials, browser: browser)
{
    public override string Id => CloudProviderIds.Xunlei;

    public override string DisplayName => "迅雷云盘";

    public override bool RequiresAuthentication => true;

    public override CloudImplementationKind ImplementationKind => CloudImplementationKind.NotImplemented;

    public override string? DocumentationUrl => "https://pan.xunlei.com/";

    public override string? LimitationNote =>
        "迅雷云盘接口要求页面签发的 x-captcha-token（纯 HTTP 返回 captcha_invalid），需要内置浏览器会话。"
        + "在真实验证通过前不提供分享文件夹整包打包下载，可在插件中展开文件夹选择其中的文件（spec v3.1）。";

    public override async Task<CloudListResult> ListChildrenAsync(
        string folderId,
        CloudCredential? credential,
        CancellationToken cancellationToken = default)
    {
        var state = GetAuthState(credential);
        if (state is CloudAuthState.AuthRequired or CloudAuthState.Expired)
        {
            return CloudListResult.Fail(CloudErrorKind.AuthRequired, "迅雷云盘需要有效的账号会话。");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return CloudListResult.Fail(
            CloudErrorKind.Unsupported,
            CloudBrowser.IsAvailable
                ? "迅雷分享接口要求 x-captcha-token，需要先在内置浏览器里打开该分享页完成验证。"
                : "迅雷分享接口要求 x-captcha-token，且当前没有可用的内置浏览器会话。");
    }
}
