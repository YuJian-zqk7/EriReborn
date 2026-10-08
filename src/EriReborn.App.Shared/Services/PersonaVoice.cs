using EriReborn.Persona;

namespace EriReborn.App.Shared.Services;

/// <summary>
/// Resolves what the interface says, where a skin may override a persona line (spec 57/7).
///
/// <para>
/// Persona wording and skin wording are two systems: a skin may only replace a persona line, never
/// be one. So the override rides on the skin's <c>texts</c> section under a <c>persona.</c> prefix,
/// which keeps the two key spaces distinct even though they share one file — editing a UI word
/// cannot spill into the character's voice, and the other way round.
/// </para>
/// </summary>
public static class PersonaVoice
{
    /// <summary>The prefix skin text overrides use for persona lines.</summary>
    public const string Prefix = "persona.";

    /// <summary>A single persona line, or the active pack's own wording for it.</summary>
    public static string Voice(
        IReadOnlyDictionary<string, string>? skinTexts,
        PersonaCatalog personas,
        string key,
        string fallback)
        => Declared(skinTexts, key) is { } declared ? declared : personas.Say(key, fallback);

    /// <summary>
    /// A state line as a message: the fact and the emotion are the pack's, only the voice may be
    /// the skin's. A skin that declares a line for the state replaces just that, leaving the
    /// technical text and the face exactly where the engine put them.
    /// </summary>
    public static PersonaMessage Message(
        IReadOnlyDictionary<string, string>? skinTexts,
        PersonaCatalog personas,
        PersonaState state,
        string technical)
    {
        var message = personas.Say(state, technical);
        var voice = Declared(skinTexts, PersonaStates.Key(state));
        return voice is null ? message : message with { Voice = voice };
    }

    private static string? Declared(IReadOnlyDictionary<string, string>? skinTexts, string key)
        => skinTexts is not null
           && skinTexts.TryGetValue(Prefix + key, out var declared)
           && !string.IsNullOrWhiteSpace(declared)
            ? declared
            : null;
}
