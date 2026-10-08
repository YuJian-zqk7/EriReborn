namespace EriReborn.Cloud;

/// <summary>
/// Holds the embedded browser that the running app installed, if any. Providers are built by the
/// platform layer before the UI exists, so the browser arrives later; this is where it lands instead
/// of threading it through every constructor at the exact right moment.
///
/// <para>
/// Nothing here pretends: with no browser installed, <see cref="IsAvailable"/> is false and the
/// providers say which page needs one.
/// </para>
/// </summary>
public static class CloudBrowser
{
    private static ICloudBrowserChannel? _current;

    /// <summary>The installed channel, or null when the app has no embedded browser.</summary>
    public static ICloudBrowserChannel? Current => _current;

    public static bool IsAvailable => _current is { IsAvailable: true };

    /// <summary>Installs (or clears, with null) the app's embedded browser.</summary>
    public static void Install(ICloudBrowserChannel? channel) => _current = channel;

    /// <summary>Returns the provider's own channel when it has one, otherwise the app-wide one.</summary>
    public static ICloudBrowserChannel? Resolve(ICloudBrowserChannel? owned) =>
        owned is { IsAvailable: true } ? owned : _current;
}
