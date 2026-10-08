using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>The job list: what long work is running, and a way to stop it.</summary>
public partial class JobsView : UserControl
{
    public JobsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
