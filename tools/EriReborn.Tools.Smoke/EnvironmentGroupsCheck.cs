using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Runs the environment page exactly as the UI does and reports each group.
/// This is the same view model the window binds to, so a regression in the page
/// shows up here (spec 9/11).
/// </summary>
internal static class EnvironmentGroupsCheck
{
    public static async Task<int> RunAsync(AppHost host)
    {
        var failures = 0;
        var page = new EnvironmentViewModel(host);

        await page.ScanEnvironmentCommand.ExecuteAsync(null);

        Console.WriteLine($"      {page.Summary}");

        var covered = 0;
        foreach (var section in page.Sections)
        {
            Console.WriteLine($"      {section.Title,-10} {section.Summary}");

            if (section.Definitions.Count == 0)
            {
                failures += Report($"{section.CategoryId} reports its coverage gap honestly", section.Summary.Contains("目录覆盖不足"));
                continue;
            }

            covered++;

            failures += Report(
                $"{section.CategoryId} produced a row per entry",
                section.Results.Count == section.Definitions.Count);

            // Unknown is a legitimate outcome for an entry whose manifest
            // declares no detector, so only the row count must be complete.
            var decided = section.Installed + section.Missing + section.Unsupported;
            Console.WriteLine(
                $"          decided {decided}/{section.Results.Count}, unknown {section.Unknown}");

            foreach (var item in section.Results.Where(r => r.Status == Core.Domain.SoftwareStatus.Installed).Take(3))
            {
                Console.WriteLine($"          [已安装] {item.Name} — {item.Detail}");
            }
        }

        // Runtime and System were always covered; drivers and security now are too.
        failures += Report("every environment group is covered by the catalog", covered == page.Sections.Count);

        // The machine facts the AI digest is built from, read off the real machine: a Windows box that
        // cannot name an adapter or a writable volume leaves the analysis planning for a machine it was
        // never told about, and "能装在哪" is exactly what the digest is asked.
        var info = host.Platform.SystemInfo.GetSystemInfo();
        Console.WriteLine($"      显卡：{string.Join("、", info.GraphicsAdapters ?? (IReadOnlyList<string>)Array.Empty<string>())}");

        foreach (var volume in info.StorageVolumes ?? (IReadOnlyList<StorageVolume>)Array.Empty<StorageVolume>())
        {
            Console.WriteLine($"      存储卷：{volume.Name} {volume.Kind}，共 {volume.TotalBytes} 字节，可用 {volume.FreeBytes} 字节");
        }

        if (string.Equals(host.PlatformId, "windows", StringComparison.OrdinalIgnoreCase))
        {
            failures += Report("Windows 报出了至少一块显卡", info.GraphicsAdapters is { Count: > 0 });
            failures += Report("Windows 报出了可写的固定卷", info.StorageVolumes?.Any(v => v.Kind == "fixed") == true);
        }

        return failures;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
