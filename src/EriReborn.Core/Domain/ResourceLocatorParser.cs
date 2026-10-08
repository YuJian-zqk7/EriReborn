using System.Text.Json;

namespace EriReborn.Core.Domain;

/// <summary>
/// Reads the <c>locator</c> object a source may carry (spec 30).
///
/// <para>
/// One reader for both the plugin and the catalog formats: they used to disagree — only plugins
/// could carry a locator — which is why a locator typed into the plugin builder survived and one
/// written into a catalog did not. The shape is the same for both, so it is parsed in one place.
/// </para>
///
/// <para>
/// Absent is allowed and means what it always meant: the item is looked for by its file name in the
/// share. A locator with no usable content is treated as absent rather than as an empty one, so
/// nothing downstream has to tell "not declared" from "declared blank".
/// </para>
/// </summary>
public static class ResourceLocatorParser
{
    public static ResourceLocator? Parse(JsonElement source)
    {
        if (!source.TryGetProperty("locator", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var locator = new ResourceLocator
        {
            Kind = string.Equals(String(element, "type"), "folder", StringComparison.OrdinalIgnoreCase)
                || string.Equals(String(element, "kind"), "folder", StringComparison.OrdinalIgnoreCase)
                ? ResourceLocatorKind.Folder
                : ResourceLocatorKind.File,
            Path = String(element, "path"),
            Name = String(element, "name"),
            ProviderItemId = String(element, "providerItemId"),
        };

        return locator.IsEmpty ? null : locator;
    }

    /// <summary>
    /// The locator a source written before locators existed implies: its file name is the item to
    /// look for in the share. Used by the one-way legacy importers, never by the readers that also
    /// write their model back — turning a legacy file name into a stored locator there would change
    /// what a file says without the user asking.
    /// </summary>
    public static ResourceLocator? FromLegacyFileName(string? fileName)
        => string.IsNullOrWhiteSpace(fileName)
            ? null
            : new ResourceLocator { Kind = ResourceLocatorKind.File, Name = fileName };

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
