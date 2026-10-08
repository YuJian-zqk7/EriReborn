using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EriReborn.Asset;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Draws a nine-slice frame behind its content.
///
/// <para>
/// When the active skin declares frame art it is stretched by the corners only,
/// so the border keeps its radius and its shadow at any size. When it does not,
/// the control falls back to a flat fill: a skin without frame art must still look
/// deliberate rather than broken (spec 107/113).
/// </para>
/// </summary>
public sealed class NineSliceBorder : Decorator
{
    public static readonly StyledProperty<string?> AssetIdProperty =
        AvaloniaProperty.Register<NineSliceBorder, string?>(nameof(AssetId));

    public static readonly StyledProperty<SliceInsets> InsetsProperty =
        AvaloniaProperty.Register<NineSliceBorder, SliceInsets>(nameof(Insets));

    public static readonly StyledProperty<IBrush?> FallbackBackgroundProperty =
        AvaloniaProperty.Register<NineSliceBorder, IBrush?>(nameof(FallbackBackground));

    public static readonly StyledProperty<IBrush?> FallbackBorderBrushProperty =
        AvaloniaProperty.Register<NineSliceBorder, IBrush?>(nameof(FallbackBorderBrush));

    public static readonly StyledProperty<double> FallbackBorderThicknessProperty =
        AvaloniaProperty.Register<NineSliceBorder, double>(nameof(FallbackBorderThickness));

    public static readonly StyledProperty<CornerRadius> FallbackCornerRadiusProperty =
        AvaloniaProperty.Register<NineSliceBorder, CornerRadius>(nameof(FallbackCornerRadius));

    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);

    static NineSliceBorder()
    {
        AffectsRender<NineSliceBorder>(
            AssetIdProperty,
            InsetsProperty,
            FallbackBackgroundProperty,
            FallbackBorderBrushProperty,
            FallbackBorderThicknessProperty,
            FallbackCornerRadiusProperty);
    }

    /// <summary>The asset sheet to draw, resolved through the asset manager.</summary>
    public string? AssetId
    {
        get => GetValue(AssetIdProperty);
        set => SetValue(AssetIdProperty, value);
    }

    /// <summary>Used only when the declared asset carries no slice of its own.</summary>
    public SliceInsets Insets
    {
        get => GetValue(InsetsProperty);
        set => SetValue(InsetsProperty, value);
    }

    public IBrush? FallbackBackground
    {
        get => GetValue(FallbackBackgroundProperty);
        set => SetValue(FallbackBackgroundProperty, value);
    }

    public IBrush? FallbackBorderBrush
    {
        get => GetValue(FallbackBorderBrushProperty);
        set => SetValue(FallbackBorderBrushProperty, value);
    }

    public double FallbackBorderThickness
    {
        get => GetValue(FallbackBorderThicknessProperty);
        set => SetValue(FallbackBorderThicknessProperty, value);
    }

    public CornerRadius FallbackCornerRadius
    {
        get => GetValue(FallbackCornerRadiusProperty);
        set => SetValue(FallbackCornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var image = LoadFrame();
        if (image is null)
        {
            DrawFallback(context, bounds);
            return;
        }

        var slice = NineSliceGeometry.Compute(
            Math.Max(1, image.PixelSize.Width),
            Math.Max(1, image.PixelSize.Height),
            DeclaredInsets(),
            (int)Math.Ceiling(bounds.Width),
            (int)Math.Ceiling(bounds.Height));

        for (var index = 0; index < NineSlice.PieceCount; index++)
        {
            var source = slice.Source[index];
            var destination = slice.Destination[index];

            // A zero-size piece is normal — a frame whose middle does not stretch —
            // and drawing it would be an error rather than a no-op.
            if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0)
            {
                continue;
            }

            context.DrawImage(
                image,
                new Rect(source.X, source.Y, source.Width, source.Height),
                new Rect(destination.X, destination.Y, destination.Width, destination.Height));
        }
    }

    /// <summary>
    /// The frame to draw. An explicit <see cref="AssetId"/> wins; otherwise the
    /// active skin decides, which is what lets every view use this control without
    /// naming a single asset. A skin that declared no frame has none.
    /// </summary>
    private AssetSheet? FrameSheet()
    {
        var assets = App.Host?.Assets;
        if (assets is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(AssetId)
            ? assets.FrameFor(App.Host?.Skins.Active?.Id)
            : assets.Resolve(AssetId);
    }

    /// <summary>The sheet decides its own insets; the property is a stand-in only.</summary>
    private SliceInsets DeclaredInsets() => FrameSheet()?.Slice ?? Insets;

    private Bitmap? LoadFrame()
    {
        var sheet = FrameSheet();
        var id = sheet?.Id ?? string.Empty;

        if (_cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        var path = App.Host?.Assets.ResolvePath(id);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                bitmap = new Bitmap(path);
            }
            catch (Exception)
            {
                // Unreadable art is missing art, and the fallback still draws a frame.
                bitmap = null;
            }
        }

        _cache[id] = bitmap;
        return bitmap;
    }

    private void DrawFallback(DrawingContext context, Rect bounds)
    {
        var radius = FallbackCornerRadius.TopLeft;

        context.DrawRectangle(
            FallbackBackground,
            FallbackBorderThickness > 0 ? new Pen(FallbackBorderBrush, FallbackBorderThickness) : null,
            bounds,
            radius,
            radius);
    }
}
