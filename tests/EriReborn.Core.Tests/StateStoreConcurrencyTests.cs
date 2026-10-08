using EriReborn.Core.Logging;
using EriReborn.Engine.Plugins;
using EriReborn.Engine.Software;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// These stores are read while a scan runs and written while the user edits.
///
/// <para>
/// <c>SystemScanService</c> scans with <c>Parallel.ForEachAsync</c>, and every
/// parallel branch reaches the detection hints through the engine. The UI writes to
/// the same store from its own thread. A plain <c>Dictionary</c> is not safe under
/// that: reading while another thread inserts or removes can throw, and can also
/// return a wrong answer without throwing, which is worse.
/// </para>
///
/// <para>
/// These tests do not prove the absence of races — nothing does. They are written to
/// fail loudly today and to keep failing if the locking is ever removed.
/// </para>
/// </summary>
public sealed class StateStoreConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erireborn-concurrency",
        Guid.NewGuid().ToString("N"));

    private readonly IAppLogger _log = AppLog.For("Test");

    public StateStoreConcurrencyTests() => Directory.CreateDirectory(_root);

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

    /// <summary>
    /// Runs writers and readers at once and rethrows whatever escaped, so the test
    /// reports the real exception rather than a timeout.
    /// </summary>
    private static void Hammer(Func<int, CancellationToken, Task> body, int writers, int readers, int iterations)
    {
        using var start = new Barrier(writers + readers);
        var failures = new List<Exception>();
        var tasks = new List<Task>();

        for (var index = 0; index < writers + readers; index++)
        {
            var isWriter = index < writers;
            var id = index;

            tasks.Add(Task.Run(() =>
            {
                try
                {
                    start.SignalAndWait();

                    for (var round = 0; round < iterations; round++)
                    {
                        body(isWriter ? id : -1, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            }));
        }

        Assert.True(
            Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(120)),
            "并发操作没有在时限内结束。");

        if (failures.Count > 0)
        {
            Assert.Fail($"{failures.Count} 次并发操作抛出异常，第一个是：{failures[0].GetType().Name}: {failures[0].Message}");
        }
    }

    // ------------------------------------------------------------ snapshots

    [Fact]
    public void The_hint_view_is_a_snapshot_not_the_live_dictionary()
    {
        // Handing out the live dictionary would move the race to the caller: it would
        // enumerate while another thread writes, which is the same failure one layer
        // out and much harder to find.
        var store = new DetectionHintStore(PathFor("snapshot-hints.json"), _log);
        store.Set("first", new DetectionHint { ArpPattern = "^First" });

        var view = store.All;
        Assert.Single(view);

        store.Set("second", new DetectionHint { ArpPattern = "^Second" });

        Assert.Single(view);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void The_install_record_view_is_a_snapshot()
    {
        var registry = new InstallationRegistry(PathFor("snapshot-installs.json"), _log);
        registry.Record(new InstallationRecord { SoftwareId = "first", Name = "First" });

        var view = registry.Records;
        Assert.Single(view);

        registry.Record(new InstallationRecord { SoftwareId = "second", Name = "Second" });

        Assert.Single(view);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void The_plugin_view_is_a_snapshot()
    {
        var store = new PluginInstallationStore(PathFor("snapshot-plugins.json"), _log);
        store.Record(new InstalledPlugin { Id = "first", Name = "First" });

        var view = store.Installed;
        Assert.Single(view);

        store.Record(new InstalledPlugin { Id = "second", Name = "Second" });

        Assert.Single(view);
        Assert.Equal(2, store.Installed.Count);
    }

    [Fact]
    public void Hints_survive_being_read_while_they_are_written()
    {
        var store = new DetectionHintStore(PathFor("hints.json"), _log);

        Hammer(
            (writer, _) =>
            {
                if (writer < 0)
                {
                    // A reader, exactly as a scan branch does it.
                    store.Get("tool-" + (Environment.TickCount & 7));
                    return Task.CompletedTask;
                }

                if ((writer & 1) == 0)
                {
                    store.Set("tool-" + writer, new DetectionHint { ArpPattern = "^Tool" + writer });
                }
                else
                {
                    store.Remove("tool-" + (writer - 1));
                }

                return Task.CompletedTask;
            },
            writers: 4,
            readers: 6,
            iterations: 300);
    }

    [Fact]
    public void Install_records_survive_being_read_while_they_are_written()
    {
        var registry = new InstallationRegistry(PathFor("installations.json"), _log);

        Hammer(
            (writer, _) =>
            {
                if (writer < 0)
                {
                    registry.Find("tool-" + (Environment.TickCount & 7));
                    return Task.CompletedTask;
                }

                if ((writer & 1) == 0)
                {
                    registry.Record(new InstallationRecord { SoftwareId = "tool-" + writer, Name = "Tool" });
                }
                else
                {
                    registry.Remove("tool-" + (writer - 1));
                }

                return Task.CompletedTask;
            },
            writers: 4,
            readers: 6,
            iterations: 300);
    }

    [Fact]
    public void Plugin_records_survive_being_read_while_they_are_written()
    {
        var store = new PluginInstallationStore(PathFor("plugins.json"), _log);

        Hammer(
            (writer, _) =>
            {
                if (writer < 0)
                {
                    store.Find("plugin-" + (Environment.TickCount & 7));
                    return Task.CompletedTask;
                }

                if ((writer & 1) == 0)
                {
                    store.Record(new InstalledPlugin { Id = "plugin-" + writer, Name = "Plugin" });
                }
                else
                {
                    store.Remove("plugin-" + (writer - 1));
                }

                return Task.CompletedTask;
            },
            writers: 4,
            readers: 6,
            iterations: 300);
    }

    [Fact]
    public void A_batch_does_not_fall_over_while_other_threads_read()
    {
        var store = new DetectionHintStore(PathFor("batch.json"), _log);

        Hammer(
            (writer, _) =>
            {
                if (writer < 0)
                {
                    store.Get("bulk-" + (Environment.TickCount & 7));
                    return Task.CompletedTask;
                }

                store.SetMany(new Dictionary<string, DetectionHint>
                {
                    ["bulk-" + writer] = new() { ArpPattern = "^Bulk" + writer },
                });

                return Task.CompletedTask;
            },
            writers: 4,
            readers: 6,
            iterations: 200);
    }
}
