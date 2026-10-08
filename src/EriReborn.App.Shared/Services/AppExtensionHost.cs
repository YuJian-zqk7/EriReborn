using EriReborn.Core.Logging;
using EriReborn.Extension;

namespace EriReborn.App.Shared.Services;

/// <summary>
/// Services handed to extensions while the application starts.
///
/// This host performs no permission checks of its own: it is wrapped by
/// <c>PermissionCheckedExtensionHost</c> at load time, so the gate exists in one
/// place rather than once per host. Settings are kept per run; persisting them is
/// a later concern.
/// </summary>
public sealed class AppExtensionHost(
    IAppLogger log,
    EriReborn.Cloud.CloudProviderRegistry? cloudProviders = null,
    EriReborn.Engine.Download.DownloadEngineSelector? downloadEngines = null,
    EriReborn.Engine.Plugins.PluginRegistry? pluginResources = null,
    EriReborn.Platform.Abstractions.INetworkService? network = null,
    EriReborn.Platform.Abstractions.ICredentialStore? credentials = null,
    EriReborn.Platform.Abstractions.IProcessService? processes = null,
    Func<string, string, Task<string?>>? pickFile = null,
    Func<string, string?>? dataDirectoryResolver = null,
    Func<EriReborn.Extension.Reader.IReaderSession, bool>? openReaderWindow = null) : IExtensionHost
{
    private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

    private readonly Func<string, string?>? _dataDirectoryResolver = dataDirectoryResolver;

    private readonly Func<EriReborn.Extension.Reader.IReaderSession, bool>? _openReaderWindow = openReaderWindow;

    /// <summary>Pages contributed by extensions, in registration order.</summary>
    public List<IExtensionPage> Pages => _pages.Select(p => p.Page).ToList();

    private readonly List<(string? Owner, IExtensionPage Page)> _pages = new();

    /// <summary>
    /// Raised whenever the page set changes — a page registered, replaced or removed.
    /// The shell rebuilds the extension part of the navigation rail on this, which is
    /// what lets an enabled extension show up without a restart.
    /// </summary>
    public event EventHandler? PagesChanged;

    private readonly Func<string, string, Task<string?>>? _pickFile = pickFile;

    /// <summary>The platform's HTTP client, handed to an extension that declared network.http.</summary>
    public EriReborn.Platform.Abstractions.INetworkService? Network => network;

    /// <summary>The platform's credential store, handed to an extension that declared credentials.read.</summary>
    public EriReborn.Platform.Abstractions.ICredentialStore? Credentials => credentials;

    /// <summary>The platform's process runner, handed to an extension that declared process.run.</summary>
    public EriReborn.Platform.Abstractions.IProcessService? Processes => processes;

    /// <summary>The host's logger, so a contributed engine logs through the app's sinks.</summary>
    public EriReborn.Core.Logging.IAppLogger? Logger => log;

    public void Log(string level, string message) => log.Write(
        level.ToLowerInvariant() switch
        {
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warn,
            "debug" => LogLevel.Debug,
            "trace" => LogLevel.Trace,
            _ => LogLevel.Info,
        },
        "extension.host",
        message);

    public string GetSetting(string key, string? fallback = null)
        => _settings.TryGetValue(key, out var value) ? value : fallback ?? string.Empty;

    public void SetSetting(string key, string value) => _settings[key] = value;

    /// <summary>Wires a contributed cloud provider into the live resolver registry.</summary>
    public void RegisterCloudProvider(EriReborn.Cloud.ICloudProvider provider)
    {
        if (cloudProviders is null)
        {
            return;
        }

        log.Warn("extension.host", $"扩展贡献了云盘平台：{provider.Id}（{provider.DisplayName}）。");
        cloudProviders.Add(provider);
    }

    /// <summary>Wires a contributed download engine into the live selector.</summary>
    public void RegisterDownloadEngine(EriReborn.Engine.Download.IDownloadEngine engine)
    {
        if (downloadEngines is null)
        {
            return;
        }

        log.Warn("extension.host", $"扩展贡献了下载引擎：{engine.Id}（{engine.DisplayName}）。");

        // Preferred, not appended: see AddPreferred — an appended engine would lose
        // to the built-in engine that claims every URL route.
        downloadEngines.AddPreferred(engine);
    }

    /// <summary>Appends contributed catalog entries to the imported-resources registry.</summary>
    public void ContributeSoftware(IEnumerable<EriReborn.Core.Domain.SoftwareDefinition> items)
    {
        if (pluginResources is null)
        {
            return;
        }

        var count = items?.Count() ?? 0;
        log.Warn("extension.host", $"扩展贡献了 {count} 条软件目录条目。");
        if (items is not null)
        {
            pluginResources.Add(items);
        }
    }

    /// <summary>Keeps a contributed page so the shell can list and render it.</summary>
    public void RegisterPage(IExtensionPage page)
    {
        RegisterPage(page, null);
    }

    public void RegisterPage(IExtensionPage page, string? ownerExtensionId)
    {
        ArgumentNullException.ThrowIfNull(page);
        log.Info("extension.host", $"扩展贡献了页面：{page.Key}（{page.Title}）。");

        // Replace by key rather than append: a refresh reloads an enabled extension and its
        // Initialize runs again — appending would leave two entries for the same page.
        var index = _pages.FindIndex(p => string.Equals(p.Page.Key, page.Key, StringComparison.Ordinal));
        if (index >= 0)
        {
            _pages[index] = (ownerExtensionId, page);
        }
        else
        {
            _pages.Add((ownerExtensionId, page));
        }

        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes back the pages of a disabled or unloaded extension.</summary>
    public void RemovePages(string ownerExtensionId)
    {
        var removed = _pages.RemoveAll(p => string.Equals(p.Owner, ownerExtensionId, StringComparison.Ordinal));
        if (removed > 0)
        {
            log.Info("extension.host", $"扩展 '{ownerExtensionId}' 的 {removed} 个页面已收回。");
            PagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Delegates to the UI layer's file picker when one was supplied.</summary>
    public Task<string?> PickFileAsync(string title, string filter)
        => _pickFile?.Invoke(title, filter) ?? Task.FromResult<string?>(null);

    /// <summary>
    /// Creates the extension's own directory under UserData/extensions-data on first
    /// ask, so whatever the extension writes there survives a restart.
    /// </summary>
    public string? GetDataDirectory(string extensionId)
    {
        if (_dataDirectoryResolver is null)
        {
            return null;
        }

        try
        {
            var path = _dataDirectoryResolver(extensionId);
            if (path is not null)
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }
        catch (Exception ex)
        {
            log.Warn("extension.host", $"为扩展 '{extensionId}' 准备数据目录失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>Delegates to the UI layer's reader window when one was supplied.</summary>
    public bool OpenReaderWindow(EriReborn.Extension.Reader.IReaderSession session)
        => _openReaderWindow?.Invoke(session) ?? false;
}
