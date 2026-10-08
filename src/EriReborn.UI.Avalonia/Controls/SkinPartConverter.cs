using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Maps the active skin to one of the second-version part sets (eri / tech / win11).
///
/// The two material libraries name their parts per family, so the family is derived from
/// the skin id rather than hand-listed per skin.
/// </summary>
public sealed class SkinPartConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SkinManifest skin)
        {
            return null;
        }

        var family = skin.Id.StartsWith("tech", StringComparison.Ordinal) ? "tech"
            : skin.Id.StartsWith("win11", StringComparison.Ordinal) ? "win11"
            : "eri";

        return $"v2_{family}_{parameter as string ?? "topbar"}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
