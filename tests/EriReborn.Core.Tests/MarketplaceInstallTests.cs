using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using EriReborn.Extension;
using EriReborn.Extension.Marketplace;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Proves the marketplace loop really runs end to end: fetch the index, download
/// the package, verify it, install it, and load the extension (spec 35/36/69).
/// </summary>
public sealed class MarketplaceInstallTests : IDisposable
{
    private const string ManifestJson = """
    {
      "id": "sample_hello",
      "name": "Hello Extension",
      "version": "1.0.0",
      "author": "EriReborn",
      "description": "A minimal extension used by the marketplace tests.",
      "assembly": "EriReborn.SampleExtension.dll",
      "trust": "community",
      "permissions": [ "network.http", "settings.read", "settings.write" ],
      "categories": [ "Utility" ]
    }
    """;

    private readonly string _root;
    private readonly TestFileSystemService _files;

    public MarketplaceInstallTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new TestFileSystemService(_root);
    }

    private static byte[] BuildPackage(string manifestJson, params (string Name, byte[] Content)[] extra)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = manifestEntry.Open())
            {
                writer.Write(Encoding.UTF8.GetBytes(manifestJson));
            }

            foreach (var (name, content) in extra)
            {
                var entry = archive.CreateEntry(name);
                using var writer = entry.Open();
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] SampleAssembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EriReborn.SampleExtension.dll");
        Assert.True(File.Exists(path), $"Sample extension assembly not found at {path}");
        return File.ReadAllBytes(path);
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private ExtensionRegistry CreateRegistry(out ExtensionInstaller installer)
    {
        var registry = new ExtensionRegistry(
            new ExtensionValidator(),
            new IsolatedExtensionLoader(AppLog.For("Test")),
            AppLog.For("Test"));

        var downloader = new HttpDownloader(
            new HttpClient(),
            _files,
            new DownloadCache(Path.Combine(_root, "cache")),
            AppLog.For("Test"));

        installer = new ExtensionInstaller(downloader, _files, registry, AppLog.For("Test"));
        return registry;
    }

    private static MarketplaceEntry Entry(string downloadUrl, string sha, long size) => new()
    {
        Id = "sample_hello",
        Name = "Hello Extension",
        Author = "EriReborn",
        Version = "1.0.0",
        Description = "A minimal extension used by the marketplace tests.",
        Categories = new[] { "Utility" },
        Tags = new[] { "demo" },
        Permissions = new[] { "network.http" },
        Trust = EriReborn.Core.Domain.SoftwareTrust.Community,
        DownloadUrl = downloadUrl,
        Sha256 = sha,
        SizeBytes = size,
    };

    [Fact]
    public async Task Index_is_fetched_and_parsed_from_the_configured_url()
    {
        var package = BuildPackage(ManifestJson, ("EriReborn.SampleExtension.dll", SampleAssembly()));
        var indexJson = JsonSerializer.Serialize(new
        {
            schema = 1,
            updatedAt = "2026-10-01T00:00:00Z",
            extensions = new[]
            {
                new
                {
                    id = "sample_hello",
                    name = "Hello Extension",
                    author = "EriReborn",
                    version = "1.0.0",
                    description = "demo",
                    categories = new[] { "Utility" },
                    tags = new[] { "demo" },
                    permissions = new[] { "network.http", "settings.read", "settings.write" },
                    trust = "community",
                    downloadUrl = "PLACEHOLDER",
                    sha256 = Sha256(package),
                    sizeBytes = package.Length,
                },
            },
        });

        using var server = new TestHttpServer(request =>
            request.Path.StartsWith("/index", StringComparison.Ordinal)
                ? TestResponse.Ok(Encoding.UTF8.GetBytes(indexJson.Replace("PLACEHOLDER", "http://example.invalid/pkg.zip")))
                : TestResponse.NotFound());

        var client = new MarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var result = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_root, "cache", "marketplace.json"));

        Assert.True(result.Success, result.Message);
        var entry = Assert.Single(result.Index.Entries);
        Assert.Equal("sample_hello", entry.Id);
        Assert.Equal(new[] { "Utility" }, entry.Categories);
        Assert.Equal(EriReborn.Core.Domain.SoftwareTrust.Community, entry.Trust);
    }

    [Fact]
    public async Task Install_downloads_verifies_extracts_and_the_extension_then_loads()
    {
        var package = BuildPackage(ManifestJson, ("EriReborn.SampleExtension.dll", SampleAssembly()));
        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        var extensionsRoot = Path.Combine(_root, "extensions");
        var workRoot = Path.Combine(_root, "work");

        var registry = CreateRegistry(out var installer);
        var outcome = await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(package), package.Length),
            extensionsRoot,
            workRoot);

        Assert.True(outcome.Success, outcome.Message);
        Assert.True(File.Exists(Path.Combine(extensionsRoot, "sample_hello", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(extensionsRoot, "sample_hello", "EriReborn.SampleExtension.dll")));

        // The real proof: the registry loads it and calls Initialize.
        var host = new RecordingHost();
        var statuses = await registry.RefreshAsync(extensionsRoot, host);

        var status = Assert.Single(statuses);
        Assert.Equal(ExtensionLoadState.Loaded, status.State);
        Assert.NotNull(status.Instance);
        Assert.Equal("sample_hello", status.Instance!.Id);
        Assert.Equal("Hello Extension", status.Instance.DisplayName);
        Assert.Contains(host.Logs, l => l.Contains("HelloExtension initialized", StringComparison.Ordinal));
        Assert.True(host.Settings.ContainsKey("initializedAt"));
    }

    [Fact]
    public async Task A_package_whose_hash_does_not_match_is_rejected_and_nothing_is_installed()
    {
        var package = BuildPackage(ManifestJson, ("EriReborn.SampleExtension.dll", SampleAssembly()));
        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        var extensionsRoot = Path.Combine(_root, "extensions");
        CreateRegistry(out var installer);
        var outcome = await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(Encoding.UTF8.GetBytes("different")), package.Length),
            extensionsRoot,
            Path.Combine(_root, "work"));

        Assert.False(outcome.Success);
        Assert.False(Directory.Exists(Path.Combine(extensionsRoot, "sample_hello")));
    }

    [Fact]
    public async Task A_package_that_claims_a_different_id_is_refused()
    {
        var package = BuildPackage(
            ManifestJson.Replace("sample_hello", "someone_else", StringComparison.Ordinal),
            ("EriReborn.SampleExtension.dll", SampleAssembly()));

        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        CreateRegistry(out var installer);
        var outcome = await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(package), package.Length),
            Path.Combine(_root, "extensions"),
            Path.Combine(_root, "work"));

        Assert.False(outcome.Success);
        Assert.Contains("不一致", outcome.Message);
    }

    [Fact]
    public async Task An_archive_entry_that_escapes_the_target_directory_is_refused()
    {
        var package = BuildPackage(
            ManifestJson,
            ("EriReborn.SampleExtension.dll", SampleAssembly()),
            ("../escaped.txt", Encoding.UTF8.GetBytes("pwned")));

        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        CreateRegistry(out var installer);
        var outcome = await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(package), package.Length),
            Path.Combine(_root, "extensions"),
            Path.Combine(_root, "work"));

        Assert.False(outcome.Success);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public async Task Uninstall_removes_the_installed_extension()
    {
        var package = BuildPackage(ManifestJson, ("EriReborn.SampleExtension.dll", SampleAssembly()));
        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        var extensionsRoot = Path.Combine(_root, "extensions");
        var registry = CreateRegistry(out var installer);

        var install = await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(package), package.Length),
            extensionsRoot,
            Path.Combine(_root, "work"));
        Assert.True(install.Success, install.Message);

        var removal = await installer.UninstallAsync("sample_hello", extensionsRoot);
        Assert.True(removal.Success, removal.Message);
        Assert.False(Directory.Exists(Path.Combine(extensionsRoot, "sample_hello")));
    }

    [Fact]
    public async Task A_pending_removal_marker_blocks_loading_and_is_cleaned_up_on_the_next_scan()
    {
        var package = BuildPackage(ManifestJson, ("EriReborn.SampleExtension.dll", SampleAssembly()));
        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        var extensionsRoot = Path.Combine(_root, "extensions");
        var registry = CreateRegistry(out var installer);

        var install = await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(package), package.Length),
            extensionsRoot,
            Path.Combine(_root, "work"));
        Assert.True(install.Success, install.Message);

        // Simulate an uninstall whose files were still mapped.
        var marker = Path.Combine(extensionsRoot, "sample_hello.pending-delete");
        await File.WriteAllTextAsync(marker, DateTimeOffset.Now.ToString("O"));

        var statuses = await registry.RefreshAsync(extensionsRoot, new RecordingHost());

        Assert.DoesNotContain(statuses, s => s.State == ExtensionLoadState.Loaded);
        Assert.False(Directory.Exists(Path.Combine(extensionsRoot, "sample_hello")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Disabled_extensions_are_discovered_but_not_loaded()
    {
        var package = BuildPackage(ManifestJson, ("EriReborn.SampleExtension.dll", SampleAssembly()));
        using var server = new TestHttpServer(_ => TestResponse.Ok(package));

        var extensionsRoot = Path.Combine(_root, "extensions");
        CreateRegistry(out var installer);
        await installer.InstallAsync(
            Entry(server.Url("/pkg.zip"), Sha256(package), package.Length),
            extensionsRoot,
            Path.Combine(_root, "work"));

        var registry = new ExtensionRegistry(
            new ExtensionValidator(),
            new IsolatedExtensionLoader(AppLog.For("Test")),
            AppLog.For("Test"));

        var statuses = await registry.RefreshAsync(extensionsRoot, new RecordingHost(), new[] { "sample_hello" });

        var status = Assert.Single(statuses);
        Assert.Equal(ExtensionLoadState.Disabled, status.State);
        Assert.Null(status.Instance);
    }

    private sealed class RecordingHost : IExtensionHost
    {
        public List<string> Logs { get; } = new();

        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public void Log(string level, string message)
        {
            lock (Logs)
            {
                Logs.Add($"{level}:{message}");
            }
        }

        public string GetSetting(string key, string? fallback = null)
            => Settings.TryGetValue(key, out var value) ? value : fallback ?? string.Empty;

        public void SetSetting(string key, string value) => Settings[key] = value;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
