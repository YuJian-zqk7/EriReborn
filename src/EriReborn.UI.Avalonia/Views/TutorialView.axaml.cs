using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EriReborn.App.Shared.ViewModels;

namespace EriReborn.UI.Avalonia.Views;

/// <summary>
/// The tutorial centre. Choosing a topic opens it and records it, so a later
/// session can resume; nothing here needs the network (spec 7-18).
/// </summary>
public partial class TutorialView : UserControl
{
    public TutorialView()
    {
        InitializeComponent();

        // Wired through the event rather than a command binding: picking a topic
        // from the list is the whole interaction, there is no separate button.
        if (this.FindControl<ListBox>("TopicList") is { } list)
        {
            list.SelectionChanged += (_, _) =>
            {
                if (DataContext is TutorialViewModel model && list.SelectedItem is TutorialTopicItem item)
                {
                    model.OpenTopicCommand.Execute(item);
                }
            };
        }

        // The topics are filled by the view model, which can finish after this control
        // is built, so the first topic is opened when either side is ready.
        DataContextChanged += (_, _) => SelectFirstTopic();
        AttachedToVisualTree += (_, _) => SelectFirstTopic();
    }

    /// <summary>
    /// Opens the first topic on arrival. An empty article pane next to a full list
    /// reads as "the tutorial is broken" rather than as "pick something".
    /// </summary>
    private void SelectFirstTopic()
    {
        if (this.FindControl<ListBox>("TopicList") is not { } list)
        {
            return;
        }

        if (list.SelectedIndex < 0 && list.ItemCount > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
