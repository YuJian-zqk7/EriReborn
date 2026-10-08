namespace EriReborn.Core.Domain;

/// <summary>What a locator points at inside a share.</summary>
public enum ResourceLocatorKind
{
    /// <summary>One file.</summary>
    File,

    /// <summary>A whole folder, taken as one resource.</summary>
    Folder,
}

/// <summary>
/// Where one resource sits inside a share's tree (spec 29/30).
///
/// <para>
/// A share link names a tree, not an item: one link holds as many files and folders as its owner put
/// there, and several resources legitimately name the same link. The locator is the part that says
/// which item this resource means. A cloud source that carries a link and no locator therefore says
/// where to look but not what to take, which is why it is treated as incomplete rather than guessed
/// at.
/// </para>
///
/// <para>
/// All three parts are optional because platforms differ: one offers a stable item id, another only
/// a path, a third only the name inside the share. <see cref="ProviderItemId"/> is preferred when it
/// exists, because a rename or a move does not change it.
/// </para>
/// </summary>
public sealed record ResourceLocator
{
    public ResourceLocatorKind Kind { get; init; } = ResourceLocatorKind.File;

    /// <summary>Path inside the share, for example "A/a1/b1/ResourceA.zip".</summary>
    public string? Path { get; init; }

    /// <summary>The item's own name, for a share whose folders were reorganised around it.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The platform's own id for the item, when it has one. Preferred over any path.
    /// </summary>
    public string? ProviderItemId { get; init; }

    /// <summary>True when this locator says nothing about where the item is.</summary>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(Path)
        && string.IsNullOrWhiteSpace(Name)
        && string.IsNullOrWhiteSpace(ProviderItemId);

    /// <summary>The path split into steps, with separators and blanks removed.</summary>
    public IReadOnlyList<string> Segments
        => (Path ?? string.Empty)
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The folders a walk has to open before it reaches the item: every step except the last, which
    /// is the item itself. A folder locator and a file locator differ in what the last step is, not
    /// in how many folders lead to it.
    /// </summary>
    public IReadOnlyList<string> FolderSegments
        => Segments.Take(Math.Max(0, Segments.Count - 1)).ToArray();

    /// <summary>
    /// The name to match at the last step: the declared name when there is one, otherwise the path's
    /// last step. Null when this locator only carries an id, in which case the id is the whole answer.
    /// </summary>
    public string? ItemName
        => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : Segments.LastOrDefault();

    /// <summary>True when reaching this item means opening at least one folder inside the share.</summary>
    public bool NeedsFolderWalk => FolderSegments.Count > 0;

    public override string ToString()
        => !string.IsNullOrWhiteSpace(ProviderItemId)
            ? $"locator:id={ProviderItemId}"
            : $"locator:{Kind}:{Path ?? Name}";
}
