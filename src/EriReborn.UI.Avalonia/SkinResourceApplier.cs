using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia;

/// <summary>
/// Projects a skin manifest onto Avalonia dynamic resources. A skin is not just
/// a palette (spec 40): metrics, typography, control sizes, navigation and
/// window rules are all applied, and every key the previous skin may have set is
/// overwritten so nothing leaks across (spec 47).
/// </summary>
public static class SkinResourceApplier
{
    public static void Apply(Application application, SkinManifest skin)
    {
        ArgumentNullException.ThrowIfNull(skin);

        var values = SkinValueSet.FromManifest(skin);
        var resources = application.Resources;

        ApplyColors(resources, values);
        ApplyTypography(resources, values);
        ApplyMetrics(resources, values);
        ApplyControls(resources, values);
        ApplyNavigation(resources, values);
        ApplyStates(resources, values);

        // The pointer over anything clickable is the skin's own hand, not the system's:
        // an arrow over a button reads as "this is not clickable".
        resources["SkinCursorLink"] = Controls.CursorManager.Resolve(skin, CursorRole.Link);

        // Dragging a splitter must show a resize pointer; without one the rail between two
        // panes looks like a plain gap and nobody tries to drag it.
        resources["SkinCursorResizeHorizontal"] = Controls.CursorManager.Resolve(skin, CursorRole.ResizeHorizontal);

        // A text field says "you can type here" with the I-beam, not the arrow.
        resources["SkinCursorText"] = Controls.CursorManager.Resolve(skin, CursorRole.Text);

        application.RequestedThemeVariant = string.Equals(skin.Theme, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    private static void ApplyColors(IResourceDictionary resources, SkinValueSet values)
    {
        foreach (var key in SkinValueSet.ColorKeys)
        {
            var resourceKey = "Skin" + char.ToUpperInvariant(key[0]) + key[1..];
            if (!values.Colors.TryGetValue(key, out var raw))
            {
                resources.Remove(resourceKey);
                continue;
            }

            try
            {
                resources[resourceKey] = Brush.Parse(raw);
            }
            catch (FormatException)
            {
                resources.Remove(resourceKey);
            }
        }
    }

    private static void ApplyTypography(IResourceDictionary resources, SkinValueSet values)
    {
        try
        {
            resources["SkinFontFamily"] = new FontFamily(values.FontFamily);
        }
        catch (Exception)
        {
            resources["SkinFontFamily"] = new FontFamily("Segoe UI");
        }

        resources["SkinFontSize"] = values.FontSize;
        resources["SkinFontSizeLarge"] = values.FontSizeLarge;
        resources["SkinFontSizeSmall"] = values.FontSizeSmall;
    }

    private static void ApplyMetrics(IResourceDictionary resources, SkinValueSet values)
    {
        resources["SkinCornerRadius"] = new CornerRadius(values.CornerRadius);
        resources["SkinCornerRadiusSmall"] = new CornerRadius(values.CornerRadiusSmall);
        resources["SkinCardPadding"] = new Thickness(values.CardPadding);

        // The rule here: project only what a style binds to. Density, touchTarget
        // and spacing are inputs that shape the values above; navigation position,
        // window chrome and window minimums are read from the manifest in code.
        // Projecting those as resources too would create keys nothing reads, and a
        // key nothing reads is indistinguishable from a feature that does not exist.
        // Every projected key is checked by a test.
    }

    private static void ApplyControls(IResourceDictionary resources, SkinValueSet values)
    {
        resources["SkinButtonHeight"] = values.ButtonHeight;
        resources["SkinInputHeight"] = values.InputHeight;
        resources["SkinListRowHeight"] = values.ListRowHeight;
        resources["SkinMinTargetHeight"] = values.MinTargetHeight;
    }

    /// <summary>
    /// The colours a control needs in order to have states at all. A skin that
    /// declares only an accent still gets hover, pressed and disabled, because
    /// these are derived rather than demanded.
    /// </summary>
    private static void ApplyStates(IResourceDictionary resources, SkinValueSet values)
    {
        SetBrush(resources, "SkinAccentHover", values.AccentHover);
        SetBrush(resources, "SkinAccentPressed", values.AccentPressed);
        SetBrush(resources, "SkinSurfaceDisabled", values.SurfaceDisabled);
        SetBrush(resources, "SkinForegroundDisabled", values.ForegroundDisabled);

        // Slider parts are addressed separately so a skin can shape each one.
        resources["SkinSliderTrackHeight"] = values.SliderTrackHeight;
        resources["SkinSliderThumbSize"] = values.SliderThumbSize;
        SetBrush(resources, "SkinSliderTrackBrush", values.SliderTrackColor);
        SetBrush(resources, "SkinSliderFillBrush", values.SliderFillColor);
        SetBrush(resources, "SkinSliderThumbBrush", values.SliderThumbColor);
    }

    /// <summary>A colour that cannot be parsed is left unset rather than turned into
    /// a wrong one, and the previous skin's value is removed so nothing leaks.</summary>
    private static void SetBrush(IResourceDictionary resources, string key, string value)
    {
        try
        {
            resources[key] = Brush.Parse(value);
        }
        catch (FormatException)
        {
            resources.Remove(key);
        }
    }

    private static void ApplyNavigation(IResourceDictionary resources, SkinValueSet values)
    {
        // Only the metric: placement is decided in code, from the skin itself.
        resources["SkinNavItemHeight"] = values.NavItemHeight;
    }
}
