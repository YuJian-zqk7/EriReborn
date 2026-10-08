using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A resource plugin is data that says where a resource can be downloaded from.
/// It is not code, and importing one must never override existing data or let the
/// file declare its own trust (spec 42/44/45/84).
/// </summary>
public sealed class PluginImportTests
{
    private static readonly string[] Providers = { "123", "baidu", "quark", "lanzou", "xunlei" };

    private static PluginImportService Service() => new(Providers, AppLog.For("Test"));

    private static string Plugin(string resources, string id = "test_plugin", string? trust = null)
    {
        var trustLine = trust is null ? string.Empty : $",\"trust\": \"{trust}\"";
        return $$"""
        {
          "schema": 1,
          "id": "{{id}}",
          "name": "Test Plugin",
          "version": "1.0.0",
          "author": "tester"{{trustLine}},
          "resources": [{{resources}}]
        }
        """;
    }

    private static string Resource(
        string id,
        string directory = "ExampleTool",
        string category = "Utility",
        string sources = "",
        string? extra = null)
    {
        var extraLine = extra is null ? string.Empty : $",{extra}";
        return $$"""
        {
          "id": "{{id}}",
          "name": "{{id}}",
          "categoryId": "{{category}}",
          "directory": "{{directory}}",
          "version": "1.0.0"{{extraLine}},
          "sources": [{{sources}}]
        }
        """;
    }

    private static string Source(string provider, string url = "https://example.invalid/a.bin")
        => $$"""{ "kind": "CloudShare", "provider": "{{provider}}", "shareUrl": "{{url}}" }""";

    private static PluginImportResult Import(string json, string? signature = null, params string[] existing)
        => Service().Import(json, signature, existing);

    // ----------------------------------------------------------- happy path

    [Fact]
    public void Import_flattens_the_node_tree_with_group_paths()
    {
        // Minecraft(group)
        // ├─ 1.21.1(group)
        // │   ├─ client(folder 资源)
        // │   │   └─ mods(file 资源) ← 父文件夹资源也计入路径
        // │   └─ （无）
        // └─ server(file 资源)
        // tool(file 资源，根层)
        var json = """
        {
          "schema": 2,
          "id": "mc_plugin",
          "name": "MC",
          "version": "1.0.0",
          "nodes": [
            {
              "nodeType": "group",
              "id": "g_mc",
              "name": "Minecraft",
              "children": [
                {
                  "nodeType": "group",
                  "id": "g_1211",
                  "name": "1.21.1",
                  "children": [
                    {
                      "nodeType": "folder",
                      "resource": {
                        "id": "mc_client",
                        "name": "client",
                        "categoryId": "Game",
                        "directory": "client",
                        "tier": "optional",
                        "mode": "install",
                        "sources": [
                          {
                            "kind": "CloudShare",
                            "provider": "123",
                            "shareUrl": "https://example.invalid/s/mc",
                            "locator": { "type": "folder", "path": "1.21.1/client", "providerItemId": "item_client" }
                          }
                        ]
                      },
                      "children": [
                        {
                          "nodeType": "file",
                          "resource": {
                            "id": "mc_mods",
                            "name": "mods",
                            "categoryId": "Game",
                            "directory": "mods",
                            "tier": "optional",
                            "mode": "install",
                            "sources": [
                              { "kind": "HttpUrl", "url": "https://example.invalid/mods.zip" }
                            ]
                          }
                        }
                      ]
                    }
                  ]
                },
                {
                  "nodeType": "file",
                  "resource": {
                    "id": "mc_server",
                    "name": "server",
                    "categoryId": "Game",
                    "directory": "server",
                    "tier": "optional",
                    "mode": "install",
                    "sources": [
                      { "kind": "HttpUrl", "url": "https://example.invalid/server.zip" }
                    ]
                  }
                }
              ]
            },
            {
              "nodeType": "file",
              "resource": {
                "id": "mc_tool",
                "name": "tool",
                "categoryId": "Utility",
                "directory": "tool",
                "tier": "optional",
                "mode": "install",
                "sources": [
                  { "kind": "Manual", "fileName": "readme.txt" }
                ]
              }
            }
          ]
        }
        """;

        var result = Import(json);

        Assert.True(result.Succeeded, result.Message);

        // 前序：client → mods → server → tool。
        Assert.Equal(
            new[] { "mc_client", "mc_mods", "mc_server", "mc_tool" },
            result.Accepted.Select(d => d.Id));

        var byId = result.Accepted.ToDictionary(d => d.Id);

        Assert.Equal(new[] { "Minecraft", "1.21.1" }, byId["mc_client"].PluginGroupPath);
        Assert.Equal(new[] { "Minecraft", "1.21.1", "client" }, byId["mc_mods"].PluginGroupPath);
        Assert.Equal(new[] { "Minecraft" }, byId["mc_server"].PluginGroupPath);

        // 根层资源路径为空数组（不是 null：它来自插件树，软件页据此与官方条目区分）。
        Assert.NotNull(byId["mc_tool"].PluginGroupPath);
        Assert.Empty(byId["mc_tool"].PluginGroupPath!);

        // 来源插件 id 由 CatalogId 携带。
        Assert.All(result.Accepted, d => Assert.Equal("mc_plugin", d.CatalogId));
    }

    [Fact]
    public void A_valid_plugin_appends_its_resources()
    {
        var json = Plugin(Resource("alpha", sources: Source("123")) + "," + Resource("beta", sources: Source("baidu")));

        var result = Import(json);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Accepted.Count);
        Assert.All(result.Accepted, d => Assert.True(d.IsPluginProvided));
        Assert.All(result.Accepted, d => Assert.Equal(CatalogProvenance.ThirdParty, d.Provenance));
    }

    [Fact]
    public void One_resource_with_many_sources_stays_one_resource()
    {
        // Five share links are five places to download ONE thing, not five things.
        var sources = string.Join(",", Providers.Select(p => Source(p)));
        var json = Plugin(Resource("multi", sources: sources));

        var result = Import(json);

        var resource = Assert.Single(result.Accepted);
        Assert.Equal(5, resource.Sources.Count);
        Assert.Equal("multi", resource.Id);
    }

    [Fact]
    public void Several_resources_in_one_plugin_are_separate_software()
    {
        var json = Plugin(string.Join(",", new[] { "a", "b", "c" }.Select(id => Resource(id, directory: "Dir" + id.ToUpperInvariant(), sources: Source("123")))));

        Assert.Equal(3, Import(json).Accepted.Count);
    }

    // ------------------------------------------------------- the C rule (§84)

    [Fact]
    public void An_id_that_already_exists_is_rejected_not_overridden()
    {
        var json = Plugin(Resource("taken", sources: Source("123")) + "," + Resource("fresh", directory: "Fresh", sources: Source("123")));

        var result = Import(json, signature: null, existing: "taken");

        Assert.True(result.Succeeded);
        var accepted = Assert.Single(result.Accepted);
        Assert.Equal("fresh", accepted.Id);
        Assert.Contains(result.Rejections, i => i.Code == "plugin.id_conflict" && i.Subject == "taken");
    }

    // ----------------------------------------------------- trust (§45/46)

    [Fact]
    public void A_plugin_cannot_declare_itself_official()
    {
        // "trust": "Official" in the file is ignored; only a signature decides.
        var json = Plugin(Resource("sneaky", sources: Source("123")), trust: "Official");

        var result = Import(json);

        Assert.True(result.Succeeded);
        Assert.Equal(SoftwareTrust.Community, Assert.Single(result.Accepted).Trust);
    }

    [Fact]
    public void A_signature_from_a_key_that_is_not_pinned_is_rejected()
    {
        var json = Plugin(Resource("signed", sources: Source("123")));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signatureBytes = key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256);
        var signature = JsonSerializer.Serialize(new PluginSignature
        {
            KeyId = "attacker-key",
            Signature = Convert.ToBase64String(signatureBytes),
        });

        var result = Import(json, signature);

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.SignatureVerify, result.Stage);
    }

    [Fact]
    public void A_tampered_plugin_fails_signature_verification()
    {
        var json = Plugin(Resource("signed", sources: Source("123")));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var real = key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256);
        var signature = JsonSerializer.Serialize(new PluginSignature
        {
            KeyId = "erireborn-catalog-2026",
            Signature = Convert.ToBase64String(real),
        });

        // Signed with a throwaway key but claiming the pinned one: the bytes will
        // not verify either way, which is the point.
        var result = Import(json, signature);

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.SignatureVerify, result.Stage);
    }

    // ------------------------------------------------------- stage reporting

    [Fact]
    public void A_malformed_file_fails_at_the_read_stage()
    {
        var result = Import("{ this is not json");

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.Read, result.Stage);
    }

    [Fact]
    public void A_plugin_without_resources_fails_at_the_schema_stage()
    {
        var result = Import(Plugin(string.Empty));

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.SchemaValidate, result.Stage);
    }

    [Fact]
    public void A_resource_without_sources_fails_at_the_resource_stage()
    {
        var result = Import(Plugin(Resource("lonely")));

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.ResourceValidate, result.Stage);
    }

    [Fact]
    public void An_unknown_provider_fails_at_the_provider_stage()
    {
        var result = Import(Plugin(Resource("weird", sources: Source("dropbox"))));

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.ProviderValidate, result.Stage);
        Assert.Contains(result.Issues, i => i.Code == "plugin.unknown_provider");
    }

    [Fact]
    public void An_illegal_directory_name_is_rejected_rather_than_slugified()
    {
        var result = Import(Plugin(Resource("bad", directory: "My Tool", sources: Source("123"))));

        Assert.False(result.Succeeded);
        Assert.Equal(PluginImportStage.SchemaValidate, result.Stage);
    }

    [Fact]
    public void A_schema_from_the_future_is_refused_rather_than_guessed_at()
    {
        var json = Plugin(Resource("future", sources: Source("123"))).Replace("\"schema\": 1", "\"schema\": 99", StringComparison.Ordinal);

        var result = Import(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, i => i.Code == "plugin.schema_too_new");
    }

    [Fact]
    public void Duplicate_resource_ids_inside_one_plugin_are_refused()
    {
        var json = Plugin(Resource("same", sources: Source("123")) + "," + Resource("same", sources: Source("123")));

        var result = Import(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, i => i.Code == "plugin.duplicate_resource");
    }

    [Fact]
    public void Every_declared_provider_is_accepted_as_an_equal_peer()
    {
        // No provider is special: each one is simply a known platform id.
        foreach (var provider in Providers)
        {
            var result = Import(Plugin(Resource("p", sources: Source(provider)), id: "p_" + provider));
            Assert.True(result.Succeeded, $"{provider}: {result.Message}");
        }
    }

    [Fact]
    public void A_source_spelled_the_catalog_way_keeps_its_provider()
    {
        // Catalog files say "providerId" where plugins say "provider". A source copied out of a
        // catalog into a plugin must not lose its platform and come back as "unknown provider (空)".
        const string catalogSpelled = """{ "kind": "CloudShare", "providerId": "123", "shareUrl": "https://example.invalid/a" }""";

        var result = Import(Plugin(Resource("copied", sources: catalogSpelled)));

        Assert.True(result.Succeeded, result.Message);
        var source = Assert.Single(Assert.Single(result.Accepted).Sources);
        Assert.Equal("123", source.ProviderId);
    }
}
