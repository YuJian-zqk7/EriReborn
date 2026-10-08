using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Core.Domain;
using EriReborn.Engine.Plugins;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>
/// Resource plugins: bundles of download sources.
///
/// A plugin is data. Importing one adds resources; it never runs anything, and
/// this page says so rather than leaving the user to guess (spec 42/182).
/// </summary>
public sealed partial class PluginsViewModel : ViewModelBase, IPageActions
{
    /// <summary>One line under the page title in the shell's top bar.</summary>
    public string Subtitle => Status;

    /// <inheritdoc />
    public IReadOnlyList<PageAction> GetPageActions() => new[]
    {
        new PageAction("刷新", RefreshInstalledCommand),
        new PageAction("打开插件文件夹", OpenPluginsFolderCommand),
        new PageAction("导入插件", ImportFromFolderCommand, Primary: true),
    };

    private readonly AppHost _host;

    public PluginsViewModel(AppHost host)
    {
        _host = host;
        Title = "插件";
        SelectedSection = Sections[0];

        var pluginsDirectory = Path.Combine(host.Paths.UserDataDirectory, "plugins");
        PluginsDirectory = pluginsDirectory;
        PluginPath = Path.Combine(pluginsDirectory, "resources.eriplugin.json");
        SignaturePath = PluginPath + PluginSignature.FileExtension;
        ReloadInstalled();

        foreach (var provider in host.CloudProviders.Providers)
        {
            Providers.Add(new PluginProviderViewModel(provider.Id, provider.DisplayName));
        }

        Status = host.CloudProviders.Providers.Count == 0
            ? "尚未注册任何网盘平台，插件的来源无法校验。"
            : $"已注册 {host.CloudProviders.Providers.Count} 个网盘平台，五者平级。";

        MarketplaceStatus = host.PluginMarketplaceIndexUrl is null
            ? "尚未配置插件商城索引地址（可设置 ERIREBORN_PLUGIN_INDEX）。"
            : "尚未加载插件商城索引。";

        _ = RefreshMarketplaceAsync();
    }

    /// <summary>The folder a plugin file is dropped into.</summary>
    public string PluginsDirectory { get; }

    /// <summary>Feedback for the open-folder action.</summary>
    [ObservableProperty]
    private string _folderStatus = string.Empty;

    /// <summary>
    /// Opens the plugin folder, creating it if needed. A plugin is data that lives in a
    /// file; without a way to reach that file the plugin story has no entry point.
    /// </summary>
    [RelayCommand]
    private void OpenPluginsFolder()
    {
        var error = _host.Platform.Shell.OpenFolder(PluginsDirectory);
        FolderStatus = error is null
            ? $"已打开插件目录：{PluginsDirectory}"
            : $"打开插件目录失败：{error}";
    }

    /// <summary>
    /// Re-reads the plugin folder so files added or removed outside the app show up
    /// without a restart. Also re-checks the marketplace index in case it changed.
    /// </summary>
    [RelayCommand]
    private void RefreshInstalled()
    {
        ReloadInstalled();
        Status = $"已刷新插件目录，共 {Installed.Count} 个插件。";
        _ = RefreshMarketplaceAsync();
    }

    /// <summary>Plugins offered by the marketplace index.</summary>
    public ObservableCollection<MarketplacePluginItem> MarketplaceEntries { get; } = new();

    [ObservableProperty]
    private string _marketplaceStatus = string.Empty;

    [ObservableProperty]
    private string _marketplaceIndexUrl = string.Empty;

    [RelayCommand]
    private async Task RefreshMarketplaceAsync()
        => MarketplaceStatus = await LoadMarketplaceEntriesAsync().ConfigureAwait(true);

    /// <summary>
    /// Reloads the list and returns what to say about it.
    ///
    /// The text is returned rather than assigned, so a refresh that follows an
    /// install does not wipe out the install's result before it can be read.
    /// </summary>
    private async Task<string> LoadMarketplaceEntriesAsync()
    {
        try
        {
            MarketplaceIndexUrl = _host.PluginMarketplaceIndexUrl ?? MarketplaceIndexUrl;
            var cacheFile = Path.Combine(_host.Paths.UserDataDirectory, "plugins", "marketplace-index.json");

            var (index, fromCache, message) = await _host.PluginMarketplace
                .FetchAsync(MarketplaceIndexUrl, cacheFile)
                .ConfigureAwait(true);

            MarketplaceEntries.Clear();
            foreach (var entry in index.Entries)
            {
                // The installed record is what makes "update" recognisable at all.
                MarketplaceEntries.Add(new MarketplacePluginItem(entry, _host.PluginInstallations.Find(entry.Id)));
            }

            return fromCache ? $"{message}（缓存）" : message;
        }
        catch (Exception ex)
        {
            return $"加载插件商城失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallMarketplacePluginAsync(MarketplacePluginItem? item)
    {
        if (item is null)
        {
            return;
        }

        var updating = item.IsInstalled;
        MarketplaceStatus = $"正在{(updating ? "更新" : "安装")} {item.Name}…";

        try
        {
            var pluginsDirectory = Path.Combine(_host.Paths.UserDataDirectory, "plugins");
            var catalogIds = _host.Catalog.Software.Select(software => software.Id).ToList();

            // An update replaces this plugin's own resources; an install adds new
            // ones. The two are different operations, not a flag on one.
            var result = updating
                ? await _host.PluginMarketplaceInstaller
                    .UpdateAsync(item.Entry, pluginsDirectory, catalogIds)
                    .ConfigureAwait(true)
                : await _host.PluginMarketplaceInstaller
                    .InstallAsync(item.Entry, pluginsDirectory, catalogIds)
                    .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                // The stage is named, so a refusal is locatable rather than vague.
                var stage = result.Stage is { } value ? $"（{Describe(value)}）" : string.Empty;
                MarketplaceStatus = $"安装失败{stage}：{result.Message}";
                return;
            }

            MarketplaceStatus = result.Message;
            RefreshRegisteredResources();

            // Re-read the index view so the installed version shown is the new one,
            // without overwriting the result the user just asked for.
            await LoadMarketplaceEntriesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MarketplaceStatus = $"安装失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Re-reads what has been registered, so the list matches reality.</summary>
    private void RefreshRegisteredResources()
    {
        Resources.Clear();
        foreach (var definition in _host.PluginResources.Imported)
        {
            Resources.Add(new PluginResourceViewModel(
                definition.Id,
                definition.Name,
                definition.Sources.Count,
                definition.Trust.ToString()));
        }
    }

    /// <summary>The five platforms, shown without any ordering or preference.</summary>
    public ObservableCollection<PluginProviderViewModel> Providers { get; } = new();

    /// <summary>Resources added by the most recent successful import.</summary>
    public ObservableCollection<PluginResourceViewModel> Resources { get; } = new();

    // ---- Left-hand directory ------------------------------------------------
    // The page used to stack three unrelated cards on top of each other. Naming the
    // three things it can do gives the user a目录 to choose from instead of a wall,
    // and each entry carries the same icon the rest of the app uses for that idea.

    private const string ImportKey = "import";
    private const string MarketplaceKey = "marketplace";
    private const string ResourcesKey = "resources";

    /// <summary>The three things this page can do, in the order a user meets them.</summary>
    public ObservableCollection<PluginSectionViewModel> Sections { get; } = new(new[]
    {
        new PluginSectionViewModel(ImportKey, "导入插件", "icon_plugin"),
        new PluginSectionViewModel(MarketplaceKey, "插件商城", "icon_search"),
        new PluginSectionViewModel(ResourcesKey, "已导入资源", "icon_cloud"),
    });

    [ObservableProperty]
    private PluginSectionViewModel? _selectedSection;

    public bool IsImportSection => SelectedSection?.Key == ImportKey;

    public bool IsMarketplaceSection => SelectedSection?.Key == MarketplaceKey;

    public bool IsResourcesSection => SelectedSection?.Key == ResourcesKey;

    partial void OnSelectedSectionChanged(PluginSectionViewModel? value)
    {
        OnPropertyChanged(nameof(IsImportSection));
        OnPropertyChanged(nameof(IsMarketplaceSection));
        OnPropertyChanged(nameof(IsResourcesSection));
    }

    [ObservableProperty]
    private string _pluginPath = string.Empty;

    [ObservableProperty]
    private string _signaturePath = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Which import stage last ran. Named, so a failure is locatable.</summary>
    [ObservableProperty]
    private string _stageText = string.Empty;

    [ObservableProperty]
    private string _trustText = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsImporting)
        {
            return;
        }

        IsImporting = true;
        Resources.Clear();
        StageText = string.Empty;
        TrustText = string.Empty;

        try
        {
            var outcome = await ImportFileAsync(PluginPath).ConfigureAwait(true);
            Status = outcome.Message;
            ReloadInstalled();
        }
        catch (Exception ex)
        {
            // Say what actually happened instead of reporting a state we did not reach.
            Status = $"导入失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Imports every plugin file sitting in the plugin folder. The folder is the entry
    /// point — drop a file in and press the button — so nobody has to type an absolute
    /// path into a text box, which is how a plugin page ends up unused.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFolderAsync()
    {
        if (IsImporting)
        {
            return;
        }

        IsImporting = true;
        Resources.Clear();
        StageText = string.Empty;
        TrustText = string.Empty;

        try
        {
            var files = Directory.Exists(PluginsDirectory)
                ? Directory.GetFiles(PluginsDirectory, "*" + ResourcePlugin.FileExtension)
                : Array.Empty<string>();

            if (files.Length == 0)
            {
                Status = $"插件文件夹里还没有 {ResourcePlugin.FileExtension} 文件：{PluginsDirectory}";
                return;
            }

            var added = 0;
            var lines = new List<string>();
            foreach (var file in files)
            {
                var outcome = await ImportFileAsync(file).ConfigureAwait(true);
                added += outcome.Accepted;
                lines.Add($"{Path.GetFileName(file)}：{outcome.Message}");
            }

            Status = $"处理了 {files.Length} 个插件文件，新增 {added} 条资源。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, lines);

            ReloadInstalled();
        }
        catch (Exception ex)
        {
            Status = $"导入失败：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// The plugins sitting in the plugin folder, with a switch each — the same shape the
    /// extensions page uses, because "installed things you can turn off" is one idea.
    /// </summary>
    public ObservableCollection<PluginItemViewModel> Installed { get; } = new();

    public string InstalledSummary => Installed.Count == 0
        ? "插件文件夹里还没有插件。"
        : $"插件文件夹里有 {Installed.Count} 个插件，已启用 {Installed.Count(item => item.IsEnabled)} 个。";

    /// <summary>
    /// Rebuilds the list by reading the folder. The files are the source of truth, not what
    /// happens to be imported: a disabled plugin's resources are gone from the registry, and a
    /// list built from the registry would lose the very entry that has to be switched back on.
    /// </summary>
    private void ReloadInstalled()
    {
        Installed.Clear();

        var present = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(PluginsDirectory))
        {
            // The whole folder is gone, so nothing it contributed can still be claimed.
            DropVanishedPlugins(present);
            OnPropertyChanged(nameof(InstalledSummary));
            return;
        }

        foreach (var path in Directory.GetFiles(PluginsDirectory, "*" + ResourcePlugin.FileExtension))
        {
            try
            {
                var (plugin, _) = PluginReader.Parse(File.ReadAllText(path));
                if (plugin is null)
                {
                    continue;
                }

                present.Add(plugin.Id);

                Installed.Add(new PluginItemViewModel(
                    plugin.Id,
                    plugin.Name,
                    plugin.Version,
                    plugin.Resources.Count,
                    !_host.UserConfig.IsPluginDisabled(plugin.Id),
                    path));
            }
            catch (Exception ex)
            {
                Status = $"{Path.GetFileName(path)} 读不出来：{ex.Message}";
            }
        }

        DropVanishedPlugins(present);
        OnPropertyChanged(nameof(InstalledSummary));
    }

    /// <summary>
    /// Takes back the resources of every plugin file that is no longer in the folder.
    ///
    /// <para>
    /// Deleting a file used to leave what it had contributed imported for the rest of the session:
    /// this page stopped listing it while the software page still showed its resources, so removing
    /// a plugin read as doing nothing until a restart. The folder is the source of truth, so a file
    /// that is not in it owns nothing.
    /// </para>
    /// </summary>
    private void DropVanishedPlugins(IReadOnlySet<string> present)
    {
        var vanished = _host.PluginResources.Imported
            .Where(item => item.IsPluginProvided && !string.IsNullOrWhiteSpace(item.CatalogId))
            .Select(item => item.CatalogId!)
            .Where(id => !present.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var id in vanished)
        {
            _host.PluginResources.Replace(id, Array.Empty<SoftwareDefinition>());
        }
    }

    /// <summary>
    /// Turns one plugin on or off. Disabling takes back exactly that plugin's resources —
    /// another plugin's entries are never collateral damage, and the official catalog is never
    /// touched. The file stays where it is either way.
    /// </summary>
    [RelayCommand]
    private async Task TogglePluginAsync(PluginItemViewModel item)
    {
        if (IsImporting)
        {
            return;
        }

        if (item.IsEnabled)
        {
            // The preference is written BEFORE the registry changes, and the order is the whole
            // point. Replacing the resources raises the event the software page rebuilds on, and a
            // rebuild that still read "enabled" put the official list straight back on screen: the
            // switch looked like it did nothing, while doing exactly what it was written to do.
            _host.UserConfig.SetPluginEnabled(item.Id, false);
            _host.PluginResources.Replace(item.Id, Array.Empty<SoftwareDefinition>());

            Status = $"已禁用「{item.Name}」：它带来的资源已从软件列表下线，文件还在，随时可以再启用。";
            ReloadInstalled();
            return;
        }

        IsImporting = true;

        // Enabling imports the file again, and an import appends one row per resource. Without this
        // the page listed the plugin's resources a second, third, fourth time — switching the official
        // plugin off and on again turned 263 rows into 526, then 789. The two import buttons clear
        // the list for the same reason; this path was the one that did not.
        Resources.Clear();

        try
        {
            // Same order in this direction, and rolled back when the import does not go through:
            // the preference and the registry must agree at every moment an event can be raised.
            _host.UserConfig.SetPluginEnabled(item.Id, true);

            var outcome = await ImportFileAsync(item.Path).ConfigureAwait(true);
            if (!outcome.Succeeded)
            {
                _host.UserConfig.SetPluginEnabled(item.Id, false);
                Status = $"启用失败：{outcome.Message}";
                return;
            }

            Status = $"已启用「{item.Name}」：{outcome.Message}";
        }
        finally
        {
            IsImporting = false;
        }

        ReloadInstalled();
    }

    /// <summary>
    /// Imports one plugin file and returns the one-line outcome plus how many resources it
    /// actually added. Shared by the folder-wide and the explicit-path entry points so the
    /// two cannot drift apart.
    /// </summary>
    private async Task<PluginImportOutcome> ImportFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new PluginImportOutcome($"找不到插件文件：{path}", 0, false);
        }

        var json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        var signaturePath = path + PluginSignature.FileExtension;
        var signature = File.Exists(signaturePath)
            ? await File.ReadAllTextAsync(signaturePath).ConfigureAwait(true)
            : null;

        // Existing ids come from the live catalog: a plugin may only add.
        var existing = _host.Catalog.Software.Select(item => item.Id).ToList();
        var result = _host.Plugins.Import(json, signature, existing);

        // A plugin the user switched off is not put back on the list by an import. The folder import
        // walks every file in the folder, so without this a disabled plugin would reappear the moment
        // anyone pressed 「导入插件」.
        if (result.PluginId is not null && _host.UserConfig.IsPluginDisabled(result.PluginId))
        {
            return new PluginImportOutcome(
                $"「{result.PluginId}」已禁用，本次跳过；要它生效请先在列表里启用。",
                0,
                false);
        }

        // Register, so the resources reach the software engine instead of only being
        // listed on this page.
        var accepted = result.Accepted.Count == 0 ? 0 : _host.PluginResources.Add(result.Accepted);

        StageText = Describe(result.Stage);
        TrustText = signature is null
            ? "未签名：本次导入不携带任何官方权威，资源记为其社区来源。"
            : "已签名：签名已对照内置信任根校验。";

        foreach (var definition in result.Accepted)
        {
            Resources.Add(new PluginResourceViewModel(
                definition.Id,
                definition.Name,
                definition.Sources.Count,
                definition.Trust.ToString()));
        }

        var rejected = result.Rejections.Count;
        var message = result.Succeeded
            ? result.Message + (rejected > 0 ? $"（{rejected} 条因 id 冲突被拒绝）" : string.Empty)
            : $"导入未通过（阶段：{Describe(result.Stage)}）：{result.Message}";
        return new PluginImportOutcome(message, accepted, result.Succeeded);
    }

    private static string Describe(PluginImportStage stage) => stage switch
    {
        PluginImportStage.Read => "读取",
        PluginImportStage.SchemaValidate => "结构校验",
        PluginImportStage.SignatureVerify => "签名校验",
        PluginImportStage.ResourceValidate => "资源校验",
        PluginImportStage.ProviderValidate => "平台校验",
        _ => "登记",
    };
}

/// <summary>The one-line outcome of importing a plugin file, plus whether it got through.</summary>
public sealed record PluginImportOutcome(string Message, int Accepted, bool Succeeded);

/// <summary>One plugin file in the folder, with a switch — the shape the extensions page uses.</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
    public PluginItemViewModel(string id, string name, string version, int resourceCount, bool enabled, string path)
    {
        Id = id;
        Name = name;
        Version = string.IsNullOrWhiteSpace(version) ? "-" : version;
        ResourceCount = resourceCount;
        Path = path;
        _isEnabled = enabled;
    }

    public string Id { get; }

    public string Name { get; }

    public string Version { get; }

    public int ResourceCount { get; }

    /// <summary>Where the file is, so enabling imports exactly this one.</summary>
    public string Path { get; }

    public string Summary => $"ID：{Id}   版本：{Version}   资源：{ResourceCount} 条";

    public string ToggleText => IsEnabled ? "禁用" : "启用";

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(ToggleText));
}

/// <summary>One plugin the marketplace offers, and whether it is already installed.</summary>
public sealed record MarketplacePluginItem(PluginMarketplaceEntry Entry, InstalledPlugin? Installed = null)
{
    public bool IsInstalled => Installed is not null;

    /// <summary>True when a different version is published than the one installed.</summary>
    public bool CanUpdate => IsInstalled
        && !string.Equals(Installed!.Version, Entry.Version, StringComparison.OrdinalIgnoreCase);

    public string InstalledText => Installed switch
    {
        null => "未安装",
        _ when CanUpdate => $"已安装 {Installed.Version} → 可更新到 {Entry.Version}",
        _ => $"已安装 {Installed.Version}",
    };

    public string ActionText => !IsInstalled ? "安装" : CanUpdate ? "更新" : "重装";

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public string Subtitle => string.Join(
        " · ",
        new[] { Entry.Author, Entry.Version }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public string Summary => Entry.Description ?? string.Empty;

    public string ResourceText => Entry.ResourceCount switch
    {
        0 => "资源数量未声明",
        1 => "1 个资源",
        _ => $"{Entry.ResourceCount} 个资源",
    };

    /// <summary>The platforms its sources use, with no ordering preference.</summary>
    public string ProvidersText => Entry.Providers.Count == 0
        ? "未声明来源平台"
        : string.Join(" / ", Entry.Providers);

    public string TrustText => Entry.IsSigned ? "索引声明已签名" : "未签名";

    public override string ToString() => Name;
}

public sealed record PluginProviderViewModel(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>One entry in the plugin page's left-hand directory.</summary>
public sealed record PluginSectionViewModel(string Key, string Title, string IconId)
{
    public override string ToString() => Title;
}

public sealed record PluginResourceViewModel(string Id, string Name, int SourceCount, string Trust)
{
    public string SourceText => SourceCount switch
    {
        0 => "无可用来来源",
        1 => "1 个来源",
        _ => $"{SourceCount} 个来源",
    };
}
