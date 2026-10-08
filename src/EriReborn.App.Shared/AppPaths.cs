namespace EriReborn.App.Shared;

/// <summary>
/// Resolves every directory the application reads or writes. Nothing else in
/// the app is allowed to hard-code a path (spec 18/70).
/// </summary>
public sealed record AppPaths
{
    public required string AssetsRoot { get; init; }

    public required string CatalogDirectory { get; init; }

    public required string SkinsDirectory { get; init; }

    public required string AssetManifestFile { get; init; }

    public required string ExtensionsDirectory { get; init; }

    public required string UserDataDirectory { get; init; }

    public required string LogDirectory { get; init; }

    public required string PlaygroundDirectory { get; init; }

    public string LogFile => Path.Combine(LogDirectory, $"erireborn-{DateTime.Now:yyyyMMdd}.log");

    public string UserConfigFile => Path.Combine(UserDataDirectory, "user_config.json");

    /// <summary>
    /// Locates the assets directory next to the executable, walking up the
    /// tree during development. An explicit override always wins.
    /// </summary>
    public static AppPaths Detect(string? assetsOverride = null, string? userDataOverride = null, string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        var assets = Absolute(assetsOverride) ?? LocateAssets(baseDir);

        var preferredUserData = Absolute(userDataOverride) ?? ResolvePreferredUserData(baseDir);

        var userData = ResolveWritableUserData(preferredUserData, baseDir);

        return new AppPaths
        {
            AssetsRoot = assets,
            CatalogDirectory = Path.Combine(assets, "catalog"),
            SkinsDirectory = Path.Combine(assets, "skins"),
            AssetManifestFile = Path.Combine(assets, "asset_manifest.json"),
            ExtensionsDirectory = Path.Combine(assets, "extensions"),
            UserDataDirectory = userData,
            LogDirectory = Path.Combine(userData, "logs"),
            PlaygroundDirectory = Path.Combine(userData, "playground"),
        };
    }

    /// <summary>
    /// A caller-supplied root, made absolute before anything uses it.
    ///
    /// <para>
    /// A relative root gets resolved twice: once by this process, and again by any child process
    /// started with that directory as its working directory. A download then lands in a doubled path
    /// — <c>.smoke-data/aria2-smoke/.smoke-data/aria2-smoke/file</c> — while the engine looks for it
    /// at the path it was given, so a perfectly good download is reported as "no file produced".
    /// Making the root absolute here means every child (aria2, an installer, a vendor setup) is
    /// handed the same path the caller meant.
    /// </para>
    /// </summary>
    private static string? Absolute(string? directory)
        => string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);

    /// <summary>
    /// Picks where user data lives when the caller did not name a directory: the folder
    /// next to the executable wins, so a build unpacked onto a drive keeps its plugins,
    /// logs and settings on that same drive instead of scattering them into the C: profile.
    /// The profile is only used when the application folder cannot be written to, which is
    /// what an install under Program Files looks like.
    /// </summary>
    public static string ResolvePreferredUserData(string baseDirectory)
    {
        var portable = Path.Combine(baseDirectory, "EriReborn-data");
        if (IsWritableDirectory(portable))
        {
            return portable;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EriReborn");
    }

    /// <summary>
    /// Returns the preferred root when the process can really write to it, and the folder
    /// next to the executable otherwise. A locked-down profile (a hardened profile or a
    /// restricted token) must not turn every save into a failure.
    /// </summary>
    public static string ResolveWritableUserData(string preferred, string baseDirectory)
    {
        if (IsWritableDirectory(preferred))
        {
            return preferred;
        }

        var fallback = Path.Combine(baseDirectory, "EriReborn-data");
        return IsWritableDirectory(fallback) ? fallback : preferred;
    }

    /// <summary>Proves writability by writing and removing a real byte, not by guessing from attributes.</summary>
    public static bool IsWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllBytes(probe, new byte[] { 1 });
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string LocateAssets(string baseDirectory)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ERIREBORN_ASSETS");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var direct = Path.Combine(baseDirectory, "assets");
        if (Directory.Exists(Path.Combine(direct, "catalog")))
        {
            return direct;
        }

        var current = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            var candidate = Path.Combine(current.FullName, "assets");
            if (Directory.Exists(Path.Combine(candidate, "catalog")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return direct;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(UserDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(PlaygroundDirectory);
        Directory.CreateDirectory(ExtensionsDirectory);
    }
}
