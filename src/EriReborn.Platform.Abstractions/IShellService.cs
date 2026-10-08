namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Opens a folder in the platform file manager. Platform-specific because the
/// mechanism differs: Windows uses explorer.exe, Android cannot browse the app
/// sandbox at all (spec 5/67). Core/Shared code calls this instead of shelling
/// out directly so no platform name leaks into the shared assembly.
/// </summary>
public interface IShellService
{
    /// <summary>
    /// Opens <paramref name="path"/> in the platform file manager.
    /// </summary>
    /// <returns>null on success, otherwise the failure message to surface.</returns>
    string? OpenFolder(string path);

    /// <summary>
    /// Opens an http(s) link with whatever handles web addresses on this platform.
    ///
    /// <para>
    /// Here rather than in a page's code-behind because the mechanism differs per platform and the
    /// scheme rule must not be re-decided at each call site — see
    /// <see cref="WebLinks.TryNormalise"/>. Anything that is not http/https is refused with a reason
    /// instead of being handed to the shell.
    /// </para>
    /// </summary>
    /// <returns>null on success, otherwise the failure message to surface.</returns>
    string? OpenUrl(string url);
}
