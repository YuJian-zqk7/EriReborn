using EriReborn.Platform.Abstractions;
using AndroidBuild = global::Android.OS.Build;

namespace EriReborn.Platform.Android;

/// <summary>Real device facts taken from Android build properties (spec 10).</summary>
public sealed class AndroidSystemInfoService : ISystemInfoService
{
    public SystemInfo GetSystemInfo()
    {
        var sdk = (int)AndroidBuild.VERSION.SdkInt;
        var memory = GetTotalMemoryBytes();

        return new SystemInfo(
            PlatformId: "android",
            OsName: "Android",
            OsVersion: $"{AndroidBuild.VERSION.Release} (API {sdk})",
            Architecture: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            MachineName: $"{AndroidBuild.Manufacturer} {AndroidBuild.Model}",
            UserName: Environment.UserName,
            TotalPhysicalMemoryBytes: memory,
            ProcessorCount: Environment.ProcessorCount,
            Is64BitOperatingSystem: Environment.Is64BitOperatingSystem,
            // Android reports the chip, not a display adapter the app can name, so this stays null:
            // the caller is told it could not be read rather than handed the SoC as if it were a GPU.
            GraphicsAdapters: null,
            StorageVolumes: ReadStorageVolumes());
    }

    /// <summary>
    /// The volume the app's files live on, which is the one an install would be written to. Measured in
    /// blocks because that is how Android reports it: the byte properties overflow on volumes above 2 TB.
    /// </summary>
    private static IReadOnlyList<StorageVolume>? ReadStorageVolumes()
    {
        try
        {
            var path = global::Android.OS.Environment.DataDirectory?.AbsolutePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var stats = new global::Android.OS.StatFs(path);
            var blockSize = stats.BlockSizeLong;
            var total = blockSize * stats.BlockCountLong;
            if (total <= 0)
            {
                return null;
            }

            return new[]
            {
                new StorageVolume("数据分区", "fixed", total, blockSize * stats.AvailableBlocksLong),
            };
        }
        catch
        {
            // Best-effort, like the memory reading above: an unreadable volume is reported as
            // unreadable rather than as absent.
            return null;
        }
    }

    /// <summary>
    /// Total physical RAM as reported by ActivityManager. The Java heap cap
    /// (Runtime.MaxMemory) is deliberately not used: it is a per-process limit,
    /// not the device's memory.
    /// </summary>
    private static long GetTotalMemoryBytes()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context.GetSystemService(global::Android.Content.Context.ActivityService)
                is global::Android.App.ActivityManager manager)
            {
                var info = new global::Android.App.ActivityManager.MemoryInfo();
                manager.GetMemoryInfo(info);
                if (info.TotalMem > 0)
                {
                    return info.TotalMem;
                }
            }
        }
        catch
        {
            // Memory reporting is best-effort.
        }

        return 0;
    }
}
