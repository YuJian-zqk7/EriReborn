namespace EriReborn.Engine.Download;

/// <summary>
/// Decides whether parallel ranges are appropriate on a given platform.
///
/// <para>
/// This is a product judgement, not a measurement. On a desktop the connection
/// is usually unmetered and a second or third socket costs nothing; on a phone
/// the same download may be on metered data and billed by the byte, so an extra
/// connection is a cost the user did not ask for. Defaulting to one stream on
/// mobile is the conservative choice, and the user can still be offered an
/// opt-in later.
/// </para>
/// </summary>
public static class SegmentPolicy
{
    /// <summary>Desktop defaults: worth it above 8 MB, at most four connections.</summary>
    public static readonly SegmentOptions Desktop = new(
        Enabled: true,
        MinimumSize: 8L * 1024 * 1024,
        MaxSegments: 4,
        MinimumSegmentSize: 2L * 1024 * 1024);

    /// <summary>Mobile defaults: one stream, because the bytes may be metered.</summary>
    public static readonly SegmentOptions Mobile = new(Enabled: false);

    public static SegmentOptions ForPlatform(string? platformId)
        => string.Equals(platformId, "android", StringComparison.OrdinalIgnoreCase)
            ? Mobile
            : Desktop;
}
