namespace EriReborn.Core.Domain;

/// <summary>
/// Software state is deliberately richer than a boolean (spec 11).
/// </summary>
public enum SoftwareStatus
{
    Unknown,
    Installed,
    Missing,
    Broken,
    Installing,
    Updating,
    Failed,
    Unsupported,
}

/// <summary>Extension / software trust grades (spec 37).</summary>
public enum SoftwareTrust
{
    Official,
    Verified,
    Community,
    Unknown,
    Invalid,
}

/// <summary>How a package is materialised on the target machine (spec 19).</summary>
public enum InstallationMode
{
    Install,
    Portable,
    Manual,
    Script,
}

public enum SoftwareTier
{
    Core,
    Recommended,
    Optional,
}

/// <summary>
/// Where a concrete resource for one software comes from. This is separate
/// from the cloud provider that hosts it (spec 29).
/// </summary>
public enum SourceKind
{
    Official,
    Winget,
    HttpUrl,
    CloudShare,
    Local,
    Manual,
}
