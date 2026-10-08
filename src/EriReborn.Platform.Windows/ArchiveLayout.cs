namespace EriReborn.Platform.Windows;

/// <summary>
/// Archive layout helpers for software packages.
/// </summary>
public static class ArchiveLayout
{
    /// <summary>
    /// Moves the contents of a lone top-level folder up one level.
    ///
    /// Portable archives almost always wrap everything in a versioned folder
    /// ("ripgrep-14.1.1-x86_64-pc-windows-msvc/"), which would otherwise leave
    /// the program at &lt;install&gt;/&lt;archive-name&gt;/ instead of directly in the
    /// install directory the user chose.
    /// </summary>
    /// <returns>True when a folder was flattened.</returns>
    public static bool FlattenSingleRoot(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return false;
        }

        var root = entries[0];
        foreach (var entry in Directory.GetFileSystemEntries(root))
        {
            var destination = Path.Combine(directory, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.Move(entry, destination);
            }
            else
            {
                File.Move(entry, destination);
            }
        }

        try
        {
            // Only ever removes the now-empty wrapper.
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover empty folder is harmless; the program is already in place.
        }

        return true;
    }
}
