using CommunityToolkit.Mvvm.ComponentModel;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>Shared base for every page view model.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;
}
