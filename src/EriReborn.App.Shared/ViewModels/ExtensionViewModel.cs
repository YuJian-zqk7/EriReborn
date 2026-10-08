using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Extension;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Extension management plus the explicit "Refresh Extensions" action
/// (spec 34/36). The refresh really rescans, revalidates and reloads.
/// </summary>
public sealed partial class ExtensionViewModel : ViewModelBase, IPageActions
{
    /// <summary>One line under the page title in the shell's top bar.</summary>
    public string Subtitle => Summary;

    /// <inheritdoc />
    public IReadOnlyList<PageAction> GetPageActions() => new[]
    {
        new PageAction("打开扩展文件夹", OpenExtensionsFolderCommand),
        new PageAction("刷新扩展", RefreshExtensionsCommand, Primary: true),
    };

    private readonly AppHost _host;
    private readonly SynchronizationContext? _context;

    public ExtensionViewModel(AppHost host)
    {
        _host = host;
        _context = SynchronizationContext.Current;
        Title = "扩展";
        ExtensionsDirectory = host.Paths.ExtensionsDirectory;

        // Startup already scanned the folder and loaded what it found. This page used to ignore that
        // result and start empty, so it read as "no extensions installed" until the user pressed
        // refresh — which is not a refresh, it is the page failing to show what the app already knows.
        Show(host.Extensions.Statuses);

        // An install from the marketplace, a dropped-in zip rescanned elsewhere or a toggle in
        // another place changes the registry; the page has to follow without a manual refresh.
        host.Extensions.StatusesChanged += OnStatusesChanged;
    }

    private void OnStatusesChanged()
    {
        if (_context is null)
        {
            Show(_host.Extensions.Statuses);
            return;
        }

        _context.Post(_ => Show(_host.Extensions.Statuses), null);
    }

    public ObservableCollection<ExtensionItemViewModel> Items { get; } = new();

    public string ExtensionsDirectory { get; }

    [ObservableProperty]
    private string _summary = "尚未扫描。";

    /// <summary>Feedback for the open-folder action.</summary>
    [ObservableProperty]
    private string _folderStatus = string.Empty;

    /// <summary>
    /// Opens the extension folder and creates it when it is missing. Installing an
    /// extension means dropping files into that folder, so it has to be one click away:
    /// refreshing only re-reads a folder the user still has to find.
    /// </summary>
    [RelayCommand]
    private void OpenExtensionsFolder()
    {
        var error = _host.Platform.Shell.OpenFolder(ExtensionsDirectory);
        FolderStatus = error is null
            ? $"已打开扩展目录：{ExtensionsDirectory}"
            : $"打开扩展目录失败：{error}";
    }

    [RelayCommand]
    private async Task RefreshExtensionsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var statuses = await _host.Extensions
                .RefreshAsync(
                    _host.Paths.ExtensionsDirectory,
                    _host.ExtensionHost,
                    _host.UserConfig.Current.DisabledExtensions,
                    CancellationToken.None)
                .ConfigureAwait(true);

            Show(statuses);
        }
        catch (Exception ex)
        {
            // A scan that throws must say so rather than coming back looking like an empty folder.
            Summary = $"扫描扩展失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Puts a scan result on the page. Both the startup scan and a manual refresh go through here, so
    /// the page cannot show one thing after a refresh and another after a start.
    /// </summary>
    private void Show(IReadOnlyList<ExtensionStatus> statuses)
    {
        Items.Clear();
        foreach (var status in statuses)
        {
            Items.Add(new ExtensionItemViewModel(status, ToggleExtensionAsync));
        }

        var loaded = statuses.Count(status => status.IsUsable);
        Summary = statuses.Count == 0
            ? $"目录中没有扩展：{ExtensionsDirectory}"
            : $"共 {statuses.Count} 个扩展，成功加载 {loaded} 个。";

        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>
    /// Enables or disables one extension live: the registry really releases (or
    /// creates) the load context, and the choice is persisted so it survives a
    /// restart (spec 34).
    /// </summary>
    private async Task ToggleExtensionAsync(ExtensionItemViewModel item)
    {
        if (IsBusy || !item.CanToggle)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (item.IsEnabled)
            {
                var disabled = _host.Extensions.Disable(item.Id);
                if (disabled is not null)
                {
                    item.Update(disabled);
                    _host.UserConfig.SetExtensionEnabled(item.Id, false);

                    // Take the extension's pages back so the navigation rail drops them
                    // at once; the host raises PagesChanged, which the shell follows.
                    _host.ExtensionHost.RemovePages(item.Id);
                }
            }
            else
            {
                // The async form: an extension that asked for its own process has to
                // start one and complete a handshake, and blocking the UI thread on
                // that is how a slow extension becomes a frozen window.
                var enabled = await _host.Extensions
                    .EnableAsync(item.Id, _host.ExtensionHost)
                    .ConfigureAwait(true);

                // Only persist "enabled" when it actually loaded again.
                if (enabled is not null && enabled.IsUsable)
                {
                    item.Update(enabled);
                    _host.UserConfig.SetExtensionEnabled(item.Id, true);
                }
                else if (enabled is not null)
                {
                    item.Update(enabled);
                }
            }

            UpdateSummary();
        }
        finally
        {
            IsBusy = false;
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void UpdateSummary()
    {
        var loaded = Items.Count(i => i.IsEnabled);
        var off = Items.Count(i => i.IsDisabled);
        var blocked = Items.Count(i => i.IsBlocked);
        Summary = Items.Count == 0
            ? $"目录中没有扩展：{ExtensionsDirectory}"
            : $"共 {Items.Count} 个扩展，已加载 {loaded} 个，已禁用 {off} 个，依赖未满足 {blocked} 个。";
    }
}
