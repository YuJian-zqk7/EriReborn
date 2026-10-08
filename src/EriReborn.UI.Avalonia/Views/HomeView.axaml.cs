using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Platform.Abstractions;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Views;

public partial class HomeView : UserControl
{
    private SpritePlayer? _sprite;
    private SpritePlayer? _companionSprite;
    private bool _subscribed;
    private HomeViewModel? _bound;

    public HomeView()
    {
        InitializeComponent();

        // Wired through the events rather than the overrides so the handler
        // signature stays inferred.
        AttachedToVisualTree += (_, _) => OnEnterTree();
        DetachedFromVisualTree += (_, _) => OnLeaveTree();

        // The data context can arrive after the view is created.
        DataContextChanged += (_, _) => BindLayout();
    }

    private void OnEnterTree()
    {
        ApplySkinArtwork();
        ApplyStatePanel();

        if (!_subscribed && App.Main is { } shell)
        {
            shell.SkinChanged += OnSkinChanged;
            _subscribed = true;
        }

        BindLayout();
    }

    /// <summary>
    /// Renders the page from its layout document and keeps it in step with the
    /// view model. Bound values are read at render time, so a property change has
    /// to trigger a re-render rather than being pushed by the renderer.
    /// </summary>
    private void BindLayout()
    {
        if (DataContext is not HomeViewModel home)
        {
            return;
        }

        if (ReferenceEquals(home, _bound))
        {
            RenderLayout(home);
            return;
        }

        UnbindLayout();

        _bound = home;
        home.LayoutChanged += OnLayoutChanged;
        home.PropertyChanged += OnViewModelPropertyChanged;

        // Pick up anything the Creative Workshop saved since the last visit.
        home.ReloadLayout();
        RenderLayout(home);
        ApplyStatePanel();
    }

    private void UnbindLayout()
    {
        if (_bound is null)
        {
            return;
        }

        _bound.LayoutChanged -= OnLayoutChanged;
        _bound.PropertyChanged -= OnViewModelPropertyChanged;
        _bound = null;
    }

    private void OnLayoutChanged(object? sender, EriReborn.Layout.LayoutDocument document)
        => RenderLayout(_bound);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeViewModel.Layout))
        {
            RenderLayout(sender as HomeViewModel);
        }

        // ScanRunning is one of these changes, and the state panel follows it.
        ApplyStatePanel();
    }

    private void RenderLayout(HomeViewModel? home)
    {
        var host = this.FindControl<ContentControl>("LayoutHost");
        if (host is null || home is null)
        {
            return;
        }

        // Links are followed by the shell: it is the layer that knows how to reach a page and how to hand
        // an address to the browser, and the page only draws them.
        host.Content = LayoutRenderer.Render(
            home.Layout,
            home.CreateBindings() with { FollowLink = FollowLayoutLink });
    }

    /// <summary>
    /// Follows one of the layout's links. A page key goes to that page; an address opens in the browser.
    /// False means nothing happened, and the click is left unhandled rather than swallowed.
    /// </summary>
    private static bool FollowLayoutLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return false;
        }

        // An address goes through the shell service instead of being started from this page. That
        // service is the one place that decides what a link is — http/https and nothing else — and a
        // page deciding it a second time is exactly how two answers to one question start to drift
        // (spec 5/67). A page key is not an address, so it falls through to navigation below.
        if (App.Host?.Platform.Shell is { } shell
            && WebLinks.TryNormalise(link, out var url, out _))
        {
            // False means nothing was opened — a refused address, or a machine with no browser — and
            // the click is then left unhandled rather than swallowed.
            return shell.OpenUrl(url) is null;
        }

        return App.Main?.NavigateTo(link) ?? false;
    }

    private void OnLeaveTree()
    {
        UnbindLayout();

        if (_subscribed && App.Main is { } shell)
        {
            shell.SkinChanged -= OnSkinChanged;
            _subscribed = false;
        }

        _sprite?.Dispose();
        _sprite = null;

        _companionSprite?.Dispose();
        _companionSprite = null;
    }

    private void OnSkinChanged(object? sender, SkinManifest manifest)
    {
        ApplySkinArtwork();
        ApplyStatePanel();
    }

    /// <summary>
    /// The panel above the page body: the skin's loading art while a scan runs, its
    /// empty-state art while the catalog is empty, and nothing the rest of the time.
    ///
    /// A scan that shows only a thin progress bar leaves the reader wondering whether
    /// anything is happening; an empty catalog that shows only a blank list leaves
    /// them unable to tell "nothing here" from "still loading". Both illustrations
    /// come from the skin's own element set, so a skin that ships none still gets the
    /// sentence.
    /// </summary>
    private void ApplyStatePanel()
    {
        var card = this.FindControl<Border>("EmptyStateCard");
        var image = this.FindControl<Image>("EmptyStateImage");
        var text = this.FindControl<TextBlock>("EmptyStateText");

        if (card is null || text is null)
        {
            return;
        }

        var scanning = _bound?.ScanRunning == true;
        var empty = _bound is { CatalogCount: 0 };

        if (!scanning && !empty)
        {
            card.IsVisible = false;
            return;
        }

        var skin = App.Host?.Skins.Active;

        if (image is not null)
        {
            var id = scanning
                ? SkinArtwork.LoadingStateIdFor(skin)
                : SkinArtwork.EmptyStateIdFor(skin);

            image.Source = SkinArtwork.Resolve(id);
            image.IsVisible = image.Source is not null;

            // The artwork changes with the state, so the editable slot names whichever picture is
            // actually on screen rather than one fixed id.
            Controls.SkinElement.SetAssetId(image, id);
        }

        // The sentence is a skin word, so the editor can change it. The key differs per state, so
        // it is declared here rather than fixed in markup: an edit must change the line the user
        // is looking at.
        var textKey = scanning ? "home.empty_state.scanning" : "home.empty_state.empty";
        Controls.SkinElement.SetTextKey(text, textKey);
        text.Text = UiTexts.Resolve(skin, textKey);

        card.IsVisible = true;
    }

    /// <summary>
    /// Shows the active skin's character. When the sheet carries sliced frames
    /// and a sprite spec it animates at the declared rate; when it carries a
    /// single image that image is shown; when it carries nothing the card hides
    /// (spec 48/50/51).
    /// </summary>
    private void ApplySkinArtwork()
    {
        // By name and by base type: the card is a NineSliceBorder, not a Border, and
        // asking for the wrong type silently returned null and left the card hidden.
        var card = this.FindControl<Control>("CharacterCard");
        var image = this.FindControl<Image>("CharacterImage");
        var title = this.FindControl<TextBlock>("CharacterTitle");
        var subtitle = this.FindControl<TextBlock>("CharacterSubtitle");
        var companion = this.FindControl<Image>("CompanionImage");

        if (card is null || image is null || title is null || subtitle is null)
        {
            return;
        }

        _sprite?.Dispose();
        _sprite = null;

        var skin = App.Host?.Skins.Active;
        var companionId = SkinArtwork.CompanionIdFor(skin);
        if (companion is not null)
        {
            // The slot names the picture actually being drawn, so the editor replaces the right one.
            if (!string.IsNullOrWhiteSpace(companionId))
            {
                Controls.SkinElement.SetAssetId(companion, companionId);
            }

            // A companion that ships frames walks; one that ships a still is shown.
            _companionSprite?.Dispose();
            _companionSprite = SpritePlayer.Create(companion, companionId);

            if (_companionSprite is null)
            {
                companion.Source = SkinArtwork.Resolve(companionId);
            }
            else
            {
                _companionSprite.Start();
            }

            companion.IsVisible = companion.Source is not null || _companionSprite is not null;
        }

        var logo = this.FindControl<Image>("LogoMark");
        if (logo is not null)
        {
            var logoId = SkinArtwork.LogoIdFor(skin);
            logo.Source = SkinArtwork.Resolve(logoId);
            logo.IsVisible = logo.Source is not null;
            if (!string.IsNullOrWhiteSpace(logoId))
            {
                Controls.SkinElement.SetAssetId(logo, logoId);
            }
        }
        var assetId = SkinArtwork.CharacterIdFor(skin);
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            Controls.SkinElement.SetAssetId(image, assetId);
        }
        var sheet = assetId is null ? null : App.Host?.Assets.Resolve(assetId);
        var sprite = SpritePlayer.Create(image, assetId);

        if (sprite is null)
        {
            // No sliced frames: fall back to the whole sheet if it exists.
            var bitmap = SkinArtwork.Resolve(assetId);
            if (bitmap is null)
            {
                card.IsVisible = false;
                return;
            }

            image.Source = bitmap;
            subtitle.Text = $"{assetId} · persona={skin?.Persona ?? "无"} · {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}";
        }
        else
        {
            _sprite = sprite;
            sprite.Start();

            var spec = sheet?.Sprite;
            subtitle.Text = spec is null
                ? $"{assetId} · persona={skin?.Persona ?? "无"} · {sprite.FrameCount} 帧"
                : $"{assetId} · persona={skin?.Persona ?? "无"} · {sprite.FrameCount} 帧 · {spec.FrameWidth}×{spec.FrameHeight} · {spec.Fps} fps · {(spec.Loop ? "循环" : "单次")}";
        }

        title.Text = skin?.Name ?? "皮肤";
        card.IsVisible = true;
    }
}
