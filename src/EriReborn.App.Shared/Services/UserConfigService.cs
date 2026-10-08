using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.App.Shared.Services;

/// <summary>
/// Non-sensitive user preferences (spec 61). Anything secret lives in the
/// platform credential store instead; this file is plain JSON on purpose.
/// </summary>
public sealed record UserPreferences
{
    /// <summary>Root directory the user chose for installations (spec 17).</summary>
    public string? InstallRoot { get; init; }

    /// <summary>
    /// Where downloaded files land before they are installed or kept. Defaults to
    /// <c>UserData/downloads</c> when the user has not picked one; the setting exists
    /// because a small system drive should not have to host multi-gigabyte game
    /// archives just because that is where the app data lives.
    /// </summary>
    public string? DownloadDirectory { get; init; }

    /// <summary>
    /// Install directories chosen for individual entries (spec 25).
    ///
    /// <para>
    /// The official directory name is EriReborn's own bookkeeping — it says nothing about where a
    /// person wants their software — so the two are kept apart: an entry here replaces the whole
    /// resolved directory for that one software id, and everything else still follows the root.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> SoftwarePathOverrides { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? SkinId { get; init; }

    public bool IncludeSubcategoryFolder { get; init; } = true;

    public string? AiBaseUrl { get; init; }

    public string? AiModel { get; init; }

    /// <summary>
    /// Which service the endpoint belongs to. Not derivable from the address in general — a gateway or
    /// a self-hosted endpoint shares no address with any known service — and a capability that has to
    /// guess the protocol would guess wrong exactly when it matters (spec 58/71).
    /// </summary>
    public string? AiProviderId { get; init; }

    /// <summary>Marketplace index URL; configuration rather than a hard-coded endpoint.</summary>
    public string? MarketplaceIndexUrl { get; init; }

    /// <summary>Where the resource-plugin marketplace index lives.</summary>
    public string? PluginMarketplaceIndexUrl { get; init; }

    /// <summary>Extensions the user switched off; they stay installed but are not loaded.</summary>
    public IReadOnlyList<string> DisabledExtensions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Plugins the user switched off. The file stays in the plugin folder — nothing is deleted —
    /// but the resources it contributed are taken back out of the software list.
    /// </summary>
    public IReadOnlyList<string> DisabledPlugins { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, the bundled official catalog is not shown in the software list;
    /// only software contributed by extensions/plugins appears. The catalog files
    /// remain on disk, so this is a display choice, not a deletion (spec).
    ///
    /// <para>
    /// Defaults to false. The shipped catalog is what makes a first run useful, and
    /// hiding it by default left the software page empty on a fresh install — the 263
    /// bundled entries had no display path at all. It stays available as a switch for
    /// anyone who wants a plugin-only list.
    /// </para>
    /// </summary>
    public bool HideBuiltInCatalog { get; init; }
}

public sealed class UserConfigService(AppPaths paths, IAppLogger log)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // config.json is user-editable, and JSON is conventionally camelCase.
        // Without this, a hand-edited file silently loses every setting that
        // differs from the property casing.
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths = paths;
    private readonly IAppLogger _log = log;

    public UserPreferences Current { get; private set; } = new();

    public string ConfigFile => _paths.UserConfigFile;

    /// <summary>
    /// Extensions that ship disabled by default. The user can turn them on from
    /// the Extensions page; this only decides the first-run state. The reader is
    /// off out of the box because not everyone wants a second e-reader.
    /// </summary>
    private static readonly string[] DefaultDisabledExtensions = { "erireborn_reader" };

    public UserPreferences Load()
    {
        try
        {
            if (File.Exists(_paths.UserConfigFile))
            {
                var json = File.ReadAllText(_paths.UserConfigFile);
                Current = JsonSerializer.Deserialize<UserPreferences>(json, Options) ?? new UserPreferences();
                _log.Info("config.load", $"Loaded user preferences from '{_paths.UserConfigFile}'.");
            }
            else
            {
                // First run: apply the shipped defaults. The reader extension is
                // off by default; everything else is on.
                Current = new UserPreferences
                {
                    DisabledExtensions = DefaultDisabledExtensions,
                };
                _log.Info("config.load", "No user preferences yet; using defaults (reader off).");
            }
        }
        catch (Exception ex)
        {
            _log.Error("config.load", "User preferences could not be read; using defaults.", ex);
            Current = new UserPreferences
            {
                DisabledExtensions = DefaultDisabledExtensions,
            };
        }

        return Current;
    }

    /// <summary>
    /// Records an extension as switched off (or on) and persists it, so the
    /// choice survives a restart (spec 34).
    /// </summary>
    public UserPreferences SetExtensionEnabled(string extensionId, bool enabled)
    {
        var disabled = Current.DisabledExtensions.ToList();
        if (enabled)
        {
            disabled.RemoveAll(id => string.Equals(id, extensionId, StringComparison.Ordinal));
        }
        else if (!disabled.Contains(extensionId, StringComparer.Ordinal))
        {
            disabled.Add(extensionId);
        }

        var updated = Current with { DisabledExtensions = disabled };
        Save(updated);
        return updated;
    }

    /// <summary>
    /// Remembers the AI service, endpoint and model. These were declared in the config
    /// model but never written, so the AI page lost them on every restart.
    /// </summary>
    public UserPreferences SetAiSettings(string? providerId, string? baseUrl, string? model)
    {
        var updated = Current with { AiProviderId = providerId, AiBaseUrl = baseUrl, AiModel = model };
        Save(updated);
        return updated;
    }

    /// <summary>
    /// Remembers — or clears — the directory one entry installs into (spec 25). A blank path removes
    /// the override, which is how "恢复默认" is expressed; nothing else has to know the difference
    /// between "never set" and "set back to the default".
    /// </summary>
    public UserPreferences SetSoftwarePathOverride(string softwareId, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareId);

        var overrides = new Dictionary<string, string>(Current.SoftwarePathOverrides, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(path))
        {
            overrides.Remove(softwareId);
        }
        else
        {
            overrides[softwareId] = path.Trim();
        }

        var updated = Current with { SoftwarePathOverrides = overrides };
        Save(updated);
        return updated;
    }

    /// <summary>True when the user has switched this extension off.</summary>
    public bool IsExtensionDisabled(string extensionId)
        => Current.DisabledExtensions.Contains(extensionId, StringComparer.Ordinal);

    /// <summary>
    /// Remembers the skin in force, so the next start opens with the same one (spec 12).
    ///
    /// <para>
    /// The field existed in the preferences model but nothing ever wrote it, so a skin the user
    /// switched on — including one they made themselves — was forgotten on every restart and the
    /// platform default came back. Persisting it is what makes "启用" mean anything.
    /// </para>
    /// </summary>
    public UserPreferences SetActiveSkin(string? skinId)
    {
        var updated = Current with { SkinId = skinId };
        Save(updated);
        return updated;
    }

    /// <summary>
    /// Records a plugin as switched off (or on) and persists it, so the choice survives a
    /// restart. The same idea as SetExtensionEnabled, for the other kind of package.
    /// </summary>
    public UserPreferences SetPluginEnabled(string pluginId, bool enabled)
    {
        var disabled = Current.DisabledPlugins.ToList();
        if (enabled)
        {
            disabled.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
        }
        else if (!disabled.Contains(pluginId, StringComparer.Ordinal))
        {
            disabled.Add(pluginId);
        }

        var updated = Current with { DisabledPlugins = disabled };
        Save(updated);
        return updated;
    }

    /// <summary>True when the user has switched this plugin off.</summary>
    public bool IsPluginDisabled(string pluginId)
        => Current.DisabledPlugins.Contains(pluginId, StringComparer.Ordinal);

    public void Save(UserPreferences preferences)
    {
        Current = preferences;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.UserConfigFile)!);
            File.WriteAllText(_paths.UserConfigFile, JsonSerializer.Serialize(preferences, Options));
            _log.Info("config.save", $"Saved user preferences to '{_paths.UserConfigFile}'.");
        }
        catch (Exception ex)
        {
            _log.Error("config.save", "User preferences could not be written.", ex);
        }
    }
}
