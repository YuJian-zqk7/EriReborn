using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Gives a value with no line-break opportunities somewhere to break.
///
/// <para>
/// A Windows path is one long "word": the line breaker has nothing to break on, so
/// <c>TextWrapping="Wrap"</c> cannot help and the text runs off the side of the page — taking
/// the card, the row and any right-aligned buttons with it. Inserting a zero-width space after
/// each separator fixes the layout without changing a single visible character of the text.
/// </para>
///
/// <para>
/// This is deliberately about layout only. A caller that needs the real value must keep using
/// the original binding, because a zero-width space is a real character in a string.
/// </para>
/// </summary>
public sealed class BreakableTextConverter : IValueConverter
{
    /// <summary>U+200B ZERO WIDTH SPACE: a break opportunity that renders as nothing.</summary>
    private const string Break = "\u200B";

    /// <summary>Shared instance, so a view can reach it with <c>x:Static</c>.</summary>
    public static readonly BreakableTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0)
        {
            return value;
        }

        return text
            .Replace("\\", "\\" + Break)
            .Replace("/", "/" + Break)
            .Replace("_", "_" + Break)
            .Replace("-", "-" + Break)
            .Replace(".", "." + Break);
    }

    /// <summary>
    /// One-way, said the way Avalonia says it. Turning the display text back into a value would
    /// have to strip the zero-width spaces again, and a converter that quietly rewrites its input
    /// on the way back is how a round trip stops being one — but "this direction does not exist"
    /// is a value here, not a failure to implement something.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
