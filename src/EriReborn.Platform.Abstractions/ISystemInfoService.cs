namespace EriReborn.Platform.Abstractions;

/// <summary>Real machine information, never a hard-coded placeholder.</summary>
/// <remarks>
/// The graphics adapters and the volumes are the two facts a platform may simply not have an answer
/// for, so they are nullable and defaulted rather than required: <c>null</c> means "this platform could
/// not read it" and an empty list means "it read it and there is nothing" — collapsing the two would
/// let "we could not look" be told to a reader as "you have none".
/// </remarks>
public sealed record SystemInfo(
    string PlatformId,
    string OsName,
    string OsVersion,
    string Architecture,
    string MachineName,
    string UserName,
    long TotalPhysicalMemoryBytes,
    int ProcessorCount,
    bool Is64BitOperatingSystem,
    IReadOnlyList<string>? GraphicsAdapters = null,
    IReadOnlyList<StorageVolume>? StorageVolumes = null);

/// <summary>
/// One volume the machine has mounted and can be asked about: what it is called, whether it is fixed or
/// removable, and how much room it has. No labels, no serial numbers — a volume is identified by the
/// name the operating system already shows the user.
/// </summary>
public sealed record StorageVolume(string Name, string Kind, long TotalBytes, long FreeBytes);

public interface ISystemInfoService
{
    SystemInfo GetSystemInfo();
}
