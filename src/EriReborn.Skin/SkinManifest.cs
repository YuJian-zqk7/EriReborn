using System.Text.Json;

namespace EriReborn.Skin;

/// <summary>
/// A full skin definition (spec 40). A skin is not just a colour set: it also
/// carries metrics, typography, control sizes, navigation and window rules.
/// </summary>
public sealed record SkinManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Persona { get; init; }

    /// <summary>
    /// The skin this one is built on, when it is a user skin rather than a shipped pack.
    ///
    /// <para>
    /// A user skin is a thin file: it names a base and lists only what it changes, so the rest —
    /// colours, sizes, pictures, typography — keeps coming from the pack. Copying the whole pack
    /// instead would freeze a snapshot of it and drift as soon as the pack is updated.
    /// </para>
    /// </summary>
    public string? BaseSkin { get; init; }

    public string Theme { get; init; } = "dark";

    /// <summary>"desktop" or "mobile"; drives layout selection (spec 8/42).</summary>
    public string Layout { get; init; } = "desktop";

    public string FontFamily { get; init; } = "Segoe UI";

    public IReadOnlyDictionary<string, string> Colors { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Metrics { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Typography { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Controls { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Navigation { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Window { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Assets { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The interface's own words this skin replaces, keyed the way <see cref="UiTexts"/> keys them
    /// (spec 5/6).
    ///
    /// <para>
    /// Only what the skin changes: anything it leaves out keeps the shipped wording, so renaming
    /// one button does not require carrying a whole translation. The same overlay rule the colours
    /// use applies here.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Texts { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Role name to cursor declaration. A skin may leave this empty and inherit the
    /// platform's own pointers, or override any role with a keyword or
    /// <c>asset:&lt;sheetId&gt;</c> (spec 112).
    /// </summary>
    public IReadOnlyDictionary<string, string> Cursors { get; init; } = new Dictionary<string, string>();

    public string? AssetManifestFile { get; init; }

    /// <summary>Where the manifest was loaded from; used to resolve relative asset paths.</summary>
    public string? SourceFile { get; init; }

    public string? GetColor(string key, string? fallback = null)
        => Colors.TryGetValue(key, out var value) ? value : fallback;

    public double GetMetric(string key, double fallback)
        => Metrics.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;

    public double GetControl(string key, double fallback)
        => Controls.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : fallback;

    public bool IsMobile => string.Equals(Layout, "mobile", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parses skin.json. Missing optional sections degrade to defaults, not to a crash.</summary>
public static class SkinManifestParser
{
    public static SkinManifest Parse(string json, string? sourceFile = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new SkinManifest
        {
            Id = String(root, "id") ?? "unknown",
            Name = String(root, "name") ?? "Unknown",
            Persona = String(root, "persona"),
            BaseSkin = String(root, "baseSkin"),
            Theme = String(root, "theme") ?? "dark",
            Layout = String(root, "layout") ?? "desktop",
            FontFamily = String(root, "fontFamily") ?? "Segoe UI",
            Colors = Map(root, "colors"),
            Metrics = Map(root, "metrics"),
            Typography = Map(root, "typography"),
            Controls = Map(root, "controls"),
            Navigation = Map(root, "navigation"),
            Window = Map(root, "window"),
            Assets = Map(root, "assets"),
            Texts = Map(root, "texts"),
            Cursors = Map(root, "cursors"),
            AssetManifestFile = String(root, "assetManifestFile"),
            SourceFile = sourceFile,
        };
    }

    private static IReadOnlyDictionary<string, string> Map(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in section.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
        }

        return map;
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
