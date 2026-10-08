using EriReborn.Cloud.Providers;
using EriReborn.Extension;

namespace EriReborn.Ext.Cloud.Baidu;

/// <summary>Contributes the Baidu Netdisk platform.</summary>
public sealed class BaiduExtension : IExtension
{
    public string Id => "erireborn_cloud_baidu";

    public string DisplayName => "百度网盘";

    public string Version => "1.0.0";

    public void Initialize(IExtensionHost host)
    {
        if (host.Network is not { } network || host.Credentials is not { } credentials)
        {
            host.Log("error", "缺少 network.http / credentials.read 权限，无法注册百度网盘。");
            return;
        }

        host.RegisterCloudProvider(new ProviderBaidu(network, credentials));
    }
}
