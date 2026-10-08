using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Shows the picture at an absolute path.
///
/// <para>
/// The workshop previews the skin it is editing, which is often not the skin in force, so the
/// asset registry cannot answer for it — the page resolves the path from the skin's own files and
/// the picture is read here.
/// </para>
/// </summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    /// <summary>
    /// Keyed by path *and* the file's timestamp and size.
    ///
    /// <para>
    /// Keying on the path alone was wrong in exactly the way this screen would notice: replacing a
    /// picture overwrites the file at the same path, so the cache would keep serving the old
    /// bitmap and the preview would appear not to update — the one thing "replace this picture"
    /// has to get right.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly PathToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
        {
            return null;
        }

        var key = path;
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            }
        }
        catch (Exception)
        {
            // An unreadable file is handled below, as missing art.
        }

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
            {
                // Read through a stream so the file is not held open: the next replacement has to
                // be able to overwrite it while this bitmap is still on screen.
                using var stream = File.OpenRead(path);
                bitmap = new Bitmap(stream);
            }
        }
        catch (Exception)
        {
            // Unreadable art is missing art; the caller shows the fallback.
            bitmap = null;
        }

        // Bounded: this runs on every rebind, and a skin can be replaced any number of times.
        if (Cache.Count > 64)
        {
            Cache.Clear();
        }

        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// One-way by design: a picture is derived from a path, never written back. Returning
    /// DoNothing is how Avalonia spells that, and it keeps the production path free of an
    /// unimplemented throw.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
