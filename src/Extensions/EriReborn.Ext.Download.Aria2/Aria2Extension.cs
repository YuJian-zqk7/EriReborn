using EriReborn.Engine.Download;
using EriReborn.Extension;

namespace EriReborn.Ext.Download.Aria2;

/// <summary>
/// Contributes the aria2 download accelerator. The engine declines every route when
/// aria2c is not installed, so registering it costs nothing on a machine without it.
/// </summary>
public sealed class Aria2Extension : IExtension
{
    public string Id => "erireborn_download_aria2";

    public string DisplayName => "aria2 下载加速";

    public string Version => "1.0.0";

    public void Initialize(IExtensionHost host)
    {
        if (host.Processes is not { } processes)
        {
            host.Log("warn", "缺少 process.run 权限，跳过 aria2 下载引擎注册。");
            return;
        }

        if (host.Logger is not { } logger)
        {
            host.Log("warn", "宿主未提供日志器，跳过 aria2 下载引擎注册。");
            return;
        }

        host.RegisterDownloadEngine(new Aria2DownloadEngine(processes, new Aria2Locator(), logger));
    }
}
