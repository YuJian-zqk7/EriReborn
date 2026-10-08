using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Extension;
using EriReborn.Extension.Marketplace;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Extension Marketplace (spec 35). Installing here really downloads, verifies,
/// extracts and loads the extension; the page never reports a state it has not
/// observed.
/// </summary>
public sealed partial class MarketplaceViewModel : ViewModelBase
{
    private const string AllCategory = "All";

    private readonly AppHost _host;
    private MarketplaceIndex _index = MarketplaceIndex.Empty;

    public MarketplaceViewModel(AppHost host)
    {
        _host = host;
        Title = "商城";
        IndexUrl = host.MarketplaceIndexUrl;
        Categories.Add(AllCategory);
        _ = LoadAsync();
    }

    public ObservableCollection<MarketplaceItemViewModel> Entries { get; } = new();

    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty]
    private string? _indexUrl;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory = AllCategory;

    [ObservableProperty]
    private string _sourceState = "尚未加载商城索引。";

    [ObservableProperty]
    private string _actionState = string.Empty;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private MarketplaceItemViewModel? _selectedEntry;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            var result = await _host.Marketplace
                .FetchAsync(IndexUrl, Path.Combine(_host.Paths.UserDataDirectory, "marketplace", "index.json"))
                .ConfigureAwait(true);

            _index = result.Index;
            SourceState = result.Message;
            RebuildCategories();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            // This runs fire-and-forget from the constructor, so an exception here
            // would be observed by nobody and the page would sit on "尚未加载" with no
            // explanation at all.
            _index = MarketplaceIndex.Empty;
            SourceState = $"加载商城索引时出错：{ex.GetType().Name}：{ex.Message}";
            RebuildCategories();
            ApplyFilter();
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task SaveIndexUrlAsync()
    {
        _host.SetMarketplaceIndexUrl(IndexUrl);
        _host.UserConfig.Save(_host.UserConfig.Current with { MarketplaceIndexUrl = IndexUrl });
        ActionState = "已保存商城索引地址。";
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallAsync(MarketplaceItemViewModel? item)
    {
        if (item is null || Busy)
        {
            return;
        }

        Busy = true;
        ActionState = $"正在安装 {item.Name}…";

        try
        {
            var outcome = await _host.ExtensionInstaller.InstallAsync(
                item.Entry,
                _host.Paths.ExtensionsDirectory,
                Path.Combine(_host.Paths.UserDataDirectory, "marketplace")).ConfigureAwait(true);

            ActionState = outcome.Message;
            if (outcome.Success)
            {
                item.IsInstalled = true;
                ActionState = $"{outcome.Message} {await RefreshLoadedExtensionsAsync().ConfigureAwait(true)}";
            }
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(MarketplaceItemViewModel? item)
    {
        if (item is null || Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            var outcome = await _host.ExtensionInstaller
                .UninstallAsync(item.Id, _host.Paths.ExtensionsDirectory)
                .ConfigureAwait(true);

            ActionState = outcome.Message;
            if (outcome.Success)
            {
                item.IsInstalled = false;
                ActionState = $"{outcome.Message} {await RefreshLoadedExtensionsAsync().ConfigureAwait(true)}";
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Reloads installed extensions so the effect of install/uninstall is real.</summary>
    private async Task<string> RefreshLoadedExtensionsAsync()
    {
        var statuses = await _host.Extensions
            .RefreshAsync(
                _host.Paths.ExtensionsDirectory,
                _host.ExtensionHost,
                _host.UserConfig.Current.DisabledExtensions)
            .ConfigureAwait(true);

        var loaded = statuses.Count(s => s.IsUsable);
        foreach (var entry in Entries)
        {
            entry.IsInstalled = statuses.Any(s => string.Equals(s.Manifest.Id, entry.Id, StringComparison.Ordinal));
        }

        return $"当前已加载 {loaded} 个扩展。";
    }

    private void RebuildCategories()
    {
        Categories.Clear();
        Categories.Add(AllCategory);
        foreach (var category in _index.Entries
                     .SelectMany(e => e.Categories)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(c => c, StringComparer.Ordinal))
        {
            Categories.Add(category);
        }
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        var query = _index.Entries.AsEnumerable();

        if (!string.Equals(SelectedCategory, AllCategory, StringComparison.Ordinal))
        {
            query = query.Where(e => e.Categories.Contains(SelectedCategory, StringComparer.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(e =>
                e.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || e.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || (e.Description ?? string.Empty).Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || e.Tags.Any(t => t.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var entry in query)
        {
            Entries.Add(new MarketplaceItemViewModel(
                entry,
                Directory.Exists(Path.Combine(_host.Paths.ExtensionsDirectory, entry.Id))));
        }
    }
}
