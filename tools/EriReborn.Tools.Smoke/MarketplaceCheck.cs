using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EriReborn.App.Shared.Services;
using EriReborn.Extension;
using EriReborn.Extension.Marketplace;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Drives the marketplace through the real composition root: fetch the index
/// over HTTP, install the package, and confirm the extension is actually
/// loaded (spec 35/36/69).
/// </summary>
internal static class MarketplaceCheck
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

    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var sampleAssembly = Path.Combine(AppContext.BaseDirectory, "EriReborn.SampleExtension.dll");
            if (!File.Exists(sampleAssembly))
            {
                Console.WriteLine($"FAIL sample extension assembly missing: {sampleAssembly}");
                return 1;
            }

            var package = BuildPackage(ManifestJson, File.ReadAllBytes(sampleAssembly));
            var sha = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();

            using var server = new LocalHttpServer();
            server.Add("/pkg.zip", package);
            server.Add("/index.json", Encoding.UTF8.GetBytes($$"""
            {
              "schema": 1,
              "updatedAt": "2026-10-01T00:00:00Z",
              "extensions": [
                {
                  "id": "sample_hello",
                  "name": "Hello Extension",
                  "author": "EriReborn",
                  "version": "1.0.0",
                  "description": "Packaged by the smoke run.",
                  "categories": [ "Utility" ],
                  "tags": [ "demo" ],
                  "permissions": [ "network.http" ],
                  "trust": "community",
                  "downloadUrl": "{{server.Url("/pkg.zip")}}",
                  "sha256": "{{sha}}",
                  "sizeBytes": {{package.Length}}
                }
              ]
            }
            """));

            // 1) Index through the real client.
            var fetch = await host.Marketplace.FetchAsync(server.Url("/index.json"), Path.Combine(work, "index.json"));
            failures += Report("marketplace index fetched", fetch.Success);
            var entry = fetch.Index.Entries.FirstOrDefault();
            failures += Report("index contains the entry", entry is not null);
            if (entry is null)
            {
                return failures;
            }

            // 2) Install through the real installer.
            var extensionsRoot = Path.Combine(work, "extensions");
            var outcome = await host.ExtensionInstaller.InstallAsync(entry, extensionsRoot, work);
            failures += Report("package installed", outcome.Success);
            if (!outcome.Success)
            {
                Console.WriteLine($"      {outcome.Message}");
                return failures;
            }

            // 3) Load through the real registry. Only plain values are kept, so
            //    the instance is not pinned when we uninstall below.
            var (loaded, instanceId, instanceVersion) = await CheckLoadedAsync(host, extensionsRoot);
            failures += Report("extension loaded", loaded);
            failures += Report("extension instance usable", instanceId == "sample_hello");
            Console.WriteLine($"      loaded={loaded} id={instanceId} v{instanceVersion}");

            // 4) Uninstall must stop the extension from loading. If the runtime
            //    still holds the assembly the files are cleaned up next start.
            var removal = await host.ExtensionInstaller.UninstallAsync("sample_hello", extensionsRoot);
            Console.WriteLine($"      uninstall: success={removal.Success} deferred={removal.DeferredRemoval} {removal.Message}");
            failures += Report("uninstall accepted", removal.Success);

            var (stillLoaded, _, _) = await CheckLoadedAsync(host, extensionsRoot);
            failures += Report("extension no longer loads after uninstall", !stillLoaded);

            // 5) Rescanning must keep it unloaded. The files themselves can only
            //    be removed by a fresh process, because this one still maps them.
            var rescan = await host.Extensions.RefreshAsync(extensionsRoot, new SmokeExtensionHost(), null);
            failures += Report(
                "rescan keeps it unloaded",
                rescan.All(s => s.State != ExtensionLoadState.Loaded));

            var directory = Path.Combine(extensionsRoot, "sample_hello");
            var remaining = Directory.Exists(directory);
            var marker = File.Exists(Path.Combine(extensionsRoot, "sample_hello.pending-delete"));
            Console.WriteLine($"      files still mapped in-process: dir={remaining} marker={marker} (cleaned on next start)");
            failures += Report("deferred removal is recorded", remaining == marker);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }

        return failures;
    }

    /// <summary>Loads via the real registry and returns only plain values.</summary>
    private static async Task<(bool Loaded, string? Id, string? Version)> CheckLoadedAsync(AppHost host, string extensionsRoot)
    {
        var statuses = await host.Extensions.RefreshAsync(extensionsRoot, new SmokeExtensionHost(), null);
        var status = statuses.FirstOrDefault(s => s.Manifest.Id == "sample_hello");
        return (status?.State == ExtensionLoadState.Loaded, status?.Instance?.Id, status?.Instance?.Version);
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }

    private static byte[] BuildPackage(string manifestJson, byte[] assembly)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = manifest.Open())
            {
                writer.Write(Encoding.UTF8.GetBytes(manifestJson));
            }

            var dll = archive.CreateEntry("EriReborn.SampleExtension.dll");
            using var dllWriter = dll.Open();
            dllWriter.Write(assembly);
        }

        return stream.ToArray();
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
