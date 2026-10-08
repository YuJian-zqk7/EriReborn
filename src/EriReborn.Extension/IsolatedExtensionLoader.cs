using System.Reflection;
using System.Runtime.Loader;
using EriReborn.Core.Logging;

namespace EriReborn.Extension;

/// <summary>
/// Load context for a single extension. It is collectible so an extension can
/// actually be unloaded, and it resolves the assemblies its dependency file lists
/// (spec 37).
///
/// <para>
/// What that buys, precisely: an extension may ship its own copy of a library, and
/// that copy belongs to this context alone — the host's types are untouched and two
/// extensions cannot see each other's. It is not a promise that a plugin cannot bring
/// a duplicate; it is a promise that a duplicate stays its own.
/// </para>
/// </summary>
internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;

    public ExtensionLoadContext(string name, string mainAssemblyPath)
        : base(name, isCollectible: true)
    {
        try
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }
        catch
        {
            // Hand-authored extensions may ship no .deps.json. That is not a
            // failure: shared assemblies still resolve from the default context.
            _resolver = null;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>
/// Loads extension assemblies in isolated, collectible contexts (spec 37).
/// </summary>
public sealed class IsolatedExtensionLoader(IAppLogger log) : IExtensionLifecycle
{
    private readonly IAppLogger _log = log;
    private readonly Dictionary<string, ExtensionLoadContext> _contexts = new(StringComparer.Ordinal);

    public ExtensionStatus Load(ExtensionManifest manifest, IExtensionHost host)
    {
        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            return new ExtensionStatus(manifest, ExtensionLoadState.Rejected, "Manifest does not name an assembly.");
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(manifest.Directory, manifest.Assembly));
        if (!File.Exists(assemblyPath))
        {
            return new ExtensionStatus(manifest, ExtensionLoadState.AssemblyMissing, $"Assembly not found: {assemblyPath}");
        }

        Unload(manifest.Id);

        try
        {
            var context = new ExtensionLoadContext($"ext-{manifest.Id}", assemblyPath);
            _contexts[manifest.Id] = context;
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var implementation = assembly.GetTypes()
                .Where(t => typeof(IExtension).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .FirstOrDefault();

            if (implementation is null)
            {
                return new ExtensionStatus(manifest, ExtensionLoadState.NoImplementation, "Assembly contains no IExtension implementation.");
            }

            if (Activator.CreateInstance(implementation) is not IExtension instance)
            {
                return new ExtensionStatus(manifest, ExtensionLoadState.LoadFailed, "IExtension implementation could not be constructed.");
            }

            // Every capability the host controls is checked against what the
            // manifest declared, at the single point where an extension starts.
            var gated = new PermissionCheckedExtensionHost(
                host,
                new ExtensionPermissions(manifest.Permissions),
                _log,
                manifest.Id);

            instance.Initialize(gated);
            _log.Info("extension.loaded", $"Loaded extension '{manifest.Id}' v{manifest.Version} from '{assemblyPath}'.");
            return new ExtensionStatus(manifest, ExtensionLoadState.Loaded, "Loaded.", instance);
        }
        catch (ReflectionTypeLoadException ex)
        {
            var details = string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message));
            _log.Error("extension.load", $"Type load failed for '{manifest.Id}'.", ex);
            return new ExtensionStatus(manifest, ExtensionLoadState.LoadFailed, $"Type load failed: {details}");
        }
        catch (Exception ex)
        {
            _log.Error("extension.load", $"Failed to load extension '{manifest.Id}'.", ex);
            return new ExtensionStatus(manifest, ExtensionLoadState.LoadFailed, ex.Message);
        }
    }

    /// <summary>Releases the extension's load context so the files can be replaced.</summary>
    public bool Unload(string extensionId)
    {
        if (!_contexts.Remove(extensionId, out var context))
        {
            return false;
        }

        try
        {
            context.Unload();
            _log.Info("extension.unload", $"Unloaded extension '{extensionId}'.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("extension.unload", $"Failed to unload '{extensionId}': {ex.Message}");
            return false;
        }
    }

    public void UnloadAll()
    {
        foreach (var id in _contexts.Keys.ToList())
        {
            Unload(id);
        }
    }
}
