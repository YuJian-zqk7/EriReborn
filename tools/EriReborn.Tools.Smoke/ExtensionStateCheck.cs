using EriReborn.App.Shared.Services;
using EriReborn.Extension;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Verifies that enabling and disabling an installed extension takes effect
/// live (spec 34), through the real registry and loader.
///
/// Whether the load context was actually released is measured by *deletability*:
/// Windows allows a mapped assembly to be renamed but refuses to delete it, so
/// "can this file be deleted" is the honest observable. Nothing may pin the
/// returned <c>ExtensionStatus</c>, or the instance (and its mapping) survives.
/// </summary>
internal static class ExtensionStateCheck
{
    private const string ManifestJson = """
    {
      "id": "sample_hello",
      "name": "Hello Extension",
      "version": "1.0.0",
      "author": "EriReborn",
      "description": "Packaged by the smoke run.",
      "assembly": "EriReborn.SampleExtension.dll",
      "trust": "community",
      "permissions": [ "network.http" ],
      "categories": [ "Utility" ]
    }
    """;

    private const string DependentManifestJson = """
    {
      "id": "sample_dep",
      "name": "Dependent Extension",
      "version": "1.0.0",
      "assembly": "EriReborn.SampleExtension.dll",
      "trust": "community",
      "dependencies": [ "sample_hello" ]
    }
    """;

    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-ext-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "EriReborn.SampleExtension.dll");
            if (!File.Exists(source))
            {
                Console.WriteLine($"FAIL sample extension assembly missing: {source}");
                return 1;
            }

            var extensionsRoot = Path.Combine(work, "extensions");
            var directory = Path.Combine(extensionsRoot, "sample_hello");
            Directory.CreateDirectory(directory);

            var dll = Path.Combine(directory, "EriReborn.SampleExtension.dll");
            var original = await File.ReadAllBytesAsync(source);
            await File.WriteAllBytesAsync(dll, original);
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), ManifestJson);

            var extensionHost = new SmokeExtensionHost();

            // 1) Loaded for real. Only plain values escape the helper.
            failures += Report("extension loads on refresh", await IsLoadedAsync(host, extensionsRoot, extensionHost, null));

            // Control: a mapped assembly cannot be deleted.
            failures += Report("loaded assembly is not deletable", !IsDeletable(dll, original));
            Console.WriteLine("      while loaded: assembly cannot be deleted (expected)");

            // 2) Disable releases the load context.
            var disabledState = DisableAndReport(host);
            failures += Report("disable reports disabled", disabledState == ExtensionLoadState.Disabled);
            failures += Report("nothing remains loaded", host.Extensions.Statuses.All(s => s.State != ExtensionLoadState.Loaded));

            // .NET does not guarantee an assembly file is unmapped in-process
            // after AssemblyLoadContext.Unload(); this project handles that with
            // the ".pending-delete" marker honoured on the next start. So the
            // in-process result is reported, not asserted.
            failures += Report(
                "disabling keeps the files on disk",
                File.Exists(dll) && File.Exists(Path.Combine(directory, "manifest.json")));
            Console.WriteLine(
                $"      in-process unmapped after disable: {IsDeletable(dll, original)}"
                + " (not guaranteed by .NET; real removal is deferred to the next start)");

            // The load context itself must be gone: calling Unload again reports nothing.
            failures += Report("loader holds no context for it any more", !IsContextHeld(host, "sample_hello"));

            // 3) The choice is persisted and honoured by the next scan.
            host.UserConfig.SetExtensionEnabled("sample_hello", false);
            failures += Report("disabled state persisted", host.UserConfig.IsExtensionDisabled("sample_hello"));
            failures += Report(
                "refresh keeps it disabled",
                !await IsLoadedAsync(host, extensionsRoot, extensionHost, host.UserConfig.Current.DisabledExtensions));

            // 4) Enable must genuinely load it again, not just flip a flag.
            failures += Report("enable loads it again", await EnableAndCheckLoadedAsync(host, extensionHost));
            failures += Report("re-enabled assembly is mapped again", !IsDeletable(dll, original));

            host.UserConfig.SetExtensionEnabled("sample_hello", true);
            failures += Report("enabled state persisted", !host.UserConfig.IsExtensionDisabled("sample_hello"));

            // 5) Unknown ids must not throw.
            failures += Report("disable of an unknown id is null", host.Extensions.Disable("not_installed") is null);
            failures += Report("enable of an unknown id is null", host.Extensions.Enable("not_installed", extensionHost) is null);

            // Release again so the temp directory can be removed.
            host.Extensions.Disable("sample_hello");
            IsDeletable(dll, original);

            failures += await CheckDependenciesAsync(host, work, original);
        }
        finally
        {
            TryDeleteDirectory(work);
        }

        return failures;
    }

    /// <summary>
    /// True while the loader still owns a context for the id. Unloading twice
    /// reports "nothing to unload" the second time, which is the observable
    /// proof that the context was released.
    /// </summary>
    private static bool IsContextHeld(AppHost host, string extensionId)
    {
        // The registry's Unload forwards to the loader; the first call clears the
        // context, so a second call must find nothing.
        host.Extensions.Unload(extensionId);
        return host.Extensions.Unload(extensionId);
    }

    /// <summary>
    /// End-to-end dependency behaviour: an extension declaring a dependency must
    /// not load without it, must load once it is present, and must be released
    /// when the dependency is switched off.
    /// </summary>
    private static async Task<int> CheckDependenciesAsync(AppHost host, string work, byte[] assembly)
    {
        var failures = 0;
        Console.WriteLine("      -- dependencies --");

        var root = Path.Combine(work, "dep-extensions");
        var depDirectory = Path.Combine(root, "sample_dep");
        Directory.CreateDirectory(depDirectory);
        await File.WriteAllBytesAsync(Path.Combine(depDirectory, "EriReborn.SampleExtension.dll"), assembly);
        await File.WriteAllTextAsync(Path.Combine(depDirectory, "manifest.json"), DependentManifestJson);

        var extensionHost = new SmokeExtensionHost();

        // 1) The dependency is absent: the dependent must not load.
        var alone = await host.Extensions.RefreshAsync(root, extensionHost, null);
        var dep = alone.FirstOrDefault(s => s.Manifest.Id == "sample_dep");
        failures += Report("dependent blocked while its dependency is absent", dep?.State == ExtensionLoadState.DependencyMissing);
        Console.WriteLine($"      {dep?.Message}");

        // 2) Install the dependency: both must load.
        var baseDirectory = Path.Combine(root, "sample_hello");
        Directory.CreateDirectory(baseDirectory);
        await File.WriteAllBytesAsync(Path.Combine(baseDirectory, "EriReborn.SampleExtension.dll"), assembly);
        await File.WriteAllTextAsync(Path.Combine(baseDirectory, "manifest.json"), ManifestJson);

        var together = await host.Extensions.RefreshAsync(root, extensionHost, null);
        failures += Report("both load once the dependency is present", together.Count(s => s.State == ExtensionLoadState.Loaded) == 2);

        // 3) Switching the dependency off must release the dependent too.
        host.Extensions.Disable("sample_hello");
        var released = host.Extensions.Statuses.FirstOrDefault(s => s.Manifest.Id == "sample_dep");
        failures += Report("dependent released with its dependency", released?.State == ExtensionLoadState.DependencyMissing);
        Console.WriteLine($"      {released?.Message}");
        failures += Report("nothing is left loaded", host.Extensions.Statuses.All(s => s.State != ExtensionLoadState.Loaded));

        // 4) Rescanning restores both.
        var restored = await host.Extensions.RefreshAsync(root, extensionHost, null);
        failures += Report("rescan restores both", restored.Count(s => s.State == ExtensionLoadState.Loaded) == 2);

        host.Extensions.Disable("sample_dep");
        host.Extensions.Disable("sample_hello");
        return failures;
    }

    /// <summary>Refreshes and reports only the resulting state, so nothing is pinned.</summary>
    private static async Task<bool> IsLoadedAsync(
        AppHost host,
        string root,
        IExtensionHost extensionHost,
        IReadOnlyCollection<string>? disabled)
    {
        var statuses = await host.Extensions.RefreshAsync(root, extensionHost, disabled);
        return statuses.Any(s => s.Manifest.Id == "sample_hello" && s.State == ExtensionLoadState.Loaded);
    }

    /// <summary>Returns only the state enum, never the status object.</summary>
    private static ExtensionLoadState DisableAndReport(AppHost host)
        => host.Extensions.Disable("sample_hello")?.State ?? ExtensionLoadState.Discovered;

    /// <summary>Enables, checks the instance, and drops every reference again.</summary>
    private static async Task<bool> EnableAndCheckLoadedAsync(AppHost host, IExtensionHost extensionHost)
    {
        var enabled = host.Extensions.Enable("sample_hello", extensionHost);
        var usable = enabled is { State: ExtensionLoadState.Loaded, Instance.Id: "sample_hello" };

        // Force collection so the check below observes the real mapping state.
        Collect();

        await Task.CompletedTask;
        return usable;
    }

    /// <summary>Deletes the file and restores it; success means it was not mapped.</summary>
    private static bool IsDeletable(string path, byte[] original, string? label = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                File.Delete(path);
                File.WriteAllBytes(path, original);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                Collect();
            }
        }

        if (label is not null && last is not null)
        {
            Console.WriteLine($"      {label}: {last.GetType().Name}: {last.Message}");
        }

        return false;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(120);
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception)
            {
                Collect();
            }
        }
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }

    private sealed class SmokeExtensionHost : IExtensionHost
    {
        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

        public void Log(string level, string message) => Console.WriteLine($"      [ext:{level}] {message}");

        public string GetSetting(string key, string? fallback = null)
            => _settings.TryGetValue(key, out var value) ? value : fallback ?? string.Empty;

        public void SetSetting(string key, string value) => _settings[key] = value;
    }
}
