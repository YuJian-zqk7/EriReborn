namespace EriReborn.Core.Catalog;

/// <summary>
/// Where a catalog entry's authority comes from. Authority is decided by
/// signature verification, never by the entry's own "trust" field, which is
/// only a claim (spec 12/35).
/// </summary>
public enum CatalogProvenance
{
    /// <summary>Shipped official data covered by a signature that verified.</summary>
    Official,

    /// <summary>
    /// An add-on file. It may only contribute ids the official catalog does not
    /// already define, and it never gains official authority.
    /// </summary>
    ThirdParty,

    /// <summary>
    /// Data whose provenance could not be verified. It carries no authority:
    /// it may be shown, but it must not override trusted data or drive
    /// high-risk operations such as unattended installation.
    /// </summary>
    Untrusted,
}
