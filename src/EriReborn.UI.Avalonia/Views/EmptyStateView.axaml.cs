using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// An empty-list state: the library's illustration plus one factual sentence.
///
/// It takes the collection itself rather than a bool, so a page needs one line and
/// no view-model change, and the state follows the collection as it fills or
/// empties. A page that says nothing at all when a list is empty leaves the reader
/// unable to tell "nothing here" from "still loading" or "broken".
/// </summary>
public partial class EmptyStateView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<EmptyStateView, IEnumerable?>(nameof(ItemsSource));

    /// <summary>Asset-library id of the illustration, e.g. empty_no_extension.</summary>
    public static readonly StyledProperty<string?> AssetIdProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(AssetId));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(Caption));

    /// <summary>
    /// The interface word this caption is: when set, the sentence is resolved through
    /// <see cref="UiTexts"/> and becomes editable in the skin editor, the same as every other label.
    /// A page that passes a one-off sentence keeps using <see cref="Caption"/> instead.
    /// </summary>
    public static readonly StyledProperty<string?> CaptionTextKeyProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(CaptionTextKey));

    private INotifyCollectionChanged? _observed;
    private bool _attached;

    public EmptyStateView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            if (App.Main is { } shell)
            {
                shell.SkinChanged += OnSkinChanged;
            }

            Refresh();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Unsubscribe();
            if (App.Main is { } shell)
            {
                shell.SkinChanged -= OnSkinChanged;
            }
        };
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? AssetId
    {
        get => GetValue(AssetIdProperty);
        set => SetValue(AssetIdProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string? CaptionTextKey
    {
        get => GetValue(CaptionTextKeyProperty);
        set => SetValue(CaptionTextKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty
            || change.Property == AssetIdProperty
            || change.Property == CaptionProperty
            || change.Property == CaptionTextKeyProperty)
        {
            Refresh();
        }
    }

    private void OnSkinChanged(object? sender, SkinManifest manifest) => Refresh();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Unsubscribe()
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
    }

    private void Refresh()
    {
        var image = this.FindControl<Image>("Art");
        var text = this.FindControl<TextBlock>("CaptionText");

        if (text is not null)
        {
            var key = CaptionTextKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                // A skin word like any other: the key is declared on the control so the editor can
                // change the sentence, and the words come from the same table every label uses.
                Controls.SkinElement.SetId(text, "empty.caption");
                Controls.SkinElement.SetKind(text, SkinElementKind.Text);
                Controls.SkinElement.SetTextKey(text, key);
                Controls.SkinElement.SetDisplayName(text, "空态说明");
                text.Text = UiTexts.Resolve(App.Host?.Skins.Active, key);
            }
            else
            {
                text.Text = Caption ?? string.Empty;
            }
        }

        var collection = ItemsSource;

        Unsubscribe();
        if (collection is INotifyCollectionChanged notifier)
        {
            _observed = notifier;
            notifier.CollectionChanged += OnCollectionChanged;
        }

        var empty = true;
        if (collection is not null)
        {
            foreach (var _ in collection)
            {
                empty = false;
                break;
            }
        }

        IsVisible = empty;

        if (!empty || image is null)
        {
            return;
        }

        image.Source = SkinArtwork.Resolve(AssetId);
        image.IsVisible = image.Source is not null;

        // The slot names the picture this page actually asked for, so the skin editor replaces the
        // right one instead of a generic empty-state illustration.
        if (!string.IsNullOrWhiteSpace(AssetId))
        {
            Controls.SkinElement.SetAssetId(image, AssetId);
        }

        // A page can build the control before the asset host is up; the skin change
        // event re-runs this, so the art appears as soon as there is a host.
        _ = _attached;
    }
}
