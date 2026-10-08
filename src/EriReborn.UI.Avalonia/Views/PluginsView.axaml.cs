using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// Imports resource plugins. The page only ever shows what the import actually
/// reported, including the stage that refused it (spec 42/47).
/// </summary>
public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
