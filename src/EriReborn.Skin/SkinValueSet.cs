using System.Globalization;

namespace EriReborn.Skin;

/// <summary>
/// The concrete presentation values a skin manifest resolves to. The Skin
/// module owns this mapping so it can be verified without a UI framework; the
/// Avalonia layer only converts these into resources (spec 40/47).
/// </summary>
public sealed record SkinValueSet
{
    public static readonly string[] ColorKeys =
    {
        "accent", "background", "surface", "surfaceAlt", "foreground", "muted", "border",
    };

    public required string SkinId { get; init; }

    public required IReadOnlyDictionary<string, string> Colors { get; init; }

    public required string FontFamily { get; init; }

    public required double FontSize { get; init; }

    public required double FontSizeLarge { get; init; }

    public required double FontSizeSmall { get; init; }

    public required double CornerRadius { get; init; }

    public required double CornerRadiusSmall { get; init; }

    public required string Density { get; init; }

    public required double Spacing { get; init; }

    public required double ButtonHeight { get; init; }

    public required double InputHeight { get; init; }

    public required double ListRowHeight { get; init; }

    public required bool TouchTarget { get; init; }

    public required double MinTargetHeight { get; init; }

    public required double NavItemHeight { get; init; }

    public required string NavMode { get; init; }

    public required string NavPosition { get; init; }

    public required double WindowMinWidth { get; init; }

    public required double WindowMinHeight { get; init; }

    public required string WindowChrome { get; init; }

    /// <summary>
    /// Card padding, derived from the density rather than declared directly.
    /// Density is an input; this is the value the UI can actually bind to.
    /// </summary>
    public required double CardPadding { get; init; }

    /// <summary>Accent colour while the pointer is over a control.</summary>
    public required string AccentHover { get; init; }

    /// <summary>Accent colour while a control is held down.</summary>
    public required string AccentPressed { get; init; }

    /// <summary>Surface of a control that cannot be used.</summary>
    public required string SurfaceDisabled { get; init; }

    /// <summary>Text of a control that cannot be used.</summary>
    public required string ForegroundDisabled { get; init; }

    // A slider is not one control: a track, a filled portion and a thumb, each of
    // which a skin should be able to address separately. Treating it as "a control
    // with a colour" is why most themes ship sliders that look like nothing else.
    public required double SliderTrackHeight { get; init; }

    public required double SliderThumbSize { get; init; }

    public required string SliderTrackColor { get; init; }

    public required string SliderFillColor { get; init; }

    public required string SliderThumbColor { get; init; }

    public static SkinValueSet FromManifest(SkinManifest skin)
    {
        ArgumentNullException.ThrowIfNull(skin);

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ColorKeys)
        {
            if (skin.GetColor(key) is { Length: > 0 } value)
            {
                colors[key] = value;
            }
        }

        var cornerRadius = Number(skin.Metrics, "cornerRadius", 8);
        // Blank or whitespace values must fall back, not become the density.
        var density = Fallback(skin.Metrics, "density", "comfortable");
        var touchTarget = Bool(skin.Controls, "touchTarget");

        var buttonHeight = Number(skin.Controls, "buttonHeight", 32);

        // A skin declares one accent; a button needs three states. Deriving the
        // other two keeps every skin usable without asking authors to hand-pick
        // colours they will mostly get wrong — and an explicit value still wins.
        var themeIsLight = string.Equals(skin.Theme, "light", StringComparison.OrdinalIgnoreCase);
        var accent = skin.GetColor("accent") ?? "#5B8DEF";
        var surface = skin.GetColor("surface") ?? "#222831";
        var foreground = skin.GetColor("foreground") ?? "#E8EAF0";

        var accentHover = skin.GetColor("accentHover")
            ?? (themeIsLight ? ColorMath.Darken(accent, 0.12) : ColorMath.Lighten(accent, 0.12));

        var accentPressed = skin.GetColor("accentPressed")
            ?? ColorMath.Darken(accent, themeIsLight ? 0.22 : 0.14);

        var surfaceDisabled = skin.GetColor("surfaceDisabled")
            ?? ColorMath.Mix(surface, skin.GetColor("background") ?? "#171C26", 0.45);

        var foregroundDisabled = skin.GetColor("foregroundDisabled")
            ?? ColorMath.Mix(foreground, surface, 0.62);

        var border = skin.GetColor("border") ?? "#3A4252";

        // The thumb has to stay grabbable on a touch screen and stay neat with a
        // mouse; both come from the skin rather than from a constant here.
        var sliderThumbSize = Number(skin.Controls, "sliderThumbSize", touchTarget ? 28 : 18);

        return new SkinValueSet
        {
            SkinId = skin.Id,
            Colors = colors,
            FontFamily = string.IsNullOrWhiteSpace(skin.FontFamily) ? "Segoe UI" : skin.FontFamily,
            FontSize = Number(skin.Typography, "fontSize", 14),
            FontSizeLarge = Number(skin.Typography, "fontSizeLarge", 18),
            FontSizeSmall = Number(skin.Typography, "fontSizeSmall", 12),
            CornerRadius = cornerRadius,
            CornerRadiusSmall = Math.Max(2, cornerRadius / 2),
            Density = density,
            Spacing = density switch
            {
                "compact" => 4,
                "spacious" => 12,
                _ => 8,
            },
            ButtonHeight = buttonHeight,
            InputHeight = Number(skin.Controls, "inputHeight", 32),
            ListRowHeight = Number(skin.Controls, "listRowHeight", 36),
            TouchTarget = touchTarget,
            MinTargetHeight = touchTarget ? 48 : buttonHeight,
            NavItemHeight = Number(skin.Navigation, "itemHeight", 40),
            NavMode = Fallback(skin.Navigation, "mode", "sidebar"),
            NavPosition = Fallback(skin.Navigation, "position", "left"),
            WindowMinWidth = Number(skin.Window, "minwidth", 960),
            WindowMinHeight = Number(skin.Window, "minheight", 600),
            WindowChrome = Fallback(skin.Window, "chrome", "system"),
            CardPadding = (density switch { "compact" => 4, "spacious" => 12, _ => 8 }) + 6,
            AccentHover = accentHover,
            AccentPressed = accentPressed,
            SurfaceDisabled = surfaceDisabled,
            ForegroundDisabled = foregroundDisabled,
            SliderTrackHeight = Number(skin.Controls, "sliderTrackHeight", 4),
            SliderThumbSize = sliderThumbSize,
            SliderTrackColor = skin.GetColor("sliderTrack") ?? ColorMath.Mix(border, surface, 0.25),
            SliderFillColor = skin.GetColor("sliderFill") ?? accent,
            SliderThumbColor = skin.GetColor("sliderThumb") ?? accentHover,
        };
    }

    private static string Fallback(IReadOnlyDictionary<string, string> map, string key, string fallback)
        => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static bool Bool(IReadOnlyDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) && parsed;

    private static double Number(IReadOnlyDictionary<string, string> map, string key, double fallback)
        => map.TryGetValue(key, out var raw)
           && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
