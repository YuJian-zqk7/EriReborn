using System.Text.Json;
using EriReborn.Asset;
using EriReborn.Cloud;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Jobs;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Engine.Download;
using EriReborn.Engine.Plugins;
using TutorialContent = EriReborn.App.Shared.Tutorial;
using EriReborn.Engine.Software;
using EriReborn.Extension.Marketplace;
using EriReborn.Extension;
using EriReborn.Extension.Signing;
using EriReborn.Persona;
using EriReborn.Platform.Abstractions;
using EriReborn.Skin;

namespace EriReborn.App.Shared.Services;

/// <summary>
/// The composition root (spec 67). Every service is constructed here,
/// explicitly, from a real platform implementation. There is no reflection and
/// no Noop fallback on the production path (spec 68).
/// </summary>
public sealed class AppHost
{
    private AppHost(
        AppPaths paths,
        IAppLogger log,
        IPlatformService platform,
        SoftwareCatalog catalog,
        SkinEngine skins,
        SkinOverrideStore skinOverrides,
        UserSkinStore userSkins,
        AssetManager assets,
        ExtensionRegistry extensions,
        CloudProviderRegistry cloudProviders,
        SoftwareEngine softwareEngine,
        SourceAvailabilityService sourceAvailability,
        SystemScanService scanService,
        InstallationRegistry installations,
        DetectionHintStore detectionHints,
        PersonaCatalog personas,
        TrustedKeyStore trustedKeys,
        HttpDownloader downloader,
        DownloadResolverRegistry resolvers,
        PluginImportService plugins,
        PluginRegistry pluginResources,
        MarketplaceClient marketplace,
        PluginMarketplaceClient pluginMarketplace,
        PluginMarketplaceInstaller pluginMarketplaceInstaller,
        PluginInstallationStore pluginInstallations,
        TutorialContent.TutorialCatalog tutorial,
        TutorialContent.TutorialProgressStore tutorialProgress,
        EriReborn.Core.Jobs.JobManager jobs,
        ExtensionInstaller extensionInstaller,
        UserConfigService userConfig,
        PathResolver pathResolver,
        EnvironmentContext environment,
        IExtensionHost extensionHost,
        DownloadEngineSelector downloadEngines,
        DownloadJobService downloadJobs)
    {
        Paths = paths;
        Log = log;
        Platform = platform;
        Catalog = catalog;
        Skins = skins;
        SkinOverrides = skinOverrides;
        UserSkins = userSkins;
        Assets = assets;
        Extensions = extensions;
        CloudProviders = cloudProviders;
        SoftwareEngine = softwareEngine;
        SourceAvailability = sourceAvailability;
        ScanService = scanService;
        Installations = installations;
        DetectionHints = detectionHints;
        Personas = personas;
        TrustedKeys = trustedKeys;
        Downloader = downloader;
        Resolvers = resolvers;
        Plugins = plugins;
        PluginResources = pluginResources;
        Marketplace = marketplace;
        PluginMarketplace = pluginMarketplace;
        PluginMarketplaceInstaller = pluginMarketplaceInstaller;
        PluginInstallations = pluginInstallations;
        Tutorial = tutorial;
        TutorialProgress = tutorialProgress;
        Jobs = jobs;
        ExtensionInstaller = extensionInstaller;
        UserConfig = userConfig;
        PathResolver = pathResolver;
        Environment = environment;
        ExtensionHost = extensionHost;
        DownloadEngines = downloadEngines;
        DownloadJobs = downloadJobs;

        // A finished download plays a short cue. The check is cheap and runs on
        // every job change, but only the terminal state of a download job acts.
        Jobs.Changed += (_, job) =>
        {
            if (job is not null
                && job.State == JobState.Completed
                && string.Equals(job.Kind, "download", StringComparison.OrdinalIgnoreCase))
            {
                var soundPath = Path.Combine(Paths.AssetsRoot, "sounds", "download-complete.mp3");
                Platform.Audio.PlaySound(soundPath);
            }
        };

        // A skin may bring its own pictures. Registered as additions to the shipped library, and
        // swapped out whenever a different skin is applied, so one skin's art never lingers under
        // another skin's id. Done here because it needs both the engine and the registry to exist.
        Skins.SkinChanged += (_, skin) => LoadSkinSheets(skin);
        LoadSkinSheets(Skins.Active);
    }

    /// <summary>
    /// Layers the workshop's skin text back over the packs discovery found.
    ///
    /// <para>
    /// The engine keeps what discovery found separately, so putting the layer on again is a reset
    /// followed by a re-register rather than an edit of what is already registered: an override the
    /// user deleted is gone from the file, and therefore from the list, instead of lingering as
    /// text the app keeps showing after it was removed.
    /// </para>
    /// </summary>
    public void ReloadSkinOverrides()
    {
        // The file is the truth: an override may have been written through the workshop's own
        // store instance, which this object does not hold. Re-reading first is what lets a
        // deletion actually remove the registered manifest instead of leaving it behind.
        SkinOverrides.Reload();

        // Back to what discovery found. The user's own skins are registered rather than discovered,
        // so they are rebuilt in the next step instead of being lost to the reset.
        Skins.ResetToDiscovered();

        foreach (var manifest in UserSkins.Apply(Skins.Discovered))
        {
            Skins.Register(manifest);
        }

        foreach (var manifest in SkinOverrides.Apply(Skins.Available))
        {
            Skins.Register(manifest);
        }
    }

    /// <summary>
    /// Applies the art a skin brings with it, replacing whatever the previous skin brought.
    ///
    /// <para>
    /// The skin's own manifest is read fresh every time rather than cached: the workshop writes it
    /// while the app is running, and a replaced picture showing up without a restart is the whole
    /// point of the feature.
    /// </para>
    /// </summary>
    public void LoadSkinSheets(SkinManifest? skin)
    {
        var (json, root) = ReadSkinAssetManifest(skin);
        Assets.LoadSkinSheets(json, root);
    }

    /// <summary>Reads a skin's own asset manifest, resolved against the skin's own directory.</summary>
    private (string? Json, string? Root) ReadSkinAssetManifest(SkinManifest? skin)
    {
        if (skin?.AssetManifestFile is not { Length: > 0 } relative
            || skin.SourceFile is not { Length: > 0 } sourceFile)
        {
            return (null, null);
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            if (string.IsNullOrWhiteSpace(directory))
            {
                return (null, null);
            }

            var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

            // The shipped packs point at the global library ("../../asset_manifest.json"). Reading
            // that a second time as if it were the skin's own pack would re-register every shipped
            // id, warn about each one as a duplicate, and add nothing.
            if (string.Equals(path, Path.GetFullPath(Paths.AssetManifestFile), StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }

            return File.Exists(path)
                ? (File.ReadAllText(path), Path.GetDirectoryName(path))
                : (null, null);
        }
        catch (Exception ex)
        {
            Log.Warn("asset.skin_manifest", $"皮肤 '{skin?.Id}' 自带的资源清单读不出来：{ex.Message}");
            return (null, null);
        }
    }

    public AppPaths Paths { get; }

    public IAppLogger Log { get; }

    /// <summary>
    /// Re-resolves the user's skins over their bases and returns what they produced.
    ///
    /// <para>
    /// The folder is read fresh, so a skin the user just created, repainted or textually edited
    /// shows up without a restart. Re-applying is idempotent, and it starts from the shipped list
    /// so a deleted skin really disappears.
    /// </para>
    /// </summary>
    public IReadOnlyList<SkinManifest> ReloadUserSkins()
    {
        // Back to the shipped list first. A deleted skin has to disappear, and the manifest it
        // registered on an earlier pass would otherwise stay behind.
        Skins.ResetToDiscovered();

        var store = new UserSkinStore(Paths, Log.For("Skin"));

        // A user skin may be based on another user skin, and its base has to be resolved before it can
        // be. Resolving in passes is what lets such a skin appear at all: a single pass was given the
        // shipped packs only, so a skin built on one of the user's own skins was dropped from the list
        // with nothing but a log line — and the picker, left without the skin that was in force, fell
        // back to the first pack, which looked like the interface changing itself.
        var resolved = new List<SkinManifest>();

        for (var pass = 0; pass < 4; pass++)
        {
            var bases = Skins.Discovered.Concat(resolved).ToList();
            var found = store.Apply(bases);

            if (found.Count == resolved.Count)
            {
                break;
            }

            resolved = found.ToList();
        }

        foreach (var manifest in resolved)
        {
            Skins.Register(manifest);
        }

        return resolved;
    }

    public IPlatformService Platform { get; }

    public SoftwareCatalog Catalog { get; }

    public SkinEngine Skins { get; }

    /// <summary>The skin text the workshop changed, layered over the read-only packs.</summary>
    public SkinOverrideStore SkinOverrides { get; }

    /// <summary>The skins the user made, resolved over the pack each one names as its base.</summary>
    public UserSkinStore UserSkins { get; }

    /// <summary>
    /// The engines a download may run on. Exposed so a page can offer a download through the one
    /// job service rather than reaching for an HTTP client of its own.
    /// </summary>
    public DownloadEngineSelector DownloadEngines { get; }

    /// <summary>
    /// The one way the interface starts a real download: it resolves, picks an engine and reports
    /// into the task list. A page that downloads without going through this is a bug.
    /// </summary>
    public DownloadJobService DownloadJobs { get; }

    /// <summary>
    /// Asks the user to confirm something, when a shell is around to draw the question. Deleting a skin
    /// or leaving the app are not things to do on a single click, so pages ask through here instead of
    /// acting; a host with no shell (tests, headless runs) has nothing to ask with and lets the action
    /// proceed as it always did.
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Shorthand for <see cref="Confirm"/> that a page can call without a null check.</summary>
    public Task<bool> ConfirmAsync(
        string title,
        string body,
        string confirmText = "确定",
        string cancelText = "取消")
        => Confirm is { } ask ? ask(title, body, confirmText, cancelText) : Task.FromResult(true);

    /// <summary>
    /// Where a download the user asked for is written. Made rather than assumed to exist, and
    /// nothing is written outside it. The user can redirect this to a different drive; without a
    /// choice it stays under the user data directory so the app never writes anywhere it has not
    /// been given permission for.
    /// </summary>
    public string DownloadDirectory
    {
        get
        {
            var chosen = UserConfig.Current.DownloadDirectory;
            var directory = string.IsNullOrWhiteSpace(chosen)
                ? Path.Combine(Paths.UserDataDirectory, "downloads")
                : chosen!;
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public AssetManager Assets { get; }

    public ExtensionRegistry Extensions { get; }

    /// <summary>
    /// The host handed to extensions. Built once from the platform's network / credentials /
    /// process services and the registries an extension may contribute to, and reused by every
    /// later (re)load from a page — otherwise a refresh would load extensions again with fewer
    /// capabilities than the startup load gave them, and a contributed cloud provider or engine
    /// would silently disappear the moment the user opened the Extensions page.
    /// </summary>
    public IExtensionHost ExtensionHost { get; }

    /// <summary>
    /// Set by the UI layer after the shell is up. Extensions call
    /// <see cref="IExtensionHost.PickFileAsync"/> which forwards here, so a file
    /// dialog can be shown from the active window. Null when no UI is attached
    /// (headless tests), in which case the task completes with null.
    /// </summary>
    public Func<string, string, Task<string?>>? PickFile { get; set; }

    /// <summary>
    /// Set by the UI layer once a window exists. An extension's
    /// <see cref="IExtensionHost.OpenReaderWindow"/> forwards here, so a reader
    /// session can be shown in a real window. Null when no UI is attached
    /// (headless tests), in which case the extension falls back to its page.
    /// </summary>
    public Func<EriReborn.Extension.Reader.IReaderSession, bool>? OpenReaderWindow { get; set; }

    /// <summary>
    /// Pages contributed by enabled extensions. The shell reads this to add
    /// navigation entries and resolve their keys. Empty when no extension
    /// contributes a page.
    /// </summary>
    public IReadOnlyList<IExtensionPage> ExtensionPages
        => ExtensionHost is AppExtensionHost appHost ? appHost.Pages : Array.Empty<IExtensionPage>();

    /// <summary>
    /// Raised when an extension registers or releases a page. The shell rebuilds the
    /// extension entries of the navigation rail, so toggling an extension takes effect
    /// immediately instead of asking for a restart.
    /// </summary>
    public event EventHandler? ExtensionPagesChanged;

    /// <summary>Re-raises the page-set change on this instance (the underlying host's event
    /// is attached in <see cref="CreateAsync"/> once the instance exists).</summary>
    public void NotifyExtensionPagesChanged() => ExtensionPagesChanged?.Invoke(this, EventArgs.Empty);

    public CloudProviderRegistry CloudProviders { get; }

    public SoftwareEngine SoftwareEngine { get; }

    /// <summary>
    /// What each source of a resource can actually do right now, and which one to use.
    /// Built here so "pick a source" has one answer across every page.
    /// </summary>
    public SourceAvailabilityService SourceAvailability { get; }

    public SystemScanService ScanService { get; }

    /// <summary>Wording provider, selected by the active skin's persona (spec 57).</summary>
    public PersonaCatalog Personas { get; }

    /// <summary>The publisher keys this installation trusts (spec 35).</summary>
    public TrustedKeyStore TrustedKeys { get; }

    /// <summary>What EriReborn has installed, and where.</summary>
    public InstallationRegistry Installations { get; }

    /// <summary>User-supplied detection paths for otherwise undecidable entries.</summary>
    public DetectionHintStore DetectionHints { get; }

    /// <summary>Shared resumable downloader (platform-neutral construction).</summary>
    public HttpDownloader Downloader { get; }

    /// <summary>
    /// Turns a source into a download route. Kept on the host so a new way of
    /// obtaining a download is registered once, in the composition root.
    /// </summary>
    public DownloadResolverRegistry Resolvers { get; }

    /// <summary>
    /// Copies the plugins that ship with the app into the user's plugin folder, once.
    ///
    /// <para>
    /// They are ordinary plugin files, not a special kind: the folder is the entry point and the
    /// ordinary import action picks them up. An existing file is never overwritten, so a copy the
    /// user replaced or deleted stays that way.
    /// </para>
    ///
    /// <para>
    /// "Never overwritten" held only while the shipped file was frozen. A regenerated bundled
    /// plugin then never reached the folder: the app went on importing the copy it had seeded
    /// earlier, so rewriting the official plugin looked like it had done nothing at all. A copy
    /// that is still in the folder is therefore refreshed when the shipped file declares a
    /// different version. A copy the user deleted is not put back.
    /// </para>
    /// </summary>
    private static void SeedBundledPlugins(AppPaths paths, IAppLogger log)
    {
        var source = Path.Combine(paths.AssetsRoot, "plugins");
        if (!Directory.Exists(source))
        {
            return;
        }

        try
        {
            var target = Path.Combine(paths.UserDataDirectory, "plugins");
            Directory.CreateDirectory(target);

            var recordPath = Path.Combine(paths.UserDataDirectory, SeededPluginsFileName);
            var seeded = ReadSeededPlugins(recordPath, log);
            if (seeded is null)
            {
                // The record is unreadable, so which bundled files the user removed is unknown.
                // Copying on that guess is what put deleted plugins back, so nothing is placed
                // rather than resurrecting a file the user deliberately deleted.
                return;
            }

            var changed = false;

            foreach (var file in Directory.GetFiles(source, "*" + ResourcePlugin.FileExtension))
            {
                var name = Path.GetFileName(file);

                var destination = Path.Combine(target, name);

                // Absent from the folder: either never placed, or placed and then deleted by the
                // user. The record is what tells those two apart.
                if (!File.Exists(destination))
                {
                    if (seeded.Contains(name))
                    {
                        // Placed before and now gone: putting a deleted plugin back is exactly
                        // what this record exists to prevent.
                        continue;
                    }

                    File.Copy(file, destination);
                    seeded.Add(name);
                    changed = true;
                    log.Info("plugin.seed", $"随包插件已放入插件文件夹：{name}");
                    continue;
                }

                // In the folder, whether this run placed it or it was already lying there from
                // before the record existed. Adopt it so a later delete is honoured, then bring
                // it up to the shipped version: without this the folder keeps whichever copy it
                // first received, and a regenerated official plugin never arrives.
                if (!seeded.Contains(name))
                {
                    seeded.Add(name);
                    changed = true;
                }

                var shippedVersion = ReadPluginVersion(file, log);
                var placedVersion = ReadPluginVersion(destination, log);

                if (shippedVersion is null ||
                    string.Equals(shippedVersion, placedVersion, StringComparison.Ordinal))
                {
                    continue;
                }

                File.Copy(file, destination, overwrite: true);
                log.Info("plugin.seed", $"随包插件已更新到 {shippedVersion}（原 {placedVersion}）：{name}");
                continue;
            }

            if (changed)
            {
                WriteSeededPlugins(recordPath, seeded, log);
            }
        }
        catch (Exception ex)
        {
            log.Warn("plugin.seed", $"随包插件无法放入插件文件夹：{ex.Message}");
        }
    }

    /// <summary>
    /// The version a plugin file declares, or null when it cannot be read.
    ///
    /// <para>
    /// Null means "unknown", and an unknown version never justifies replacing a file: the
    /// comparison is what permits the overwrite, so failing to read either side leaves the
    /// folder alone.
    /// </para>
    /// </summary>
    private static string? ReadPluginVersion(string path, IAppLogger log)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex)
        {
            log.Warn("plugin.seed", $"插件版本读取失败：{Path.GetFileName(path)}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>Which bundled plugin files have been placed, so a deleted one is not resurrected.</summary>
    private const string SeededPluginsFileName = "seeded-plugins.json";

    /// <returns>The names already placed; null when the record could not be read.</returns>
    private static HashSet<string>? ReadSeededPlugins(string path, IAppLogger log)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return names is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(names.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            log.Warn("plugin.seed", $"已放置插件记录无法读取，本次不放置任何随包插件：{ex.Message}");
            return null;
        }
    }

    private static void WriteSeededPlugins(string path, HashSet<string> names, IAppLogger log)
    {
        try
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(names.OrderBy(name => name, StringComparer.Ordinal)));
        }
        catch (Exception ex)
        {
            // Not fatal: the plugins already placed stay placed, only the memory of it is lost.
            log.Warn("plugin.seed", $"已放置插件记录无法写入：{ex.Message}");
        }
    }

    /// <summary>
    /// Imports every plugin file the user has left switched on.
    ///
    /// <para>
    /// The switch lives in the config, while a plugin's resources only exist once its file has been
    /// imported — and importing used to happen only when the button on the plugins page was
    /// pressed. A plugin could therefore read as enabled for a whole session while contributing
    /// nothing, which made switching it off look like it did nothing at all.
    /// </para>
    /// </summary>
    private static void ImportEnabledPlugins(
        AppPaths paths,
        PluginImportService importer,
        PluginRegistry registry,
        IReadOnlyList<SoftwareDefinition> catalogSoftware,
        UserPreferences preferences,
        IAppLogger log)
    {
        var folder = Path.Combine(paths.UserDataDirectory, "plugins");
        if (!Directory.Exists(folder))
        {
            return;
        }

        // Existing ids come from the live catalog: a plugin may only add.
        var existing = catalogSoftware.Select(item => item.Id).ToList();

        foreach (var path in Directory.GetFiles(folder, "*" + ResourcePlugin.FileExtension))
        {
            try
            {
                var json = File.ReadAllText(path);
                var signaturePath = path + PluginSignature.FileExtension;
                var signature = File.Exists(signaturePath) ? File.ReadAllText(signaturePath) : null;

                var result = importer.Import(json, signature, existing);
                if (!result.Succeeded || result.PluginId is null)
                {
                    log.Warn("plugin.startup", $"{Path.GetFileName(path)} 未能导入：{result.Message}");
                    continue;
                }

                if (preferences.DisabledPlugins.Contains(result.PluginId, StringComparer.Ordinal))
                {
                    continue;
                }

                var added = registry.Add(result.Accepted);
                log.Info("plugin.startup", $"已导入启用的插件 {result.PluginId}：{added} 条资源。");
            }
            catch (Exception ex)
            {
                log.Warn("plugin.startup", $"{Path.GetFileName(path)} 读取失败：{ex.Message}");
            }
        }
    }

    /// <summary>Imports resource plugins. A plugin adds resources and nothing else.</summary>
    public PluginImportService Plugins { get; }

    /// <summary>Resources imported from plugins; the official catalog is never modified.</summary>
    public PluginRegistry PluginResources { get; }

    /// <summary>
    /// The official catalog plus resources imported from plugins.
    ///
    /// Pages read this rather than the catalog directly, so an imported plugin
    /// actually reaches the software engine instead of only appearing on the
    /// plugins page.
    /// </summary>
    public IReadOnlyList<SoftwareDefinition> AllSoftware()
    {
        var imported = PluginResources.Imported;

        // The official catalog stays on disk, but the list hides it by default so
        // the software page reflects what extensions/plugins actually contributed
        // (spec: the official list is meant to arrive via plugins/extensions, not bundled).
        //
        // The bundled official plugin is the official list's switch as well. While it is switched off
        // the official software is withheld, because the list otherwise stayed exactly as full as
        // before — the same software was still arriving from the bundled copy — and the switch read
        // as a no-op. Nothing is deleted: the catalog files and the plugin file both stay put.
        if (OfficialCatalogWithheld)
        {
            return imported;
        }

        // A bundled entry that a plugin file owns is left out: the plugin is where that software
        // comes from, so switching the plugin off has to take the software away rather than let the
        // bundled copy stand in for it under a different id.
        var owned = OwnedDirectories();
        var bundled = Catalog.Software
            .Where(item => !owned.Contains(item.DirectoryName))
            .ToList();

        if (imported.Count == 0)
        {
            return bundled;
        }

        var official = new HashSet<string>(bundled.Select(item => item.Id), StringComparer.Ordinal);
        return bundled
            .Concat(imported.Where(item => !official.Contains(item.Id)))
            .ToList();
    }

    /// <summary>
    /// The install slots owned by every plugin file in the folder, whether it is switched on or off.
    ///
    /// <para>
    /// The bundled official catalogue ships the same software the official plugin does, under plain
    /// ids. While both were listed, switching the plugin off removed its own resources and the
    /// bundled copy took their place in the same breath — so the software page looked untouched.
    /// Reading ownership from the files keeps the answer independent of which plugins happen to be
    /// switched on at this moment.
    /// </para>
    /// </summary>
    private IReadOnlySet<string> OwnedDirectories()
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);

        var folder = Path.Combine(Paths.UserDataDirectory, "plugins");
        if (!Directory.Exists(folder))
        {
            return owned;
        }

        foreach (var path in Directory.GetFiles(folder, "*" + ResourcePlugin.FileExtension))
        {
            try
            {
                var (plugin, _) = PluginReader.Parse(File.ReadAllText(path));
                if (plugin is null)
                {
                    continue;
                }

                foreach (var resource in plugin.Resources)
                {
                    if (!string.IsNullOrWhiteSpace(resource.DirectoryName))
                    {
                        owned.Add(resource.DirectoryName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("plugin.ownership", $"{Path.GetFileName(path)} 读不出目录名：{ex.Message}");
            }
        }

        return owned;
    }

    /// <summary>The id of the plugin that ships with the app; its switch is the official list's switch.</summary>
    public const string BundledOfficialPluginId = "erireborn_official";

    /// <summary>
    /// True while the bundled official catalogue is being withheld from the software list: either
    /// the user hid it outright, or the plugin that ships with the app — which is the official
    /// list's switch — is switched off.
    ///
    /// <para>
    /// Public because the software page explains an empty list: a page that shows nothing has to
    /// say which of the two reasons applies, or it reads as a bug.
    /// </para>
    /// </summary>
    public bool OfficialCatalogWithheld
        => UserConfig.Current.HideBuiltInCatalog || OfficialListSwitchedOff();

    /// <summary>True while the plugin that ships with the app is switched off.</summary>
    private bool OfficialListSwitchedOff()
        => UserConfig.Current.DisabledPlugins.Contains(BundledOfficialPluginId, StringComparer.Ordinal);

    public MarketplaceClient Marketplace { get; }

    /// <summary>Reads the resource-plugin marketplace index.</summary>
    public PluginMarketplaceClient PluginMarketplace { get; }

    /// <summary>Downloads a plugin from the marketplace and registers its resources.</summary>
    public PluginMarketplaceInstaller PluginMarketplaceInstaller { get; }

    /// <summary>Which plugins are installed, and at which version.</summary>
    public PluginInstallationStore PluginInstallations { get; }

    /// <summary>The bundled tutorial: available with or without a network.</summary>
    public TutorialContent.TutorialCatalog Tutorial { get; }

    /// <summary>What the user has already seen, so a restart does not nag.</summary>
    public TutorialContent.TutorialProgressStore TutorialProgress { get; }

    /// <summary>Long work, as observable and cancellable jobs (spec 10).</summary>
    public EriReborn.Core.Jobs.JobManager Jobs { get; }

    /// <summary>
    /// Where the plugin marketplace index lives. Separate from the extension
    /// marketplace's, because the two ecosystems are not the same thing
    /// (spec 49/176).
    /// </summary>
    public string? PluginMarketplaceIndexUrl =>
        UserConfig.Current.PluginMarketplaceIndexUrl
        ?? System.Environment.GetEnvironmentVariable("ERIREBORN_PLUGIN_INDEX");

    public ExtensionInstaller ExtensionInstaller { get; }

    public string? MarketplaceIndexUrl =>
        _marketplaceOverride
        ?? UserConfig.Current.MarketplaceIndexUrl
        ?? System.Environment.GetEnvironmentVariable("ERIREBORN_MARKETPLACE_INDEX");

    public UserConfigService UserConfig { get; }

    public PathResolver PathResolver { get; }

    public EnvironmentContext Environment { get; set; }

    private string? _marketplaceOverride;

    /// <summary>Lets the settings page point the client at a different index at runtime.</summary>
    public void SetMarketplaceIndexUrl(string? url) => _marketplaceOverride = url;

    public string PlatformId => Platform.PlatformId;

    /// <summary>
    /// Names the real secure-storage mechanism for the running platform. Saying
    /// "DPAPI" on Android would be a false claim about how secrets are kept.
    /// </summary>
    public string CredentialStoreDescription => !Platform.Credentials.IsAvailable
        ? "不可用"
        : PlatformId switch
        {
            "windows" => "Windows DPAPI（当前用户）",
            "android" => "Android Keystore（AES-GCM）",
            _ => "平台安全凭据存储",
        };

    /// <summary>
    /// Builds the object graph. The cloud registry is supplied by the host so
    /// this assembly never references a platform-specific project.
    /// </summary>
    public static async Task<AppHost> CreateAsync(
        AppPaths paths,
        IPlatformService platform,
        CloudProviderRegistry cloudRegistry,
        IAppLogger log,
        CancellationToken cancellationToken = default,
        EriReborn.Engine.Download.DownloadEngineSelector? downloadEngines = null)
    {
        paths.EnsureDirectories();

        // --- User preferences (needed by extensions and the install root) ----
        var userConfig = new UserConfigService(paths, log.For("Config"));
        var preferences = userConfig.Load();

        // --- Catalog (real data, validated on load) -------------------------
        var catalogReader = new CatalogReader(log.For("Catalog"));
        var catalog = await catalogReader.LoadDirectoryAsync(paths.CatalogDirectory, cancellationToken).ConfigureAwait(false);

        // --- Bundled plugins -------------------------------------------------
        // The official plugin is an ordinary plugin file: it is copied into the folder the plugin
        // page reads, once, so it is imported exactly the way a hand-made plugin is. It is not a
        // special kind of plugin, and it has no special button.
        SeedBundledPlugins(paths, log);

        // --- Skins ----------------------------------------------------------
        // The user's own skins are scanned too, and win on an id clash: a skin the user made is
        // the one they can edit, so it is the one that should be in force.
        var userSkins = new UserSkinStore(paths, log.For("Skin"));
        var skinEngine = new SkinEngine(log.For("Skin"));

        await skinEngine.DiscoverAsync(paths.SkinsDirectory, cancellationToken).ConfigureAwait(false);

        // A user skin is a thin file naming a base skin plus what it changes, so it is resolved
        // over its base here rather than discovered as a skin in its own right. Registering it
        // makes it eligible for the default-skin choice below and for the picker, like any pack.
        foreach (var manifest in userSkins.Apply(skinEngine.Discovered))
        {
            skinEngine.Register(manifest);
        }

        // Skin text the workshop changed is layered over both the packs and the user's own skins, so
        // an edit survives a restart. Nothing has been overridden yet here, so these are registered
        // over what is already in the list rather than resetting it.
        var skinOverrides = new SkinOverrideStore(paths, log.For("Skin"));
        foreach (var manifest in skinOverrides.Apply(skinEngine.Available))
        {
            skinEngine.Register(manifest);
        }

        // The default skin is applied during startup so the first frame is
        // already themed (spec 41). Each platform prefers its own Eri skin,
        // which is why Windows and Android do not share one default.
        // An explicit request (developer or smoke run) wins over the platform default,
        // and the skin the user last switched on wins over the platform default too:
        // otherwise a user skin would be forgotten on every restart.
        var requestedSkin = System.Environment.GetEnvironmentVariable("ERIREBORN_SKIN");
        var platformSkin = $"eri_{platform.PlatformId}";
        var savedSkin = string.IsNullOrWhiteSpace(preferences.SkinId) ? null : preferences.SkinId;
        var defaultSkin = (!string.IsNullOrWhiteSpace(requestedSkin)
                ? skinEngine.Available.FirstOrDefault(s => string.Equals(s.Id, requestedSkin, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? (savedSkin is not null
                ? skinEngine.Available.FirstOrDefault(s => string.Equals(s.Id, savedSkin, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? skinEngine.Available.FirstOrDefault(s => string.Equals(s.Id, platformSkin, StringComparison.Ordinal))
            ?? skinEngine.Available.FirstOrDefault(s => s.IsMobile == platform.PlatformId.Equals("android", StringComparison.OrdinalIgnoreCase))
            ?? skinEngine.Available.FirstOrDefault();
        if (defaultSkin is not null)
        {
            await skinEngine.ApplyAsync(defaultSkin, cancellationToken).ConfigureAwait(false);
        }

        // --- Assets ---------------------------------------------------------
        var assetManager = new AssetManager(log.For("Asset"));
        if (File.Exists(paths.AssetManifestFile))
        {
            try
            {
                var manifestJson = await File.ReadAllTextAsync(paths.AssetManifestFile, cancellationToken).ConfigureAwait(false);
                assetManager.LoadManifest(manifestJson, paths.AssetsRoot);
            }
            catch (Exception ex)
            {
                log.Error("startup.assets", "Asset manifest could not be loaded.", ex);
            }
        }
        else
        {
            log.Warn("startup.assets", $"Asset manifest not found: {paths.AssetManifestFile}");
        }

        // --- Trust anchors (spec 35) ----------------------------------------
        // Trust is decided by these keys, never by a package's own manifest.
        var trustedKeys = TrustedKeyStore.FromFile(
            Path.Combine(paths.AssetsRoot, "trust", "keys.json"),
            log.For("Trust"));

        // --- Persona (separate from Skin, spec 57) --------------------------
        var personas = new PersonaCatalog(log.For("Persona"));
        var personaDirectory = Path.Combine(paths.AssetsRoot, "personas");
        personas.Load(PersonaCatalog.Discover(personaDirectory));
        if (personas.Count == 0)
        {
            log.Warn("startup.personas", $"No persona packs were found in '{personaDirectory}'.");
        }
        if (skinEngine.Active is { } activeSkin)
        {
            personas.Activate(activeSkin.Persona);
        }

        // --- Extensions (registry only; loading happens once the registries exist) ---
        var extensionLoader = new IsolatedExtensionLoader(log.For("Extension"));
        var extensionRegistry = new ExtensionRegistry(
            new ExtensionValidator(),
            extensionLoader,
            log.For("Extension"));

        // --- Cloud + engine -------------------------------------------------
        var pathResolver = new PathResolver();

        // First-hand evidence about what EriReborn placed on this machine, plus
        // user-supplied detection paths for entries whose manifest declares none.
        var installations = new InstallationRegistry(
            Path.Combine(paths.UserDataDirectory, "installations.json"),
            log.For("Installations"));
        installations.Load();

        var detectionHints = new DetectionHintStore(
            Path.Combine(paths.UserDataDirectory, "detection-hints.json"),
            log.For("Hints"));
        detectionHints.Load();

        var softwareEngine = new SoftwareEngine(platform, pathResolver, installations, detectionHints, log.For("Software"));
        // The engine hands this to every install request so install packages land in the same
        // directory cloud downloads do — the one the user can redirect off the system drive.
        softwareEngine.DownloadDirectory = preferences.DownloadDirectory;
        var scanService = new SystemScanService(softwareEngine, log.For("Scan"));

        // What each source can do right now, and which one to use. Built from the cloud
        // registry the host supplies, so this assembly still names no platform itself.
        var sourceAvailability = new SourceAvailabilityService(cloudRegistry, log.For("Source"));

        // Built from platform abstractions only, so Windows and Android share it.
        var downloader = new HttpDownloader(
            platform.Network.Client,
            platform.Files,
            new DownloadCache(Path.Combine(paths.UserDataDirectory, "downloads")),
            log.For("Download"),
            segments: SegmentPolicy.ForPlatform(platform.PlatformId));

        // Source kind decides which resolver runs; no provider is named here. The drive resolver is
        // asked first because a file in the user's own drive has no share link: it is a cloud source
        // the share resolver would otherwise claim and then fail on, for want of a link it never had.
        var resolvers = new DownloadResolverRegistry(
            new IDownloadResolver[]
            {
                new CloudDriveResolver(cloudRegistry, log.For("Resolve")),
                new CloudShareResolver(cloudRegistry, log.For("Resolve")),
                new DirectUrlResolver(),
                new NonDownloadableResolver(),
            },
            log.For("Resolve"));

        // The engines a download may run on. A host that supplies none still gets the built-in HTTP
        // engine, because it needs nothing but the downloader this method already builds — so a
        // download never claims "no engine" while a perfectly good one is sitting right here. The
        // desktop supplies its own selector, which also carries the built-in engine plus whatever
        // extensions added.
        var engines = downloadEngines
            ?? new DownloadEngineSelector(
                new IDownloadEngine[] { new NativeHttpDownloadEngine(downloader, log.For("Download")) },
                log.For("Download"));

        var plugins = new PluginImportService(CloudProviderIds.All, log.For("Plugins"));
        var pluginResources = new PluginRegistry();

        // A plugin the user left switched on is imported here, not only when the button on the plugins
        // page is pressed. The switch lives in the config while the resources only exist once the file
        // has been imported, so without this pass an enabled plugin would contribute nothing and
        // switching it off would change nothing either — the software page would never move.
        ImportEnabledPlugins(paths, plugins, pluginResources, catalog.Software, preferences, log);

        var marketplace = new MarketplaceClient(platform.Network, log.For("Marketplace"));
        var tutorial = TutorialContent.TutorialCatalogReader.Load(paths.AssetsRoot, log.For("Tutorial"));
        var tutorialProgress = new TutorialContent.TutorialProgressStore(
            Path.Combine(paths.UserDataDirectory, "tutorial-progress.json"),
            log.For("Tutorial"));
        tutorialProgress.Load();

        var jobs = new EriReborn.Core.Jobs.JobManager(maxConcurrent: 2, log.For("Jobs"));

        // One service through which every interface-started download becomes a job. The page never
        // touches an HTTP client: it asks this for a job and watches the task list.
        var downloadJobs = new DownloadJobService(resolvers, engines, jobs, log.For("Download"));

        var pluginMarketplace = new PluginMarketplaceClient(platform.Network, log.For("PluginMarketplace"));
        var pluginInstallations = new PluginInstallationStore(
            Path.Combine(paths.UserDataDirectory, "plugins", "installed.json"),
            log.For("Plugins"));
        pluginInstallations.Load();

        var pluginMarketplaceInstaller = new PluginMarketplaceInstaller(
            downloader,
            plugins,
            pluginResources,
            pluginInstallations,
            log.For("PluginMarketplace"));
        var extensionInstaller = new ExtensionInstaller(
            downloader,
            platform.Files,
            extensionRegistry,
            log.For("Extension"),
            trustedKeys);

        // Loading at startup is what makes "cleaned up on next start" true, and it means
        // installed extensions are live without visiting a page. It runs here — after the
        // objects an extension contributes to exist — because the host hands those very
        // objects over: the cloud registry, the download-engine selector and the imported
        // resource registry, plus the platform's network/credentials/process services.
        // One host object, reused by every later (re)load from a page, so an extension
        // always gets the same capabilities the startup load gave it.
        //
        // The pick-file callback forwards to the AppHost instance, which the UI
        // layer fills in once a window exists. A local holds the reference because
        // the host is not constructed until below.
        AppHost? hostRef = null;
        Func<string, string, Task<string?>> pickFile = (title, filter) =>
            hostRef?.PickFile is { } handler ? handler(title, filter) : Task.FromResult<string?>(null);

        // Extensions get a per-extension sandbox under UserData/extensions-data; the
        // directory is created on first ask, and its contents outlive the process.
        string? DataDirectoryFor(string extensionId)
            => Path.Combine(paths.UserDataDirectory, "extensions-data", extensionId);

        // Same forward-and-fill pattern as the pick-file callback: the UI layer sets
        // Host.OpenReaderWindow once a window exists, and the host created below is
        // reachable through the local.
        bool OpenReaderWindowFor(EriReborn.Extension.Reader.IReaderSession session)
            => hostRef?.OpenReaderWindow is { } handler && handler(session);

        var extensionHost = new AppExtensionHost(
            log.For("Extension"),
            cloudRegistry,
            engines,
            pluginResources,
            platform.Network,
            platform.Credentials,
            platform.Processes,
            pickFile,
            DataDirectoryFor,
            OpenReaderWindowFor);

        await extensionRegistry
            .RefreshAsync(
                paths.ExtensionsDirectory,
                extensionHost,
                preferences.DisabledExtensions,
                cancellationToken)
            .ConfigureAwait(false);

        // The install root comes from user preferences when set, otherwise the
        // per-user playground directory (spec 17/18).
        var environment = ResolveEnvironment(paths, preferences);

        log.Info(
            "startup.ready",
            $"Platform={platform.PlatformId}, catalog={catalog.Software.Count} item(s), skins={skinEngine.Available.Count}, assets={assetManager.Sheets.Count}, cloud={cloudRegistry.Providers.Count}.");

        var host = new AppHost(
            paths,
            log,
            platform,
            catalog,
            skinEngine,
            skinOverrides,
            userSkins,
            assetManager,
            extensionRegistry,
            cloudRegistry,
            softwareEngine,
            sourceAvailability,
            scanService,
            installations,
            detectionHints,
            personas,
            trustedKeys,
            downloader,
            resolvers,
            plugins,
            pluginResources,
            marketplace,
            pluginMarketplace,
            pluginMarketplaceInstaller,
            pluginInstallations,
            tutorial,
            tutorialProgress,
            jobs,
            extensionInstaller,
            userConfig,
            pathResolver,
            environment,
            extensionHost,
            engines,
            downloadJobs);

        // Page registrations before this point happened while nobody could listen yet;
        // from here on every change reaches the shell through the host's own event.
        extensionHost.PagesChanged += (_, _) => host.NotifyExtensionPagesChanged();

        hostRef = host;
        return host;
    }

    private static EnvironmentContext ResolveEnvironment(AppPaths paths, UserPreferences preferences)
    {
        var root = string.IsNullOrWhiteSpace(preferences.InstallRoot)
            ? Path.Combine(paths.PlaygroundDirectory, "Software")
            : preferences.InstallRoot!;

        return new EnvironmentContext
        {
            RootPath = root,
            IncludeSubcategoryFolder = preferences.IncludeSubcategoryFolder,

            // Per-entry choices travel with the rest of the preferences, so a directory the user
            // picked for one software is still in force after a restart (spec 25).
            SoftwarePathOverrides = preferences.SoftwarePathOverrides,
        };
    }
}
