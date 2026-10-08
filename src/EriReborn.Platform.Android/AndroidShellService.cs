using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android apps live in a per-app sandbox; the system file manager cannot browse
/// arbitrary sandboxed paths without a file-provider grant the app does not
/// request. Reporting the path honestly beats a silent crash (spec 67/68). The
/// folder is still real and reachable over USB / device file explorer.
/// </summary>
public sealed class AndroidShellService : IShellService
{
    private readonly IAppLogger _log;

    public AndroidShellService(IAppLogger log) => _log = log;

    public string? OpenFolder(string path)
    {
        _log.Warn("shell.openfolder", $"Android 无法从应用内打开文件管理器：{path}");
        return $"Android 无法从应用内打开文件管理器；目录路径为：{path}";
    }

    /// <summary>
    /// Opens an http(s) link in whatever app the device has registered for web addresses — a browser,
    /// or the app that owns the domain. Unlike a sandbox folder, this is something Android really can
    /// do, so it is implemented rather than reported as impossible.
    /// </summary>
    public string? OpenUrl(string url)
    {
        if (!WebLinks.TryNormalise(url, out var absolute, out var refusal))
        {
            _log.Warn("shell.openurl", "拒绝打开非 http/https 链接：" + url);
            return refusal;
        }

        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse(absolute));

            // Started from outside an activity, so the new task flag is required or the call is refused.
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);

            return null;
        }
        catch (Exception ex)
        {
            _log.Error("shell.openurl", $"无法打开链接：{absolute}", ex);
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
