using EriReborn.Core.Logging;
using EriReborn.Engine.Plugins;
using EriReborn.Engine.Software;
using EriReborn.Extension.Signing;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Every one of these stores loads a file and then writes it back.
///
/// <para>
/// That combination is what turns "start empty" from a degraded session into
/// permanent loss: if an unreadable moment empties the in-memory copy, the next
/// write puts the emptiness on disk. Mutation testing found four of them doing
/// exactly that — clearing before the read that could fail. These tests reload
/// after a failure and then write, which is the sequence that loses the data.
/// </para>
/// </summary>
public sealed class StateStoreReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-stores",
        Guid.NewGuid().ToString("N"));

    private readonly IAppLogger _log = AppLog.For("Test");

    public StateStoreReloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        finally
        {
            // Best effort.
        }
    }

    private string PathFor(string name) => Path.Combine(_root, name);

    // ------------------------------------------------------ install records

    private static InstallationRecord Install(string id) => new()
    {
        SoftwareId = id,
        Name = id,
        Version = "1.0",
    };

    [Fact]
    public void A_failed_install_reload_cannot_be_written_back_as_an_empty_file()
    {
        var path = PathFor("installations.json");
        var registry = new InstallationRegistry(path, _log);

        registry.Record(Install("first"));
        Assert.Equal(1, registry.Count);

        // The file becomes unreadable for a moment.
        File.WriteAllText(path, "{ truncated");
        registry.Load();

        Assert.Equal(1, registry.Count);
        Assert.NotNull(registry.Records.FirstOrDefault(r => r.SoftwareId == "first"));

        // And the next write must not be the moment the other record disappears.
        registry.Record(Install("second"));
        registry.Load();

        Assert.Equal(2, registry.Count);
        Assert.Contains(registry.Records, r => r.SoftwareId == "first");
        Assert.Contains(registry.Records, r => r.SoftwareId == "second");
    }

    [Fact]
    public void A_missing_install_file_really_is_empty()
    {
        var registry = new InstallationRegistry(PathFor("absent.json"), _log);
        registry.Record(Install("first"));

        File.Delete(PathFor("absent.json"));
        registry.Load();

        // No file is not a read failure: there is genuinely nothing recorded.
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void A_good_install_reload_replaces_rather_than_accumulates()
    {
        var path = PathFor("replace.json");
        var registry = new InstallationRegistry(path, _log);

        registry.Record(Install("first"));
        registry.Load();

        Assert.Equal(1, registry.Count);
    }

    // --------------------------------------------------------- detection hints

    [Fact]
    public void A_failed_hint_reload_cannot_be_written_back_as_an_empty_file()
    {
        var path = PathFor("hints.json");
        var store = new DetectionHintStore(path, _log);

        store.Set("my_tool", new DetectionHint { ArpPattern = "^My Tool" });
        Assert.Equal(1, store.Count);

        File.WriteAllText(path, "{ truncated");
        store.Load();

        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get("my_tool"));

        store.Set("other_tool", new DetectionHint { ArpPattern = "^Other" });
        store.Load();

        Assert.Equal(2, store.Count);
        Assert.NotNull(store.Get("my_tool"));
        Assert.NotNull(store.Get("other_tool"));
    }

    [Fact]
    public void The_legacy_bare_path_form_is_still_read()
    {
        var path = PathFor("legacy-hints.json");
        File.WriteAllText(path, """{ "my_tool": "C:\\Tools\\MyTool" }""");

        var store = new DetectionHintStore(path, _log);
        store.Load();

        Assert.Equal("C:\\Tools\\MyTool", store.Get("my_tool")?.Path);
    }

    // ------------------------------------------------------- plugin installs

    private static InstalledPlugin Plugin(string id) => new() { Id = id, Name = id };

    [Fact]
    public void A_failed_plugin_reload_cannot_be_written_back_as_an_empty_file()
    {
        var path = PathFor("plugins.json");
        var store = new PluginInstallationStore(path, _log);

        store.Record(Plugin("first"));
        Assert.Single(store.Installed);

        File.WriteAllText(path, "{ truncated");
        store.Load();

        Assert.Single(store.Installed);
        Assert.NotNull(store.Find("first"));

        store.Record(Plugin("second"));
        store.Load();

        Assert.Equal(2, store.Installed.Count);
        Assert.NotNull(store.Find("first"));
        Assert.NotNull(store.Find("second"));
    }

    // ----------------------------------------------------------- trusted keys

    [Fact]
    public void A_failed_key_reload_drops_every_key_because_trust_fails_closed()
    {
        // The one store where this is the intended answer, and the opposite of the
        // other three. Nothing about an install record is a security decision, so
        // losing one is pure loss; losing the ability to verify a package is the safe
        // direction, and an unreadable trust file is not evidence that the keys it
        // used to hold are still the keys it holds.
        var store = new TrustedKeyStore(_log);

        store.LoadJson("""
        { "keys": [ { "keyId": "good", "publicKey": "AAAA", "trust": "Verified" } ] }
        """);
        Assert.Equal(1, store.Count);

        store.LoadJson("{ truncated");

        Assert.Equal(0, store.Count);
        Assert.Null(store.Find("good"));
    }

    [Fact]
    public void A_good_key_reload_still_replaces()
    {
        var store = new TrustedKeyStore(_log);

        store.LoadJson("""
        { "keys": [ { "keyId": "first", "publicKey": "AAAA" } ] }
        """);
        store.LoadJson("""
        { "keys": [ { "keyId": "second", "publicKey": "BBBB" } ] }
        """);

        Assert.Equal(1, store.Count);
        Assert.Null(store.Find("first"));
        Assert.NotNull(store.Find("second"));
    }

    [Fact]
    public void A_key_with_no_id_or_no_public_key_is_not_trusted()
    {
        var store = new TrustedKeyStore(_log);

        store.LoadJson("""
        { "keys": [
            { "keyId": "", "publicKey": "AAAA" },
            { "keyId": "no_key", "publicKey": "" },
            { "keyId": "fine", "publicKey": "BBBB" }
        ] }
        """);

        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Find("fine"));
    }
}
