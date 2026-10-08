using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Maps a catalog category id to its library icon.
///
/// The library ships six category pictures (browser, devtools, driver, media,
/// productivity, runtime) against thirteen catalog categories, so the rest fall back to
/// a state icon rather than pretending a category picture exists.
/// </summary>
public sealed class CategoryIconConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = "icon_search",
        ["Browser"] = "swcat_browser",
        ["Development"] = "swcat_devtools",
        ["Runtime"] = "swcat_runtime",
        ["System_Drivers"] = "swcat_driver",
        ["Media"] = "swcat_media",
        ["Office"] = "swcat_productivity",
        ["Utility"] = "swcat_productivity",
        ["Network"] = "icon_network",
        ["System_Security"] = "icon_security",
        ["System"] = "icon_settings",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string id && Icons.TryGetValue(id, out var icon) ? icon : "icon_info";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
