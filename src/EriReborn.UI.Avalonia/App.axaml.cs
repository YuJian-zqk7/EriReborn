using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.UI.Avalonia.Views;

namespace EriReborn.UI.Avalonia;

public partial class App : Application
{
    /// <summary>
    /// Set by the platform entry point after the composition root has run.
    /// The UI never constructs services itself (spec 67).
    /// </summary>
    public static AppHost? Host { get; set; }

    public static MainViewModel? Main { get; set; }

    /// <summary>Set when startup failed so the shell can explain why (spec 66).</summary>
    public static EriReborn.Core.Diagnostics.StartupReport? StartupFailure { get; set; }

    private bool _skinHooked;

    /// <summary>
    /// Wires skin switching and applies the startup skin. This must run for
    /// every host — desktop window and Android single view alike — otherwise
    /// the shell renders with no skin resources (spec 47).
    /// </summary>
    public static void ApplyActiveSkin()
    {
        var application = Current;
        var host = Host;
        if (application is null || host is null)
        {
            return;
        }

        var main = Main;
        if (host.Skins.Active is null && main is not null && host.Skins.Available.FirstOrDefault() is { } first)
        {
            main.ActiveSkin = first;
        }

        if (host.Skins.Active is { } active)
        {
            SkinResourceApplier.Apply(application, active);
        }
    }

    private void HookMainViewModel()
    {
        if (_skinHooked || Main is null)
        {
            return;
        }

        Main.SkinChanged += (_, manifest) => SkinResourceApplier.Apply(this, manifest);
        _skinHooked = true;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        WindowBase? topLevel = null;

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = StartupFailure is { Succeeded: false } desktopFailure
                    ? new StartupErrorWindow(desktopFailure)
                    : Main is not null
                        ? new MainWindow { DataContext = Main }
                        : new StartupErrorWindow(StartupReportForMissingHost());
                topLevel = desktop.MainWindow;
                break;

            case ISingleViewApplicationLifetime singleView:
                // Android and other single-view hosts share the same shell (spec 8).
                singleView.MainView = StartupFailure is { Succeeded: false } mobileFailure
                    ? new StartupErrorView { DataContext = new StartupErrorViewModel(mobileFailure) }
                    : Main is not null
                        ? new MainView { DataContext = Main }
                        : new StartupErrorView { DataContext = new StartupErrorViewModel(StartupReportForMissingHost()) };
                topLevel = singleView.MainView as WindowBase;
                break;
        }

        // Extensions ask for files through the host; route that to the active
        // window's storage provider so a real picker opens. Done after the
        // window exists, because StorageProvider needs a top-level. The task is
        // returned as-is: blocking on it here deadlocked the UI thread, because
        // the dialog itself needs that thread to come back.
        if (Host is not null && topLevel is not null)
        {
            Host.PickFile = (title, filter) => PickFileAsync(topLevel, title, filter);

            // A reader session opens in a real window. The view model is created on
            // this thread — the one the shell was built on — so its event marshalling
            // captures the right context from birth.
            Host.OpenReaderWindow = session =>
            {
                var viewModel = new ReaderWindowViewModel(session, Host.Log.For("Reader"));
                var window = new ReaderWindow { DataContext = viewModel };
                if (topLevel is Window owner)
                {
                    window.Show(owner);
                }
                else
                {
                    window.Show();
                }

                return true;
            };
        }

        // Both hosts need the skin before the first frame is presented.
        HookMainViewModel();
        ApplyActiveSkin();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Shows a file-open dialog owned by the given top-level. Returns the chosen
    /// path or null when the user cancels. The filter string uses the
    /// "Name|*.ext;*.ext2" form that Windows file dialogs understand.
    /// </summary>
    private static async Task<string?> PickFileAsync(WindowBase topLevel, string title, string filter)
    {
        if (topLevel.StorageProvider is not { } storage)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        // Parse "Name|*.ext;*.ext2" into file type choices. The pipe separates
        // the display name from the pattern list; a missing pipe means one
        // pattern with no friendly name.
        var parts = filter.Split('|');
        if (parts.Length >= 2)
        {
            options.FileTypeFilter = new[]
            {
                new FilePickerFileType(parts[0])
                {
                    Patterns = parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries),
                },
            };
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(options);
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static EriReborn.Core.Diagnostics.StartupReport StartupReportForMissingHost()
        => EriReborn.Core.Diagnostics.StartupReport.Failure(
            EriReborn.Core.Diagnostics.StartupFailureKind.Dependency,
            "应用宿主未初始化。");
}
