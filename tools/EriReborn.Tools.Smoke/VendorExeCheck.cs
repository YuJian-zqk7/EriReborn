using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Paths;
using EriReborn.Platform.Windows;

namespace EriReborn.Tools.Smoke;

/// <summary>
/// Verifies a real vendor EXE installer end to end in an environment without
/// administrator rights: the per-user install is the case that can actually be
/// proven here.
///
/// The silent switches are the ones the vendor documents, and they are declared
/// on the source exactly as a catalog entry would have to declare them. The tool
/// never invents installer arguments.
/// </summary>
internal static class VendorExeCheck
{
    private const string Url = "https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64-install.exe";
    private const string DisplayPrefix = "SumatraPDF";
    private const string ProductPattern = "^SumatraPDF";

    /// <summary>
    /// Taken from the installer itself, which documents "-install" and "-silent"
    /// ("installs {appName} silently (without user interaction)"). Nothing here
    /// is guessed.
    /// </summary>
    private static readonly string[] SilentArguments = { "-install", "-silent" };

    /// <summary>The vendor's own silent uninstall switch.</summary>
    private const string SilentUninstallArgument = "-silent";

    public static async Task<int> RunAsync(EriReborn.Engine.Software.SoftwareEngine engine, bool uninstall)
    {
        if (uninstall)
        {
            return Remove();
        }

        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var definition = new SoftwareDefinition
        {
            Id = "vendor_exe_probe",
            Name = "SumatraPDF",
            CategoryId = "Development",
            DirectoryName = "VendorExeProbe",
            Mode = InstallationMode.Install,
            Trust = SoftwareTrust.Verified,
            Provenance = CatalogProvenance.Official,
            Detector = new DetectorSpec(DetectorKind.Arp, ProductPattern),
            Sources = new[]
            {
                new SoftwareSource
                {
                    Kind = SourceKind.HttpUrl,
                    Url = Url,
                    FileName = "SumatraPDF-install.exe",
                    Arguments = SilentArguments,
                },
            },
        };

        var context = new EnvironmentContext { RootPath = Path.Combine(work, "root") };

        try
        {
            Console.WriteLine($"      downloading {Url}");
            var exe = Path.Combine(work, "setup.exe");

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                byte[] bytes;
                try
                {
                    bytes = await client.GetByteArrayAsync(Url);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine();
                    Console.WriteLine("SKIP vendor EXE: the package could not be fetched, so nothing was verified.");
                    Console.WriteLine($"     reason: {ex.GetType().Name}: {ex.Message}");
                    return 0;
                }

                await File.WriteAllBytesAsync(exe, bytes);
                Console.WriteLine($"      {bytes.Length:N0} bytes");
            }

            var trust = AuthenticodeVerifier.Verify(exe);
            Console.WriteLine($"      signature : {trust.Trust} (0x{AuthenticodeVerifier.LastStatus:X8})");
            Console.WriteLine($"      signer    : {trust.Signer ?? "(none)"} / issuer {trust.Issuer ?? "(none)"}");

            failures += Report("the vendor EXE carries a valid Authenticode signature", trust.IsValid);
            failures += Report("the signer is identified", !string.IsNullOrWhiteSpace(trust.Signer));

            var before = await engine.DetectAsync(definition, context);
            Console.WriteLine($"      before    : {before.Outcome} {before.Detail}");

            if (before.Outcome == DetectionOutcome.Detected)
            {
                Console.WriteLine();
                Console.WriteLine("SKIP vendor EXE: the product is already present, so no install was verified.");
                return 0;
            }

            var result = await engine.InstallAsync(definition, context);
            Console.WriteLine($"      install   : {result.State} — {result.Message}");
            Console.WriteLine($"      verified  : {result.Verified?.Outcome} version={result.Verified?.Version ?? "(none)"}");

            failures += Report("the EXE installed and re-detection confirmed it", result.State == InstallState.Succeeded);
            failures += Report("post-install detection says Detected",
                result.Verified?.Outcome == DetectionOutcome.Detected);

            engine.InvalidateDetectionCache();
            var after = await engine.DetectAsync(definition, context);
            Console.WriteLine($"      after     : {after.Outcome} version={after.Version ?? "(none)"} source={after.Source}");
            failures += Report("an independent re-detection also finds it",
                after.Outcome == DetectionOutcome.Detected);

            Console.WriteLine();
            Console.WriteLine("unsigned installer gate:");
            failures += await ProbeUnsignedAsync(engine);

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

    /// <summary>
    /// A real vendor installer that carries no signature must not be executed.
    /// AutoHotkey's official installer is genuinely unsigned, which makes it the
    /// honest test case rather than a synthetic one.
    /// </summary>
    public static async Task<int> ProbeUnsignedAsync(EriReborn.Engine.Software.SoftwareEngine engine)
    {
        const string url = "https://www.autohotkey.com/download/ahk-v2.exe";

        var failures = 0;
        var work = Path.Combine(Path.GetTempPath(), "erireborn-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var definition = new SoftwareDefinition
        {
            Id = "vendor_unsigned_probe",
            Name = "AutoHotkey",
            CategoryId = "Utility",
            DirectoryName = "VendorUnsignedProbe",
            Mode = InstallationMode.Install,
            Trust = SoftwareTrust.Verified,
            Provenance = CatalogProvenance.Official,
            Detector = new DetectorSpec(DetectorKind.Arp, "^AutoHotkey"),
            Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = url, FileName = "ahk-v2.exe" } },
        };

        var context = new EnvironmentContext { RootPath = Path.Combine(work, "root") };

        try
        {
            var sample = Path.Combine(work, "ahk-v2.exe");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                try
                {
                    await File.WriteAllBytesAsync(sample, await client.GetByteArrayAsync(url));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine();
                    Console.WriteLine("SKIP unsigned probe: the package could not be fetched.");
                    Console.WriteLine($"     reason: {ex.GetType().Name}: {ex.Message}");
                    return 0;
                }
            }

            var trust = AuthenticodeVerifier.Verify(sample);
            Console.WriteLine($"      unsigned sample: {trust.Trust} (0x{AuthenticodeVerifier.LastStatus:X8})");
            failures += Report("the real vendor installer is genuinely unsigned", !trust.IsValid);

            var result = await engine.InstallAsync(definition, context);
            Console.WriteLine($"      install refused: {result.State} — {result.Message}");

            failures += Report("an unsigned installer is refused before execution",
                result.State == InstallState.PublisherUntrusted);

            engine.InvalidateDetectionCache();
            var after = await engine.DetectAsync(definition, context);
            failures += Report("the refused package installed nothing",
                after.Outcome != DetectionOutcome.Detected);

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

    private static int Remove()
    {
        var failures = 0;
        var entry = VendorInstallerCheck.FindUninstallEntryFor(DisplayPrefix);
        if (entry is null)
        {
            Console.WriteLine("      uninstall : nothing registered, nothing to remove.");
            return 0;
        }

        Console.WriteLine($"      uninstall : {entry.Value.DisplayName}");

        var command = entry.Value.UninstallString;
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.WriteLine("FAIL no uninstall string was registered");
            return 1;
        }

        // Split the registered command into program and arguments. The arguments
        // must be preserved: SumatraPDF registers '"...\SumatraPDF.exe" -uninstall',
        // and dropping "-uninstall" would merely open the reader.
        var trimmed = command.Trim();
        string path;
        string existingArguments;

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            path = trimmed[1..closing];
            existingArguments = trimmed[(closing + 1)..].Trim();
        }
        else
        {
            var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            path = space > 0 ? trimmed[..space] : trimmed;
            existingArguments = space > 0 ? trimmed[(space + 1)..].Trim() : string.Empty;
        }

        var arguments = string.IsNullOrWhiteSpace(existingArguments)
            ? SilentUninstallArgument
            : $"{existingArguments} {SilentUninstallArgument}";

        Console.WriteLine($"      running  : \"{path}\" {arguments}");

        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(path, arguments) { UseShellExecute = false })!;

        process.WaitForExit(TimeSpan.FromMinutes(10));

        Console.WriteLine($"      uninstaller exit={process.ExitCode}");
        failures += Report("the product uninstalled cleanly", process.ExitCode == 0);

        // A vendor uninstaller may relaunch itself and return before it is done,
        // so the exit code alone is not proof that the product is gone.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (VendorInstallerCheck.FindUninstallEntryFor(DisplayPrefix) is null)
            {
                Console.WriteLine($"      registry cleared after {attempt * 2}s");
                return failures;
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        failures += Report("it is gone from the uninstall registry", false);
        return failures;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label}");
        return ok ? 0 : 1;
    }
}
