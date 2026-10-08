using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Turns a container's measured height into a <c>MaxHeight</c> that shrinks before the
/// container's star-sized sibling is squeezed to zero.
///
/// <para>
/// The software page stacks "auxiliary panes that scroll themselves" (the dynamic card
/// stack, the expanded advanced section) above the main list. A fixed <c>MaxHeight</c>
/// (320 / 240) is fine on a tall window, but when the window is short — or both panes are
/// open at once — the two Auto rows take their full caps first and the star row (the list)
/// receives whatever is left, which can be 0. Binding the caps to the live container height
/// makes the auxiliary panes yield space instead: their own ScrollViewers scroll, while the
/// list keeps a usable minimum height.
/// </para>
///
/// <para>
/// Parameter format (invariant): <c>"reserve,max,min"</c>. The result is
/// <c>Clamp(containerHeight - reserve, min, max)</c> — <paramref name="parameter"/> reserve
/// is the height that must stay available for everything else (the list plus its margin),
/// max is the cap on tall windows, min is how small the pane itself may get.
/// </para>
/// </summary>
public sealed class AdaptiveMaxHeightConverter : IValueConverter
{
    /// <summary>Shared instance, so a view can reach it with <c>x:Static</c>.</summary>
    public static readonly AdaptiveMaxHeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double height || double.IsNaN(height) ||
            parameter is not string spec)
        {
            // No reliable measurement yet: do not constrain anything. The binding re-evaluates
            // once Bounds is real.
            return double.PositiveInfinity;
        }

        var parts = spec.Split(',');
        if (parts.Length != 3
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var reserve)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var min))
        {
            return double.PositiveInfinity;
        }

        return Math.Clamp(height - reserve, min, max);
    }

    /// <summary>One-way only: MaxHeight never flows back to the container's Bounds.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
