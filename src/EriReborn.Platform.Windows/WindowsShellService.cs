using System.Diagnostics;
using System.IO;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Opens a folder with explorer.exe. The failure path is logged with the full
/// exception so a directory that cannot be created or opened is locatable
/// rather than reported as a bare message (spec 26).
/// </summary>
public sealed class WindowsShellService : IShellService
{
    private readonly IAppLogger _log;

    public WindowsShellService(IAppLogger log) => _log = log;

    /// <summary>
    /// Opens an http(s) link in the user's default browser.
    ///
    /// <para>
    /// Unlike the folder path, a link is handed to ShellExecute as the file name — that is exactly how
    /// Windows opens the default browser. The scheme is checked first (see
    /// <see cref="WebLinks.TryNormalise"/>), because ShellExecute on an arbitrary scheme runs whatever
    /// this machine associates with it.
    /// </para>
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
            Process.Start(new ProcessStartInfo
            {
                FileName = absolute,
                UseShellExecute = true,
            });

            return null;
        }
        catch (Exception ex)
        {
            _log.Error("shell.openurl", $"无法打开链接：{absolute}", ex);
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public string? OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "文件夹路径为空。";
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var target = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Name explorer.exe as the executable and pass the folder as an argument.
            // Handing the directory to ShellExecute instead (FileName = target) relies on
            // the machine's folder "open" association; when that association is broken,
            // ShellExecuteEx fails outright ("系统找不到所需的全部信息") and the folder
            // cannot be opened by any ShellExecute-based caller. explorer.exe itself still
            // launches, so calling it directly is the reliable path.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + target + "\"",
                UseShellExecute = true,
            });

            return null;
        }
        catch (Exception ex)
        {
            _log.Error("shell.openfolder", $"无法打开文件夹：{path}", ex);
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
