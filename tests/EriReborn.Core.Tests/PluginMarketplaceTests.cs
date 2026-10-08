using System.Security.Cryptography;
using System.Text;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Download;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The plugin marketplace exists to discover resource plugins. Its shape is
/// deliberately not the extension marketplace's: one offers resources, the other
/// offers capability (spec 49/176/182).
/// </summary>
public sealed class PluginMarketplaceTests : IDisposable
{
    private readonly string _temp;
    private readonly TestFileSystemService _files;

    public PluginMarketplaceTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _files = new TestFileSystemService(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string PluginJson(string resourceId)
        => $$"""
        {
          "schema": 1,
          "id": "market_plugin",
          "name": "Market Plugin",
          "version": "2.0.0",
          "resources": [
            {
              "id": "{{resourceId}}",
              "name": "{{resourceId}}",
              "categoryId": "Utility",
              "directory": "{{resourceId}}",
              "version": "2.0.0",
              "sources": [ { "kind": "CloudShare", "provider": "123", "shareUrl": "https://example.invalid/s/1" } ]
            }
          ]
        }
        """;

    private static string IndexJson(string pluginUrl, string? sha, string? signatureUrl = null, string version = "2.0.0")
        => $$"""
        {
          "schema": 1,
          "plugins": [
            {
              "id": "market_plugin",
              "name": "Market Plugin",
              "author": "tester",
              "version": "{{version}}",
              "resourceCount": 1,
              "providers": ["123", "baidu"],
              "downloadUrl": "{{pluginUrl}}",
              "signatureUrl": {{(signatureUrl is null ? "null" : $"\"{signatureUrl}\"")}},
              "sha256": {{(sha is null ? "null" : $"\"{sha}\"")}},
              "signed": {{(signatureUrl is null ? "false" : "true")}}
            }
          ]
        }
        """;

    /// <summary>
    /// A server that serves its own index. The index has to name the plugin's
    /// real address, which is only known once the listener exists — so the
    /// document is built on demand rather than up front.
    /// </summary>
    /// <param name="advertiseSignature">
    /// Whether the index names a signature file. Kept separate from whether one
    /// can actually be fetched, because "claims to be signed" and "is signed" are
    /// different states and only one of them may be trusted.
    /// </param>
    private static TestHttpServer MarketServer(
        string pluginJson,
        string? sha,
        string? signatureBody = null,
        bool advertiseSignature = false,
        string version = "2.0.0")
    {
        TestHttpServer? server = null;
        var hasSignature = advertiseSignature || signatureBody is not null;

        server = new TestHttpServer(request => request.Path switch
        {
            "/index.json" => new TestResponse(
                200,
                Encoding.UTF8.GetBytes(IndexJson(
                    server!.Url("/plugin.json"),
                    sha,
                    hasSignature ? server.Url("/plugin.sig") : null,
                    version))),
            "/plugin.json" => new TestResponse(200, Encoding.UTF8.GetBytes(pluginJson)),
            "/plugin.sig" => signatureBody is null
                ? TestResponse.NotFound()
                : new TestResponse(200, Encoding.UTF8.GetBytes(signatureBody)),
            _ => TestResponse.NotFound(),
        });

        return server;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static PluginImportService ImportService()
        => new(new[] { "123", "baidu", "quark", "lanzou", "xunlei" }, AppLog.For("Test"));

    private HttpDownloader Downloader()
        => new(new HttpClient(), _files, new DownloadCache(Path.Combine(_temp, "cache")), AppLog.For("Test"));

    private PluginMarketplaceInstaller Installer(PluginRegistry registry, PluginInstallationStore? store = null)
        => new(
            Downloader(),
            ImportService(),
            registry,
            store ?? new PluginInstallationStore(Path.Combine(_temp, "installed.json"), AppLog.For("Test")),
            AppLog.For("Test"));

    // --------------------------------------------------------------- the index

    [Fact]
    public void The_index_is_parsed_from_its_own_document_shape()
    {
        var index = PluginMarketplaceClient.Parse("""
        {
          "schema": 1,
          "plugins": [
            { "id": "a", "name": "A", "resourceCount": 3, "providers": ["123", "quark"], "signed": true },
            { "name": "no id" },
            { "id": "b", "name": "B" }
          ]
        }
        """);

        Assert.Equal(2, index.Entries.Count);

        var first = index.Entries[0];
        Assert.Equal("a", first.Id);
        Assert.Equal(3, first.ResourceCount);
        Assert.Contains("quark", first.Providers);
        Assert.True(first.IsSigned);

        // An entry without an id cannot be addressed, so it is skipped.
        Assert.DoesNotContain(index.Entries, entry => entry.Name == "no id");
    }

    [Fact]
    public void An_extension_document_is_not_mistaken_for_a_plugin_document()
    {
        // The two marketplaces must not share a shape (spec 192).
        var index = PluginMarketplaceClient.Parse("""
        { "schema": 1, "extensions": [ { "id": "x", "name": "X" } ] }
        """);

        Assert.Empty(index.Entries);
    }

    // ------------------------------------------------------------ the fetching

    [Fact]
    public async Task With_no_configured_url_the_cache_is_used_when_it_exists()
    {
        var cache = Path.Combine(_temp, "plugin-index.json");
        await File.WriteAllTextAsync(cache, """{ "plugins": [ { "id": "cached", "name": "Cached" } ] }""");

        var client = new PluginMarketplaceClient(
            new TestNetworkService(new HttpClient()),
            AppLog.For("Test"));

        var (index, fromCache, message) = await client.FetchAsync(indexUrl: null, cache);

        Assert.Single(index.Entries);
        Assert.True(fromCache);
        Assert.Contains("缓存", message);
    }

    [Fact]
    public async Task With_no_url_and_no_cache_the_reason_is_stated()
    {
        var client = new PluginMarketplaceClient(
            new TestNetworkService(new HttpClient()),
            AppLog.For("Test"));

        var (index, fromCache, message) = await client.FetchAsync(null, Path.Combine(_temp, "absent.json"));

        Assert.Empty(index.Entries);
        Assert.False(fromCache);
        Assert.Contains("未配置", message);
    }

    [Fact]
    public async Task An_unreachable_index_falls_back_to_the_cache_instead_of_an_empty_page()
    {
        var cache = Path.Combine(_temp, "plugin-index.json");
        await File.WriteAllTextAsync(cache, """{ "plugins": [ { "id": "cached", "name": "Cached" } ] }""");

        var client = new PluginMarketplaceClient(
            new TestNetworkService(new HttpClient()),
            AppLog.For("Test"));

        // Nothing is listening on this port.
        var (index, fromCache, message) = await client.FetchAsync("http://127.0.0.1:9/nope.json", cache);

        Assert.Single(index.Entries);
        Assert.True(fromCache);
        Assert.Contains("网络不可用", message);
    }

    // -------------------------------------------------------- installing a plugin

    [Fact]
    public async Task Installing_downloads_imports_and_registers_the_plugin()
    {
        var bytes = Encoding.UTF8.GetBytes(PluginJson("from_market"));

        using var server = MarketServer(Encoding.UTF8.GetString(bytes), Sha(bytes));

        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));
        var entry = Assert.Single(index.Entries);

        var registry = new PluginRegistry();
        var result = await Installer(registry).InstallAsync(entry, Path.Combine(_temp, "plugins"), Array.Empty<string>());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.AddedResources);
        Assert.True(registry.Contains("from_market"));

        // An unsigned plugin never gains official authority.
        Assert.Equal(PluginTrust.Unknown, result.Trust);
    }

    [Fact]
    public async Task A_download_whose_hash_does_not_match_is_refused()
    {
        var bytes = Encoding.UTF8.GetBytes(PluginJson("tampered"));

        using var server = MarketServer(Encoding.UTF8.GetString(bytes), new string('0', 64));

        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));

        var registry = new PluginRegistry();
        var result = await Installer(registry).InstallAsync(
            index.Entries[0],
            Path.Combine(_temp, "plugins"),
            Array.Empty<string>());

        Assert.False(result.Succeeded);
        Assert.Empty(registry.Imported);
    }

    [Fact]
    public async Task A_plugin_the_index_calls_signed_is_refused_when_no_signature_can_be_fetched()
    {
        var bytes = Encoding.UTF8.GetBytes(PluginJson("claims_signed"));

        // The index promises a signature, and the signature file is not there.
        using var server = MarketServer(
            Encoding.UTF8.GetString(bytes),
            Sha(bytes),
            signatureBody: null,
            advertiseSignature: true);

        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));

        var registry = new PluginRegistry();
        var result = await Installer(registry).InstallAsync(
            index.Entries[0],
            Path.Combine(_temp, "plugins"),
            Array.Empty<string>());

        // Claiming a signature is not having one.
        Assert.False(result.Succeeded);
        Assert.Contains("签名", result.Message);
        Assert.Empty(registry.Imported);
    }

    [Fact]
    public async Task A_signature_from_an_unpinned_key_is_refused()
    {
        var bytes = Encoding.UTF8.GetBytes(PluginJson("badly_signed"));
        var signature = """{ "keyId": "attacker", "signature": "AAAA" }""";

        using var server = MarketServer(Encoding.UTF8.GetString(bytes), Sha(bytes), signature);

        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));

        var registry = new PluginRegistry();
        var result = await Installer(registry).InstallAsync(
            index.Entries[0],
            Path.Combine(_temp, "plugins"),
            Array.Empty<string>());

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.SignatureVerify, result.Stage);
        Assert.Empty(registry.Imported);
    }

    [Fact]
    public async Task An_id_that_already_exists_is_not_registered_but_the_install_still_reports_why()
    {
        var bytes = Encoding.UTF8.GetBytes(PluginJson("taken"));

        using var server = MarketServer(Encoding.UTF8.GetString(bytes), Sha(bytes));

        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));

        var registry = new PluginRegistry();
        var result = await Installer(registry).InstallAsync(
            index.Entries[0],
            Path.Combine(_temp, "plugins"),
            new[] { "taken" });

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.AddedResources);
        Assert.Empty(registry.Imported);
    }

    // --------------------------------------------------------------- updating

    [Fact]
    public async Task Updating_replaces_the_previous_resources_instead_of_adding_to_them()
    {
        var registry = new PluginRegistry();
        var installer = Installer(registry);
        var directory = Path.Combine(_temp, "plugins");

        var first = Encoding.UTF8.GetBytes(PluginJson("update_tool_v1"));
        using (var server = MarketServer(Encoding.UTF8.GetString(first), Sha(first), version: "1.0.0"))
        {
            var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
            var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "i1.json"));

            var installed = await installer.InstallAsync(index.Entries[0], directory, Array.Empty<string>());
            Assert.True(installed.Succeeded, installed.Message);
        }

        Assert.True(registry.Contains("update_tool_v1"));

        var second = Encoding.UTF8.GetBytes(PluginJson("update_tool_v2"));
        using (var server = MarketServer(Encoding.UTF8.GetString(second), Sha(second), version: "2.0.0"))
        {
            var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
            var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "i2.json"));

            var updated = await installer.UpdateAsync(index.Entries[0], directory, Array.Empty<string>());

            Assert.True(updated.Succeeded, updated.Message);
            Assert.True(updated.WasUpdate);
        }

        // The old version's resource is gone, not merely joined by the new one.
        Assert.False(registry.Contains("update_tool_v1"));
        Assert.True(registry.Contains("update_tool_v2"));
        Assert.Single(registry.Imported);
    }

    [Fact]
    public async Task An_update_is_not_blocked_by_the_plugins_own_previous_ids()
    {
        var registry = new PluginRegistry();
        var installer = Installer(registry);
        var directory = Path.Combine(_temp, "plugins");

        // Same resource id in both versions: the update must not read that as a
        // conflict with itself.
        var bytes = Encoding.UTF8.GetBytes(PluginJson("stable_id"));

        using var server = MarketServer(Encoding.UTF8.GetString(bytes), Sha(bytes), version: "3.0.0");
        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));

        var installed = await installer.InstallAsync(index.Entries[0], directory, Array.Empty<string>());
        Assert.True(installed.Succeeded, installed.Message);

        var updated = await installer.UpdateAsync(index.Entries[0], directory, Array.Empty<string>());

        Assert.True(updated.Succeeded, updated.Message);
        Assert.True(registry.Contains("stable_id"));
        Assert.Single(registry.Imported);
    }

    [Fact]
    public void Replacing_one_plugins_resources_leaves_another_plugins_alone()
    {
        var registry = new PluginRegistry();

        registry.Add(new[]
        {
            new EriReborn.Core.Domain.SoftwareDefinition
            {
                Id = "other_tool", Name = "Other", CategoryId = "Utility", DirectoryName = "Other", CatalogId = "other_plugin",
            },
            new EriReborn.Core.Domain.SoftwareDefinition
            {
                Id = "mine_v1", Name = "Mine", CategoryId = "Utility", DirectoryName = "Mine", CatalogId = "my_plugin",
            },
        });

        registry.Replace("my_plugin", new[]
        {
            new EriReborn.Core.Domain.SoftwareDefinition
            {
                Id = "mine_v2", Name = "Mine", CategoryId = "Utility", DirectoryName = "Mine", CatalogId = "my_plugin",
            },
        });

        Assert.False(registry.Contains("mine_v1"));
        Assert.True(registry.Contains("mine_v2"));

        // The other plugin is not collateral damage.
        Assert.True(registry.Contains("other_tool"));
        Assert.Contains("other_tool", registry.OwnedIds("other_plugin"));
    }

    [Fact]
    public async Task Installing_records_the_version_so_an_update_can_be_recognised()
    {
        var bytes = Encoding.UTF8.GetBytes(PluginJson("recorded"));

        using var server = MarketServer(Encoding.UTF8.GetString(bytes), Sha(bytes), version: "5.1.0");

        var client = new PluginMarketplaceClient(new TestNetworkService(new HttpClient()), AppLog.For("Test"));
        var (index, _, _) = await client.FetchAsync(server.Url("/index.json"), Path.Combine(_temp, "index.json"));

        var registry = new PluginRegistry();
        var store = new PluginInstallationStore(Path.Combine(_temp, "installed.json"), AppLog.For("Test"));

        await Installer(registry, store).InstallAsync(index.Entries[0], Path.Combine(_temp, "plugins"), Array.Empty<string>());

        var record = store.Find("market_plugin");
        Assert.NotNull(record);
        Assert.Equal("5.1.0", record!.Version);
        Assert.Contains("recorded", record.ResourceIds);

        // And it survives a restart of the app.
        var reloaded = new PluginInstallationStore(Path.Combine(_temp, "installed.json"), AppLog.For("Test"));
        reloaded.Load();
        Assert.Equal("5.1.0", reloaded.Find("market_plugin")!.Version);
    }

    [Fact]
    public void A_damaged_record_file_is_reported_rather_than_fatal()
    {
        var path = Path.Combine(_temp, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        var store = new PluginInstallationStore(path, AppLog.For("Test"));
        store.Load();

        Assert.Empty(store.Installed);
    }

    [Fact]
    public async Task An_entry_without_a_download_url_is_reported_rather_than_attempted()
    {
        var registry = new PluginRegistry();
        var result = await Installer(registry).InstallAsync(
            new PluginMarketplaceEntry { Id = "x", Name = "X" },
            Path.Combine(_temp, "plugins"),
            Array.Empty<string>());

        Assert.False(result.Succeeded);
        Assert.Contains("下载地址", result.Message);
    }
}
