using Avalonia;
using Avalonia.Controls;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// An image whose source is an asset id rather than a file path (spec 48).
///
/// The library art is registered by id, so nothing in a view has to know where a
/// file lives — and a skin change re-resolves it instead of leaving yesterday's
/// picture on screen. An id that resolves to nothing hides the control rather than
/// showing a placeholder.
/// </summary>
public sealed class AssetImage : Image
{
    public static readonly StyledProperty<string?> AssetIdProperty =
        AvaloniaProperty.Register<AssetImage, string?>(nameof(AssetId));

    /// <summary>
    /// What this art *is* ("install"), for a button whose picture the skin chooses rather
    /// than the view. Ignored when <see cref="AssetId"/> names a file directly.
    /// </summary>
    public static readonly StyledProperty<string?> ActionKeyProperty =
        AvaloniaProperty.Register<AssetImage, string?>(nameof(ActionKey));

    public static readonly DirectProperty<AssetImage, bool> HasArtProperty =
        AvaloniaProperty.RegisterDirect<AssetImage, bool>(nameof(HasArt), o => o.HasArt);

    private bool _hasArt;

    public AssetImage()
    {
        AttachedToVisualTree += (_, _) =>
        {
            if (App.Main is { } shell)
            {
                shell.SkinChanged += OnSkinChanged;
            }

            Refresh();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (App.Main is { } shell)
            {
                shell.SkinChanged -= OnSkinChanged;
            }
        };
    }

    /// <summary>Asset-manifest id, for example win_close or icon_download.</summary>
    public string? AssetId
    {
        get => GetValue(AssetIdProperty);
        set => SetValue(AssetIdProperty, value);
    }

    public string? ActionKey
    {
        get => GetValue(ActionKeyProperty);
        set => SetValue(ActionKeyProperty, value);
    }

    /// <summary>
    /// True once art actually resolved. A label bound against it can step aside only when
    /// there is a picture to take its place, so no button ever ends up wordless.
    /// </summary>
    public bool HasArt
    {
        get => _hasArt;
        private set => SetAndRaise(HasArtProperty, ref _hasArt, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AssetIdProperty || change.Property == ActionKeyProperty)
        {
            Refresh();
        }
    }

    private void OnSkinChanged(object? sender, SkinManifest manifest) => Refresh();

    /// <summary>
    /// The name this control draws from — the asset id, or the id an action key resolves to.
    ///
    /// <para>
    /// Exposed because the skin editor has to be able to say "replace <em>this</em>": the user
    /// clicks the picture they want to change, and this is the name the replacement is filed under.
    /// </para>
    /// </summary>
    public string? ResolvedAssetId { get; private set; }

    private void Refresh()
    {
        var asked = AssetId ?? ActionArt.Resolve(ActionKey);
        ResolvedAssetId = asked;

        // A skin may point a name at a picture of its own. Since the shipped library keeps its own
        // ids, that map — not a redefinition of the id — is what lets a user replace one icon
        // without changing what the id means for anyone else.
        var mapped = asked is not null
            && App.Main?.ActiveSkin is { } skin
            && skin.Assets.TryGetValue(asked, out var own)
            && !string.IsNullOrWhiteSpace(own)
                ? own
                : asked;

        var art = SkinArtwork.Resolve(mapped);

        // No art is not the same as no Source: a control the compositor has already drawn can be asked to
        // draw once more as it loses its picture, and a null Source there throws inside the render pass and
        // takes the application with it. A transparent pixel keeps that from being possible; HasArt still
        // answers honestly, so anything that steps aside for real art keeps doing so.
        Source = art ?? SkinArtwork.Transparent;
        HasArt = art is not null;
        IsVisible = HasArt;
    }
}
