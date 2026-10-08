using Avalonia;
using Avalonia.Controls;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// A label whose words come from the active skin (spec 5/6).
///
/// <para>
/// It is a <see cref="TextBlock"/>, so it is used exactly like one — classes, sizes, wrapping and
/// trimming all carry over — and a view keeps no copy of the wording. The text is looked up when
/// the control is attached and again whenever the skin changes, which is what makes a rename show
/// up without a restart.
/// </para>
///
/// <code>&lt;controls:UiText Key="nav.settings" /&gt;</code>
/// </summary>
public sealed class UiText : TextBlock
{
    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<UiText, string?>(nameof(Key));

    public UiText()
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

    /// <summary>Stable name for the wording, for example nav.settings.</summary>
    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == KeyProperty)
        {
            Refresh();
        }
    }

    private void OnSkinChanged(object? sender, SkinManifest manifest) => Refresh();

    private void Refresh()
        => Text = Key is { Length: > 0 } key ? UiTexts.Resolve(App.Main?.ActiveSkin, key) : string.Empty;
}
