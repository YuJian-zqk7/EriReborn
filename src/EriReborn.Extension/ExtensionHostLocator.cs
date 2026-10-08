namespace EriReborn.Extension;

/// <summary>How to start the extension host, once it has been found.</summary>
public sealed record ExtensionHostLaunch(string Executable, IReadOnlyList<string> Arguments)
{
    public bool IsRunnable => !string.IsNullOrWhiteSpace(Executable);
}

/// <summary>
/// Finds the extension host next to the running application.
///
/// <para>
/// Two layouts are real: a development build, where the host sits beside the app
/// as its own executable, and a published one, where only the managed assembly
/// travels. Both are handled, and neither is guessed at: a missing host means
/// process isolation is unavailable, which callers must be able to say out loud
/// rather than fail with a confusing exception later.
/// </para>
/// </summary>
public static class ExtensionHostLocator
{
    public const string HostAssemblyName = "EriReborn.Extension.Host";

    /// <summary>
    /// The launch recipe for the host in <paramref name="baseDirectory"/>, or one
    /// that is not runnable when the host is not shipped.
    /// </summary>
    public static ExtensionHostLaunch Locate(string baseDirectory, string? dotnetHost = null)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return new ExtensionHostLaunch(string.Empty, Array.Empty<string>());
        }

        // A native apphost next to the app: nothing to prefix.
        var executable = Path.Combine(baseDirectory, HostAssemblyName + ".exe");
        if (File.Exists(executable))
        {
            return new ExtensionHostLaunch(executable, Array.Empty<string>());
        }

        // Published layouts keep the managed assembly; it is started through the
        // shared runtime instead of a generated apphost.
        var assembly = Path.Combine(baseDirectory, HostAssemblyName + ".dll");
        if (File.Exists(assembly))
        {
            return new ExtensionHostLaunch(dotnetHost ?? "dotnet", new[] { assembly });
        }

        return new ExtensionHostLaunch(string.Empty, Array.Empty<string>());
    }
}
