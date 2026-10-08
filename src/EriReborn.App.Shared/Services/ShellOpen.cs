namespace EriReborn.App.Shared.Services;

// LEGACY: folder opening moved to EriReborn.Platform.Abstractions.IShellService
// (WindowsShellService / AndroidShellService), reached via IPlatformService.Shell.
// PluginsViewModel and ExtensionViewModel no longer call this. Delete once the
// external harness copy is confirmed unused.
/// <summary>
/// Opens a folder in the platform file manager.
///
/// Installing a plugin or an extension means dropping a file into a folder, so that
/// folder has to be one click away. A path printed in a status line is not an entry
/// point: the user cannot be expected to reconstruct it by hand.
/// </summary>
public static class ShellOpen
{
    /// <summary>Opens the folder, creating it when it does not exist yet.</summary>
    /// <returns>null on success, otherwise the failure message.</returns>
    public static string? Folder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : path,
                Arguments = OperatingSystem.IsWindows() ? '"' + path + '"' : string.Empty,
                UseShellExecute = true,
            });

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
