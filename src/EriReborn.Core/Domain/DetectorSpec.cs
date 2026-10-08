namespace EriReborn.Core.Domain;

/// <summary>
/// Declarative detection instruction from a manifest (spec 21).
/// No platform type leaks into Core.
/// </summary>
public sealed record DetectorSpec(DetectorKind Kind, string? Param = null, string? SecondaryParam = null)
{
    public static readonly DetectorSpec None = new(DetectorKind.None);

    public bool IsNone => Kind == DetectorKind.None;
}

/// <summary>Detector kinds understood by the platform abstraction.</summary>
public enum DetectorKind
{
    None,
    Arp,
    RegistryRelease,
    RegistrySubKey,
    DotNet,
    Video,
    Sound,
    Pnp,
    WebView2,
    File,
    Msix,
    Command,
    Service,

    /// <summary>Android package presence, resolved through PackageManager.</summary>
    AndroidPackage,
}
