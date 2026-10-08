using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EriReborn.App.Shared.ViewModels;

namespace EriReborn.UI.Avalonia.Views;

public partial class CloudView : UserControl
{
    public CloudView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the platform's own sign-in page inside our shell. The page is the real one: the user
    /// has to see where the password goes, and the platform does its own 2FA.
    /// </summary>
    private void OpenBrowserLogin(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CloudProviderViewModel provider)
        {
            return;
        }

        // The mapping lives in one place so the view model can tell whether the button applies.
        var page = CloudSignInPages.For(provider.Id);

        if (page is null)
        {
            return;
        }

        new BrowserWindow(provider.DisplayName, new Uri(page), provider.AcceptCapturedCredentialAsync).Show();
    }
}