using EriReborn.Cloud.Providers;
using EriReborn.Extension;

namespace EriReborn.Ext.Cloud.Quark;

/// <summary>Contributes the Quark cloud-drive platform.</summary>
public sealed class QuarkExtension : IExtension
{
    public string Id => "erireborn_cloud_quark";

    public string DisplayName => "夸克网盘";

    public string Version => "1.0.0";

    public void Initialize(IExtensionHost host)
    {
        if (host.Network is not { } network || host.Credentials is not { } credentials)
        {
            host.Log("error", "缺少 network.http / credentials.read 权限，无法注册夸克网盘。");
            return;
        }

        host.RegisterCloudProvider(new ProviderQuark(network, credentials));
    }
}
