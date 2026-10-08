using System.Text.Json;
using System.Threading.Tasks;
using EriReborn.Core.Domain;
using EriReborn.Core.Validation;

namespace EriReborn.Extension;

/// <summary>The contract every extension assembly implements.</summary>
public interface IExtension
{
    string Id { get; }

    string DisplayName { get; }

    string Version { get; }

    void Initialize(IExtensionHost host);
}

/// <summary>Services an extension is allowed to reach.</summary>
public interface IExtensionHost
{
    void Log(string level, string message);

    string GetSetting(string key, string? fallback = null);

    void SetSetting(string key, string value);

    // Capability registration bridge. Second-edition left this seam open: an
    // extension may contribute a cloud provider, a download engine, or catalog
    // entries, and the host wires them into the running registries. The defaults
    // are no-ops so every existing host keeps compiling unchanged.
    void RegisterCloudProvider(EriReborn.Cloud.ICloudProvider provider) { }

    void RegisterDownloadEngine(EriReborn.Engine.Download.IDownloadEngine engine) { }

    void ContributeSoftware(IEnumerable<EriReborn.Core.Domain.SoftwareDefinition> items) { }

    /// <summary>
    /// Registers a page contributed by this extension. The host shows it in the
    /// navigation (only while the extension is enabled) and renders it with a
    /// generic view, so an extension never has to reference the UI toolkit.
    /// </summary>
    void RegisterPage(IExtensionPage page) { }

    /// <summary>
    /// Same as <see cref="RegisterPage(IExtensionPage)"/> but tags the page with the
    /// extension that contributed it, so the host can take the page back when that
    /// extension is disabled or unloaded. The permission-checked wrapper supplies the
    /// id automatically; extensions themselves use the one-argument form.
    /// </summary>
    void RegisterPage(IExtensionPage page, string? ownerExtensionId) => RegisterPage(page);

    /// <summary>
    /// Takes back the pages a disabled or unloaded extension contributed. Default is
    /// no-op so hosts that never accepted pages stay unaffected.
    /// </summary>
    void RemovePages(string ownerExtensionId) { }

    /// <summary>
    /// Asks the shell for a file path. Returns a task that completes with the chosen
    /// path, or null when the user cancels. Asynchronous by design: the file dialog is
    /// shown and awaited on the UI thread, and blocking on it from a synchronous
    /// contract deadlocked the whole interface.
    /// </summary>
    Task<string?> PickFileAsync(string title, string filter) => Task.FromResult<string?>(null);

    /// <summary>
    /// The extension's own persistent directory — a path under the host's user data
    /// where the extension may write whatever state it needs to survive a restart.
    /// The host owns the root; the extension owns the file names. Null when the host
    /// offers no storage (headless hosts), in which case the extension degrades to
    /// in-memory state.
    /// </summary>
    string? GetDataDirectory(string extensionId) => null;

    /// <summary>
    /// Asks the host to show a reader session in a real window. Returns false when
    /// the host has no window system (tests, single-view hosts) — the caller then
    /// falls back to its inline page. Rich interaction stays UI-agnostic the same
    /// way pages do: the extension implements <see cref="Reader.IReaderSession"/>,
    /// the host renders it.
    /// </summary>
    bool OpenReaderWindow(Reader.IReaderSession session) => false;

    // Platform dependency bridge. An extension that only contributes a provider or
    // an engine still has to construct it from the platform's own parts — the HTTP
    // client, the credential store, the process runner. These are handed out only
    // when the matching permission was declared (the gate lives in
    // PermissionCheckedExtensionHost). Defaults are null so every existing host
    // keeps compiling unchanged.
    EriReborn.Platform.Abstractions.INetworkService? Network => null;

    EriReborn.Platform.Abstractions.ICredentialStore? Credentials => null;

    EriReborn.Platform.Abstractions.IProcessService? Processes => null;

    // Logging is never gated (hiding an extension's own errors would make failures
    // invisible), so the host's logger is handed over unconditionally. A contributed
    // download engine takes an IAppLogger, and this is where it gets one.
    EriReborn.Core.Logging.IAppLogger? Logger => null;
}

/// <summary>An action button shown at the bottom of an extension page.</summary>
public sealed record ExtensionPageAction(string Label, Action OnClick, bool IsPrimary = false);

/// <summary>
/// A page contributed by an extension. The host renders it generically:
/// a title, a scrollable body of text, and a row of action buttons. This keeps
/// extensions free of any UI dependency while still letting them offer a screen.
/// </summary>
public interface IExtensionPage
{
    /// <summary>Stable navigation key, e.g. "reader".</summary>
    string Key { get; }

    /// <summary>Shown in the navigation rail and the page title bar.</summary>
    string Title { get; }

    /// <summary>The text body the page shows right now.</summary>
    string Body { get; }

    /// <summary>Raised when <see cref="Body"/> changes so the view refreshes.</summary>
    event EventHandler? BodyChanged;

    /// <summary>Buttons shown under the body.</summary>
    IReadOnlyList<ExtensionPageAction> Actions { get; }
}

/// <summary>
/// Lets a component release an extension's load context before its files are
/// removed. Without this, deleting a loaded assembly fails because the runtime
/// keeps the file mapped.
/// </summary>
public interface IExtensionLifecycle
{
    /// <summary>Requests unloading of the extension's load context.</summary>
    bool Unload(string extensionId);
}

public enum ExtensionLoadState
{
    Discovered,
    Validated,
    Rejected,
    AssemblyMissing,
    NoImplementation,
    Loaded,
    LoadFailed,

    /// <summary>Installed but switched off by the user (spec 34).</summary>
    Disabled,

    /// <summary>A declared dependency is missing, disabled or itself unloadable.</summary>
    DependencyMissing,
}

/// <summary>
/// Where an extension runs.
///
/// <para>
/// In-process is the default and stays the cheapest: no handshake, no protocol,
/// and existing extensions keep working untouched. An extension that opts into
/// <see cref="Process"/> pays for a child process and gets containment in return —
/// its memory and its crashes are its own.
/// </para>
/// </summary>
public enum ExtensionIsolation
{
    InProcess,
    Process,
}

/// <summary>Manifest of an installed extension (spec 34/35).</summary>
public sealed record ExtensionManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    public string? Assembly { get; init; }

    public SoftwareTrust Trust { get; init; } = SoftwareTrust.Unknown;

    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>Categories this extension contributes, and whether they are official.</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public string? Homepage { get; init; }

    public string Directory { get; init; } = string.Empty;

    public string? ManifestPath { get; init; }

    /// <summary>Where this extension runs. Opt-in, because isolation is not free.</summary>
    public ExtensionIsolation Isolation { get; init; } = ExtensionIsolation.InProcess;

    /// <summary>
    /// What the manifest actually said, kept so the validator can reject a
    /// misspelling instead of silently reading it as "in process".
    /// </summary>
    public string? IsolationText { get; init; }

    public bool RunsInOwnProcess => Isolation == ExtensionIsolation.Process;
}

public sealed record ExtensionStatus(
    ExtensionManifest Manifest,
    ExtensionLoadState State,
    string Message,
    IExtension? Instance = null,
    OutOfProcessExtensionRunner? Process = null)
{
    public bool IsUsable => State == ExtensionLoadState.Loaded;

    /// <summary>True when this extension is answering from another process.</summary>
    public bool IsIsolated => Process is not null;
}

public static class ExtensionManifestParser
{
    public static ExtensionManifest? Parse(string json, string directory, string manifestPath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var id = Str(root, "id");
        var name = Str(root, "name");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ExtensionManifest
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Version = Str(root, "version"),
            Author = Str(root, "author"),
            Description = Str(root, "description"),
            Assembly = Str(root, "assembly") ?? Str(root, "main"),
            Trust = ParseTrust(Str(root, "trust")),
            Permissions = ReadArray(root, "permissions"),
            Dependencies = ReadArray(root, "dependencies"),
            Categories = ReadArray(root, "categories"),
            Homepage = Str(root, "homepage"),
            Directory = directory,
            ManifestPath = manifestPath,
            Isolation = ParseIsolation(Str(root, "isolation")),
            IsolationText = Str(root, "isolation"),
        };
    }

    /// <summary>
    /// Only the two known words count. A misspelled "proces" must not silently
    /// become in-process, because that is the difference between containment and
    /// none — so it is reported by the validator instead (see below).
    /// </summary>
    public static ExtensionIsolation ParseIsolation(string? value)
        => string.Equals(value, "process", StringComparison.OrdinalIgnoreCase)
            ? ExtensionIsolation.Process
            : ExtensionIsolation.InProcess;

    private static SoftwareTrust ParseTrust(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "official" => SoftwareTrust.Official,
        "verified" => SoftwareTrust.Verified,
        "community" => SoftwareTrust.Community,
        "invalid" => SoftwareTrust.Invalid,
        _ => SoftwareTrust.Unknown,
    };

    private static IReadOnlyList<string> ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Rejects malformed or unsafe manifests before anything is loaded.</summary>
public sealed class ExtensionValidator
{
    private static readonly HashSet<string> KnownPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "filesystem.read",
        "filesystem.write",
        "process.run",
        "registry.read",
        "network.http",
        "catalog.contribute",
        "skin.contribute",
        "ui.contribute",
        ExtensionPermissions.CloudContribute,
        ExtensionPermissions.DownloaderContribute,
        // Settings are a real capability the host controls, so they need a name
        // that can be declared and then actually checked.
        ExtensionPermissions.SettingsRead,
        ExtensionPermissions.SettingsWrite,
        // Platform dependencies an extension must declare before the host hands
        // them over (see IExtensionHost.Network / Credentials / Processes).
        ExtensionPermissions.NetworkAccess,
        ExtensionPermissions.CredentialsAccess,
        ExtensionPermissions.ProcessAccess,
    };

    public ValidationResult Validate(ExtensionManifest manifest)
    {
        var issues = new List<ValidationIssue>();

        if (!DirectoryNameValidator.ValidateName(manifest.Id).IsValid)
        {
            issues.AddRange(DirectoryNameValidator.ValidateName(manifest.Id).Issues.Select(i => i with { Subject = "extension.id" }));
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            issues.Add(new ValidationIssue("extension.no_assembly", "Manifest does not name an assembly.", manifest.Id));
        }
        else if (!string.Equals(Path.GetExtension(manifest.Assembly), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue("extension.assembly_type", "Assembly must be a .dll.", manifest.Id));
        }

        // A misspelled isolation value silently means "in process", which is the
        // opposite of what the author asked for.
        if (manifest.IsolationText is { Length: > 0 } declared
            && !declared.Equals("process", StringComparison.OrdinalIgnoreCase)
            && !declared.Equals("inprocess", StringComparison.OrdinalIgnoreCase)
            && !declared.Equals("in-process", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue("extension.unknown_isolation", $"Unknown isolation '{declared}'.", manifest.Id));
        }

        if (manifest.Trust == SoftwareTrust.Invalid)
        {
            issues.Add(new ValidationIssue("extension.invalid_trust", "Manifest declares the extension as invalid.", manifest.Id));
        }

        foreach (var permission in manifest.Permissions)
        {
            if (!KnownPermissions.Contains(permission))
            {
                issues.Add(new ValidationIssue("extension.unknown_permission", $"Unknown permission '{permission}'.", manifest.Id));
            }
        }

        foreach (var category in manifest.Categories)
        {
            if (!DirectoryNameValidator.ValidateName(category).IsValid)
            {
                issues.Add(new ValidationIssue("extension.bad_category", $"Category '{category}' violates the official naming rule.", manifest.Id));
            }
        }

        return ValidationResult.From(issues);
    }
}
