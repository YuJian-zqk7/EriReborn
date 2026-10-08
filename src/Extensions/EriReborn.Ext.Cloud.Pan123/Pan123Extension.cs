using EriReborn.Cloud.Providers;
using EriReborn.Extension;

namespace EriReborn.Ext.Cloud.Pan123;

/// <summary>
/// Contributes the 123 cloud-drive platform. It is an ordinary extension: the host
/// hands it the platform's HTTP client and credential store (because the manifest
/// declared network.http / credentials.read) and the provider it constructs is wired
/// into the live cloud registry.
/// </summary>
public sealed class Pan123Extension : IExtension
{
    public string Id => "erireborn_cloud_123pan";

    public string DisplayName => "123 云盘";

    public string Version => "1.0.0";

    public void Initialize(IExtensionHost host)
    {
        if (host.Network is not { } network || host.Credentials is not { } credentials)
        {
            host.Log("error", "缺少 network.http / credentials.read 权限，无法注册 123 云盘。");
            return;
        }

        host.RegisterCloudProvider(new Provider123(network, credentials));
    }
}
