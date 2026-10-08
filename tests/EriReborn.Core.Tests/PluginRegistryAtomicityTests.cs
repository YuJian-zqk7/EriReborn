using EriReborn.Core.Domain;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A plugin update must never leave the plugin emptier than it was.
///
/// <para>
/// The old replacement removed the plugin's resources first and checked the new
/// ones afterwards, so an id colliding with another plugin silently dropped that
/// resource — after the previous version's were already gone. The caller then
/// recorded a successful install of a plugin that had lost its contents.
/// </para>
/// </summary>
public sealed class PluginRegistryAtomicityTests
{
    private static SoftwareDefinition Definition(string id, string owner) => new()
    {
        Id = id,
        Name = id,
        CategoryId = "Utility",
        DirectoryName = id,
        CatalogId = owner,
    };

    private static PluginRegistry RegistryWith(params SoftwareDefinition[] definitions)
    {
        var registry = new PluginRegistry();
        registry.Add(definitions);
        return registry;
    }

    [Fact]
    public void A_replacement_with_nothing_in_the_way_goes_through()
    {
        var registry = RegistryWith(Definition("mine_v1", "my_plugin"));

        var outcome = registry.ReplaceChecked("my_plugin", new[] { Definition("mine_v2", "my_plugin") });

        Assert.True(outcome.IsComplete);
        Assert.Equal(1, outcome.Removed);
        Assert.Equal(1, outcome.Added);
        Assert.False(registry.Contains("mine_v1"));
        Assert.True(registry.Contains("mine_v2"));
    }

    [Fact]
    public void A_plugin_may_replace_its_own_ids()
    {
        // Its own ids are not a collision; otherwise a plugin could never update
        // anything it already contributed.
        var registry = RegistryWith(Definition("mine_v1", "my_plugin"));

        var outcome = registry.ReplaceChecked("my_plugin", new[] { Definition("mine_v1", "my_plugin") });

        Assert.True(outcome.IsComplete);
        Assert.True(registry.Contains("mine_v1"));
        Assert.Single(registry.Imported);
    }

    [Fact]
    public void A_replacement_colliding_with_another_plugin_is_refused_as_a_whole()
    {
        var registry = RegistryWith(
            Definition("other_tool", "other_plugin"),
            Definition("mine_v1", "my_plugin"));

        var outcome = registry.ReplaceChecked("my_plugin", new[]
        {
            Definition("mine_v2", "my_plugin"),
            Definition("other_tool", "my_plugin"),
        });

        Assert.False(outcome.IsComplete);
        Assert.Contains("other_tool", outcome.Rejected);

        // Nothing was touched: the old version still works, and the new id was not
        // half-applied.
        Assert.True(registry.Contains("mine_v1"));
        Assert.False(registry.Contains("mine_v2"));
        Assert.True(registry.Contains("other_tool"));
    }

    [Fact]
    public void A_refused_replacement_reports_that_it_changed_nothing()
    {
        var registry = RegistryWith(Definition("other_tool", "other_plugin"));

        var outcome = registry.ReplaceChecked("my_plugin", new[]
        {
            Definition("other_tool", "my_plugin"),
            Definition("mine_v1", "my_plugin"),
        });

        Assert.Equal(0, outcome.Removed);
        Assert.Equal(0, outcome.Added);
        Assert.False(registry.Owns("my_plugin"));
    }

    [Fact]
    public void A_file_declaring_one_id_twice_is_refused_rather_than_imported_twice()
    {
        var registry = new PluginRegistry();

        var outcome = registry.ReplaceChecked("my_plugin", new[]
        {
            Definition("dupe", "my_plugin"),
            Definition("dupe", "my_plugin"),
        });

        Assert.False(outcome.IsComplete);
        Assert.False(registry.Contains("dupe"));
        Assert.Empty(registry.Imported);
    }

    [Fact]
    public void A_fresh_install_uses_the_same_rule()
    {
        // The plugin owns nothing yet, so replacing is the same operation as adding,
        // and it refuses collisions a plain add would have skipped.
        var registry = RegistryWith(Definition("other_tool", "other_plugin"));

        var outcome = registry.ReplaceChecked("brand_new", new[] { Definition("brand_new_tool", "brand_new") });

        Assert.True(outcome.IsComplete);
        Assert.True(registry.Contains("brand_new_tool"));
    }

    [Fact]
    public void The_plain_replace_still_reports_how_many_were_added()
    {
        var registry = new PluginRegistry();

        Assert.Equal(1, registry.Replace("my_plugin", new[] { Definition("mine", "my_plugin") }));

        // And a refused one adds nothing.
        Assert.Equal(0, registry.Replace("other_plugin", new[] { Definition("mine", "other_plugin") }));
    }

    // ------------------------------------------------------------ concurrency

    [Fact]
    public async Task The_registry_survives_being_enumerated_while_it_is_written()
    {
        // A real path, not a hypothetical one: PluginMarketplaceInstaller awaits with
        // ConfigureAwait(false), so its ReplaceChecked runs on a thread-pool thread,
        // while the software page reads Imported on the UI thread.
        var registry = new PluginRegistry();
        using var start = new Barrier(10);
        var failures = new List<Exception>();
        var tasks = new List<Task>();

        for (var index = 0; index < 10; index++)
        {
            var writer = index < 4;
            var id = index;

            tasks.Add(Task.Run(() =>
            {
                try
                {
                    start.SignalAndWait();

                    for (var round = 0; round < 300; round++)
                    {
                        if (writer)
                        {
                            registry.ReplaceChecked(
                                "plugin-" + id,
                                new[] { Definition("res-" + id + "-" + round, "plugin-" + id) });
                        }
                        else
                        {
                            // Exactly what the software page does.
                            foreach (var _ in registry.Imported)
                            {
                            }
                        }
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

        // Awaited rather than blocked on: a test method that blocks on tasks can
        // deadlock the runner rather than report the failure.
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));

        if (failures.Count > 0)
        {
            Assert.Fail($"{failures.Count} 次并发操作抛出异常，第一个是：{failures[0].GetType().Name}: {failures[0].Message}");
        }
    }

    [Fact]
    public void The_registry_still_only_ever_appends_to_its_own_collection()
    {
        var registry = new PluginRegistry();
        var changed = 0;
        registry.Changed += (_, _) => changed++;

        registry.ReplaceChecked("my_plugin", new[] { Definition("mine", "my_plugin") });
        registry.ReplaceChecked("other_plugin", new[] { Definition("theirs", "other_plugin") });

        Assert.Equal(2, changed);
        Assert.Equal(2, registry.Imported.Count);

        // A refused replacement changes nothing, so nobody is told to refresh.
        var before = changed;
        registry.ReplaceChecked("my_plugin", new[] { Definition("theirs", "my_plugin") });
        Assert.Equal(before, changed);
    }
}
