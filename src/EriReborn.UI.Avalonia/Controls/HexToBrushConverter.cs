using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Turns a hex colour string from a view model into a brush.
///
/// The view models stay free of Avalonia types — they state a colour, the view layer
/// decides that a colour is a brush.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string hex && Color.TryParse(hex, out var colour) ? new SolidColorBrush(colour) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
