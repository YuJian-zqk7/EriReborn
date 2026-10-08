using System.Text.Encodings.Web;
using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Software;

/// <summary>What EriReborn itself installed, and where (spec 19).</summary>
public sealed record InstallationRecord
{
    public required string SoftwareId { get; init; }

    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? Directory { get; init; }

    public string? Source { get; init; }

    public DateTimeOffset InstalledAt { get; init; }
}

/// <summary>
/// Persistent record of installs performed by EriReborn. It is the only
/// first-hand evidence the application has about software it placed on the
/// machine, so detection can report a real result instead of "unknown"
/// (spec 10/22). Non-sensitive, so plain JSON is appropriate.
/// </summary>
public sealed class InstallationRegistry(string filePath, IAppLogger log)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, InstallationRecord> _records = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards every read and write of <c>_records</c>.
    ///
    /// <para>
    /// The scan runs on thread-pool threads and reaches these records through
    /// detection, while an install writes to them from its own thread. A plain
    /// dictionary under that is not merely exception-prone: .NET reports the
    /// collection's state as <i>corrupted</i>, which means a lookup can answer wrongly
    /// without raising anything at all.
    /// </para>
    /// </summary>
    private readonly object _gate = new();

    public string FilePath => filePath;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    /// <summary>A snapshot: a caller enumerating this cannot race a writer.</summary>
    public IReadOnlyCollection<InstallationRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Reads the records from disk.
    ///
    /// <para>
    /// <b>Nothing is discarded until the file has been read successfully.</b> The
    /// records were cleared first before, so a single unreadable moment — another
    /// process writing the file, a partial flush — emptied the registry, and the next
    /// <see cref="Record"/> then wrote that emptiness back over the file. What was
    /// meant to be a degraded session was permanent loss.
    /// </para>
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            // Nothing is discarded until the file has been read successfully.
            if (!File.Exists(filePath))
            {
                // No file is not a failure: it genuinely means nothing has been recorded.
                _records.Clear();
                return;
            }

            var loaded = new Dictionary<string, InstallationRecord>(StringComparer.Ordinal);

            try
            {
                var json = File.ReadAllText(filePath);
                var records = JsonSerializer.Deserialize<List<InstallationRecord>>(json, Options);

                foreach (var record in records ?? new List<InstallationRecord>())
                {
                    if (!string.IsNullOrWhiteSpace(record.SoftwareId))
                    {
                        loaded[record.SoftwareId] = record;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("installations.load", "Install records could not be read; keeping what was already loaded.", ex);
                return;
            }

            _records.Clear();
            foreach (var pair in loaded)
            {
                _records[pair.Key] = pair.Value;
            }

            log.Info("installations.load", $"Loaded {_records.Count} install record(s).");
        }
    }

    /// <summary>
    /// Records an install, and reports whether it reached the disk.
    ///
    /// <para>
    /// A failed write used to be logged and forgotten, so the caller reported a
    /// successful install for a record that existed only in memory — and the next
    /// startup had never heard of it. Memory and disk agree now: either both, or
    /// neither.
    /// </para>
    /// </summary>
    public bool Record(InstallationRecord record)
    {
        lock (_gate)
        {
            var had = _records.TryGetValue(record.SoftwareId, out var previous);

            _records[record.SoftwareId] = record;
            if (Save())
            {
                return true;
            }

            if (had)
            {
                _records[record.SoftwareId] = previous!;
            }
            else
            {
                _records.Remove(record.SoftwareId);
            }

            return false;
        }
    }

    public InstallationRecord? Find(string softwareId)
    {
        lock (_gate)
        {
            return _records.GetValueOrDefault(softwareId);
        }
    }

    /// <summary>Removes an install record, and reports whether the change reached the disk.</summary>
    public bool Remove(string softwareId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(softwareId, out var removed))
            {
                return false;
            }

            _records.Remove(softwareId);
            if (Save())
            {
                return true;
            }

            // Put it back: reporting a removal that is not on disk is the same lie in
            // the other direction.
            _records[softwareId] = removed;
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
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(_records.Values.ToList(), Options));
            return true;
        }
        catch (Exception ex)
        {
            log.Error("installations.save", "Install records could not be written.", ex);
            return false;
        }
    }
}
