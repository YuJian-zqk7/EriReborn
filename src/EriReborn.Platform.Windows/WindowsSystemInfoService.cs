using System.Runtime.InteropServices;
using System.Security;
using EriReborn.Platform.Abstractions;
using Microsoft.Win32;

namespace EriReborn.Platform.Windows;

/// <summary>Real machine and OS facts (spec 10).</summary>
public sealed class WindowsSystemInfoService : ISystemInfoService
{
    /// <summary>
    /// The display-adapter class key. Every installed adapter gets a numbered subkey under it, and that
    /// subkey holds the driver's own name for the device — the one place it can be read without taking on
    /// a WMI dependency.
    /// </summary>
    private const string DisplayAdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public SystemInfo GetSystemInfo()
    {
        var os = Environment.OSVersion;
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        return new SystemInfo(
            PlatformId: "windows",
            OsName: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : os.Platform.ToString(),
            OsVersion: $"{os.Version} (Build {Environment.OSVersion.Version.Build})",
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            MachineName: Environment.MachineName,
            UserName: Environment.UserName,
            TotalPhysicalMemoryBytes: memory,
            ProcessorCount: Environment.ProcessorCount,
            Is64BitOperatingSystem: Environment.Is64BitOperatingSystem,
            GraphicsAdapters: ReadGraphicsAdapters(),
            StorageVolumes: ReadStorageVolumes());
    }

    /// <summary>
    /// The adapters Windows has a driver for, by the driver's own name.
    ///
    /// <para>
    /// <c>null</c> when the registry could not be read at all, so the caller can say "we could not look"
    /// rather than leave a blank that reads as "there is none".
    /// </para>
    /// </summary>
    private static IReadOnlyList<string>? ReadGraphicsAdapters()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey);
            if (classKey is null)
            {
                // No such class key on this machine: it answered, and the answer is that it lists none.
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // Only the four-digit device subkeys; the class key also carries settings subkeys
                // ("Properties", "Configuration", …) that name no device.
                if (subKeyName.Length != 4 || !int.TryParse(subKeyName, out _))
                {
                    continue;
                }

                using var deviceKey = classKey.OpenSubKey(subKeyName);
                if (deviceKey?.GetValue("DriverDesc") is not string description
                    || string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                // A machine with both an integrated and a discrete adapter lists both, and the same
                // adapter can appear twice; one line each is what a reader needs.
                var trimmed = description.Trim();
                if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(trimmed);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every mounted volume the app could write to. <c>null</c> when the platform refused to answer,
    /// which is not the same as a machine with no drives.
    /// </summary>
    private static IReadOnlyList<StorageVolume>? ReadStorageVolumes()
    {
        try
        {
            var volumes = new List<StorageVolume>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                // Network and optical drives are left out: neither is a place an install goes.
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    volumes.Add(new StorageVolume(
                        drive.Name,
                        drive.DriveType == DriveType.Removable ? "removable" : "fixed",
                        drive.TotalSize,
                        drive.AvailableFreeSpace));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A volume that answered the listing and then failed the question (an empty card
                    // reader, a drive being ejected) is skipped rather than reported as broken.
                }
            }

            return volumes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
