namespace EriReborn.Engine.Download;

/// <summary>
/// Finds the aria2 executable.
///
/// Discovery rather than a fixed path, because the common ways of installing it
/// all land somewhere different: a package manager shim directory, a versioned
/// package folder, or a plain directory on PATH. A hard-coded path would work on
/// exactly one machine.
/// </summary>
public sealed class Aria2Locator(
    Func<string, bool>? fileExists = null,
    Func<string, IReadOnlyList<string>>? findUnder = null,
    Func<IReadOnlyList<string>>? candidates = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, IReadOnlyList<string>> _findUnder = findUnder ?? FindUnder;
    private readonly Func<IReadOnlyList<string>> _candidates = candidates ?? (() => Candidates());

    /// <summary>The first existing candidate, or null when aria2 is not installed.</summary>
    public string? Find()
    {
        var direct = _candidates().FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && _fileExists(path));
        if (direct is not null)
        {
            return direct;
        }

        // WinGet keeps each package under a versioned folder, so the exact path
        // depends on the installed version and has to be scanned for. A scan that
        // fails is "not found here", never an exception that stops discovery.
        foreach (var root in ScanRoots())
        {
            IReadOnlyList<string> found;
            try
            {
                found = _findUnder(root);
            }
            catch (Exception)
            {
                continue;
            }

            var hit = found.FirstOrDefault(path => _fileExists(path));
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>An explicit override first, then PATH, then the conventional installs.</summary>
    public static IReadOnlyList<string> Candidates()
    {
        var candidates = new List<string>();

        var overridden = Environment.GetEnvironmentVariable("ARIA2C");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            candidates.Add(overridden);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length > 0)
            {
                candidates.Add(Path.Combine(trimmed, "aria2c.exe"));
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "aria2c.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "aria2", "aria2c.exe"));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(profile, "scoop", "shims", "aria2c.exe"));
        candidates.Add(Path.Combine(profile, "scoop", "apps", "aria2", "current", "aria2c.exe"));

        return candidates;
    }

    /// <summary>Directories that may contain a versioned package folder.</summary>
    public static IReadOnlyList<string> ScanRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new[]
        {
            Path.Combine(local, "Microsoft", "WinGet", "Packages"),
        };
    }

    private static IReadOnlyList<string> FindUnder(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.GetFiles(root, "aria2c.exe", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            // An unreadable directory means "not found here", not a failure.
            return Array.Empty<string>();
        }
    }
}
