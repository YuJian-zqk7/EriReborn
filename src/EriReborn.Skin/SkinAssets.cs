namespace EriReborn.Skin;

/// <summary>One thing a skin got wrong, and why it matters.</summary>
public sealed record SkinAssetFinding(string Slot, string Reason);

public sealed record SkinAssetValidation(
    IReadOnlyList<SkinAssetFinding> Unknown,
    IReadOnlyList<SkinAssetFinding> Empty)
{
    public bool IsClean => Unknown.Count == 0 && Empty.Count == 0;

    public string Describe()
        => string.Join(
            "；",
            Unknown.Select(item => $"未知槽 '{item.Slot}'（{item.Reason}）")
                .Concat(Empty.Select(item => $"空槽 '{item.Slot}'（{item.Reason}）")));
}

/// <summary>
/// The named asset slots a skin can declare.
///
/// <para>
/// Slots rather than free-form keys: the UI asks for "the companion", not for
/// whatever id a skin happened to invent, and a skin that declares none of a slot
/// simply has none. This lives in the skin module rather than in the UI so it can
/// be tested without a UI framework.
/// </para>
/// </summary>
public static class SkinAssets
{
    /// <summary>The character that speaks for this skin.</summary>
    public const string Character = "character";

    /// <summary>
    /// The companion that travels with the character. Eri's is 小黑, the black cat
    /// that follows her: its own art, its own slot, and optional.
    /// </summary>
    public const string Companion = "companion";

    /// <summary>
    /// The wildcard a skin uses to say "my sliced sheets are named like this". It is
    /// not an id, which is why it is a slot of its own rather than a value of one.
    /// </summary>
    public const string Sheets = "sheets";

    /// <summary>The skin's brand mark, drawn in the window header.</summary>
    public const string Logo = "logo";

    /// <summary>Art shown where a list has nothing in it yet.</summary>
    public const string EmptyState = "emptyState";

    /// <summary>Art shown while something is being worked on.</summary>
    public const string LoadingState = "loadingState";

    /// <summary>Slots whose value is a single asset id.</summary>
    public static IReadOnlyList<string> Slots { get; } = new[] { Character, Companion, Logo, EmptyState, LoadingState };

    /// <summary>Every key a skin may put in its assets section.</summary>
    public static IReadOnlyList<string> Known { get; } = new[] { Character, Companion, Sheets, Logo, EmptyState, LoadingState };

    /// <summary>
    /// What a skin's assets section got wrong.
    ///
    /// <para>
    /// A misspelled slot is invisible by construction: nothing reads it, so nothing
    /// complains, and the skin simply shows nothing where the author expected their
    /// art. That is the failure this reports.
    /// </para>
    /// </summary>
    public static SkinAssetValidation Validate(SkinManifest? skin)
    {
        var unknown = new List<SkinAssetFinding>();
        var empty = new List<SkinAssetFinding>();

        if (skin is null)
        {
            return new SkinAssetValidation(unknown, empty);
        }

        foreach (var (slot, value) in skin.Assets)
        {
            if (!Known.Contains(slot, StringComparer.Ordinal))
            {
                unknown.Add(new SkinAssetFinding(slot, "不是已知的资产槽，没有任何界面会读取它。"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                empty.Add(new SkinAssetFinding(slot, "声明了槽位但没有给出资源 id。"));
            }
        }

        return new SkinAssetValidation(unknown, empty);
    }

    /// <summary>The id declared for a slot, or null when the skin declares none.</summary>
    public static string? IdFor(SkinManifest? skin, string slot)
    {
        if (skin is null || string.IsNullOrWhiteSpace(slot))
        {
            return null;
        }

        return skin.Assets.TryGetValue(slot, out var id) && !string.IsNullOrWhiteSpace(id)
            ? id
            : null;
    }

    /// <summary>Every slot this skin filled, so a caller can report what is missing.</summary>
    public static IReadOnlyList<string> DeclaredSlots(SkinManifest? skin)
        => skin is null
            ? Array.Empty<string>()
            : Slots.Where(slot => IdFor(skin, slot) is not null).ToList();
}
