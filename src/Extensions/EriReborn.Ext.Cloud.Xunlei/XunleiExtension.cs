using EriReborn.Cloud.Providers;
using EriReborn.Extension;

namespace EriReborn.Ext.Cloud.Xunlei;

/// <summary>Contributes the Xunlei cloud platform.</summary>
public sealed class XunleiExtension : IExtension
{
    public string Id => "erireborn_cloud_xunlei";

    public string DisplayName => "迅雷云盘";

    public string Version => "1.0.0";

    public void Initialize(IExtensionHost host)
    {
        if (host.Network is not { } network || host.Credentials is not { } credentials)
        {
            host.Log("error", "缺少 network.http / credentials.read 权限，无法注册迅雷云盘。");
            return;
        }

        host.RegisterCloudProvider(new ProviderXunlei(network, credentials));
    }
}
