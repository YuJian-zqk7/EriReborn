using EriReborn.Core.Domain;

namespace EriReborn.Cloud;

/// <summary>
/// Picks the entry a locator means out of one folder's listing (spec 30).
///
/// <para>
/// One folder at a time, in the order that survives a share being reorganised: the platform's own id
/// first, then the name. A whole path is deliberately not compared here, because the walk arrives one
/// folder at a time — that is what makes a folder the owner moved harmless, since only the last step
/// is ever matched by name.
/// </para>
/// </summary>
public static class ResourceLocatorMatcher
{
    /// <summary>
    /// The entry a locator means, or null when this folder holds no such item. A locator that names an
    /// id and nothing else is answered by the id alone: without a name there is nothing else to go on,
    /// and matching some other entry would fetch the wrong thing.
    /// </summary>
    public static CloudFile? Find(ResourceLocator locator, IReadOnlyList<CloudFile> entries)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(entries);

        if (!string.IsNullOrWhiteSpace(locator.ProviderItemId))
        {
            var byId = entries.FirstOrDefault(entry =>
                string.Equals(entry.Id, locator.ProviderItemId, StringComparison.Ordinal));

            if (byId is not null)
            {
                return byId;
            }
        }

        return locator.ItemName is { Length: > 0 } name ? FindByName(name, entries) : null;
    }

    /// <summary>
    /// The one entry with this name. Two entries sharing a name in one folder cannot be told apart,
    /// and choosing either would fetch the wrong file — so the answer is "cannot tell", which the
    /// caller reports rather than hides.
    /// </summary>
    public static CloudFile? FindByName(string name, IReadOnlyList<CloudFile> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var matches = entries
            .Where(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// How many entries carry this name. Nothing found and two found both make
    /// <see cref="FindByName"/> answer null, but they need different words: one means the item was
    /// moved or renamed, the other means the locator is not specific enough to choose. Counting is
    /// what lets the walker say which one happened instead of blaming the wrong cause.
    /// </summary>
    public static int CountByName(string name, IReadOnlyList<CloudFile> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries.Count(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
