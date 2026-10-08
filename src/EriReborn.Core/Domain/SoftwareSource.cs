namespace EriReborn.Core.Domain;

/// <summary>
/// One concrete resource location for a piece of software (spec 29).
/// CloudProvider is the platform; SoftwareSource is the address on it.
/// </summary>
public sealed record SoftwareSource
{
    public required SourceKind Kind { get; init; }

    /// <summary>Cloud provider id when <see cref="Kind"/> is CloudShare.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Direct download URL for HttpUrl sources.</summary>
    public string? Url { get; init; }

    /// <summary>Share page URL for cloud sources.</summary>
    public string? ShareUrl { get; init; }

    /// <summary>
    /// Where the item sits inside that share (spec 30). A share link names a tree, so a source that
    /// carries a link and no locator says where to look but not what to take — which is why the
    /// locator exists. Optional: a source written before locators still loads, it simply cannot point
    /// past the share's own file name.
    /// </summary>
    public ResourceLocator? Locator { get; init; }

    /// <summary>
    /// True when this source has no location recorded and one is still needed.
    ///
    /// <para>
    /// A share link names a tree, and the official share's root holds folders and no files, so a cloud
    /// source without a locator cannot be resolved by name. Saying so is a fact about the source, and it
    /// has to survive a round-trip: before this existed, opening such a plugin in the editor and saving it
    /// dropped the mark, and the entry went back to looking complete while being un-resolvable.
    /// </para>
    /// </summary>
    public bool NeedsLocatorResolution { get; init; }

    /// <summary>winget package identifier.</summary>
    public string? WingetId { get; init; }

    /// <summary>File name inside the share / at the URL.</summary>
    public string? FileName { get; init; }

    /// <summary>Expected SHA-256 of the final complete file.</summary>
    public string? Sha256 { get; init; }

    public string? Architecture { get; init; }

    public long? SizeBytes { get; init; }

    public string? Version { get; init; }

    /// <summary>
    /// Explicit silent-install arguments declared by the manifest. Scripts and
    /// installers are never given made-up switches (spec 19/20).
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public override string ToString() => Kind switch
    {
        SourceKind.Winget => $"winget:{WingetId}",
        SourceKind.HttpUrl => $"url:{Url}",
        SourceKind.CloudShare => $"{ProviderId}:{ShareUrl}",
        SourceKind.Local => $"local:{FileName}",
        _ => Kind.ToString(),
    };
}
