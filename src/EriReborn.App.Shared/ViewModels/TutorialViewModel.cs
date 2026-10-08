using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.Tutorial;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>One entry in the reference tree, with the depth it should be shown at.</summary>
public sealed record TutorialTopicItem(TutorialTopic Topic, int Depth)
{
    public string Id => Topic.Id;

    public string Title => Topic.Title;

    /// <summary>Indentation, because the tree is flattened for a flat list control.</summary>
    public string Indent => new(' ', Depth * 2);

    public override string ToString() => Title;
}

/// <summary>
/// The tutorial: a short first-run walkthrough, plus the full reference tree.
///
/// The walkthrough is deliberately short and only covers getting started; the
/// concepts live in the tree, where they can be read when they are relevant
/// rather than all at once (spec 7-18).
/// </summary>
public sealed partial class TutorialViewModel : ViewModelBase
{
    private readonly AppHost _host;

    public TutorialViewModel(AppHost host)
    {
        _host = host;
        Title = "使用教程";

        RebuildTopicList();

        // Arriving here before finishing the walkthrough means it is still owed.
        ShowOnboarding = !host.TutorialProgress.Current.OnboardingCompleted;
        ApplyStep(0);
    }

    /// <summary>The reference tree, flattened with depth so a flat list can show it.</summary>
    public ObservableCollection<TutorialTopicItem> Topics { get; } = new();

    public bool HasTutorial => _host.Tutorial.Onboarding.Count > 0 || Topics.Count > 0;

    [ObservableProperty]
    private bool _showOnboarding;

    [ObservableProperty]
    private int _stepIndex;

    [ObservableProperty]
    private string _stepTitle = string.Empty;

    [ObservableProperty]
    private string _stepBody = string.Empty;

    [ObservableProperty]
    private string _stepCounter = string.Empty;

    [ObservableProperty]
    private string _selectedTitle = string.Empty;

    [ObservableProperty]
    private string _selectedBody = string.Empty;

    public bool CanGoBack => StepIndex > 0;

    public bool CanGoForward => StepIndex < _host.Tutorial.Onboarding.Count - 1;

    public int StepCount => _host.Tutorial.Onboarding.Count;

    [RelayCommand]
    private void Next()
    {
        if (CanGoForward)
        {
            ApplyStep(StepIndex + 1);
            return;
        }

        // Finishing the last step is what completes the walkthrough.
        Complete();
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            ApplyStep(StepIndex - 1);
        }
    }

    [RelayCommand]
    private void Skip() => Complete();

    /// <summary>Lets the walkthrough be seen again from the tutorial centre.</summary>
    [RelayCommand]
    private void RestartOnboarding()
    {
        _host.TutorialProgress.Reset();
        ShowOnboarding = true;
        ApplyStep(0);
    }

    [RelayCommand]
    private void OpenTopic(TutorialTopicItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedTitle = item.Topic.Title;
        SelectedBody = string.Join(Environment.NewLine + Environment.NewLine, item.Topic.Body);

        // Recorded, so a later session can resume where the user stopped.
        _host.TutorialProgress.MarkSeen(item.Topic.Id);
    }

    /// <summary>
    /// Raised when the walkthrough is finished or skipped, so the shell can move
    /// the user somewhere useful instead of leaving them on a finished wizard.
    /// </summary>
    public event EventHandler? OnboardingFinished;

    private void Complete()
    {
        _host.TutorialProgress.MarkCompleted(_host.Tutorial.Onboarding.ElementAtOrDefault(StepIndex)?.Id);
        ShowOnboarding = false;
        OnboardingFinished?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStep(int index)
    {
        var steps = _host.Tutorial.Onboarding;
        if (steps.Count == 0)
        {
            StepTitle = "教程内容不可用。";
            StepBody = "随包的教程文件没有加载成功，请检查安装是否完整。";
            StepCounter = string.Empty;
            return;
        }

        StepIndex = Math.Clamp(index, 0, steps.Count - 1);
        var step = steps[StepIndex];

        StepTitle = step.Title;
        StepBody = string.Join(Environment.NewLine + Environment.NewLine, step.Body);
        StepCounter = $"第 {StepIndex + 1} / {steps.Count} 步";

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void RebuildTopicList()
    {
        Topics.Clear();
        foreach (var topic in _host.Tutorial.Topics)
        {
            Add(topic, 0);
        }
    }

    private void Add(TutorialTopic topic, int depth)
    {
        Topics.Add(new TutorialTopicItem(topic, depth));

        foreach (var child in topic.Children)
        {
            Add(child, depth + 1);
        }
    }
}
