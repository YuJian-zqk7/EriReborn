using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Plugins;

/// <summary>What a plugin contributed, and which version contributed it.</summary>
public sealed record InstalledPlugin
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Sha256 { get; init; }

    public string? SourceUrl { get; init; }

    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The resource ids this plugin currently owns, so an update can replace exactly them.</summary>
    public IReadOnlyList<string> ResourceIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Remembers which plugins are installed and at which version.
///
/// Plugins are updated independently of the app and of each other (spec 198), so
/// "what is installed" has to be recorded somewhere durable rather than inferred
/// from whatever happens to be in memory.
/// </summary>
public sealed class PluginInstallationStore(string filePath, IAppLogger log)
{
    private readonly string _filePath = filePath;
    private readonly IAppLogger _log = log;
    private readonly List<InstalledPlugin> _installed = new();

    /// <summary>
    /// Guards every read and write of <c>_installed</c>. A list mutated while another
    /// thread enumerates it throws, and the plugins page reads this while the
    /// marketplace writes it.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>A snapshot: a caller enumerating this cannot race a writer.</summary>
    public IReadOnlyList<InstalledPlugin> Installed
    {
        get
        {
            lock (_gate)
            {
                return _installed.ToList();
            }
        }
    }

    public InstalledPlugin? Find(string id)
    {
        lock (_gate)
        {
            return _installed.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Reads the record file. A damaged file is reported and the records already
    /// loaded are kept, rather than taking the plugins page down with them.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            // Kept until the file has been read successfully: Record writes this list
            // back to disk, so an empty list is not a harmless session state — it
            // becomes the file.
            if (!File.Exists(_filePath))
            {
                _installed.Clear();
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var records = JsonSerializer.Deserialize<List<InstalledPlugin>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (records is not null)
                {
                    _installed.Clear();
                    _installed.AddRange(records.Where(item => !string.IsNullOrWhiteSpace(item.Id)));
                }
            }
            catch (Exception ex)
            {
                _log.Error("plugin.store", $"插件安装记录无法读取，保留已加载的内容：{ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Inserts or replaces one plugin's record, then writes the file.
    /// </summary>
    /// <returns>True when the record reached the disk; false means nothing changed.</returns>
    public bool Record(InstalledPlugin record)
    {
        lock (_gate)
        {
            var previous = _installed.Where(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal)).ToList();

            _installed.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
            _installed.Add(record);

            if (Save())
            {
                return true;
            }

            // Put the previous version back rather than leaving memory claiming a
            // record the file does not have.
            _installed.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
            _installed.AddRange(previous);
            return false;
        }
    }

    /// <returns>True when the removal reached the disk.</returns>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _installed.Where(item => string.Equals(item.Id, id, StringComparison.Ordinal)).ToList();
            if (removed.Count == 0)
            {
                return false;
            }

            _installed.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (Save())
            {
                return true;
            }

            _installed.AddRange(removed);
            return false;
        }
    }

    /// <summary>
    /// Writes the file. Called with <c>_gate</c> held, so it takes no lock of its own.
    /// </summary>
    /// <returns>True when the file was written; false means nothing was persisted.</returns>
    private bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _filePath,
                JsonSerializer.Serialize(_installed, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            // A record that cannot be written must not take the install down with it —
            // but it must not be reported as stored either.
            _log.Error("plugin.store", $"插件安装记录无法写入：{ex.Message}", ex);
            return false;
        }
    }
}
