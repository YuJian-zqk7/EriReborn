using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.Extension;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Wraps an <see cref="IExtensionPage"/> so the generic extension-page view can
/// bind to it. The page's body and actions come straight from the extension;
/// this class only adapts them to the MVVM surface the view expects.
/// </summary>
public sealed partial class ExtensionPageViewModel : ObservableObject
{
    private readonly IExtensionPage _page;

    /// <summary>Thread this view model was built on (a navigation on the UI thread);
    /// page body updates are marshalled here no matter which thread raised them.</summary>
    private readonly SynchronizationContext? _context;

    public ExtensionPageViewModel(IExtensionPage page)
    {
        _page = page;
        _context = SynchronizationContext.Current;
        Title = page.Title;
        Body = page.Body;

        foreach (var action in page.Actions)
        {
            Actions.Add(new ExtensionActionViewModel(action));
        }

        page.BodyChanged += (_, _) =>
        {
            void Apply()
            {
                Body = page.Body;
                OnPropertyChanged(nameof(Body));
            }

            // An extension may raise this from a worker thread (a file loading in the
            // background, say). Observable property changes have to happen where the
            // view can hear them, so the update is bounced to the UI thread.
            if (_context is null || SynchronizationContext.Current == _context)
            {
                Apply();
            }
            else
            {
                _context.Post(_ => Apply(), null);
            }
        };
    }

    public string Title { get; }

    [ObservableProperty]
    private string _body = string.Empty;

    public ObservableCollection<ExtensionActionViewModel> Actions { get; } = new();
}

/// <summary>One action button on an extension page, bound to the view.</summary>
public sealed partial class ExtensionActionViewModel : ObservableObject
{
    private readonly ExtensionPageAction _action;

    public ExtensionActionViewModel(ExtensionPageAction action)
    {
        _action = action;
        Label = action.Label;
        IsPrimary = action.IsPrimary;
    }

    public string Label { get; }

    public bool IsPrimary { get; }

    [RelayCommand]
    private void Invoke() => _action.OnClick();
}
