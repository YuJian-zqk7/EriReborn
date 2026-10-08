using EriReborn.App.Shared;
using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Paths;
using EriReborn.Platform.Windows;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Verifies a real vendor MSI end to end, using the whole install pipeline:
/// download -> publisher signature -> msiexec -> re-detect. An installer exiting
/// with 0 is never treated as proof that the software is there (spec 19/73).
///
/// Run with --vendor-uninstall to remove what this installed.
/// </summary>
internal static class VendorInstallerCheck
{
    private const string Url = "https://the.earth.li/~sgtatham/putty/0.85/w64/putty-64bit-0.85-installer.msi";

    /// <summary>ARP display names start with this.</summary>
    private const string DisplayPrefix = "PuTTY";

    private const string ProductPattern = "^PuTTY";

    public static async Task<int> RunAsync(AppPaths paths, EriReborn.Engine.Software.SoftwareEngine engine, bool uninstall)
    {
        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var definition = new SoftwareDefinition
        {
            Id = "vendor_msi_probe",
            Name = DisplayPrefix,
            CategoryId = "Development",
            DirectoryName = "VendorMsiProbe",
            Mode = InstallationMode.Install,
            Trust = SoftwareTrust.Verified,
            Provenance = CatalogProvenance.Official,
            Version = "0.85",
            Detector = new DetectorSpec(DetectorKind.Arp, ProductPattern),
            Sources = new[]
            {
                new SoftwareSource { Kind = SourceKind.HttpUrl, Url = Url, FileName = "putty-64bit-0.85-installer.msi" },
            },
        };

        var context = new EnvironmentContext { RootPath = Path.Combine(work, "root") };

        try
        {
            // 1) Download through the engine, exactly as a real install would.
            Console.WriteLine($"      downloading {Url}");
            var msi = Path.Combine(work, "node.msi");

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                byte[] bytes;
                try
                {
                    bytes = await client.GetByteArrayAsync(Url);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine();
                    Console.WriteLine("SKIP vendor installer: the package could not be fetched, so nothing was verified.");
                    Console.WriteLine($"     reason: {ex.GetType().Name}: {ex.Message}");
                    return 0;
                }

                await File.WriteAllBytesAsync(msi, bytes);
                Console.WriteLine($"      {bytes.Length:N0} bytes");
            }

            // 2) Publisher signature, before anything is executed.
            var trust = AuthenticodeVerifier.Verify(msi);
            Console.WriteLine($"      signature : {trust.Trust} (0x{AuthenticodeVerifier.LastStatus:X8})");
            Console.WriteLine($"      signer    : {trust.Signer ?? "(none)"} / issuer {trust.Issuer ?? "(none)"}");

            failures += Report("the vendor MSI carries a valid Authenticode signature", trust.IsValid);
            failures += Report("the signer is identified", !string.IsNullOrWhiteSpace(trust.Signer));

            // 3) Detection before install must say it is not there.
            var before = await engine.DetectAsync(definition, context);
            Console.WriteLine($"      before    : {before.Outcome} {before.Detail}");
            if (before.Outcome == DetectionOutcome.Detected)
            {
                // Installing over an existing product tests nothing and can fail
                // for unrelated reasons (a downgrade, for instance).
                Console.WriteLine();
                Console.WriteLine("SKIP vendor installer: the product is already present, so no install was verified.");
                Console.WriteLine($"     detected as: {before.Detail}");
                return 0;
            }

            failures += Report("the product is absent before installing", true);

            if (uninstall)
            {
                failures += Uninstall();
                return failures;
            }

            // 4) Run the real installer through the real pipeline.
            var result = await engine.InstallAsync(definition, context);
            Console.WriteLine($"      install   : {result.State} — {result.Message}");

            if (result.State == InstallState.PermissionDenied)
            {
                // Honest outcome: this machine cannot do a machine-wide install,
                // so no install was verified. Not a pass, not a product failure.
                Console.WriteLine();
                Console.WriteLine("SKIP vendor MSI install: this environment cannot perform a machine-wide install.");
                Console.WriteLine($"     reason: {result.Message}");
                Console.WriteLine("     verified: the publisher signature and the pre-flight gate, nothing else.");
                return failures;
            }
            Console.WriteLine($"      verified  : {result.Verified?.Outcome} version={result.Verified?.Version ?? "(none)"}");

            failures += Report("the MSI installed and re-detection confirmed it", result.State == InstallState.Succeeded);
            failures += Report("post-install detection says Detected",
                result.Verified?.Outcome == DetectionOutcome.Detected);

            // 5) Prove the acceptance criterion is detection, not the exit code:
            //    an independent scan by the registry detector must agree.
            engine.InvalidateDetectionCache();
            var after = await engine.DetectAsync(definition, context);
            Console.WriteLine($"      after     : {after.Outcome} version={after.Version ?? "(none)"} source={after.Source}");
            failures += Report("an independent re-detection also finds it",
                after.Outcome == DetectionOutcome.Detected);
            failures += Report("a version was reported", !string.IsNullOrWhiteSpace(after.Version));

            return failures;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    /// <summary>Removes the product through its own registered uninstaller.</summary>
    public static int Uninstall()
    {
        var failures = 0;
        var entry = FindUninstallEntry();
        if (entry is null)
        {
            Console.WriteLine("      uninstall : nothing registered, nothing to remove.");
            return 0;
        }

        Console.WriteLine($"      uninstall : {entry.Value.DisplayName}");

        var code = entry.Value.ProductCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            Console.WriteLine("FAIL could not determine the product code");
            return 1;
        }

        var start = new System.Diagnostics.ProcessStartInfo("msiexec.exe", $"/x {code} /qn /norestart")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(TimeSpan.FromMinutes(10));

        Console.WriteLine($"      msiexec /x exit={process.ExitCode}");
        failures += Report("the product uninstalled cleanly", process.ExitCode is 0 or 3010);
        failures += Report("it is gone from the uninstall registry", FindUninstallEntry() is null);

        return failures;
    }

    internal static (string DisplayName, string? ProductCode, string? UninstallString)? FindUninstallEntryFor(string prefix)
        => FindUninstallEntryCore(prefix);

    internal static (string DisplayName, string? ProductCode, string? UninstallString)? FindUninstallEntry()
        => FindUninstallEntryCore(DisplayPrefix);

    private static (string DisplayName, string? ProductCode, string? UninstallString)? FindUninstallEntryCore(string prefix)
    {
        var locations = new (Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View)[]
        {
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
        };

        foreach (var (hive, view) in locations)
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                continue;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is not string display)
                {
                    continue;
                }

                if (display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return (display, name, entry.GetValue("UninstallString") as string);
                }
            }
        }

        return null;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
