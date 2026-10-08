using EriReborn.Core.Logging;
using EriReborn.Engine.Plugins;
using EriReborn.Engine.Software;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A write that fails must not be reported as one that worked.
///
/// <para>
/// These stores used to mutate memory, swallow the write failure into a log line, and
/// tell the caller the operation succeeded. The user's override or install record was
/// then only in memory — and the next startup had never heard of it, after the page
/// had confirmed it worked. Memory and disk have to agree: either both, or neither.
/// </para>
/// </summary>
public sealed class StateStoreWriteFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-writefail",
        Guid.NewGuid().ToString("N"));

    private readonly IAppLogger _log = AppLog.For("Test");

    public StateStoreWriteFailureTests() => Directory.CreateDirectory(_root);

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

    /// <summary>
    /// A path whose parent is a file, so creating the directory throws. This is how a
    /// write really fails: a full disk, a permission, a path that cannot exist.
    /// </summary>
    private string UnwritablePath(string name)
    {
        var blocker = Path.Combine(_root, "blocker-" + name);
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, "state.json");
    }

    private string WritablePath(string name) => Path.Combine(_root, name);

    private static InstallationRecord Install(string id) => new() { SoftwareId = id, Name = id };

    private static InstalledPlugin Plugin(string id) => new() { Id = id, Name = id };

    // ------------------------------------------------------ install records

    [Fact]
    public void A_record_that_cannot_be_written_is_not_kept_in_memory()
    {
        var registry = new InstallationRegistry(UnwritablePath("installs"), _log);

        Assert.False(registry.Record(Install("first")));

        // Reporting success would leave the caller — and the page — believing an install
        // was recorded that the next startup will not know about.
        Assert.Equal(0, registry.Count);
        Assert.Null(registry.Find("first"));
    }

    [Fact]
    public void A_failed_record_leaves_the_previous_version_in_place()
    {
        var path = WritablePath("upgrade.json");
        var registry = new InstallationRegistry(path, _log);

        Assert.True(registry.Record(new InstallationRecord { SoftwareId = "tool", Name = "old" }));

        // Make the path unwritable and try to replace it.
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(registry.Record(new InstallationRecord { SoftwareId = "tool", Name = "new" }));

        // The old record is still what memory says, which is also what the disk says.
        Assert.Equal("old", registry.Find("tool")?.Name);
    }

    [Fact]
    public void A_removal_that_cannot_be_written_keeps_the_record()
    {
        var path = WritablePath("remove.json");
        var registry = new InstallationRegistry(path, _log);
        Assert.True(registry.Record(Install("first")));

        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(registry.Remove("first"));
        Assert.NotNull(registry.Find("first"));
    }

    [Fact]
    public void A_removal_of_something_absent_is_false_without_a_write()
    {
        var registry = new InstallationRegistry(WritablePath("absent.json"), _log);

        Assert.False(registry.Remove("never-there"));
    }

    // --------------------------------------------------------- detection hints

    [Fact]
    public void A_hint_that_cannot_be_written_is_not_kept_in_memory()
    {
        var store = new DetectionHintStore(UnwritablePath("hints"), _log);

        Assert.False(store.Set("my_tool", new DetectionHint { ArpPattern = "^My Tool" }));

        Assert.Equal(0, store.Count);
        Assert.Null(store.Get("my_tool"));
    }

    [Fact]
    public void A_hint_removal_that_cannot_be_written_keeps_the_hint()
    {
        var path = WritablePath("hints-remove.json");
        var store = new DetectionHintStore(path, _log);
        Assert.True(store.Set("my_tool", new DetectionHint { ArpPattern = "^My Tool" }));

        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(store.Remove("my_tool"));
        Assert.NotNull(store.Get("my_tool"));
    }

    [Fact]
    public void A_batch_that_cannot_be_written_changes_nothing_at_all()
    {
        var path = WritablePath("hints-batch.json");
        var store = new DetectionHintStore(path, _log);
        Assert.True(store.Set("kept", new DetectionHint { ArpPattern = "^Kept" }));

        File.Delete(path);
        Directory.CreateDirectory(path);

        var applied = store.SetMany(new Dictionary<string, DetectionHint>
        {
            ["one"] = new() { ArpPattern = "^One" },
            ["two"] = new() { ArpPattern = "^Two" },
        });

        // Half a batch would leave memory ahead of the file, which is the state that
        // loses data on the next load.
        Assert.Equal(0, applied);
        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get("kept"));
        Assert.Null(store.Get("one"));
    }

    [Fact]
    public void Setting_an_empty_hint_goes_through_removal()
    {
        var path = WritablePath("hints-empty.json");
        var store = new DetectionHintStore(path, _log);
        Assert.True(store.Set("my_tool", new DetectionHint { ArpPattern = "^My Tool" }));

        // An empty hint means "forget this one", so the answer is the removal's answer.
        Assert.True(store.Set("my_tool", new DetectionHint()));
        Assert.Null(store.Get("my_tool"));

        Assert.False(store.Set("never-there", new DetectionHint()));
    }

    // --------------------------------------------------------- plugin records

    [Fact]
    public void A_plugin_record_that_cannot_be_written_is_not_kept_in_memory()
    {
        var store = new PluginInstallationStore(UnwritablePath("plugins"), _log);

        Assert.False(store.Record(Plugin("first")));

        Assert.Empty(store.Installed);
        Assert.Null(store.Find("first"));
    }

    [Fact]
    public void A_plugin_removal_that_cannot_be_written_keeps_the_record()
    {
        var path = WritablePath("plugins-remove.json");
        var store = new PluginInstallationStore(path, _log);
        Assert.True(store.Record(Plugin("first")));

        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(store.Remove("first"));
        Assert.NotNull(store.Find("first"));
    }

    [Fact]
    public void A_plugin_removal_of_something_absent_is_false()
    {
        var store = new PluginInstallationStore(WritablePath("plugins-absent.json"), _log);

        Assert.False(store.Remove("never-there"));
    }

    // ------------------------------------------------------------ the happy path

    [Fact]
    public void A_write_that_works_still_reports_success()
    {
        // The failure handling must not have turned everything into a refusal.
        var registry = new InstallationRegistry(WritablePath("ok-installs.json"), _log);
        var hints = new DetectionHintStore(WritablePath("ok-hints.json"), _log);
        var plugins = new PluginInstallationStore(WritablePath("ok-plugins.json"), _log);

        Assert.True(registry.Record(Install("first")));
        Assert.True(hints.Set("my_tool", new DetectionHint { ArpPattern = "^My Tool" }));
        Assert.True(plugins.Record(Plugin("first")));

        Assert.Equal(1, hints.SetMany(new Dictionary<string, DetectionHint>
        {
            ["other"] = new() { ArpPattern = "^Other" },
        }));

        Assert.True(registry.Remove("first"));
        Assert.True(hints.Remove("my_tool"));
        Assert.True(plugins.Remove("first"));
    }
}
