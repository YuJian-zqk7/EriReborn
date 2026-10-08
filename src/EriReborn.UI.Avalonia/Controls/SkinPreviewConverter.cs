using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Maps a skin to its library illustration (skin_&lt;id&gt;_card).
///
/// The naming convention lives here, in the UI layer, rather than on the skin model:
/// the asset library decides what the files are called, and the skin manifest should
/// not have to know about picture file names.
/// </summary>
public sealed class SkinPreviewConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            // A manifest, or the row the workshop lists it as: both name a skin, and the card is
            // named after the skin.
            SkinManifest skin => Card(skin.Id, skin.BaseSkin),
            WorkshopSkinEntry entry => Card(entry.Id, entry.BaseSkin),
            _ => null,
        };

    /// <summary>
    /// The card id for a skin. A skin the user built on another one shows that one's illustration:
    /// its own card art does not exist, and a card that draws nothing reads as a broken entry
    /// rather than as "this is your new skin, so far it looks like its base".
    /// </summary>
    private static string Card(string id, string? baseSkin)
        => string.IsNullOrWhiteSpace(baseSkin) ? $"skin_{id}_card" : $"skin_{baseSkin}_card";

    /// <summary>
    /// One-way by design: picture names are derived from a skin, never written back.
    /// Returning DoNothing is how Avalonia spells that, and it keeps the production path
    /// free of an unimplemented throw.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
