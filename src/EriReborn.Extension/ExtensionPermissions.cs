namespace EriReborn.Extension;

/// <summary>
/// What an extension declared it needs.
///
/// <para>
/// This is a <b>declaration, not a sandbox</b>. Extensions load as ordinary
/// in-process assemblies, so code that ignores the host can call the BCL directly
/// and no check here can stop it. Claiming otherwise would be security theatre.
/// </para>
///
/// <para>
/// What this does do is refuse to hand over the capabilities the application
/// actually controls — settings today, catalog and skin contributions next — and
/// let the user see what an extension asked for before enabling it. An extension
/// that declares nothing gets nothing from the host.
/// </para>
/// </summary>
public sealed class ExtensionPermissions
{
    /// <summary>Read the settings the host keeps for this extension.</summary>
    public const string SettingsRead = "settings.read";

    /// <summary>Write the settings the host keeps for this extension.</summary>
    public const string SettingsWrite = "settings.write";

    /// <summary>Logging is never gated: hiding an extension's own errors would make failures invisible.</summary>
    public const string LogAlways = "log";

    /// <summary>Contribute software entries to the catalog (additions only; the official list is immutable).</summary>
    public const string CatalogContribute = "catalog.contribute";

    /// <summary>Register a cloud-drive provider the resolver can route share links to.</summary>
    public const string CloudContribute = "cloud.contribute";

    /// <summary>Register a download engine (e.g. an aria2-backed accelerator).</summary>
    public const string DownloaderContribute = "downloader.contribute";

    /// <summary>Contribute a UI skin.</summary>
    public const string SkinContribute = "skin.contribute";

    /// <summary>Contribute UI affordances (pages, actions).</summary>
    public const string UiContribute = "ui.contribute";

    /// <summary>Reach the platform's HTTP client (a contributed provider needs it).</summary>
    public const string NetworkAccess = "network.http";

    /// <summary>Reach the platform's credential store (a contributed provider stores logins there).</summary>
    public const string CredentialsAccess = "credentials.read";

    /// <summary>Reach the platform's process runner (a contributed download engine shells out).</summary>
    public const string ProcessAccess = "process.run";

    private readonly HashSet<string> _declared;

    public ExtensionPermissions(IEnumerable<string>? declared)
        => _declared = new HashSet<string>(declared ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>An extension that declared nothing.</summary>
    public static ExtensionPermissions None { get; } = new(Array.Empty<string>());

    public IReadOnlyCollection<string> Declared => _declared;

    public bool Allows(string permission) => _declared.Contains(permission);

    public override string ToString() => _declared.Count == 0 ? "(无声明)" : string.Join(", ", _declared.OrderBy(p => p, StringComparer.Ordinal));
}
