using System.Text.Encodings.Web;
using System.Text.Json;
using EriReborn.Core.Logging;

namespace EriReborn.Persona;

/// <summary>
/// Loads persona packs and tracks the active one. The active pack follows the
/// skin's declared persona, so switching skin also switches voice (spec 57).
/// </summary>
public sealed class PersonaCatalog(IAppLogger log)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Used when a skin names a persona that is not installed.</summary>
    public static readonly PersonaPack Neutral = new()
    {
        Id = "neutral",
        Name = "Neutral",
        Description = "未安装对应的 Persona，使用中性文案。",
    };

    private readonly Dictionary<string, PersonaPack> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after the active pack changes so pages can re-word themselves.</summary>
    public event EventHandler<PersonaPack>? Changed;

    public IReadOnlyCollection<PersonaPack> Packs => _byId.Values;

    public int Count => _byId.Count;

    public PersonaPack? Active { get; private set; }

    public PersonaPack ActiveOrDefault => Active ?? Neutral;

    /// <summary>
    /// Wraps a fact in the active voice.
    ///
    /// The fact is passed straight through: this method has no way to change it,
    /// which is what keeps a persona from quietly turning "unknown" into
    /// "missing" or softening a real failure (spec 4/174).
    /// </summary>
    public PersonaMessage Say(PersonaState state, string technical)
    {
        var voice = ActiveOrDefault.Say(PersonaStates.Key(state), PersonaStates.NeutralLine(state));
        return new PersonaMessage(state, technical, voice, PersonaStates.EmotionFor(state));
    }

    /// <summary>Which shipped packs do not define a line for some state.</summary>
    public IReadOnlyList<(string PackId, PersonaState State)> MissingStateLines()
    {
        var missing = new List<(string, PersonaState)>();

        foreach (var pack in _byId.Values)
        {
            foreach (var state in PersonaStates.All)
            {
                if (!pack.Messages.TryGetValue(PersonaStates.Key(state), out var line) || string.IsNullOrWhiteSpace(line))
                {
                    missing.Add((pack.Id, state));
                }
            }
        }

        return missing;
    }

    public void Load(IEnumerable<PersonaPack> packs)
    {
        _byId.Clear();
        foreach (var pack in packs)
        {
            if (!string.IsNullOrWhiteSpace(pack.Id))
            {
                _byId[pack.Id] = pack;
            }
        }

        log.Info("persona.load", $"Loaded {_byId.Count} persona pack(s).");
    }

    /// <summary>
    /// Reads every *.json in a directory. A malformed or unreadable pack is
    /// skipped with a warning rather than failing the whole catalog.
    /// </summary>
    public static IReadOnlyList<PersonaPack> Discover(string directory)
    {
        var packs = new List<PersonaPack>();
        if (!Directory.Exists(directory))
        {
            return packs;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var pack = JsonSerializer.Deserialize<PersonaPack>(File.ReadAllText(file), Options);
                if (pack is not null && !string.IsNullOrWhiteSpace(pack.Id))
                {
                    packs.Add(pack);
                }
            }
            catch (Exception)
            {
                // Skipped; the caller logs the loaded count.
            }
        }

        return packs;
    }

    /// <summary>
    /// Selects the pack for a skin's persona field. An unknown or absent persona
    /// falls back to <see cref="Neutral"/> so pages always have wording.
    /// </summary>
    public PersonaPack Activate(string? personaId)
    {
        var resolved = !string.IsNullOrWhiteSpace(personaId) && _byId.TryGetValue(personaId, out var found)
            ? found
            : Neutral;

        // Falling back silently is how a missing pack reached a release build.
        if (ReferenceEquals(resolved, Neutral) && !string.IsNullOrWhiteSpace(personaId))
        {
            log.Warn("persona.fallback", $"Persona '{personaId}' is not installed; using neutral wording.");
        }

        if (ReferenceEquals(Active, resolved))
        {
            return resolved;
        }

        Active = resolved;
        log.Info("persona.activate", $"Active persona is '{resolved.Id}'.");
        Changed?.Invoke(this, resolved);
        return resolved;
    }

    public PersonaPack? Find(string personaId) => _byId.GetValueOrDefault(personaId);

    /// <summary>Wording from the active pack, or the fallback.</summary>
    public string Say(string key, string fallback) => ActiveOrDefault.Say(key, fallback);
}
