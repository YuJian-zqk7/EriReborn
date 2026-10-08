using System.Text.Encodings.Web;
using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.Engine.Software;

/// <summary>
/// A user-supplied detection override for one catalog entry. Most Windows
/// software registers itself in Add/Remove Programs, so an ARP name pattern is
/// the highest-leverage override; a file path covers the rest.
/// </summary>
public sealed record DetectionHint
{
    /// <summary>Absolute path to a file or directory that must exist.</summary>
    public string? Path { get; init; }

    /// <summary>Regular expression matched against the ARP display name.</summary>
    public string? ArpPattern { get; init; }

    /// <summary>File name expected inside the software's own directory.</summary>
    public string? FileName { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Path)
        && string.IsNullOrWhiteSpace(ArpPattern)
        && string.IsNullOrWhiteSpace(FileName);

    /// <summary>Describes the override for the UI and logs.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ArpPattern))
        {
            parts.Add($"ARP ~ /{ArpPattern}/");
        }

        if (!string.IsNullOrWhiteSpace(Path))
        {
            parts.Add(Path!);
        }

        if (!string.IsNullOrWhiteSpace(FileName))
        {
            parts.Add($"文件 {FileName}");
        }

        return parts.Count == 0 ? "（空）" : string.Join(" · ", parts);
    }
}

/// <summary>
/// Paths and detector patterns the user assigned to catalog entries whose
/// manifest declares no detector. This is how an undecidable entry becomes
/// decidable without inventing catalog data (spec 22).
/// </summary>
public sealed class DetectionHintStore(string filePath, IAppLogger log)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // The same footgun as user_config.json: this file is user-editable and
        // JSON is conventionally camelCase, so a hand-written "arpPattern" must
        // bind to ArpPattern instead of being silently ignored.
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, DetectionHint> _hints = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards every read and write of <c>_hints</c>.
    ///
    /// <para>
    /// This is the store the race is most visible in: a scan reads hints from every
    /// parallel branch of <c>Parallel.ForEachAsync</c>, and the user writes one from
    /// the UI thread. A plain dictionary under that does not merely throw — .NET
    /// reports its state as <i>corrupted</i>, so a lookup can answer wrongly with no
    /// error at all.
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
                return _hints.Count;
            }
        }
    }

    /// <summary>A snapshot: a caller enumerating this cannot race a writer.</summary>
    public IReadOnlyDictionary<string, DetectionHint> All
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, DetectionHint>(_hints, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Reads the hints from disk.
    ///
    /// <para>
    /// Replaced only once the file has been read successfully, for the same reason as
    /// the install records: the next <see cref="Set"/> writes this dictionary back to
    /// disk.
    /// </para>
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(filePath))
            {
                _hints.Clear();
                return;
            }

            var loaded = new Dictionary<string, DetectionHint>(StringComparer.Ordinal);

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(filePath));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        // Older files stored a bare path string; keep reading those.
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var legacy = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(legacy))
                            {
                                loaded[property.Name] = new DetectionHint { Path = legacy, AddedAt = DateTimeOffset.Now };
                            }

                            continue;
                        }

                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var hint = property.Value.Deserialize<DetectionHint>(Options);
                        if (hint is not null && !hint.IsEmpty)
                        {
                            loaded[property.Name] = hint;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("hints.load", "Detection hints could not be read; keeping what was already loaded.", ex);
                return;
            }

            _hints.Clear();
            foreach (var pair in loaded)
            {
                _hints[pair.Key] = pair.Value;
            }

            log.Info("hints.load", $"Loaded {_hints.Count} detection hint(s).");
        }
    }

    public DetectionHint? Get(string softwareId)
    {
        lock (_gate)
        {
            return _hints.GetValueOrDefault(softwareId);
        }
    }

    /// <summary>The legacy accessor, kept for callers that only want a path.</summary>
    public string? GetPath(string softwareId)
    {
        lock (_gate)
        {
            return _hints.GetValueOrDefault(softwareId)?.Path;
        }
    }

    /// <summary>
    /// Stores one override, and reports whether it reached the disk.
    ///
    /// <para>
    /// The write used to be logged and forgotten, so the page reported the override as
    /// applied while it existed only in memory. The next startup had never heard of it,
    /// and the user had been told it worked.
    /// </para>
    /// </summary>
    public bool Set(string softwareId, DetectionHint hint)
    {
        lock (_gate)
        {
            if (hint.IsEmpty)
            {
                return RemoveUnlocked(softwareId);
            }

            var had = _hints.TryGetValue(softwareId, out var previous);

            _hints[softwareId] = hint with { AddedAt = DateTimeOffset.Now };
            if (Save())
            {
                return true;
            }

            if (had)
            {
                _hints[softwareId] = previous!;
            }
            else
            {
                _hints.Remove(softwareId);
            }

            return false;
        }
    }

    /// <summary>
    /// Stores many overrides with a single write, for applying suggestions in
    /// bulk. Empty hints remove rather than add.
    /// </summary>
    /// <returns>How many were stored. Zero means the write failed and nothing changed.</returns>
    public int SetMany(IReadOnlyDictionary<string, DetectionHint> hints)
    {
        lock (_gate)
        {
            // The whole batch is rolled back on failure: a partial write would leave
            // memory ahead of the file, which is the state that loses data on the next
            // load.
            var snapshot = new Dictionary<string, DetectionHint>(_hints, StringComparer.Ordinal);

            var applied = 0;
            foreach (var (softwareId, hint) in hints)
            {
                if (hint.IsEmpty)
                {
                    if (_hints.Remove(softwareId))
                    {
                        applied++;
                    }

                    continue;
                }

                _hints[softwareId] = hint with { AddedAt = DateTimeOffset.Now };
                applied++;
            }

            if (applied == 0)
            {
                return 0;
            }

            if (Save())
            {
                return applied;
            }

            _hints.Clear();
            foreach (var pair in snapshot)
            {
                _hints[pair.Key] = pair.Value;
            }

            return 0;
        }
    }

    public bool Remove(string softwareId)
    {
        lock (_gate)
        {
            return RemoveUnlocked(softwareId);
        }
    }

    /// <summary>
    /// The removal body. Takes no lock, so <see cref="Set"/> can call it while it
    /// already holds one.
    /// </summary>
    private bool RemoveUnlocked(string softwareId)
    {
        if (!_hints.TryGetValue(softwareId, out var removed))
        {
            return false;
        }

        _hints.Remove(softwareId);
        if (Save())
        {
            return true;
        }

        _hints[softwareId] = removed;
        return false;
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

            File.WriteAllText(filePath, JsonSerializer.Serialize(_hints, Options));
            return true;
        }
        catch (Exception ex)
        {
            log.Error("hints.save", "Detection hints could not be written.", ex);
            return false;
        }
    }
}
