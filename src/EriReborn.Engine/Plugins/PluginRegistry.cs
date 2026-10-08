using EriReborn.Core.Domain;

namespace EriReborn.Engine.Plugins;

/// <summary>What a replacement did, and what it refused.</summary>
public sealed record PluginReplaceOutcome(int Removed, int Added, IReadOnlyList<string> Rejected)
{
    /// <summary>True when the whole new set was taken and the plugin is fully updated.</summary>
    public bool IsComplete => Rejected.Count == 0;
}

/// <summary>
/// The resources imported from plugins during this session.
///
/// The official catalog is immutable, and it should stay that way: a plugin
/// appends, it never rewrites established data (spec 84). Keeping the imported
/// resources in their own collection makes that a property of the type rather
/// than a rule someone has to remember.
/// </summary>
public sealed class PluginRegistry
{
    private readonly List<SoftwareDefinition> _imported = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards every read and write of the two collections.
    ///
    /// <para>
    /// This is written from a thread-pool thread and read from the UI thread, and that
    /// is a real path rather than a hypothetical one:
    /// <c>PluginMarketplaceInstaller</c> awaits with <c>ConfigureAwait(false)</c>, so its
    /// replacement runs off the UI thread, while the software page enumerates
    /// <see cref="Imported"/> on it. A list enumerated while another thread adds to it
    /// throws.
    /// </para>
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// A snapshot. Handing out the live list would move the race to the caller, which
    /// is the same failure one layer out and much harder to find.
    /// </summary>
    public IReadOnlyList<SoftwareDefinition> Imported
    {
        get
        {
            lock (_gate)
            {
                return _imported.ToList();
            }
        }
    }

    /// <summary>Raised after resources change, so the UI can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Replaces everything one plugin contributed. That is what an update is.
    ///
    /// Only that plugin's resources are touched: another plugin's resources are
    /// never collateral damage, and the official catalog is never involved.
    /// </summary>
    public int Replace(string pluginId, IEnumerable<SoftwareDefinition> definitions)
        => ReplaceChecked(pluginId, definitions).Added;

    /// <summary>
    /// Replaces a plugin's resources, and reports what it would not take.
    ///
    /// <para>
    /// <b>All or nothing.</b> The old entries used to be removed before the new ones
    /// were checked, so a new resource colliding with another plugin's id silently
    /// dropped that resource — after the previous version's were already gone. A
    /// failed update could therefore leave a plugin emptier than before, while the
    /// caller recorded a successful install. Nothing is mutated now unless the whole
    /// new set can be taken.
    /// </para>
    /// </summary>
    public PluginReplaceOutcome ReplaceChecked(string pluginId, IEnumerable<SoftwareDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        ArgumentNullException.ThrowIfNull(definitions);

        PluginReplaceOutcome outcome;
        var changed = false;

        lock (_gate)
        {
            var mine = new HashSet<string>(
                _imported.Where(item => IsOwnedBy(item, pluginId)).Select(item => item.Id),
                StringComparer.Ordinal);

            var rejected = new List<string>();
            var accepted = new List<SoftwareDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var definition in definitions)
            {
                // One file declaring the same id twice is a defect in the file, not a
                // reason to import it twice.
                if (!seen.Add(definition.Id))
                {
                    rejected.Add(definition.Id);
                    continue;
                }

                // An id another plugin owns is not up for grabs; the incoming definition
                // is refused rather than allowed to take it.
                if (_ids.Contains(definition.Id) && !mine.Contains(definition.Id))
                {
                    rejected.Add(definition.Id);
                    continue;
                }

                accepted.Add(definition);
            }

            if (rejected.Count > 0)
            {
                // Refused as a whole, so the version already installed keeps working.
                return new PluginReplaceOutcome(0, 0, rejected);
            }

            var removed = _imported.Where(item => IsOwnedBy(item, pluginId)).ToList();
            foreach (var item in removed)
            {
                _imported.Remove(item);
                _ids.Remove(item.Id);
            }

            foreach (var definition in accepted)
            {
                if (_ids.Add(definition.Id))
                {
                    _imported.Add(definition);
                }
            }

            changed = removed.Count > 0 || accepted.Count > 0;
            outcome = new PluginReplaceOutcome(removed.Count, accepted.Count, rejected);
        }

        // Raised outside the lock: the handler on the other end reads this registry
        // again, and an event that can re-enter its own lock is a deadlock waiting for
        // a different handler to be written.
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return outcome;
    }

    /// <summary>The resources one plugin currently contributes.</summary>
    public IReadOnlyList<string> OwnedIds(string pluginId)
    {
        lock (_gate)
        {
            return _imported.Where(item => IsOwnedBy(item, pluginId)).Select(item => item.Id).ToList();
        }
    }

    /// <summary>True when this plugin has already contributed something.</summary>
    public bool Owns(string pluginId)
    {
        lock (_gate)
        {
            return _imported.Any(item => IsOwnedBy(item, pluginId));
        }
    }

    private static bool IsOwnedBy(SoftwareDefinition definition, string pluginId)
        => string.Equals(definition.CatalogId, pluginId, StringComparison.Ordinal);

    /// <summary>Adds the accepted resources and reports how many were new.</summary>
    public int Add(IEnumerable<SoftwareDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var added = 0;

        lock (_gate)
        {
            foreach (var definition in definitions)
            {
                // Second guard against duplicates: importing the same plugin twice
                // must not produce two entries for one resource.
                if (!_ids.Add(definition.Id))
                {
                    continue;
                }

                _imported.Add(definition);
                added++;
            }
        }

        if (added > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    public bool Contains(string id)
    {
        lock (_gate)
        {
            return _ids.Contains(id);
        }
    }
}
