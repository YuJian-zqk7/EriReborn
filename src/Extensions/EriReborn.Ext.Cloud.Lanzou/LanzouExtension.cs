using EriReborn.Cloud.Providers;
using EriReborn.Extension;

namespace EriReborn.Ext.Cloud.Lanzou;

/// <summary>
/// Contributes the Lanzou cloud platform. Its embedded-browser channel is resolved
/// from the process-wide <c>CloudBrowser.Current</c>, so the extension does not have
/// to be handed one.
/// </summary>
public sealed class LanzouExtension : IExtension
{
    public string Id => "erireborn_cloud_lanzou";

    public string DisplayName => "蓝奏云";

    public string Version => "1.0.0";

    public void Initialize(IExtensionHost host)
    {
        if (host.Network is not { } network || host.Credentials is not { } credentials)
        {
            host.Log("error", "缺少 network.http / credentials.read 权限，无法注册蓝奏云。");
            return;
        }

        host.RegisterCloudProvider(new ProviderLanzou(network, credentials));
    }
}
